using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideDock.Host;

internal static partial class Program
{
    private const int DefaultDiscoveryPort = 27182;

    private enum ConnectionMode
    {
        Usb,
        Lan
    }

    private sealed class LanSecurityManager : IDisposable
    {
        private const int MaxHandshakeLineBytes = 16 * 1024;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
        private readonly X509Certificate2 _certificate;
        private readonly string _pairingCode;
        private readonly string _hostId;
        private readonly string _pairingsPath;
        private readonly object _pairingsLock = new();
        private readonly Dictionary<string, LanPairedDevice> _pairedDevices;
        private readonly ConcurrentDictionary<string, LanSession> _sessions = new(StringComparer.Ordinal);
        private int _disposed;

        private LanSecurityManager(
            X509Certificate2 certificate,
            string pairingCode,
            string hostId,
            string pairingsPath,
            Dictionary<string, LanPairedDevice> pairedDevices)
        {
            _certificate = certificate;
            _pairingCode = pairingCode;
            _hostId = hostId;
            _pairingsPath = pairingsPath;
            _pairedDevices = pairedDevices;
        }

        public string PairingCode => _pairingCode;

        public string HostId => _hostId;

        public string CertificateFingerprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData));

        public static LanSecurityManager Create()
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDock",
                "Host");
            Directory.CreateDirectory(dataDirectory);

            var certificatePath = Path.Combine(dataDirectory, "lan-identity.pfx");
            var hostIdPath = Path.Combine(dataDirectory, "lan-host-id.txt");
            var pairingsPath = Path.Combine(dataDirectory, "lan-pairings.json");
            var certificate = LoadOrCreateCertificate(certificatePath);
            var hostId = LoadOrCreateHostId(hostIdPath);
            var pairings = LoadPairings(pairingsPath);
            var pairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            return new LanSecurityManager(certificate, pairingCode, hostId, pairingsPath, pairings);
        }

        public async Task<Stream> OpenEncryptedStreamAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HandshakeTimeout);
            try
            {
                await sslStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        AllowRenegotiation = false
                    },
                    timeoutCts.Token);
                return sslStream;
            }
            catch
            {
                await sslStream.DisposeAsync();
                throw;
            }
        }

        public async Task<LanControlSessionLease> AuthenticateControlAsync(
            Stream stream,
            TcpClient client,
            CancellationToken cancellationToken)
        {
            var line = await ReadHandshakeLineAsync(stream, cancellationToken);
            ProtocolMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ProtocolMessage>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                await WriteAuthErrorAsync(stream, "AUTH_INVALID", "Invalid authentication request.", cancellationToken);
                throw new InvalidDataException("LAN control authentication JSON is invalid.", ex);
            }

            if (message is null
                || !message.Type.Equals("auth", StringComparison.Ordinal)
                || message.Payload is not JsonObject payload)
            {
                await WriteAuthErrorAsync(stream, "AUTH_REQUIRED", "Authentication is required.", cancellationToken);
                throw new UnauthorizedAccessException("LAN control channel did not provide an authentication request.");
            }

            var deviceId = ReadRequiredString(payload, "deviceId");
            var deviceName = ReadOptionalString(payload, "deviceName", "Android device");
            var pairingToken = ReadOptionalString(payload, "pairingToken", string.Empty);
            var requestedPairingCode = ReadOptionalString(payload, "pairingCode", string.Empty);
            var remoteAddress = RemoteAddress(client);
            string effectivePairingToken;
            var newlyPaired = false;

            lock (_pairingsLock)
            {
                if (_pairedDevices.TryGetValue(deviceId, out var paired)
                    && FixedTimeEquals(pairingToken, paired.PairingToken))
                {
                    effectivePairingToken = paired.PairingToken;
                    _pairedDevices[deviceId] = paired with
                    {
                        DeviceName = deviceName,
                        LastConnectedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    SavePairingsLocked();
                }
                else if (FixedTimeEquals(requestedPairingCode, _pairingCode))
                {
                    effectivePairingToken = CreateToken();
                    _pairedDevices[deviceId] = new LanPairedDevice(
                        deviceId,
                        deviceName,
                        effectivePairingToken,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    SavePairingsLocked();
                    newlyPaired = true;
                }
                else
                {
                    effectivePairingToken = string.Empty;
                }
            }

            if (string.IsNullOrEmpty(effectivePairingToken))
            {
                await WriteAuthErrorAsync(
                    stream,
                    "PAIRING_REQUIRED",
                    "This device is not paired. Enter the pairing code shown on the Windows host.",
                    cancellationToken);
                throw new UnauthorizedAccessException($"LAN device authentication failed for {deviceId} from {remoteAddress}.");
            }

            RemoveSessionsForDevice(deviceId);
            var sessionToken = CreateToken();
            var session = new LanSession(sessionToken, deviceId, deviceName, remoteAddress);
            _sessions[sessionToken] = session;

            await WriteProtocolMessageAsync(stream, "auth_ack", new JsonObject
            {
                ["hostId"] = _hostId,
                ["hostName"] = Environment.MachineName,
                ["certificateFingerprint"] = CertificateFingerprint,
                ["pairingToken"] = effectivePairingToken,
                ["sessionToken"] = sessionToken,
                ["paired"] = true,
                ["newlyPaired"] = newlyPaired
            }, cancellationToken);

            Log("LAN AUTH", $"authenticated deviceId={deviceId} deviceName={deviceName} remote={remoteAddress} newlyPaired={newlyPaired}");
            return new LanControlSessionLease(this, session);
        }

        public async Task<Stream> OpenAuthenticatedDataStreamAsync(
            TcpClient client,
            string channel,
            CancellationToken cancellationToken)
        {
            var stream = await OpenEncryptedStreamAsync(client, cancellationToken);
            try
            {
                var line = await ReadHandshakeLineAsync(stream, cancellationToken);
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length != 3
                    || !fields[0].Equals("SIDEDOCK/1", StringComparison.Ordinal)
                    || !fields[1].Equals(channel, StringComparison.Ordinal)
                    || !_sessions.TryGetValue(fields[2], out var session)
                    || !session.RemoteAddress.Equals(RemoteAddress(client), StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHandshakeLineAsync(stream, "ERROR AUTH_REQUIRED", cancellationToken);
                    throw new UnauthorizedAccessException($"LAN {channel} channel authentication failed from {RemoteAddress(client)}.");
                }

                await WriteHandshakeLineAsync(stream, "OK", cancellationToken);
                Log("LAN AUTH", $"bound channel={channel} deviceId={session.DeviceId} remote={session.RemoteAddress}");
                return new LanSessionBoundStream(stream, session);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }

        public IReadOnlyList<LanPairedDevice> GetPairedDevices()
        {
            lock (_pairingsLock)
            {
                return _pairedDevices.Values
                    .OrderByDescending(device => device.LastConnectedAtUnixMs)
                    .ToArray();
            }
        }

        public bool RemovePairedDevice(string deviceId)
        {
            lock (_pairingsLock)
            {
                if (!_pairedDevices.Remove(deviceId))
                {
                    return false;
                }

                SavePairingsLocked();
            }

            RemoveSessionsForDevice(deviceId);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var session in _sessions.Values)
            {
                session.Deactivate();
            }
            _sessions.Clear();
            _certificate.Dispose();
        }

        public void RevokeSession(LanSession session)
        {
            session.Deactivate();
            _sessions.TryRemove(new KeyValuePair<string, LanSession>(session.SessionToken, session));
        }

        private void RemoveSessionsForDevice(string deviceId)
        {
            foreach (var pair in _sessions)
            {
                if (pair.Value.DeviceId.Equals(deviceId, StringComparison.Ordinal))
                {
                    pair.Value.Deactivate();
                    _sessions.TryRemove(pair);
                }
            }
        }

        private void SavePairingsLocked()
        {
            var tempPath = _pairingsPath + ".tmp";
            var json = JsonSerializer.Serialize(
                new LanPairingFile(1, _pairedDevices.Values.OrderBy(device => device.DeviceId).ToArray()),
                new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _pairingsPath, overwrite: true);
        }

        private static Dictionary<string, LanPairedDevice> LoadPairings(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new Dictionary<string, LanPairedDevice>(StringComparer.Ordinal);
                }

                var file = JsonSerializer.Deserialize<LanPairingFile>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
                return (file?.Devices ?? Array.Empty<LanPairedDevice>())
                    .Where(device => !string.IsNullOrWhiteSpace(device.DeviceId)
                        && !string.IsNullOrWhiteSpace(device.PairingToken))
                    .ToDictionary(device => device.DeviceId, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Log("LAN AUTH", $"failed to load pairing store; starting empty error={ex.Message}");
                return new Dictionary<string, LanPairedDevice>(StringComparer.Ordinal);
            }
        }

        private static X509Certificate2 LoadOrCreateCertificate(string path)
        {
            if (File.Exists(path))
            {
                return new X509Certificate2(
                    File.ReadAllBytes(path),
                    (string?)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN=SideDock {Environment.MachineName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(Environment.MachineName);
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            foreach (var address in GetLanAddresses())
            {
                san.AddIpAddress(address);
            }

            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(5));
            var pfx = generated.Export(X509ContentType.Pfx);
            File.WriteAllBytes(path, pfx);
            return new X509Certificate2(
                pfx,
                (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        }

        private static string LoadOrCreateHostId(string path)
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            var hostId = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, hostId, Encoding.UTF8);
            return hostId;
        }

        private static async Task<string> ReadHandshakeLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HandshakeTimeout);
            using var buffer = new MemoryStream();
            var single = new byte[1];
            while (buffer.Length < MaxHandshakeLineBytes)
            {
                var read = await stream.ReadAsync(single, timeoutCts.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException("Connection closed during LAN authentication.");
                }

                if (single[0] == (byte)'\n')
                {
                    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)).TrimEnd('\r');
                }

                buffer.WriteByte(single[0]);
            }

            throw new InvalidDataException("LAN authentication request is too large.");
        }

        private static Task WriteHandshakeLineAsync(Stream stream, string line, CancellationToken cancellationToken)
        {
            return WriteBytesAsync(stream, Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
        }

        private static async Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static Task WriteAuthErrorAsync(
            Stream stream,
            string code,
            string message,
            CancellationToken cancellationToken)
        {
            return WriteProtocolMessageAsync(stream, "auth_error", new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }, cancellationToken);
        }

        private static Task WriteProtocolMessageAsync(
            Stream stream,
            string type,
            JsonNode payload,
            CancellationToken cancellationToken)
        {
            var message = new ProtocolMessage(
                V: 1,
                Type: type,
                Seq: 0,
                Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload: payload);
            return WriteHandshakeLineAsync(stream, JsonSerializer.Serialize(message, JsonOptions), cancellationToken);
        }

        private static string ReadRequiredString(JsonObject payload, string name)
        {
            var value = ReadOptionalString(payload, name, string.Empty);
            return !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidDataException($"LAN authentication field '{name}' is required.");
        }

        private static string ReadOptionalString(JsonObject payload, string name, string fallback)
        {
            if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            {
                return fallback;
            }

            try
            {
                return node.GetValue<string>()?.Trim() ?? fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }

        private static string CreateToken()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            return leftBytes.Length == rightBytes.Length
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static string RemoteAddress(TcpClient client)
        {
            return (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "unknown";
        }
    }

    private sealed class LanControlSessionLease : IDisposable
    {
        private readonly LanSecurityManager _owner;
        private readonly LanSession _session;
        private int _disposed;

        public LanControlSessionLease(LanSecurityManager owner, LanSession session)
        {
            _owner = owner;
            _session = session;
        }

        public string DeviceId => _session.DeviceId;

        public string DeviceName => _session.DeviceName;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.RevokeSession(_session);
            }
        }
    }

    private sealed class LanDiscoveryService(
        LanSecurityManager security,
        int controlPort,
        CancellationToken appToken)
    {
        private static readonly TimeSpan AnnouncementInterval = TimeSpan.FromSeconds(2);
        private readonly JsonObject _announcement = new()
        {
            ["service"] = "SideDock",
            ["version"] = 1,
            ["hostId"] = security.HostId,
            ["hostName"] = Environment.MachineName,
            ["controlPort"] = controlPort,
            ["tls"] = true,
            ["certificateFingerprint"] = security.CertificateFingerprint
        };

        public async Task RunAsync()
        {
            using var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultDiscoveryPort));
            listener.EnableBroadcast = true;
            using var broadcaster = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            var payload = Encoding.UTF8.GetBytes(_announcement.ToJsonString(JsonOptions));
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DefaultDiscoveryPort);
            var nextAnnouncement = DateTimeOffset.MinValue;

            Log("LAN DISCOVERY", $"listening udp=0.0.0.0:{DefaultDiscoveryPort}");
            while (!appToken.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextAnnouncement)
                {
                    await SendAnnouncementAsync(broadcaster, payload, broadcastEndpoint, appToken);
                    nextAnnouncement = DateTimeOffset.UtcNow + AnnouncementInterval;
                }

                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                receiveCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    var request = await listener.ReceiveAsync(receiveCts.Token);
                    var text = Encoding.UTF8.GetString(request.Buffer);
                    JsonObject? query = null;
                    try
                    {
                        query = JsonNode.Parse(text) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        // Ignore unrelated UDP traffic on the discovery port.
                    }

                    var service = query?["service"]?.GetValue<string>() ?? string.Empty;
                    if (query is not null
                        && service.Equals("SideDock", StringComparison.OrdinalIgnoreCase)
                        && query.TryGetPropertyValue("query", out var queryNode)
                        && queryNode?.GetValue<bool>() == true)
                    {
                        await SendAnnouncementAsync(listener, payload, request.RemoteEndPoint, appToken);
                    }
                }
                catch (OperationCanceledException) when (!appToken.IsCancellationRequested)
                {
                    // Poll periodically so announcements continue even when no requests arrive.
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Log("LAN DISCOVERY", $"udp receive failed error={ex.Message}");
                }
            }
        }

        private static async Task SendAnnouncementAsync(
            UdpClient client,
            byte[] payload,
            IPEndPoint endpoint,
            CancellationToken cancellationToken)
        {
            try
            {
                await client.SendAsync(payload, endpoint, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during host shutdown.
            }
            catch (SocketException ex)
            {
                Log("LAN DISCOVERY", $"udp send failed endpoint={endpoint} error={ex.Message}");
            }
        }
    }

    private static class WindowsFirewallManager
    {
        public static async Task EnsureRulesAsync(
            IReadOnlyList<int> tcpPorts,
            int udpPort,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            if (!IsAdministrator())
            {
                Log(
                    "LAN FIREWALL",
                    $"status=requires_elevation tcpPorts={string.Join(',', tcpPorts)} udpPort={udpPort} "
                    + "message=Run SideDock as administrator once to create private-network firewall rules.");
                return;
            }

            var programPath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(programPath))
            {
                Log("LAN FIREWALL", "status=error message=Unable to resolve SideDock.Host executable path.");
                return;
            }

            var tcpResult = await RunNetshAsync(
                "advfirewall firewall add rule "
                + "name=\"SideDock LAN TCP\" dir=in action=allow enable=yes profile=private "
                + $"program=\"{programPath}\" protocol=TCP localport={string.Join(',', tcpPorts)}",
                cancellationToken);
            var udpResult = await RunNetshAsync(
                "advfirewall firewall add rule "
                + "name=\"SideDock LAN Discovery\" dir=in action=allow enable=yes profile=private "
                + $"program=\"{programPath}\" protocol=UDP localport={udpPort}",
                cancellationToken);
            var success = tcpResult == 0 && udpResult == 0;
            Log(
                "LAN FIREWALL",
                $"status={(success ? "ready" : "error")} tcpPorts={string.Join(',', tcpPorts)} udpPort={udpPort} "
                + $"tcpExit={tcpResult} udpExit={udpResult}");
        }

        private static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<int> RunNetshAsync(string arguments, CancellationToken cancellationToken)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return -1;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
    }

    private sealed class LanSession
    {
        private int _active = 1;

        public LanSession(string sessionToken, string deviceId, string deviceName, string remoteAddress)
        {
            SessionToken = sessionToken;
            DeviceId = deviceId;
            DeviceName = deviceName;
            RemoteAddress = remoteAddress;
        }

        public string SessionToken { get; }
        public string DeviceId { get; }
        public string DeviceName { get; }
        public string RemoteAddress { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Deactivate()
        {
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private sealed class LanSessionBoundStream(Stream inner, LanSession session) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
        public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }

        public override void Flush()
        {
            EnsureActive();
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            return inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            EnsureActive();
            return inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            EnsureActive();
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            EnsureActive();
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureActive();
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void EnsureActive()
        {
            if (!session.IsActive)
            {
                throw new IOException("The authenticated LAN control session is no longer active.");
            }
        }
    }

    private sealed record LanPairedDevice(
        string DeviceId,
        string DeviceName,
        string PairingToken,
        long PairedAtUnixMs,
        long LastConnectedAtUnixMs);

    private sealed record LanPairingFile(int Version, IReadOnlyList<LanPairedDevice> Devices);

    private static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(unicast.Address)
                        && !unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log("LAN", $"address enumeration failed error={ex.Message}");
        }

        return addresses.Distinct().ToArray();
    }
}
