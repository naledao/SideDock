using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SideDock.Host.App.Launcher;

internal static class Program
{
    private const string InnerExe = "SideDock.Host.App.exe";
    private const string PayloadSuffix = ".HostPayload.zip";
    private const uint MbIconError = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var payloadRoot = EnsurePayloadExtracted();
            var innerExe = ResolveInnerExe(payloadRoot);

            var startInfo = new ProcessStartInfo
            {
                FileName = innerExe,
                WorkingDirectory = payloadRoot,
                UseShellExecute = false
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var launched = Process.Start(startInfo);
            if (launched is null)
            {
                throw new InvalidOperationException($"Unable to start {InnerExe}.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return 1;
        }
    }

    private static string ResolveInnerExe(string payloadRoot)
    {
        var direct = Path.Combine(payloadRoot, InnerExe);
        if (File.Exists(direct))
        {
            return direct;
        }

        var nested = Directory.GetFiles(payloadRoot, InnerExe, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (nested is not null)
        {
            return nested;
        }

        throw new FileNotFoundException($"{InnerExe} was not found after extracting the launcher payload.", direct);
    }

    private static string EnsurePayloadExtracted()
    {
        var buildKey = GetBuildKey();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "Launcher",
            buildKey);
        var markerPath = Path.Combine(root, ".payload-version");

        if (File.Exists(markerPath) &&
            string.Equals(File.ReadAllText(markerPath).Trim(), buildKey, StringComparison.Ordinal) &&
            File.Exists(Path.Combine(root, InnerExe)))
        {
            return root;
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "HostPayload.zip");
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(PayloadSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Launcher payload resource was not found.");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Launcher payload resource stream was not found."))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, root);
        File.WriteAllText(markerPath, buildKey, Encoding.UTF8);
        File.Delete(zipPath);
        return root;
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

    private static void ShowError(string message)
    {
        MessageBox(IntPtr.Zero, message, "SideDock launch failed", MbIconError);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
