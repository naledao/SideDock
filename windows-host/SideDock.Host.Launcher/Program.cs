using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SideDock.Host.App.Launcher;

internal static class Program
{
    private const string InnerExe = "SideDock.Host.App.exe";
    private const string PayloadSuffix = ".HostPayload.zip";
    private const uint MbIconError = 0x00000010;
    private static readonly TimeSpan LauncherMutexTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileRetryDelay = TimeSpan.FromMilliseconds(150);

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
        if (TryResolveInnerExe(payloadRoot, out var innerExe))
        {
            return innerExe;
        }

        var direct = Path.Combine(payloadRoot, InnerExe);
        throw new FileNotFoundException($"{InnerExe} was not found after extracting the launcher payload.", direct);
    }

    private static string EnsurePayloadExtracted()
    {
        var buildKey = GetBuildKey();
        var launcherRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "Launcher");
        var root = Path.Combine(launcherRoot, buildKey);

        Directory.CreateDirectory(launcherRoot);

        var readyRoot = FindReadyPayload(launcherRoot, buildKey);
        if (readyRoot is not null)
        {
            return readyRoot;
        }

        using var mutex = new Mutex(false, $@"Local\SideDock.Host.Launcher.{buildKey}");
        var hasMutex = false;
        try
        {
            hasMutex = WaitForLauncherMutex(mutex);
            if (!hasMutex)
            {
                throw new TimeoutException("Timed out waiting for another SideDock launcher instance to finish preparing the payload.");
            }

            readyRoot = FindReadyPayload(launcherRoot, buildKey);
            if (readyRoot is not null)
            {
                return readyRoot;
            }

            var stagingRoot = Path.Combine(launcherRoot, $".extracting-{buildKey}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stagingRoot);
                ExtractPayload(stagingRoot, buildKey);
                return PublishPayload(stagingRoot, root, buildKey);
            }
            catch
            {
                TryDeleteDirectory(stagingRoot);
                throw;
            }
        }
        finally
        {
            if (hasMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static bool TryResolveInnerExe(string payloadRoot, out string innerExe)
    {
        var direct = Path.Combine(payloadRoot, InnerExe);
        if (File.Exists(direct))
        {
            innerExe = direct;
            return true;
        }

        try
        {
            var nested = Directory.EnumerateFiles(payloadRoot, InnerExe, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (nested is not null)
            {
                innerExe = nested;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }

        innerExe = string.Empty;
        return false;
    }

    private static string? FindReadyPayload(string launcherRoot, string buildKey)
    {
        var preferredRoot = Path.Combine(launcherRoot, buildKey);
        if (IsPayloadReady(preferredRoot, buildKey))
        {
            return preferredRoot;
        }

        try
        {
            if (!Directory.Exists(launcherRoot))
            {
                return null;
            }

            return Directory.EnumerateDirectories(launcherRoot, $"{buildKey}-*", SearchOption.TopDirectoryOnly)
                .Where(path => IsPayloadReady(path, buildKey))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsPayloadReady(string payloadRoot, string buildKey)
    {
        var markerPath = Path.Combine(payloadRoot, ".payload-version");
        try
        {
            return File.Exists(markerPath) &&
                string.Equals(File.ReadAllText(markerPath).Trim(), buildKey, StringComparison.Ordinal) &&
                TryResolveInnerExe(payloadRoot, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool WaitForLauncherMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(LauncherMutexTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void ExtractPayload(string targetRoot, string buildKey)
    {
        var zipPath = Path.Combine(targetRoot, "HostPayload.zip");
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(PayloadSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Launcher payload resource was not found.");

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Launcher payload resource stream was not found."))
        using (var output = File.Create(zipPath))
        {
            resource.CopyTo(output);
        }

        ZipFile.ExtractToDirectory(zipPath, targetRoot);
        if (!TryResolveInnerExe(targetRoot, out _))
        {
            throw new FileNotFoundException($"{InnerExe} was not found after extracting the launcher payload.", Path.Combine(targetRoot, InnerExe));
        }

        File.WriteAllText(Path.Combine(targetRoot, ".payload-version"), buildKey, Encoding.UTF8);
        TryDeleteFile(zipPath);
    }

    private static string PublishPayload(string stagingRoot, string preferredRoot, string buildKey)
    {
        if (TryDeleteDirectory(preferredRoot))
        {
            MoveDirectory(stagingRoot, preferredRoot);
            return preferredRoot;
        }

        var launcherRoot = Path.GetDirectoryName(preferredRoot)
            ?? throw new InvalidOperationException("Unable to resolve the SideDock launcher cache directory.");
        var fallbackRoot = Path.Combine(launcherRoot, $"{buildKey}-{Guid.NewGuid():N}");
        MoveDirectory(stagingRoot, fallbackRoot);
        return fallbackRoot;
    }

    private static void MoveDirectory(string source, string destination)
    {
        RetryFileOperation(() => Directory.Move(source, destination));
    }

    private static bool TryDeleteDirectory(string path)
    {
        return TryFileOperation(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        });
    }

    private static bool TryDeleteFile(string path)
    {
        return TryFileOperation(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        });
    }

    private static bool TryFileOperation(Action action)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 7)
                {
                    return false;
                }

                Thread.Sleep(FileRetryDelay);
            }
        }

        return false;
    }

    private static void RetryFileOperation(Action action)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 7 && ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(FileRetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                break;
            }
        }

        throw lastError ?? new IOException("File operation failed.");
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
