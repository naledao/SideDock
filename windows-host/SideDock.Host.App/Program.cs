using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace SideDock.Host.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new HostMainForm());
    }
}

internal sealed class HostMainForm : Form
{
    private const string HostExe = "SideDock.Host.exe";

    private readonly ComboBox _videoSource = new();
    private readonly ComboBox _resolution = new();
    private readonly ComboBox _refreshRate = new();
    private readonly CheckBox _enableInput = new();
    private readonly TextBox _adbPath = new();
    private readonly TextBox _controlPort = new();
    private readonly TextBox _videoPort = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Label _statusLabel = new();

    private Process? _hostProcess;
    private string? _payloadRoot;
    private string? _hostPath;

    public HostMainForm()
    {
        Text = "SideDock Host";
        MinimumSize = new Size(880, 300);
        Size = new Size(980, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        AutoScroll = true;

        BuildLayout();
        SetRunningState(false);

        FormClosing += (_, _) => StopHost();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            Text = "SideDock Windows Host",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(title, 0, 0);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 6)
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(settings, 0, 1);

        ConfigureCombo(_videoSource, "idd-gpu", "idd", "realtime");
        ConfigureCombo(_resolution, "720p", "1080p", "2k");
        ConfigureCombo(_refreshRate, "30", "60", "120");
        _controlPort.Text = "27183";
        _videoPort.Text = "27184";
        _adbPath.PlaceholderText = "Optional adb.exe path";
        _enableInput.Text = "Enable input injection";
        _enableInput.AutoSize = true;
        _enableInput.Checked = true;

        AddLabeled(settings, "Video source", _videoSource, 0);
        AddLabeled(settings, "Resolution", _resolution, 1);
        AddLabeled(settings, "Refresh rate", _refreshRate, 2);
        AddLabeled(settings, "Control port", _controlPort, 3);
        AddLabeled(settings, "Video port", _videoPort, 4);
        AddLabeled(settings, "ADB path", _adbPath, 5);

        var inputOptions = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8)
        };
        inputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        inputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputOptions.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
        inputOptions.Controls.Add(_enableInput, 1, 0);
        root.Controls.Add(inputOptions, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 12)
        };
        ConfigureActionButton(_startButton, "Start");
        _startButton.Click += (_, _) => StartHost();
        ConfigureActionButton(_stopButton, "Stop");
        _stopButton.Click += (_, _) => StopHost();
        buttons.MinimumSize = new Size(0, _startButton.PreferredSize.Height + 8);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(14, 8, 0, 0);
        buttons.Controls.Add(_startButton);
        buttons.Controls.Add(_stopButton);
        buttons.Controls.Add(_statusLabel);
        root.Controls.Add(buttons, 0, 3);
    }

    private static void ConfigureActionButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(120, 0);
        button.Padding = new Padding(12, 4, 12, 4);
        button.Margin = new Padding(0, 0, 8, 0);
    }

    private static void ConfigureCombo(ComboBox comboBox, params string[] items)
    {
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.Items.AddRange(items);
        comboBox.SelectedIndex = 0;
        comboBox.Dock = DockStyle.Fill;
    }

    private static void AddLabeled(TableLayoutPanel panel, string label, Control control, int row)
    {
        var leftColumn = row % 2 == 0 ? 0 : 2;
        var rightColumn = leftColumn + 1;
        var targetRow = row / 2;
        while (panel.RowCount <= targetRow)
        {
            panel.RowCount += 1;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 8, 7)
        }, leftColumn, targetRow);

        control.Margin = new Padding(0, 4, 16, 4);
        panel.Controls.Add(control, rightColumn, targetRow);
    }

    private void StartHost()
    {
        if (_hostProcess is { HasExited: false })
        {
            return;
        }

        try
        {
            _hostPath ??= ExtractHostPayload();
            var arguments = BuildArguments();
            var startInfo = new ProcessStartInfo
            {
                FileName = _hostPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(_hostPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var adbPath = _adbPath.Text.Trim();
            if (!string.IsNullOrWhiteSpace(adbPath))
            {
                startInfo.Environment["SIDEDOCK_ADB"] = adbPath;
            }

            AppendLog($"> {Path.GetFileName(_hostPath)} {arguments}");
            _hostProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _hostProcess.OutputDataReceived += (_, eventArgs) => AppendLog(eventArgs.Data);
            _hostProcess.ErrorDataReceived += (_, eventArgs) => AppendLog(eventArgs.Data);
            _hostProcess.Exited += (_, _) => RunOnUiThread(() =>
            {
                AppendLog($"Host exited with code {_hostProcess?.ExitCode}");
                SetRunningState(false);
            });

            _hostProcess.Start();
            _hostProcess.BeginOutputReadLine();
            _hostProcess.BeginErrorReadLine();
            SetRunningState(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to start SideDock Host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetRunningState(false);
        }
    }

    private string BuildArguments()
    {
        var args = new List<string>
        {
            "--video-source", Selected(_videoSource),
            "--resolution", Selected(_resolution),
            "--refresh-rate", Selected(_refreshRate),
            "--control-port", Port(_controlPort, "control"),
            "--video-port", Port(_videoPort, "video")
        };

        if (_enableInput.Checked)
        {
            args.Add("--enable-input-injection");
        }

        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string Selected(ComboBox comboBox)
    {
        return comboBox.SelectedItem?.ToString() ?? "";
    }

    private static string Port(TextBox textBox, string name)
    {
        var text = textBox.Text.Trim();
        if (!int.TryParse(text, out var port) || port < 1 || port > 65535)
        {
            throw new InvalidOperationException($"Invalid {name} port: {text}");
        }

        return port.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ') || argument.Contains('"')
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }

    private string ExtractHostPayload()
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".HostPayload.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded host payload was not found.");

        _payloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "HostApp");

        if (Directory.Exists(_payloadRoot))
        {
            Directory.Delete(_payloadRoot, recursive: true);
        }

        Directory.CreateDirectory(_payloadRoot);
        var zipPath = Path.Combine(_payloadRoot, "HostPayload.zip");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded host payload stream was not found."))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, _payloadRoot);
        File.Delete(zipPath);

        return Directory.GetFiles(_payloadRoot, HostExe, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{HostExe} was not found in the embedded payload.");
    }

    private void StopHost()
    {
        var process = _hostProcess;
        if (process is null || process.HasExited)
        {
            SetRunningState(false);
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to stop Host: {ex.Message}");
        }
        finally
        {
            SetRunningState(false);
        }
    }

    private void SetRunningState(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _statusLabel.Text = running ? "Running" : "Stopped";
        _statusLabel.ForeColor = running ? Color.FromArgb(24, 128, 72) : Color.FromArgb(128, 42, 42);
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The form can be closing while the hosted process is exiting.
        }
    }
}
