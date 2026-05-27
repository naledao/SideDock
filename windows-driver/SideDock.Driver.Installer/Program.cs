using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
const string AppName = "SideDock Driver Installer";
const string DeviceToolExe = "SideDock.Idd.DeviceTool.exe";
const string DriverInf = "SideDock.Idd.inf";
const string DriverHardwareId = @"SWD\SIDEDOCKIDD\SIDEDOCKIDD";

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

    var deviceToolPath = Directory.GetFiles(deviceToolDir, DeviceToolExe, SearchOption.AllDirectories)
        .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        ?? Directory.GetFiles(deviceToolDir, DeviceToolExe, SearchOption.AllDirectories).FirstOrDefault()
        ?? FailFile(DeviceToolExe);

    Console.WriteLine($"Driver INF: {infPath}");
    Console.WriteLine($"Device tool: {deviceToolPath}");
    Console.WriteLine();

    InstallDriver(infPath);
    RemoveExistingSoftwareDevice();
    StartDeviceTool(deviceToolPath);

    Console.WriteLine();
    Console.WriteLine("SideDock driver installation has started.");
    Console.WriteLine("Keep the SideDock.Idd.DeviceTool window running while using the virtual display.");
    Console.WriteLine("Open Windows Display Settings and look for 'SideDock Virtual Display'.");
    Pause();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Pause();
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

static void RemoveExistingSoftwareDevice()
{
    Console.WriteLine("Removing any stale SideDock software device instance.");
    Run("pnputil.exe", $"/remove-device {QuoteProcessArgument(DriverHardwareId)}", allowFailure: true);
}

static void StartDeviceTool(string deviceToolPath)
{
    Console.WriteLine("Starting SideDock software device tool.");
    Process.Start(new ProcessStartInfo
    {
        FileName = deviceToolPath,
        WorkingDirectory = Path.GetDirectoryName(deviceToolPath) ?? Environment.CurrentDirectory,
        UseShellExecute = true
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

static void Pause()
{
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.Write("Press Enter to exit...");
        Console.ReadLine();
    }
}

internal readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
