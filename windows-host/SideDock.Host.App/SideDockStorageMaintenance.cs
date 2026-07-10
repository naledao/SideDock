using System.Diagnostics;

namespace SideDock.Host.App;

internal static class SideDockStorageMaintenance
{
    private static readonly string[] ManagedDirectoryNames =
    [
        "Launcher",
        "HostApp",
        "DriverInstaller",
        "VirtualAudioCable"
    ];

    private static readonly TimeSpan WorkingDirectoryGracePeriod = TimeSpan.FromDays(1);

    public static StorageScanResult CalculateSize()
    {
        return Scan().ToResult();
    }

    public static StorageCleanupResult Clean()
    {
        var before = Scan();
        var warnings = new List<string>(before.Warnings);
        var deletedDirectories = 0;
        var skippedDirectories = 0;

        foreach (var candidate in before.Directories
                     .Where(directory => !directory.IsProtected)
                     .OrderBy(directory => directory.LastWriteTimeUtc))
        {
            try
            {
                if (!Directory.Exists(candidate.Path))
                {
                    continue;
                }

                Directory.Delete(candidate.Path, recursive: true);
                deletedDirectories++;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                skippedDirectories++;
                warnings.Add($"跳过 {candidate.DisplayName}：{ex.Message}");
            }
        }

        var after = Scan();
        warnings.AddRange(after.Warnings);

        return new StorageCleanupResult(
            before.ToResult(),
            after.ToResult(),
            deletedDirectories,
            skippedDirectories,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static StorageInventory Scan()
    {
        var directories = new List<StorageDirectoryCandidate>();
        var warnings = new List<string>();
        var activeExecutablePaths = GetActiveExecutablePaths();
        var currentBuildKey = GetCurrentBuildKey();
        var sideDockRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock");

        foreach (var managedDirectoryName in ManagedDirectoryNames)
        {
            var managedRoot = Path.Combine(sideDockRoot, managedDirectoryName);
            ScanManagedRoot(
                managedRoot,
                managedDirectoryName,
                currentBuildKey,
                activeExecutablePaths,
                directories,
                warnings);
        }

        return new StorageInventory(directories, warnings);
    }

    private static void ScanManagedRoot(
        string managedRoot,
        string managedDirectoryName,
        string currentBuildKey,
        IReadOnlyList<string> activeExecutablePaths,
        List<StorageDirectoryCandidate> directories,
        List<string> warnings)
    {
        if (!Directory.Exists(managedRoot))
        {
            return;
        }

        IReadOnlyList<DirectoryInfo> childDirectories;
        try
        {
            childDirectories = new DirectoryInfo(managedRoot).EnumerateDirectories().ToArray();
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            warnings.Add($"无法读取 {managedDirectoryName} 目录：{ex.Message}");
            return;
        }

        var recognizedDirectories = new List<(DirectoryInfo Directory, bool IsWorkingDirectory)>();
        foreach (var childDirectory in childDirectories)
        {
            bool isReparsePoint;
            DateTime lastWriteTimeUtc;
            try
            {
                isReparsePoint = childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint);
                lastWriteTimeUtc = childDirectory.LastWriteTimeUtc;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                warnings.Add($"无法检查 {managedDirectoryName}\\{childDirectory.Name}：{ex.Message}");
                continue;
            }

            if (isReparsePoint)
            {
                warnings.Add($"已跳过链接目录 {managedDirectoryName}\\{childDirectory.Name}。");
                continue;
            }

            var isBuildDirectory = IsBuildDirectoryName(childDirectory.Name);
            var isWorkingDirectory = IsWorkingDirectoryName(childDirectory.Name);
            if (!isBuildDirectory && !isWorkingDirectory)
            {
                continue;
            }

            recognizedDirectories.Add((childDirectory, isWorkingDirectory));
        }

        var newestBuildDirectoryPath = recognizedDirectories
            .Where(item => !item.IsWorkingDirectory)
            .OrderByDescending(item => item.Directory.LastWriteTimeUtc)
            .Select(item => item.Directory.FullName)
            .FirstOrDefault();

        foreach (var (directory, isWorkingDirectory) in recognizedDirectories)
        {
            var fullPath = directory.FullName;
            if (!IsDirectChildOf(fullPath, managedRoot))
            {
                warnings.Add($"已跳过超出预期位置的目录 {managedDirectoryName}\\{directory.Name}。");
                continue;
            }

            var isCurrentBuild = !managedDirectoryName.Equals("Launcher", StringComparison.OrdinalIgnoreCase)
                && directory.Name.Equals(currentBuildKey, StringComparison.OrdinalIgnoreCase);
            var isNewestBuild = PathsEqual(fullPath, newestBuildDirectoryPath);
            var isInUse = activeExecutablePaths.Any(path => IsPathWithin(path, fullPath));
            var isRecentWorkingDirectory = isWorkingDirectory
                && DateTime.UtcNow - directory.LastWriteTimeUtc < WorkingDirectoryGracePeriod;
            var isProtected = isCurrentBuild || isNewestBuild || isInUse || isRecentWorkingDirectory;
            var totalBytes = CalculateDirectorySize(fullPath, managedDirectoryName, warnings);

            directories.Add(new StorageDirectoryCandidate(
                fullPath,
                $"{managedDirectoryName}\\{directory.Name}",
                totalBytes,
                directory.LastWriteTimeUtc,
                isProtected));
        }
    }

    private static long CalculateDirectorySize(
        string root,
        string managedDirectoryName,
        List<string> warnings)
    {
        var totalBytes = 0L;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        warnings.Add($"无法读取 {Path.GetFileName(file)} 的大小：{ex.Message}");
                    }
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if (File.GetAttributes(childDirectory).HasFlag(FileAttributes.ReparsePoint))
                        {
                            warnings.Add($"已跳过 {managedDirectoryName} 中的链接目录 {Path.GetFileName(childDirectory)}。");
                            continue;
                        }

                        pendingDirectories.Push(childDirectory);
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        warnings.Add($"无法检查目录 {Path.GetFileName(childDirectory)}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                warnings.Add($"无法读取 {managedDirectoryName} 中的目录 {Path.GetFileName(directory)}：{ex.Message}");
            }
        }

