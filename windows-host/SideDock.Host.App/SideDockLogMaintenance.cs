using System.Globalization;

namespace SideDock.Host.App;

internal static class SideDockLogMaintenance
{
    internal const int RetainedRecentLogFileCount = 1;

    private static readonly string[] LogFileExtensions =
    [
        ".log",
        ".txt",
        ".jsonl",
        ".dmp",
        ".dump"
    ];

    public static LogScanResult CalculateSize()
    {
        return ScanLogs().ToResult();
    }

    public static LogCleanupResult Clean(bool retainRecentLogs)
    {
        var before = ScanLogs();
        var warnings = new List<string>(before.Warnings);
        var retainedPaths = retainRecentLogs
            ? before.Files
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Take(RetainedRecentLogFileCount)
                .Select(file => file.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var deletedFiles = 0;
        var deletedBytes = 0L;
        var retainedFiles = 0;
        var skippedFiles = 0;

        foreach (var file in before.Files)
        {
            if (retainedPaths.Contains(file.Path))
            {
                retainedFiles++;
                continue;
            }

            try
            {
                File.Delete(file.Path);
                deletedFiles++;
                deletedBytes += file.Length;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                skippedFiles++;
                warnings.Add($"跳过 {Path.GetFileName(file.Path)}：{ex.Message}");
            }
        }

        var after = ScanLogs();
        warnings.AddRange(after.Warnings);

        return new LogCleanupResult(
            before.ToResult(),
            after.ToResult(),
            deletedFiles,
            deletedBytes,
            retainedFiles,
            skippedFiles,
            warnings);
    }

    private static LogInventory ScanLogs()
    {
        var files = new List<LogFileCandidate>();
        var warnings = new List<string>();
        var missingLocations = 0;
        var existingLocations = 0;

        foreach (var location in LogLocations())
        {
            if (Directory.Exists(location.Directory))
            {
                existingLocations++;
                EnumerateLogFiles(location, files, warnings);
            }
            else
            {
                missingLocations++;
            }
        }

        return new LogInventory(files, warnings, existingLocations, missingLocations);
    }

    private static IEnumerable<LogLocation> LogLocations()
    {
        yield return new LogLocation(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDock",
                "logs"),
            true,
            true);

        yield return new LogLocation(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SideDock",
                "Logs"),
            true,
            true);

        yield return new LogLocation(
            AppContext.BaseDirectory,
            false,
            false);
    }

    private static void EnumerateLogFiles(
        LogLocation location,
        List<LogFileCandidate> files,
        List<string> warnings)
    {
        IReadOnlyList<string> filePaths;
        try
        {
            filePaths = Directory.EnumerateFiles(location.Directory).ToArray();
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            warnings.Add($"无法读取日志目录 {location.Directory}：{ex.Message}");
            return;
        }

        foreach (var path in filePaths)
        {
            if (IsLogFile(path, location.MatchAllLogsInDirectory))
            {
                TryAddFile(path, files, warnings);
            }
        }

        if (!location.Recursive)
        {
            return;
        }

        IReadOnlyList<string> childDirectories;
        try
        {
            childDirectories = Directory.EnumerateDirectories(location.Directory).ToArray();
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            warnings.Add($"无法读取日志子目录 {location.Directory}：{ex.Message}");
            return;
        }

        foreach (var childDirectory in childDirectories)
        {
            EnumerateLogFiles(location with { Directory = childDirectory }, files, warnings);
        }
    }

    private static bool IsLogFile(string path, bool matchAllLogsInDirectory)
    {
        var fileName = Path.GetFileName(path);
        if (!matchAllLogsInDirectory
            && !fileName.StartsWith("virtual-display-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return LogFileExtensions.Any(candidate => candidate.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryAddFile(string path, List<LogFileCandidate> files, List<string> warnings)
    {
        try
        {
            var info = new FileInfo(path);
            files.Add(new LogFileCandidate(
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc));
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            warnings.Add($"无法读取日志文件 {Path.GetFileName(path)}：{ex.Message}");
        }
    }

    private static bool IsFileSystemException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException;
    }

    private sealed record LogLocation(
        string Directory,
        bool Recursive,
        bool MatchAllLogsInDirectory);

    private sealed record LogFileCandidate(
        string Path,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed class LogInventory(
        IReadOnlyList<LogFileCandidate> files,
        IReadOnlyList<string> warnings,
        int existingLocations,
        int missingLocations)
    {
        public IReadOnlyList<LogFileCandidate> Files { get; } = files;

        public IReadOnlyList<string> Warnings { get; } = warnings;

        public LogScanResult ToResult()
        {
            return new LogScanResult(
                Files.Sum(file => file.Length),
                Files.Count,
                existingLocations,
                missingLocations,
                Warnings);
        }
    }
}

internal sealed record LogScanResult(
    long TotalBytes,
    int FileCount,
    int ExistingLocationCount,
    int MissingLocationCount,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
}

internal sealed record LogCleanupResult(
    LogScanResult Before,
    LogScanResult After,
    int DeletedFiles,
    long DeletedBytes,
    int RetainedFiles,
    int SkippedFiles,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0 || SkippedFiles > 0;

    public string RetentionDescription => RetainedFiles > 0
        ? string.Format(
            CultureInfo.InvariantCulture,
            "已保留最近 {0} 个日志文件。",
            RetainedFiles)
        : string.Empty;
}
