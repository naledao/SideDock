using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
const string AppName = "SideDock Driver Installer";
const string DeviceToolExe = "SideDock.Idd.DeviceTool.exe";
const string DriverInf = "SideDock.Idd.inf";
const string DriverCertificate = "SideDock.Idd.cer";
const string DriverCatalog = "SideDock.Idd.cat";
const string DriverBinary = "SideDock.Idd.dll";
const string DriverHardwareId = @"SWD\SideDockIdd\SideDockIdd";
var options = InstallerOptions.Parse(args);

// Tee everything written to the console into a transcript buffer so a full,
// copyable diagnostic report can be persisted for the (non-elevated) host app,
// which cannot capture this elevated process's stdout/stderr directly.
var transcript = new StringBuilder();
var standardOut = Console.Out;
var standardError = Console.Error;
var transcriptWriter = new StringWriter(transcript);
Console.SetOut(new TeeTextWriter(standardOut, transcriptWriter));
Console.SetError(new TeeTextWriter(standardError, transcriptWriter));
var currentStep = "检查运行环境";

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

    currentStep = "解压驱动负载";
    var payloadRoot = ExtractPayload();
    var driverPackageDir = FindDirectory(payloadRoot, "SideDock.Idd")
        ?? FailDirectory("SideDock.Idd");
    var deviceToolDir = FindDirectory(payloadRoot, "SideDock.Idd.DeviceTool")
        ?? FailDirectory("SideDock.Idd.DeviceTool");

    currentStep = "定位已签名驱动文件";
    var infPath = ResolveSignedDriverInf(driverPackageDir);

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

    currentStep = "信任驱动证书";
    TrustDriverCertificate(certificatePath);
    currentStep = "停止已有 DeviceTool 进程";
    StopExistingDeviceToolProcesses();
    currentStep = "移除旧的虚拟显示设备";
    RemoveExistingSoftwareDevice();
    currentStep = "卸载旧驱动包";
    RemoveExistingDriverPackages();
    currentStep = "安装驱动包 (pnputil /add-driver)";
    InstallDriver(infPath);
    currentStep = "启动 DeviceTool";
    StartDeviceTool(deviceToolPath, options.HideDeviceTool);

    currentStep = "完成";
    Console.WriteLine();
    Console.WriteLine("SideDock driver installation has started.");
    Console.WriteLine("Keep the SideDock.Idd.DeviceTool window running while using the virtual display.");
    Console.WriteLine("Open Windows Display Settings and look for 'SideDock Virtual Display'.");
    WriteResultReport(options.ResultPath, BuildReport(true, 0, currentStep, null, transcript.ToString(), Environment.CommandLine));
    Pause(options.NoPause);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    WriteResultReport(options.ResultPath, BuildReport(false, 1, currentStep, ex, transcript.ToString(), Environment.CommandLine));
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

