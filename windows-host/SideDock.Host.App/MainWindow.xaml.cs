using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace SideDock.Host.App;

public sealed partial class MainWindow : Window
{
    private const string HostExe = "SideDock.Host.exe";
    private const string DeviceToolExe = "SideDock.Idd.DeviceTool.exe";
    private const string DriverInstallerExe = "SideDock.Driver.Installer.exe";
    private static readonly string DeviceToolProcessName = Path.GetFileNameWithoutExtension(DeviceToolExe);

    private readonly DispatcherTimer _displayStatusTimer = new();
    private readonly Brush _successBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 132, 86));
    private readonly Brush _dangerBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
    private readonly Brush _secondaryBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 96, 96, 96));

    private Process? _hostProcess;
    private Process? _deviceToolProcess;
    private string? _payloadRoot;
    private string? _hostPath;
    private string? _deviceToolPath;
    private string? _driverInstallerPath;
    private bool _hostOwnsVirtualDisplay;
    private int? _hostStopRequestedProcessId;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 760));

        Closed += (_, _) => StopHost();

        SetRunningState(false);
        RefreshVirtualDisplayState();

        _displayStatusTimer.Interval = TimeSpan.FromSeconds(2);
        _displayStatusTimer.Tick += (_, _) => RefreshVirtualDisplayState();
        _displayStatusTimer.Start();
    }

    private void StartHostButton_Click(object sender, RoutedEventArgs e)
    {
        StartHost();
    }

    private void StopHostButton_Click(object sender, RoutedEventArgs e)
    {
        StopHost();
    }

    private void StartDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        StartVirtualDisplay(failureAction: "启动虚拟显示器失败");
    }

    private async void InstallDriverButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallDriverAsync();
    }

    private void StopDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        StopVirtualDisplay();
    }

    private void StartHost()
    {
        if (_hostProcess is { HasExited: false })
        {
            return;
        }

        string? hostPath = null;
        string? arguments = null;
        string? workingDirectory = null;
        string? adbPath = null;
        HostProcessLog? hostLog = null;

        try
        {
            _hostStopRequestedProcessId = null;

            if (ShouldManageVirtualDisplayWithHost())
            {
                var displayWasRunning = IsVirtualDisplayToolRunning();
                if (!StartVirtualDisplay(failureAction: "启动主机时无法启动虚拟显示器"))
                {
                    return;
                }

                _hostOwnsVirtualDisplay = !displayWasRunning && IsVirtualDisplayToolRunning();
            }

            hostPath = _hostPath ??= ResolveHostPath();
            arguments = BuildArguments();
            workingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            adbPath = AdbPathBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(adbPath))
            {
                startInfo.Environment["SIDEDOCK_ADB"] = adbPath;
            }

            hostLog = new HostProcessLog(hostPath, arguments, workingDirectory, adbPath);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => hostLog.Append("stdout", args.Data);
            process.ErrorDataReceived += (_, args) => hostLog.Append("stderr", args.Data);
            process.Exited += (_, _) => DispatcherQueue.TryEnqueue(() => HandleHostExited(process, hostLog));

            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动 {HostExe}。");
            }

            _hostProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetRunningState(true);
        }
        catch (Exception ex)
        {
            StopHostOwnedVirtualDisplay();
            _hostProcess = null;
            var details = BuildHostFailureDetails(
                "启动主机时发生异常",
                hostPath,
                arguments,
                workingDirectory,
                adbPath,
                exitCode: null,
                hostLog,
                ex);
            ShowErrorWithDetails(
                "无法启动 SideDock 主机",
                $"启动 SideDock 主机时出错：{ex.Message}。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。",
                details);
            SetRunningState(false);
        }
    }

    private string BuildArguments()
    {
        var args = new List<string>
        {
            "--video-source", Selected(VideoSourceCombo),
            "--resolution", Selected(ResolutionCombo),
            "--refresh-rate", Selected(RefreshRateCombo),
            "--control-port", Port(ControlPortBox, "control"),
            "--video-port", Port(VideoPortBox, "video")
        };

        if (InputInjectionSwitch.IsOn)
        {
            args.Add("--enable-input-injection");
        }

        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string Selected(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    }

    private static string Port(NumberBox numberBox, string name)
    {
        if (double.IsNaN(numberBox.Value))
        {
            throw new InvalidOperationException($"{name} 端口无效。");
        }

        var port = (int)numberBox.Value;
        if (port < 1 || port > 65535)
        {
            throw new InvalidOperationException($"{name} 端口无效: {port}");
        }

        return port.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }

    private string ResolveHostPath()
    {
        foreach (var candidate in EnumerateHostCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var extracted = TryExtractHostPayload();
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        throw new FileNotFoundException(
            $"未找到 {HostExe}。请先构建 SideDock.Host，或把 HostPayload.zip 打进桌面端包。");
    }

    private string TryExtractHostPayload()
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".HostPayload.zip", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return string.Empty;
        }

        _payloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "HostApp",
            GetBuildKey());

        var hostPath = FindExtractedExecutable(_payloadRoot, HostExe);
        if (!string.IsNullOrWhiteSpace(hostPath))
        {
            return hostPath;
        }

        Directory.CreateDirectory(_payloadRoot);
        var zipPath = Path.Combine(_payloadRoot, "HostPayload.zip");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded host payload stream was not found."))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, _payloadRoot, overwriteFiles: true);
        File.Delete(zipPath);

        return FindExtractedExecutable(_payloadRoot, HostExe);
    }

    private IEnumerable<string> EnumerateHostCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, HostExe);
        yield return Path.Combine(baseDirectory, "SideDock.Host", HostExe);
        yield return Path.Combine(baseDirectory, "SideDock.Host", "x64", "Release", HostExe);
        yield return Path.Combine(baseDirectory, "SideDock.Host", "x64", "Debug", HostExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, HostExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Host", HostExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Host", "x64", "Release", HostExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Host", "x64", "Debug", HostExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-host",
            "SideDock.Host",
            "bin",
            "Debug",
            "net8.0",
            HostExe));
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-host",
            "SideDock.Host",
            "bin",
            "Release",
            "net8.0",
            HostExe));
    }

    private void StopHost()
    {
        var process = _hostProcess;
        try
        {
            if (process is not null && !process.HasExited)
            {
                _hostStopRequestedProcessId = TryGetProcessId(process);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(_hostProcess, process))
            {
                _hostProcess = null;
            }

            StopHostOwnedVirtualDisplay();
            SetRunningState(false);
        }
    }

    private bool StartVirtualDisplay(bool showCopyableError = true, string failureAction = "启动虚拟显示器失败")
    {
        if (IsVirtualDisplayToolRunning())
        {
            RefreshVirtualDisplayState();
            return true;
        }

        int? exitCode = null;
        DeviceToolDiagnostics? diagnostics = null;

        try
        {
            _deviceToolPath ??= ResolveDeviceToolPath();
            var workingDirectory = Path.GetDirectoryName(_deviceToolPath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = _deviceToolPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _deviceToolProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {DeviceToolExe}.");

            if (_deviceToolProcess.WaitForExit(2500))
            {
                exitCode = _deviceToolProcess.ExitCode;
                _deviceToolProcess = null;
                diagnostics = CaptureDeviceToolDiagnostics(_deviceToolPath, workingDirectory);
                throw new InvalidOperationException($"{DeviceToolExe} exited with code {exitCode}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (showCopyableError)
            {
                ShowErrorWithDetails(
                    "无法启动虚拟显示器",
                    $"{failureAction}：{ex.Message}。下面是详细诊断信息，点“复制详情”可一键复制。",
                    BuildVirtualDisplayFailureDetails(ex, exitCode, diagnostics));
            }
            else
            {
                ShowError("无法启动虚拟显示器", ex.Message);
            }

            return false;
        }
        finally
        {
            RefreshVirtualDisplayState();
        }
    }

    private void StopVirtualDisplay()
    {
        var processes = GetVirtualDisplayToolProcesses();
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
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        _deviceToolProcess = null;
        RefreshVirtualDisplayState();
    }

    private void StopHostOwnedVirtualDisplay()
    {
        if (!_hostOwnsVirtualDisplay)
        {
            return;
        }

        StopVirtualDisplay();
        _hostOwnsVirtualDisplay = false;
    }

    private string ResolveDeviceToolPath()
    {
        foreach (var candidate in EnumerateDeviceToolCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        foreach (var root in EnumerateDeviceToolSearchRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var match = Directory.GetFiles(root, DeviceToolExe, SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        throw new FileNotFoundException(
            $"未找到 {DeviceToolExe}。请先安装 SideDock 驱动包，或先构建 SideDock.Idd.DeviceTool。");
    }

    private async Task InstallDriverAsync()
    {
        InstallDriverButton.IsEnabled = false;
        DriverInstallStatusText.Text = "正在启动驱动安装器，请在管理员权限弹窗中选择“是”。";

        // The installer runs elevated (runas), so we cannot capture its stdout/stderr
        // directly. Instead we hand it a result-file path; it writes a full diagnostic
        // report there that we read back after it exits.
        var reportPath = BuildDriverInstallLogPath();

        try
        {
            _driverInstallerPath ??= ResolveDriverInstallerPath();
            var startInfo = new ProcessStartInfo
            {
                FileName = _driverInstallerPath,
                Arguments = $"--from-app --result {QuoteArgument(reportPath)}",
                WorkingDirectory = Path.GetDirectoryName(_driverInstallerPath) ?? Environment.CurrentDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {DriverInstallerExe}。");

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                _deviceToolPath = null;
                DriverInstallStatusText.Text = "驱动安装流程已完成。若显示器没有立即出现，请点“启动显示器”。";
                TryDeleteFile(reportPath);
            }
            else
            {
                DriverInstallStatusText.Text = $"驱动安装未完成（退出码 {process.ExitCode}）。点开详情可一键复制。";
                var report = TryReadFile(reportPath);
                var summary = $"安装器以退出码 {process.ExitCode} 结束。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。";
                var details = !string.IsNullOrWhiteSpace(report)
                    ? $"日志文件: {reportPath}{Environment.NewLine}{Environment.NewLine}{report}"
                    : $"安装器以退出码 {process.ExitCode} 结束，但未生成诊断报告（{reportPath} 不存在）。{Environment.NewLine}"
                      + $"可能原因：驱动安装器版本过旧，或无法写入日志目录。{Environment.NewLine}"
                      + $"安装器路径: {_driverInstallerPath}";
                ShowErrorWithDetails("驱动安装未完成", summary, details);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            DriverInstallStatusText.Text = "驱动安装已取消（未授予管理员权限）。";
            ShowError("驱动安装已取消", "你在管理员权限弹窗中选择了“否”，安装未开始。请重新点击“安装/修复驱动”，并在弹窗中选择“是”。");
        }
        catch (Exception ex)
        {
            DriverInstallStatusText.Text = "驱动安装未完成。";
            var report = TryReadFile(reportPath);
            var details = !string.IsNullOrWhiteSpace(report)
                ? $"日志文件: {reportPath}{Environment.NewLine}{Environment.NewLine}{report}"
                : ex.ToString();
            ShowErrorWithDetails("无法安装驱动", $"启动或执行驱动安装器时出错：{ex.Message}", details);
        }
        finally
        {
            InstallDriverButton.IsEnabled = true;
            RefreshVirtualDisplayState();
        }
    }

    private string ResolveDriverInstallerPath()
    {
        foreach (var candidate in EnumerateDriverInstallerCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var extracted = TryExtractDriverInstallerPayload();
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted;
        }

        throw new FileNotFoundException(
            $"未找到 {DriverInstallerExe}。请使用包含驱动安装器的 SideDock 桌面端发布包。");
    }

    private string TryExtractDriverInstallerPayload()
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".DriverInstallerPayload.zip", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return string.Empty;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "DriverInstaller",
            GetBuildKey());

        var driverInstallerPath = FindExtractedExecutable(root, DriverInstallerExe);
        if (!string.IsNullOrWhiteSpace(driverInstallerPath))
        {
            return driverInstallerPath;
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "DriverInstallerPayload.zip");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("未找到内置驱动安装器资源流。"))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
        File.Delete(zipPath);

        return FindExtractedExecutable(root, DriverInstallerExe);
    }

    private static string FindExtractedExecutable(string root, string executableName)
    {
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        var directPath = Path.Combine(root, executableName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return Directory.GetFiles(root, executableName, SearchOption.AllDirectories).FirstOrDefault()
            ?? string.Empty;
    }

    private static string GetBuildKey()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var info = new FileInfo(processPath);
            return $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
        }

        return "unknown";
    }

    private IEnumerable<string> EnumerateDriverInstallerCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, DriverInstallerExe);
        yield return Path.Combine(baseDirectory, "SideDock.Driver.Installer", DriverInstallerExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, DriverInstallerExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Driver.Installer", DriverInstallerExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-driver",
            "SideDock.Driver.Installer",
            "bin",
            "Release",
            "net8.0-windows",
            "win-x64",
            "publish",
            DriverInstallerExe));
    }

    private IEnumerable<string> EnumerateDeviceToolCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, DeviceToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.Idd.DeviceTool", DeviceToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.Idd.DeviceTool", "x64", "Release", DeviceToolExe);
        yield return Path.Combine(baseDirectory, "SideDock.Idd.DeviceTool", "x64", "Debug", DeviceToolExe);

        if (_payloadRoot is not null)
        {
            yield return Path.Combine(_payloadRoot, DeviceToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Idd.DeviceTool", DeviceToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Idd.DeviceTool", "x64", "Release", DeviceToolExe);
            yield return Path.Combine(_payloadRoot, "SideDock.Idd.DeviceTool", "x64", "Debug", DeviceToolExe);
        }

        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-driver",
            "SideDock.Idd.DeviceTool",
            "x64",
            "Release",
            DeviceToolExe));
        yield return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "windows-driver",
            "SideDock.Idd.DeviceTool",
            "x64",
            "Debug",
            DeviceToolExe));
    }

    private IEnumerable<string> EnumerateDeviceToolSearchRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SideDock",
            "Driver",
            "payload");

        if (_payloadRoot is not null)
        {
            yield return _payloadRoot;
        }
    }

    private bool ShouldManageVirtualDisplayWithHost()
    {
        return ManageDisplaySwitch.IsOn && RequiresVirtualDisplay();
    }

    private bool RequiresVirtualDisplay()
    {
        var videoSource = Selected(VideoSourceCombo);
        return videoSource.Equals("idd", StringComparison.OrdinalIgnoreCase)
            || videoSource.Equals("idd-gpu", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleHostExited(Process process, HostProcessLog hostLog)
    {
        var isCurrentHost = ReferenceEquals(_hostProcess, process);
        var processId = TryGetProcessId(process);
        var expectedStop = processId.HasValue && _hostStopRequestedProcessId == processId;
        if (expectedStop)
        {
            _hostStopRequestedProcessId = null;
        }

        if (!isCurrentHost)
        {
            return;
        }

        _hostProcess = null;
        StopHostOwnedVirtualDisplay();
        SetRunningState(false);

        if (expectedStop)
        {
            return;
        }

        var exitCode = TryGetExitCode(process);
        var summary = exitCode.HasValue
            ? $"SideDock 主机进程已退出（退出码 {exitCode.Value}）。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。"
            : "SideDock 主机进程已退出。下面是详细诊断信息，点“复制详情”可一键复制后发给开发者排查。";
        var details = BuildHostFailureDetails(
            "主机进程退出",
            hostLog.HostPath,
            hostLog.Arguments,
            hostLog.WorkingDirectory,
            hostLog.AdbPath,
            exitCode,
            hostLog,
            exception: null);

        ShowErrorWithDetails("SideDock 主机已退出", summary, details);
    }

    private string BuildHostFailureDetails(
        string reason,
        string? hostPath,
        string? arguments,
        string? workingDirectory,
        string? adbPath,
        int? exitCode,
        HostProcessLog? hostLog,
        Exception? exception)
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 主机启动失败诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"原因: {reason}");
        if (exitCode.HasValue)
        {
            report.AppendLine($"退出码: {exitCode.Value}");
        }

        report.AppendLine($"Host 路径: {FormatOptional(hostPath)}");
        report.AppendLine($"工作目录: {FormatOptional(workingDirectory)}");
        report.AppendLine($"启动参数: {FormatOptional(arguments)}");
        report.AppendLine($"ADB 路径: {FormatOptional(adbPath)}");
        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"分辨率: {Selected(ResolutionCombo)}");
        report.AppendLine($"刷新率: {Selected(RefreshRateCombo)}");
        report.AppendLine($"控制端口: {FormatNumberBox(ControlPortBox)}");
        report.AppendLine($"视频端口: {FormatNumberBox(VideoPortBox)}");
        report.AppendLine($"启用触控输入: {InputInjectionSwitch.IsOn}");
        report.AppendLine($"自动管理虚拟显示器: {ManageDisplaySwitch.IsOn}");

        if (exception is not null)
        {
            report.AppendLine();
            report.AppendLine("---- 异常 ----");
            report.AppendLine(exception.ToString());
        }

        if (hostLog is not null)
        {
            report.AppendLine();
            report.AppendLine("---- stdout/stderr ----");
            report.Append(hostLog.Snapshot());
        }

        return report.ToString();
    }

    private DeviceToolDiagnostics CaptureDeviceToolDiagnostics(string deviceToolPath, string workingDirectory)
    {
        var output = new StringBuilder();
        var outputGate = new object();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = deviceToolPath,
                Arguments = "--oneshot",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, args) => AppendDiagnosticLine(output, outputGate, "stdout", args.Data);
            process.ErrorDataReceived += (_, args) => AppendDiagnosticLine(output, outputGate, "stderr", args.Data);

            if (!process.Start())
            {
                return new DeviceToolDiagnostics(null, "无法启动诊断进程。", TimedOut: false);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(12_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                return new DeviceToolDiagnostics(TryGetExitCode(process), SnapshotOutput(output, outputGate), TimedOut: true);
            }

            process.WaitForExit();
            return new DeviceToolDiagnostics(TryGetExitCode(process), SnapshotOutput(output, outputGate), TimedOut: false);
        }
        catch (Exception ex)
        {
            AppendDiagnosticLine(output, outputGate, "exception", ex.ToString());
            return new DeviceToolDiagnostics(null, SnapshotOutput(output, outputGate), TimedOut: false);
        }
    }

    private string BuildVirtualDisplayFailureDetails(
        Exception exception,
        int? exitCode,
        DeviceToolDiagnostics? diagnostics)
    {
        var report = new StringBuilder();
        report.AppendLine("SideDock 虚拟显示器启动失败诊断报告");
        report.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"DeviceTool 路径: {FormatOptional(_deviceToolPath)}");
        report.AppendLine($"进程名: {DeviceToolProcessName}");
        if (exitCode.HasValue)
        {
            report.AppendLine($"原始退出码: {exitCode.Value}");
        }

        report.AppendLine($"视频源: {Selected(VideoSourceCombo)}");
        report.AppendLine($"自动管理虚拟显示器: {ManageDisplaySwitch.IsOn}");
        report.AppendLine();
        report.AppendLine("---- 异常 ----");
        report.AppendLine(exception.ToString());
        if (diagnostics is not null)
        {
            report.AppendLine();
            report.AppendLine("---- DeviceTool --oneshot 诊断输出 ----");
            if (diagnostics.ExitCode.HasValue)
            {
                report.AppendLine($"诊断退出码: {diagnostics.ExitCode.Value}");
            }

            if (diagnostics.TimedOut)
            {
                report.AppendLine("诊断运行超时，已停止诊断进程。");
            }

            report.AppendLine(string.IsNullOrWhiteSpace(diagnostics.Output)
                ? "(没有捕获到 stdout/stderr 输出)"
                : diagnostics.Output.TrimEnd());
        }

        return report.ToString();
    }

    private static void AppendDiagnosticLine(StringBuilder output, object gate, string stream, string? line)
    {
        if (line is not null)
        {
            lock (gate)
            {
                output.AppendLine($"{stream}: {line}");
            }
        }
    }

    private static string SnapshotOutput(StringBuilder output, object gate)
    {
        lock (gate)
        {
            return output.ToString();
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(空)" : value;
    }

    private static string FormatNumberBox(NumberBox numberBox)
    {
        return double.IsNaN(numberBox.Value) ? "(无效)" : ((int)numberBox.Value).ToString();
    }

    private void RefreshVirtualDisplayState()
    {
        var running = IsVirtualDisplayToolRunning();
        StartDisplayButton.IsEnabled = !running;
        StopDisplayButton.IsEnabled = running;
        DisplayStatusText.Text = running ? "显示器已启动" : "显示器未启动";
        DisplayStatusText.Foreground = running ? _successBrush : _dangerBrush;
        DisplayStatusSubtext.Text = running
            ? "SideDock 虚拟显示器工具正在运行。"
            : "虚拟显示器工具未运行。";
    }

    private static bool IsVirtualDisplayToolRunning()
    {
        var processes = GetVirtualDisplayToolProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static Process[] GetVirtualDisplayToolProcesses()
    {
        try
        {
            return Process.GetProcessesByName(DeviceToolProcessName);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private void SetRunningState(bool running)
    {
        StartHostButton.IsEnabled = !running;
        StopHostButton.IsEnabled = running;
        OverallStatusText.Text = running ? "运行中" : "未启动";
        OverallStatusText.Foreground = running ? _successBrush : _dangerBrush;
    }

    private async void ShowError(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private static string BuildDriverInstallLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SideDock",
            "Logs");

        // Pre-create the directory from the (non-elevated) host so the elevated installer
        // only needs to write the file into it, and so this process reliably owns and can
        // read the report back even when elevation runs under a different admin account.
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // The installer also creates this directory; ignore and let it try.
        }

        return Path.Combine(directory, $"driver-install-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: a leftover success log is harmless.
        }
    }

    private async void ShowErrorWithDetails(string title, string summary, string details)
    {
        var summaryText = new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap
        };

        // A read-only, selectable multiline TextBox: the 复制详情 button copies it in one
        // click, and the user can still select + Ctrl+C manually if the clipboard API fails.
        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            IsSpellCheckEnabled = false,
            MinWidth = 560,
            MaxHeight = 480
        };
        ScrollViewer.SetVerticalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(detailBox, ScrollBarVisibility.Auto);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(summaryText);
        panel.Children.Add(detailBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "复制详情",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true; // Keep the dialog open so the user can copy again / read on.
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(details);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                sender.PrimaryButtonText = "已复制 ✓";
            }
            catch
            {
                sender.PrimaryButtonText = "复制失败，请手动选择文本复制";
            }
        };

        await dialog.ShowAsync();
    }

    private sealed class HostProcessLog(
        string hostPath,
        string arguments,
        string workingDirectory,
        string? adbPath)
    {
        private const int MaxCharacters = 128 * 1024;
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();
        private bool _truncated;

        public string HostPath { get; } = hostPath;
        public string Arguments { get; } = arguments;
        public string WorkingDirectory { get; } = workingDirectory;
        public string? AdbPath { get; } = adbPath;

        public void Append(string stream, string? line)
        {
            if (line is null)
            {
                return;
            }

            var entry = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {stream}: {line}{Environment.NewLine}";
            lock (_gate)
            {
                if (_buffer.Length + entry.Length > MaxCharacters)
                {
                    var overflow = _buffer.Length + entry.Length - MaxCharacters;
                    _buffer.Remove(0, Math.Min(overflow, _buffer.Length));
                    _truncated = true;
                }

                _buffer.Append(entry);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                if (_buffer.Length == 0)
                {
                    return "(没有捕获到 stdout/stderr 输出)" + Environment.NewLine;
                }

                var prefix = _truncated
                    ? $"(日志较长，已保留最后 {MaxCharacters / 1024} KB){Environment.NewLine}"
                    : string.Empty;
                return prefix + _buffer;
            }
        }
    }

    private sealed record DeviceToolDiagnostics(int? ExitCode, string Output, bool TimedOut);
}
