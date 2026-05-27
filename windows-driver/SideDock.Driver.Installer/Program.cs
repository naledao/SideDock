using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
const string AppName = "SideDock Driver Installer";
const string DeviceToolExe = "SideDock.Idd.DeviceTool.exe";
const string DriverInf = "SideDock.Idd.inf";
const string DriverCertificate = "SideDock.Idd.cer";
const string DriverHardwareId = @"SWD\SIDEDOCKIDD\SIDEDOCKIDD";
var options = InstallerOptions.Parse(args);

try
{
    Console.Title = AppName;
    Console.WriteLine(AppName);
    Console.WriteLine();

    if (!IsAdministrator())
    {
        return RelaunchElevated(args);
    }

    if (!OperatingSystem.IsWindows())
    {
        Fail("This installer only runs on Windows.");
        return 1;
    }

    var payloadRoot = ExtractPayload();
    var driverPackageDir = FindDirectory(payloadRoot, "SideDock.Idd")
        ?? FailDirectory("SideDock.Idd");
    var deviceToolDir = FindDirectory(payloadRoot, "SideDock.Idd.DeviceTool")
        ?? FailDirectory("SideDock.Idd.DeviceTool");

    var infPath = Directory.GetFiles(driverPackageDir, DriverInf, SearchOption.AllDirectories)
        .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        ?? Directory.GetFiles(driverPackageDir, DriverInf, SearchOption.AllDirectories).FirstOrDefault()
        ?? FailFile(DriverInf);

    var certificatePath = Directory.GetFiles(driverPackageDir, DriverCertificate, SearchOption.AllDirectories)
        .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        ?? Directory.GetFiles(driverPackageDir, DriverCertificate, SearchOption.AllDirectories).FirstOrDefault()
        ?? FailFile(DriverCertificate);

    var deviceToolPath = Directory.GetFiles(deviceToolDir, DeviceToolExe, SearchOption.AllDirectories)
        .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        ?? Directory.GetFiles(deviceToolDir, DeviceToolExe, SearchOption.AllDirectories).FirstOrDefault()
        ?? FailFile(DeviceToolExe);

    Console.WriteLine($"Driver INF: {infPath}");
    Console.WriteLine($"Driver certificate: {certificatePath}");
    Console.WriteLine($"Device tool: {deviceToolPath}");
    Console.WriteLine();

    TrustDriverCertificate(certificatePath);
    InstallDriver(infPath);
    RemoveExistingSoftwareDevice();
    StartDeviceTool(deviceToolPath, options.HideDeviceTool);

    Console.WriteLine();
    Console.WriteLine("SideDock driver installation has started.");
    Console.WriteLine("Keep the SideDock.Idd.DeviceTool window running while using the virtual display.");
    Console.WriteLine("Open Windows Display Settings and look for 'SideDock Virtual Display'.");
    Pause(options.NoPause);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Pause(options.NoPause);
    return 1;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static int RelaunchElevated(string[] args)
{
    var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
    var startInfo = new ProcessStartInfo
    {
        FileName = exePath,
        UseShellExecute = true,
        Verb = "runas",
        Arguments = string.Join(" ", args.Select(QuoteArgument))
    };

    Process.Start(startInfo);
    return 0;
}

static string QuoteArgument(string argument)
{
    return argument.Contains(' ') || argument.Contains('"')
        ? "\"" + argument.Replace("\"", "\\\"") + "\""
        : argument;
}

static string ExtractPayload()
{
    var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .FirstOrDefault(name => name.EndsWith(".DriverPayload.zip", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Embedded driver payload was not found.");

    var root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SideDock",
        "Driver");
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    Directory.CreateDirectory(root);
    var zipPath = Path.Combine(root, "DriverPayload.zip");

    using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException("Embedded driver payload stream was not found."))
    using (var output = File.Create(zipPath))
    {
        resource.CopyTo(output);
    }

    var payloadRoot = Path.Combine(root, "payload");
    ZipFile.ExtractToDirectory(zipPath, payloadRoot);
    File.Delete(zipPath);
    return payloadRoot;
}

static string? FindDirectory(string root, string directoryName)
{
    return Directory.GetDirectories(root, directoryName, SearchOption.AllDirectories)
        .OrderBy(path => path.Length)
        .FirstOrDefault();
}

static string FailDirectory(string name)
{
    throw new DirectoryNotFoundException($"Required directory was not found in payload: {name}");
}

static string FailFile(string name)
{
    throw new FileNotFoundException($"Required file was not found in payload: {name}");
}

static void Fail(string message)
{
    throw new InvalidOperationException(message);
}

static void InstallDriver(string infPath)
{
    Console.WriteLine("Installing driver package with pnputil.");
    RunChecked("pnputil.exe", $"/add-driver {QuoteProcessArgument(infPath)} /install");
}

static void TrustDriverCertificate(string certificatePath)
{
    Console.WriteLine("Trusting SideDock self-signed driver certificate.");
    using var certificate = new X509Certificate2(certificatePath);
    AddCertificateIfMissing(StoreName.Root, certificate);
    AddCertificateIfMissing(StoreName.TrustedPublisher, certificate);
}

static void AddCertificateIfMissing(StoreName storeName, X509Certificate2 certificate)
{
    using var store = new X509Store(storeName, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadWrite);

    var matches = store.Certificates.Find(
        X509FindType.FindByThumbprint,
        certificate.Thumbprint,
        validOnly: false);

    if (matches.Count > 0)
    {
        Console.WriteLine($"Certificate already exists in LocalMachine\\{storeName}.");
        return;
    }

    store.Add(certificate);
    Console.WriteLine($"Added certificate to LocalMachine\\{storeName}.");
}

static void RemoveExistingSoftwareDevice()
{
    Console.WriteLine("Removing any stale SideDock software device instance.");
    Run("pnputil.exe", $"/remove-device {QuoteProcessArgument(DriverHardwareId)}", allowFailure: true);
}

static void StartDeviceTool(string deviceToolPath, bool hideWindow)
{
    Console.WriteLine("Starting SideDock software device tool.");
    Process.Start(new ProcessStartInfo
    {
        FileName = deviceToolPath,
        WorkingDirectory = Path.GetDirectoryName(deviceToolPath) ?? Environment.CurrentDirectory,
        UseShellExecute = true,
        WindowStyle = hideWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
    });
}

static string QuoteProcessArgument(string value)
{
    return "\"" + value.Replace("\"", "\\\"") + "\"";
}

static void RunChecked(string fileName, string arguments)
{
    var result = Run(fileName, arguments, allowFailure: false);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");
    }
}

