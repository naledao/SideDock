using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace SideDock.VirtualCamera.Tool;

internal static class Program
{
    private const string CameraName = "SideDock Camera";
    private const string MediaSourceFriendlyName = "SideDock Camera Media Source";
    private const string MediaSourceClsid = "{951EE24C-E200-4E62-8035-F76214F695D2}";
    private const string MediaSourceDllName = "SideDock.VirtualCamera.MediaSource.dll";
    private const string InProcServer32SubKey = $@"Software\Classes\CLSID\{MediaSourceClsid}\InProcServer32";
    private const int MfVersion = 0x00020070;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] RequestedDeviceProperties =
    [
        "{6EDC630D-C2E3-43B7-B2D1-20525A1AF120},3",
        "{6ac1fbf7-45f7-4e06-bda7-f817ebfa04d1},4",
        "{6ac1fbf7-45f7-4e06-bda7-f817ebfa04d1},5",
        "{6ac1fbf7-45f7-4e06-bda7-f817ebfa04d1},6",
        "{6ac1fbf7-45f7-4e06-bda7-f817ebfa04d1},7"
    ];

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var command = args[0].Trim().ToLowerInvariant();
            var options = CommandOptions.Parse(args.Skip(1).ToArray());
            return command switch
            {
                "install" or "register" => Install(options),
                "uninstall" => await UninstallAsync(options),
                "start" => await StartAsync(options),
                "ensure-start" => await EnsureStartAsync(options),
                "stop" => await StopAsync(options),
                "remove" => await RemoveAsync(options),
                "status" => await PrintStatusAsync(options),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
SideDock.VirtualCamera.Tool

Commands:
  install [--scope user|machine] [--dll <SideDock.VirtualCamera.MediaSource.dll>]
  ensure-start [--scope user|machine] [--dll <dll>] [--lifetime system|session] [--access currentUser|allUsers]
  start [--lifetime system|session] [--access currentUser|allUsers]
  stop [--lifetime system|session] [--access currentUser|allUsers]
  remove [--lifetime system|session] [--access currentUser|allUsers]
  uninstall [--scope user|machine]
  status

Defaults: --scope user, --lifetime system, --access currentUser.
""");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static int Install(CommandOptions options)
    {
        var dllPath = InstallRegistration(options);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            command = "install",
            scope = options.Scope,
            clsid = MediaSourceClsid,
            dllPath
        }, JsonOptions));
        return 0;
    }

    private static async Task<int> EnsureStartAsync(CommandOptions options)
    {
        var registration = GetRegistration(options.Scope);
        if (options.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            var dllPath = ResolveMediaSourceDllPath(options);
            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException("Media source DLL was not found.", dllPath);
            }

            var expectedPath = GetMachineMediaSourcePath(dllPath);
            var userRegistration = GetRegistration("user");
            if (!registration.Registered
                || !string.Equals(registration.DllPath, expectedPath, StringComparison.OrdinalIgnoreCase)
                || userRegistration.Registered)
            {
                InstallRegistration(options);
            }
        }
        else if (!registration.Registered)
        {
            InstallRegistration(options);
        }

        return await StartAsync(options);
    }

    private static async Task<int> StartAsync(CommandOptions options)
    {
        EnsureWindows11VirtualCameraApi();
        using var mf = MediaFoundationSession.Start();
        var camera = CreateVirtualCamera(options);
        try
        {
            Check(camera.Start(IntPtr.Zero), "IMFVirtualCamera.Start");
            var symbolicLink = TryReadAllocatedString(camera, Native.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
            WriteToolStatus("started", symbolicLink, "");
            await PrintStatusAsync(options);
            return 0;
        }
        finally
        {
            camera.Shutdown();
            Marshal.ReleaseComObject(camera);
        }
    }

    private static async Task<int> StopAsync(CommandOptions options)
    {
        EnsureWindows11VirtualCameraApi();
        using var mf = MediaFoundationSession.Start();
        var camera = CreateVirtualCamera(options);
        try
        {
            Check(camera.Stop(), "IMFVirtualCamera.Stop");
            WriteToolStatus("stopped", "", "");
            await PrintStatusAsync(options);
            return 0;
        }
        finally
        {
            camera.Shutdown();
            Marshal.ReleaseComObject(camera);
        }
    }

    private static async Task<int> RemoveAsync(CommandOptions options)
    {
        EnsureWindows11VirtualCameraApi();
        using var mf = MediaFoundationSession.Start();
        var camera = CreateVirtualCamera(options);
        try
        {
            _ = camera.Stop();
            Check(camera.Remove(), "IMFVirtualCamera.Remove");
            WriteToolStatus("removed", "", "");
            await PrintStatusAsync(options);
            return 0;
        }
        finally
        {
            camera.Shutdown();
            Marshal.ReleaseComObject(camera);
        }
    }

    private static async Task<int> UninstallAsync(CommandOptions options)
    {
        try
        {
            await RemoveAsync(options);
        }
        catch (Exception ex)
        {
            WriteToolStatus("remove_failed", "", ex.Message);
        }

        RemoveComRegistration(options.Scope);
        await PrintStatusAsync(options);
        return 0;
    }

    private static async Task<int> PrintStatusAsync(CommandOptions options)
    {
        var userRegistration = GetRegistration("user");
        var machineRegistration = GetRegistration("machine");
        var devices = await FindSideDockCameraDevicesAsync();
        var servedStatus = ReadServedFrameStatus();
        var toolStatus = ReadToolStatus();

        var status = new
        {
            friendlyName = CameraName,
            mediaSourceClsid = MediaSourceClsid,
            registered = userRegistration.Registered || machineRegistration.Registered,
            registeredScopes = new
            {
                user = userRegistration,
                machine = machineRegistration
            },
            running = devices.Count > 0,
            devices,
            lastToolState = toolStatus,
            servedFrame = servedStatus,
            statusFile = StatusFilePath
        };

        Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
        return 0;
    }

    private static IMFVirtualCamera CreateVirtualCamera(CommandOptions options)
    {
        Check(Native.MFCreateVirtualCamera(
            MFVirtualCameraType.SoftwareCameraSource,
            options.Lifetime,
            options.Access,
            CameraName,
            MediaSourceClsid,
            IntPtr.Zero,
            0,
            out var camera), "MFCreateVirtualCamera");

        return camera;
    }

    private static async Task<List<CameraDeviceStatus>> FindSideDockCameraDevicesAsync()
    {
        var result = new List<CameraDeviceStatus>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                MediaDevice.GetVideoCaptureSelector(),
                RequestedDeviceProperties);

            foreach (var device in devices)
            {
                if (!device.Name.Contains(CameraName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new CameraDeviceStatus(device.Name, device.Id, device.IsEnabled));
            }
        }
        catch (Exception ex)
        {
            result.Add(new CameraDeviceStatus("(enumeration failed)", ex.Message, false));
        }

        return result;
    }

    private static RegistrationStatus GetRegistration(string scope)
    {
        var hive = ScopeHive(scope);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(InProcServer32SubKey);
            var dllPath = key?.GetValue(null) as string;
            return new RegistrationStatus(
                Registered: !string.IsNullOrWhiteSpace(dllPath),
                DllPath: dllPath ?? "",
                DllExists: !string.IsNullOrWhiteSpace(dllPath) && File.Exists(dllPath),
                Error: "");
        }
        catch (Exception ex)
        {
            return new RegistrationStatus(false, "", false, ex.Message);
        }
    }

    private static void InstallComRegistration(string scope, string dllPath)
    {
        var hive = ScopeHive(scope);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var clsidKey = root.CreateSubKey($@"Software\Classes\CLSID\{MediaSourceClsid}", writable: true)
            ?? throw new InvalidOperationException("Failed to create CLSID registry key.");
        clsidKey.SetValue(null, MediaSourceFriendlyName, RegistryValueKind.String);

        using var inprocKey = clsidKey.CreateSubKey("InProcServer32", writable: true)
            ?? throw new InvalidOperationException("Failed to create InProcServer32 registry key.");
        inprocKey.SetValue(null, Path.GetFullPath(dllPath), RegistryValueKind.String);
        inprocKey.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
    }

    private static string InstallRegistration(CommandOptions options)
    {
        var sourceDllPath = ResolveMediaSourceDllPath(options);
        if (!File.Exists(sourceDllPath))
        {
            throw new FileNotFoundException("Media source DLL was not found.", sourceDllPath);
        }

        var registrationDllPath = options.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase)
            ? StageMachineMediaSource(sourceDllPath)
            : sourceDllPath;
        InstallComRegistration(options.Scope, registrationDllPath);

        // HKCU takes precedence over HKLM when COM resolves HKCR. Remove an old
        // per-user SideDock registration so it cannot redirect Frame Server back
        // to a launcher/AppData path that LocalService cannot access.
        if (options.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            RemoveComRegistration("user");
        }

        return registrationDllPath;
    }

    private static string StageMachineMediaSource(string sourceDllPath)
    {
        var targetPath = GetMachineMediaSourcePath(sourceDllPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Machine media source directory is unavailable."));
        if (!File.Exists(targetPath))
        {
            File.Copy(sourceDllPath, targetPath, overwrite: false);
        }

        return targetPath;
    }

    private static string GetMachineMediaSourcePath(string sourceDllPath)
    {
        using var stream = File.OpenRead(sourceDllPath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SideDock",
            "VirtualCamera",
            $"SideDock.VirtualCamera.MediaSource.{fingerprint}.dll");
    }

    private static void RemoveComRegistration(string scope)
    {
        var hive = ScopeHive(scope);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree($@"Software\Classes\CLSID\{MediaSourceClsid}", throwOnMissingSubKey: false);
    }

    private static RegistryHive ScopeHive(string scope)
    {
        return scope.Equals("machine", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;
    }

    private static string ResolveMediaSourceDllPath(CommandOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MediaSourceDllPath))
        {
            return Path.GetFullPath(options.MediaSourceDllPath);
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, MediaSourceDllName)
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            candidates.Add(Path.Combine(
                current.FullName,
                "windows-host",
                "SideDock.VirtualCamera.MediaSource",
                "x64",
                "Debug",
                MediaSourceDllName));
            candidates.Add(Path.Combine(
                current.FullName,
                "SideDock.VirtualCamera.MediaSource",
                "x64",
                "Debug",
                MediaSourceDllName));
            current = current.Parent;
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static object? ReadServedFrameStatus()
    {
        try
        {
            return File.Exists(StatusFilePath)
                ? JsonDocument.Parse(File.ReadAllText(StatusFilePath)).RootElement.Clone()
                : null;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static object? ReadToolStatus()
    {
        var path = Path.Combine(StatusDirectory, "virtual-camera-tool-status.json");
        try
        {
            return File.Exists(path)
                ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()
                : null;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static void WriteToolStatus(string state, string symbolicLink, string error)
    {
        Directory.CreateDirectory(StatusDirectory);
        File.WriteAllText(
            Path.Combine(StatusDirectory, "virtual-camera-tool-status.json"),
            JsonSerializer.Serialize(new
            {
                state,
                friendlyName = CameraName,
                symbolicLink,
                updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                error
            }, JsonOptions));
    }

    private static string StatusDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SideDock");

    private static string StatusFilePath => Path.Combine(StatusDirectory, "virtual-camera-status.json");

    private static void EnsureWindows11VirtualCameraApi()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("Media Foundation Virtual Camera requires Windows 11 build 22000 or newer.");
        }
    }

    private static string TryReadAllocatedString(IMFVirtualCamera camera, Guid key)
    {
        var pointer = IntPtr.Zero;
        try
        {
            var hr = camera.GetAllocatedString(ref key, out pointer, out _);
            if (hr < 0 || pointer == IntPtr.Zero)
            {
                return "";
            }

            return Marshal.PtrToStringUni(pointer) ?? "";
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Native.CoTaskMemFree(pointer);
            }
        }
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"{operation} failed with HRESULT 0x{hr:X8}.", hr);
        }
    }

    private sealed record CameraDeviceStatus(string Name, string Id, bool IsEnabled);

    private sealed record RegistrationStatus(bool Registered, string DllPath, bool DllExists, string Error);

    private sealed class CommandOptions
    {
        public string Scope { get; private set; } = "user";

        public string? MediaSourceDllPath { get; private set; }

        public MFVirtualCameraLifetime Lifetime { get; private set; } = MFVirtualCameraLifetime.System;

        public MFVirtualCameraAccess Access { get; private set; } = MFVirtualCameraAccess.CurrentUser;

        public static CommandOptions Parse(string[] args)
        {
            var options = new CommandOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                string NextValue()
                {
                    if (index + 1 >= args.Length)
                    {
                        throw new ArgumentException($"{arg} requires a value.");
                    }

                    return args[++index];
                }

                switch (arg.ToLowerInvariant())
                {
                    case "--scope":
                        options.Scope = NormalizeScope(NextValue());
                        break;
                    case "--dll":
                    case "--source":
                        options.MediaSourceDllPath = NextValue();
                        break;
                    case "--lifetime":
                        options.Lifetime = ParseLifetime(NextValue());
                        break;
                    case "--access":
                        options.Access = ParseAccess(NextValue());
                        break;
                }
            }

            return options;
        }

        private static string NormalizeScope(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "user" or "currentuser" or "current-user" => "user",
                "machine" or "allusers" or "all-users" => "machine",
                _ => throw new ArgumentException("--scope must be user or machine.")
            };
        }

        private static MFVirtualCameraLifetime ParseLifetime(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "session" => MFVirtualCameraLifetime.Session,
                "system" => MFVirtualCameraLifetime.System,
                _ => throw new ArgumentException("--lifetime must be system or session.")
            };
        }

        private static MFVirtualCameraAccess ParseAccess(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "currentuser" or "current-user" or "user" => MFVirtualCameraAccess.CurrentUser,
                "allusers" or "all-users" or "machine" => MFVirtualCameraAccess.AllUsers,
                _ => throw new ArgumentException("--access must be currentUser or allUsers.")
            };
        }
    }

    private sealed class MediaFoundationSession : IDisposable
    {
        private MediaFoundationSession()
        {
        }

        public static MediaFoundationSession Start()
        {
            Check(Native.MFStartup(MfVersion, 0), "MFStartup");
            return new MediaFoundationSession();
        }

        public void Dispose()
        {
            _ = Native.MFShutdown();
        }
    }

    private enum MFVirtualCameraType
    {
        SoftwareCameraSource = 0
    }

    private enum MFVirtualCameraLifetime
    {
        Session = 0,
        System = 1
    }

    private enum MFVirtualCameraAccess
    {
        CurrentUser = 0,
        AllUsers = 1
    }

    [ComImport]
    [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int valueType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
        [PreserveSig] int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int blobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int value);
        [PreserveSig] int SetUINT64(ref Guid guidKey, long value);
        [PreserveSig] int SetDouble(ref Guid guidKey, double value);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int items);
        [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
    }

    [ComImport]
    [Guid("1C08A864-EF6C-4C75-AF59-5F2D68DA9563")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFVirtualCamera
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int valueType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
        [PreserveSig] int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int blobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int value);
        [PreserveSig] int SetUINT64(ref Guid guidKey, long value);
        [PreserveSig] int SetDouble(ref Guid guidKey, double value);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int items);
        [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
        [PreserveSig] int AddDeviceSourceInfo([MarshalAs(UnmanagedType.LPWStr)] string deviceSourceInfo);
        [PreserveSig] int AddProperty(IntPtr key, int type, IntPtr data, int dataBytes);
        [PreserveSig] int AddRegistryEntry([MarshalAs(UnmanagedType.LPWStr)] string entryName, [MarshalAs(UnmanagedType.LPWStr)] string? subkeyPath, int registryType, IntPtr data, int dataBytes);
        [PreserveSig] int Start(IntPtr callback);
        [PreserveSig] int Stop();
        [PreserveSig] int Remove();
        [PreserveSig] int GetMediaSource(out IntPtr mediaSource);
        [PreserveSig] int SendCameraProperty(ref Guid propertySet, int propertyId, int propertyFlags, IntPtr propertyPayload, int propertyPayloadLength, IntPtr data, int dataLength, out int dataWritten);
        [PreserveSig] int CreateSyncEvent(ref Guid eventSet, int eventId, int eventFlags, IntPtr eventHandle, out IntPtr cameraSyncObject);
        [PreserveSig] int CreateSyncSemaphore(ref Guid eventSet, int eventId, int eventFlags, IntPtr semaphoreHandle, int semaphoreAdjustment, out IntPtr cameraSyncObject);
        [PreserveSig] int Shutdown();
    }

    private static class Native
    {
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfsensorgroup.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int MFCreateVirtualCamera(
            MFVirtualCameraType type,
            MFVirtualCameraLifetime lifetime,
            MFVirtualCameraAccess access,
            string friendlyName,
            string sourceId,
            IntPtr categories,
            int categoryCount,
            [MarshalAs(UnmanagedType.Interface)] out IMFVirtualCamera virtualCamera);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern void CoTaskMemFree(IntPtr value);
    }
}