static void StopExistingDeviceToolProcesses()
{
    var processName = Path.GetFileNameWithoutExtension(DeviceToolExe);
    var processes = Process.GetProcessesByName(processName);
    if (processes.Length == 0)
    {
        return;
    }

    Console.WriteLine("Stopping existing SideDock device tool processes.");
    foreach (var process in processes)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to stop {processName} (pid {process.Id}): {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}

static string ResolveSignedDriverInf(string driverPackageDir)
{
    var candidates = Directory.GetFiles(driverPackageDir, DriverInf, SearchOption.AllDirectories)
        .Select(path => new
        {
            Path = path,
            Directory = Path.GetDirectoryName(path) ?? string.Empty
        })
        .Where(candidate => HasDriverCatalog(candidate.Directory))
        .OrderByDescending(candidate => candidate.Directory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .ThenBy(candidate => candidate.Path.Length)
        .ToList();

    if (candidates.Count > 0)
    {
        return candidates[0].Path;
    }

    var discovered = Directory.GetFiles(driverPackageDir, DriverInf, SearchOption.AllDirectories);
    var message = discovered.Length == 0
        ? $"Required file was not found in payload: {DriverInf}"
        : $"No signed driver package was found for {DriverInf}. Expected a matching .cat file in the same directory.";

    throw new FileNotFoundException(message);
}

static bool HasDriverCatalog(string directory)
{
    if (string.IsNullOrWhiteSpace(directory))
    {
        return false;
    }

    return Directory.GetFiles(directory, "*.cat", SearchOption.TopDirectoryOnly).Length > 0;
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

static void RemoveExistingDriverPackages()
{
    Console.WriteLine("Scanning installed Display-class drivers for SideDock packages.");
    var packages = EnumerateInstalledDriverPackages();
    var sideDockPackages = packages
        .Where(IsSideDockDriverPackage)
        .OrderByDescending(package => package.PublishedName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (sideDockPackages.Count == 0)
    {
        Console.WriteLine("No installed SideDock driver packages were found.");
        return;
    }

    foreach (var package in sideDockPackages)
    {
        Console.WriteLine($"Deleting old driver package {package.PublishedName}.");
        if (!string.IsNullOrWhiteSpace(package.OriginalName))
        {
            Console.WriteLine($"  Original Name: {package.OriginalName}");
        }
        if (!string.IsNullOrWhiteSpace(package.ProviderName))
        {
            Console.WriteLine($"  Provider Name: {package.ProviderName}");
        }
        if (!string.IsNullOrWhiteSpace(package.CatalogFile))
        {
            Console.WriteLine($"  Catalog File: {package.CatalogFile}");
        }

        RunChecked("pnputil.exe", $"/delete-driver {QuoteProcessArgument(package.PublishedName)} /uninstall /force");
    }
}

static IReadOnlyList<DriverPackageInfo> EnumerateInstalledDriverPackages()
{
    // /class and /files are Windows 11-only filters for /enum-drivers (/class
    // since 21H2, /files since 22H2); Windows 10 pnputil rejects them, prints
    // usage, and exits 1. Use the basic form (available since Windows 10 1607)
    // and let the value-based fallback in ParseDriverPackages identify SideDock
    // packages. It keys on non-localized values (oemXX.inf, SideDock.Idd.inf,
    // the Display class GUID), so it also works on localized Windows where
    // pnputil localizes the field labels. Enumeration is best-effort cleanup
    // and must not abort the install before /add-driver.
    var result = Run("pnputil.exe", "/enum-drivers", allowFailure: true);
    if (result.ExitCode != 0)
    {
        Console.WriteLine("Warning: pnputil /enum-drivers failed; skipping old driver package cleanup.");
        return Array.Empty<DriverPackageInfo>();
    }

    return ParseDriverPackages(result.Stdout);
}

static IReadOnlyList<DriverPackageInfo> ParseDriverPackages(string output)
{
    var packages = new List<DriverPackageInfo>();
    DriverPackageBuilder? builder = null;
    var inDriverFiles = false;

    foreach (var rawLine in SplitLines(output))
    {
        var line = rawLine.TrimEnd();
        if (string.IsNullOrWhiteSpace(line))
        {
            CommitCurrentPackage();
            inDriverFiles = false;
            continue;
        }

        if (line.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
        {
            CommitCurrentPackage();
            builder = new DriverPackageBuilder
            {
                PublishedName = ValueAfterColon(line)
            };
            inDriverFiles = false;
            continue;
        }

        if (builder is null)
        {
            continue;
        }

        if (TryReadField(line, "Original Name:", out var value))
        {
            builder.OriginalName = value;
        }
        else if (TryReadField(line, "Provider Name:", out value))
        {
            builder.ProviderName = value;
        }
        else if (TryReadField(line, "Class Name:", out value))
        {
            builder.ClassName = value;
        }
        else if (TryReadField(line, "Class GUID:", out value))
        {
            builder.ClassGuid = value;
        }
        else if (TryReadField(line, "Catalog File:", out value))
        {
            builder.CatalogFile = value;
        }
        else if (line.StartsWith("Driver Files:", StringComparison.OrdinalIgnoreCase))
        {
            inDriverFiles = true;
        }
        else if (inDriverFiles)
        {
            var file = line.Trim();
            if (!string.IsNullOrWhiteSpace(file))
            {
                builder.DriverFiles.Add(file);
            }
        }
    }

    CommitCurrentPackage();
    AddFallbackPackagesFromRawBlocks(output, packages);
    return packages;

    void CommitCurrentPackage()
    {
        if (builder is null || string.IsNullOrWhiteSpace(builder.PublishedName))
        {
            return;
        }

        packages.Add(builder.Build());
        builder = null;
    }
}

static void AddFallbackPackagesFromRawBlocks(string output, List<DriverPackageInfo> packages)
{
    var knownPackages = new HashSet<string>(
        packages.Select(package => package.PublishedName),
        StringComparer.OrdinalIgnoreCase);

    foreach (var block in SplitDriverPackageBlocks(output))
    {
        var originalName = ContainsExactSideDockFile(block, DriverInf) ? DriverInf : null;
        var catalogFile = ContainsExactSideDockFile(block, DriverCatalog) ? DriverCatalog : null;
        var driverFiles = ContainsExactSideDockFile(block, DriverBinary)
            ? new[] { DriverBinary }
            : Array.Empty<string>();

        if (originalName is null && catalogFile is null && driverFiles.Length == 0)
        {
            continue;
        }

        var classGuid = block.Contains(DriverPackageInfo.DisplayClassGuid, StringComparison.OrdinalIgnoreCase)
            ? DriverPackageInfo.DisplayClassGuid
            : null;
        var className = block.Contains(DriverPackageInfo.DisplayClassName, StringComparison.OrdinalIgnoreCase)
            ? DriverPackageInfo.DisplayClassName
            : null;
        if (classGuid is null && className is null)
        {
            continue;
        }

        var publishedNameMatch = Regex.Match(block, @"\boem\d+\.inf\b", RegexOptions.IgnoreCase);
        if (!publishedNameMatch.Success)
        {
            continue;
        }

        var publishedName = publishedNameMatch.Value;
        if (!knownPackages.Add(publishedName))
        {
            continue;
        }

        packages.Add(new DriverPackageInfo(
            publishedName,
            originalName,
            ProviderName: block.Contains("SideDock", StringComparison.OrdinalIgnoreCase) ? "SideDock" : null,
            className,
            classGuid,
            catalogFile,
            driverFiles));
    }
}

static IEnumerable<string> SplitDriverPackageBlocks(string output)
{
    var blockLines = new List<string>();
    foreach (var rawLine in SplitLines(output))
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            if (blockLines.Count > 0)
            {
                yield return string.Join(Environment.NewLine, blockLines);
                blockLines.Clear();
            }

            continue;
        }

        blockLines.Add(rawLine);
    }

    if (blockLines.Count > 0)
    {
        yield return string.Join(Environment.NewLine, blockLines);
    }
}

static bool TryReadField(string line, string fieldName, out string value)
{
    if (!line.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
    {
        value = string.Empty;
        return false;
    }

    value = ValueAfterColon(line);
    return true;
}

static string ValueAfterColon(string line)
{
    var separatorIndex = line.IndexOf(':');
    if (separatorIndex < 0 || separatorIndex == line.Length - 1)
    {
        return string.Empty;
    }

    return line[(separatorIndex + 1)..].Trim();
}

static IEnumerable<string> SplitLines(string value)
{
    return value.Replace("\r\n", "\n").Split('\n');
}

static bool IsSideDockDriverPackage(DriverPackageInfo package)
{
    if (!package.IsDisplayClass)
    {
        return false;
    }

    return IsExactSideDockValue(package.ProviderName, "SideDock")
        || IsExactSideDockFile(package.OriginalName, DriverInf)
        || IsExactSideDockFile(package.CatalogFile, DriverCatalog)
        || package.DriverFiles.Any(file => IsExactSideDockFile(file, DriverBinary));
}

static bool IsExactSideDockValue(string? value, string expected)
{
    return string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

static bool IsExactSideDockFile(string? value, string expectedFileName)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var normalized = value.Trim().Replace('/', Path.DirectorySeparatorChar);
    return string.Equals(Path.GetFileName(normalized), expectedFileName, StringComparison.OrdinalIgnoreCase);
}

static bool ContainsExactSideDockFile(string value, string expectedFileName)
{
    return Regex.IsMatch(
        value,
        $@"(?<![\w.-]){Regex.Escape(expectedFileName)}(?![\w.-])",
        RegexOptions.IgnoreCase);
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

static ProcessResult RunChecked(string fileName, string arguments)
{
    var result = Run(fileName, arguments, allowFailure: false);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");
    }

    return result;
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

static string BuildReport(bool success, int exitCode, string step, Exception? ex, string transcript, string commandLine)
{
    var report = new StringBuilder();
    report.AppendLine("SideDock 驱动安装诊断报告");
    report.AppendLine("================================");
    report.AppendLine($"结果: {(success ? "成功" : "失败")}");
    report.AppendLine($"退出码: {exitCode}");
    if (!success)
    {
        report.AppendLine($"失败步骤: {step}");
    }
    report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    report.AppendLine($"安装器版本: {Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"}");
    report.AppendLine($"操作系统: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
    report.AppendLine($"管理员权限: {(IsAdministrator() ? "是" : "否")}");
    report.AppendLine($"命令行: {commandLine}");

    if (ex is not null)
    {
        report.AppendLine();
        report.AppendLine("错误信息:");
        report.AppendLine(ex.Message);
        report.AppendLine();
        report.AppendLine($"异常类型: {ex.GetType().FullName}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            report.AppendLine("堆栈跟踪:");
            report.AppendLine(ex.StackTrace);
        }
    }

    report.AppendLine();
    report.AppendLine("---- 完整安装日志 ----");
    report.Append(transcript);
    return report.ToString();
}

static void WriteResultReport(string? path, string content)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
    catch (Exception ex)
    {
        // A failure to persist the diagnostic report must never mask the real result.
        Console.Error.WriteLine($"WARN: 无法写入诊断报告到 {path}: {ex.Message}");
    }
}

internal readonly record struct InstallerOptions(bool NoPause, bool HideDeviceTool, string? ResultPath)
{
    public static InstallerOptions Parse(string[] args)
    {
        var fromApp = args.Any(arg => arg.Equals("--from-app", StringComparison.OrdinalIgnoreCase));
        var noPause = fromApp || args.Any(arg => arg.Equals("--no-pause", StringComparison.OrdinalIgnoreCase));
        var hideDeviceTool = fromApp || args.Any(arg => arg.Equals("--hide-device-tool", StringComparison.OrdinalIgnoreCase));

        string? resultPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--result", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                resultPath = args[i + 1];
            }
            else if (args[i].StartsWith("--result=", StringComparison.OrdinalIgnoreCase))
            {
                resultPath = args[i]["--result=".Length..];
            }
        }

        return new InstallerOptions(noPause, hideDeviceTool, resultPath);
    }
}

internal readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);

// Mirrors everything written to one TextWriter (the real console) into a second
// one (an in-memory transcript), so the full install log can be saved to disk.
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly TextWriter _secondary;

    public TeeTextWriter(TextWriter primary, TextWriter secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public override Encoding Encoding => _primary.Encoding;

    public override void Write(char value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    public override void Write(string? value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _primary.WriteLine(value);
        _secondary.WriteLine(value);
    }

    public override void Flush()
    {
        _primary.Flush();
        _secondary.Flush();
    }
}

internal sealed class DriverPackageBuilder
{
    public string PublishedName { get; set; } = string.Empty;

    public string? OriginalName { get; set; }

    public string? ProviderName { get; set; }

    public string? ClassName { get; set; }

    public string? ClassGuid { get; set; }

    public string? CatalogFile { get; set; }

    public List<string> DriverFiles { get; } = new();

    public DriverPackageInfo Build()
    {
        return new DriverPackageInfo(
            PublishedName,
            OriginalName,
            ProviderName,
            ClassName,
            ClassGuid,
            CatalogFile,
            DriverFiles.ToArray());
    }
}

internal sealed record DriverPackageInfo(
    string PublishedName,
    string? OriginalName,
    string? ProviderName,
    string? ClassName,
    string? ClassGuid,
    string? CatalogFile,
    IReadOnlyList<string> DriverFiles)
{
    public const string DisplayClassName = "Display";
    public const string DisplayClassGuid = "{4D36E968-E325-11CE-BFC1-08002BE10318}";

    public bool IsDisplayClass =>
        string.Equals(ClassName, DisplayClassName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ClassGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase);
}