static ProcessResult Run(string fileName, string arguments, bool allowFailure)
{
    Console.WriteLine($"> {fileName} {arguments}");
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (!string.IsNullOrWhiteSpace(stdout))
    {
        Console.WriteLine(stdout.TrimEnd());
    }

    if (!string.IsNullOrWhiteSpace(stderr))
    {
        Console.Error.WriteLine(stderr.TrimEnd());
    }

    if (!allowFailure && process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
    }

    return new ProcessResult(process.ExitCode, stdout, stderr);
}

static void Pause(bool noPause)
{
    if (!noPause && !Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.Write("Press Enter to exit...");
        Console.ReadLine();
    }
}

internal readonly record struct InstallerOptions(bool NoPause, bool HideDeviceTool)
{
    public static InstallerOptions Parse(string[] args)
    {
        var fromApp = args.Any(arg => arg.Equals("--from-app", StringComparison.OrdinalIgnoreCase));
        var noPause = fromApp || args.Any(arg => arg.Equals("--no-pause", StringComparison.OrdinalIgnoreCase));
        var hideDeviceTool = fromApp || args.Any(arg => arg.Equals("--hide-device-tool", StringComparison.OrdinalIgnoreCase));
        return new InstallerOptions(noPause, hideDeviceTool);
    }
}

internal readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