        return totalBytes;
    }

    private static IReadOnlyList<string> GetActiveExecutablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddFullPath(Environment.ProcessPath, paths);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                           or NotSupportedException
                                           or System.ComponentModel.Win32Exception)
        {
            return paths.ToArray();
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    TryAddFullPath(process.MainModule?.FileName, paths);
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                                   or NotSupportedException
                                                   or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return paths.ToArray();
    }

    private static void TryAddFullPath(string? path, HashSet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            paths.Add(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    private static string GetCurrentBuildKey()
    {
        var processPath = Environment.ProcessPath;
        try
        {
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                var info = new FileInfo(processPath);
                return $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
            }
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
        }

        return string.Empty;
    }

    private static bool IsBuildDirectoryName(string name)
    {
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || !IsHex(parts[0]) || !IsHex(parts[1]))
        {
            return false;
        }

        return parts.Length == 2 || parts[2].Length == 32 && IsHex(parts[2]);
    }

    private static bool IsWorkingDirectoryName(string name)
    {
        const string extractingPrefix = ".extracting-";
        return name.StartsWith(extractingPrefix, StringComparison.OrdinalIgnoreCase)
            && IsBuildDirectoryName(name[extractingPrefix.Length..]);
    }

    private static bool IsHex(string value)
    {
        return value.Length > 0 && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');
    }

    private static bool IsDirectChildOf(string path, string expectedParent)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return PathsEqual(parent, Path.GetFullPath(expectedParent));
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
            .Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException;
    }

    private sealed record StorageDirectoryCandidate(
        string Path,
        string DisplayName,
        long TotalBytes,
        DateTime LastWriteTimeUtc,
        bool IsProtected);

    private sealed class StorageInventory(
        IReadOnlyList<StorageDirectoryCandidate> directories,
        IReadOnlyList<string> warnings)
    {
        public IReadOnlyList<StorageDirectoryCandidate> Directories { get; } = directories;

        public IReadOnlyList<string> Warnings { get; } = warnings;

        public StorageScanResult ToResult()
        {
            var reclaimableDirectories = Directories.Where(directory => !directory.IsProtected).ToArray();
            return new StorageScanResult(
                Directories.Sum(directory => directory.TotalBytes),
                Directories.Count,
                reclaimableDirectories.Sum(directory => directory.TotalBytes),
                reclaimableDirectories.Length,
                Directories.Count - reclaimableDirectories.Length,
                Warnings);
        }
    }
}

internal sealed record StorageScanResult(
    long TotalBytes,
    int DirectoryCount,
    long ReclaimableBytes,
    int ReclaimableDirectoryCount,
    int ProtectedDirectoryCount,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
}

internal sealed record StorageCleanupResult(
    StorageScanResult Before,
    StorageScanResult After,
    int DeletedDirectories,
    int SkippedDirectories,
    IReadOnlyList<string> Warnings)
{
    public long ReleasedBytes => Math.Max(0, Before.TotalBytes - After.TotalBytes);

    public bool HasWarnings => Warnings.Count > 0 || SkippedDirectories > 0;
}
