using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideDock.Host.App;

internal enum AppUpdateCheckStatus
{
    NotConfigured,
    Checking,
    UpToDate,
    UpdateAvailable,
    Failed
}

internal sealed class AppUpdateCheckResult
{
    public AppUpdateCheckStatus Status { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string CurrentVersion { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public Uri? ReleaseNotesUri { get; init; }

    public Uri? DownloadUri { get; init; }
}

internal static class AppUpdateService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<AppUpdateCheckResult> CheckAsync(
        AppSettings settings,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedSettings = settings.Normalize();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            var release = normalizedSettings.UpdateSourceKind switch
            {
                AppUpdateSourceKind.None => null,
                AppUpdateSourceKind.GitHubReleases => await FetchGitHubReleaseAsync(normalizedSettings, timeoutCts.Token),
                AppUpdateSourceKind.Manifest => await FetchManifestReleaseAsync(normalizedSettings, timeoutCts.Token),
                _ => null
            };

            if (release is null)
            {
                return NotConfigured(currentVersion);
            }

            if (release.Status == AppUpdateCheckStatus.Failed
                || release.Status == AppUpdateCheckStatus.NotConfigured)
            {
                return WithCurrentVersionIfMissing(release, currentVersion);
            }

            return CompareVersions(currentVersion, release);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(currentVersion, "检查失败", "检查更新超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            return Failed(currentVersion, "检查失败", $"网络请求失败：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return Failed(currentVersion, "检查失败", $"更新源返回的数据无法解析：{ex.Message}");
        }
        catch (Exception ex)
        {
            return Failed(currentVersion, "检查失败", $"检查更新失败：{ex.Message}");
        }
    }

    private static AppUpdateCheckResult WithCurrentVersionIfMissing(AppUpdateCheckResult result, string currentVersion)
    {
        return string.IsNullOrWhiteSpace(result.CurrentVersion)
            ? new AppUpdateCheckResult
            {
                Status = result.Status,
                Title = result.Title,
                Detail = result.Detail,
                CurrentVersion = currentVersion,
                LatestVersion = result.LatestVersion,
                PublishedAt = result.PublishedAt,
                ReleaseNotesUri = result.ReleaseNotesUri,
                DownloadUri = result.DownloadUri
            }
            : result;
    }

    private static async Task<AppUpdateCheckResult?> FetchGitHubReleaseAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var repository = settings.UpdateGitHubRepository.Trim();
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        if (!TrySplitGitHubRepository(repository, out var owner, out var repo))
        {
            return Failed(
                AppVersionInfo.CurrentVersion,
                "检查失败",
                "GitHub 仓库格式无效，请使用 owner/repo 或 https://github.com/owner/repo。");
        }

        if (settings.ReleaseChannel == AppReleaseChannel.Preview)
        {
            return await FetchGitHubPreviewReleaseAsync(owner, repo, cancellationToken);
        }

        var uri = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest");
        using var request = CreateJsonRequest(uri, "application/vnd.github+json");
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return GitHubHttpFailure(response, repository);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, JsonOptions, cancellationToken);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return Failed(AppVersionInfo.CurrentVersion, "检查失败", "GitHub Releases 返回的数据缺少版本号。");
        }

        return FromGitHubRelease(release);
    }

    private static async Task<AppUpdateCheckResult> FetchGitHubPreviewReleaseAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=20");
        using var request = CreateJsonRequest(uri, "application/vnd.github+json");
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return GitHubHttpFailure(response, $"{owner}/{repo}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseDto>>(stream, JsonOptions, cancellationToken)
            ?? new List<GitHubReleaseDto>();
        var latest = SelectHighestSemanticRelease(releases);

        return latest is null
            ? Failed(AppVersionInfo.CurrentVersion, "检查失败", "GitHub Releases 中没有可用发布版本。")
            : FromGitHubRelease(latest);
    }

    private static GitHubReleaseDto? SelectHighestSemanticRelease(IEnumerable<GitHubReleaseDto> releases)
    {
        GitHubReleaseDto? selected = null;
        SemanticAppVersion? selectedVersion = null;

        foreach (var release in releases.Where(release => !string.IsNullOrWhiteSpace(release.TagName)))
        {
            if (!SemanticAppVersion.TryParse(release.TagName, out var releaseVersion))
            {
                selected ??= release;
                continue;
            }

            if (selectedVersion is null
                || releaseVersion.CompareTo(selectedVersion) > 0
                || (releaseVersion.CompareTo(selectedVersion) == 0
                    && (release.PublishedAt ?? DateTimeOffset.MinValue) > (selected?.PublishedAt ?? DateTimeOffset.MinValue)))
            {
                selected = release;
                selectedVersion = releaseVersion;
            }
        }

        return selected;
    }

    private static async Task<AppUpdateCheckResult?> FetchManifestReleaseAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(settings.UpdateManifestUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Failed(AppVersionInfo.CurrentVersion, "检查失败", "更新 manifest URL 必须是 http 或 https 地址。");
        }

        using var request = CreateJsonRequest(uri, "application/json");
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return HttpFailure(response, "更新 manifest");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifestDto>(stream, JsonOptions, cancellationToken);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.LatestVersion))
        {
            return Failed(AppVersionInfo.CurrentVersion, "检查失败", "更新 manifest 缺少 latestVersion。");
        }

        return new AppUpdateCheckResult
        {
            Status = AppUpdateCheckStatus.UpToDate,
            Title = string.Empty,
            Detail = string.Empty,
            CurrentVersion = AppVersionInfo.CurrentVersion,
            LatestVersion = manifest.LatestVersion.Trim(),
            PublishedAt = manifest.PublishedAt,
            ReleaseNotesUri = TryCreateHttpUri(manifest.ReleaseNotesUrl, "releaseNotesUrl"),
            DownloadUri = TryCreateHttpUri(manifest.DownloadUrl, "downloadUrl")
        };
    }

    private static AppUpdateCheckResult CompareVersions(string currentVersion, AppUpdateCheckResult release)
    {
        if (!SemanticAppVersion.TryParse(currentVersion, out var current))
        {
            return Failed(currentVersion, "检查失败", $"当前版本号无法解析：{currentVersion}");
        }

        if (!SemanticAppVersion.TryParse(release.LatestVersion, out var latest))
        {
            return Failed(currentVersion, "检查失败", $"更新源返回的版本号无法解析：{release.LatestVersion}");
        }

        var latestVersion = release.LatestVersion?.Trim() ?? string.Empty;
        if (latest.CompareTo(current) > 0)
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateCheckStatus.UpdateAvailable,
                Title = "发现新版本",
                Detail = $"当前版本 {currentVersion}，最新版本 {latestVersion}。",
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                PublishedAt = release.PublishedAt,
                ReleaseNotesUri = release.ReleaseNotesUri,
                DownloadUri = release.DownloadUri
            };
        }

        return new AppUpdateCheckResult
        {
            Status = AppUpdateCheckStatus.UpToDate,
            Title = "已是最新版本",
            Detail = $"当前版本 {currentVersion} 已不低于发布源版本 {latestVersion}。",
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            PublishedAt = release.PublishedAt,
            ReleaseNotesUri = release.ReleaseNotesUri,
            DownloadUri = release.DownloadUri
        };
    }

    private static AppUpdateCheckResult FromGitHubRelease(GitHubReleaseDto release)
    {
        return new AppUpdateCheckResult
        {
            Status = AppUpdateCheckStatus.UpToDate,
            Title = string.Empty,
            Detail = string.Empty,
            CurrentVersion = AppVersionInfo.CurrentVersion,
            LatestVersion = release.TagName?.Trim(),
            PublishedAt = release.PublishedAt,
            ReleaseNotesUri = TryCreateHttpUri(release.HtmlUrl, "html_url"),
            DownloadUri = TryCreateHttpUri(release.HtmlUrl, "html_url")
        };
    }

    private static HttpRequestMessage CreateJsonRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"SideDock.Host.App/{BuildUserAgentVersion(AppVersionInfo.CurrentVersion)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    private static string BuildUserAgentVersion(string version)
    {
        var cleaned = new string(version
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "0.0.0" : cleaned;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static bool TrySplitGitHubRepository(string repository, out string owner, out string repo)
    {
        var normalized = repository.Trim().Trim('/');
        if (normalized.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://github.com/".Length..].Trim('/');
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        owner = parts.Length == 2 ? parts[0] : string.Empty;
        repo = parts.Length == 2 ? parts[1] : string.Empty;
        return !string.IsNullOrWhiteSpace(owner)
            && !string.IsNullOrWhiteSpace(repo)
            && owner.All(IsGitHubPathCharacter)
            && repo.All(IsGitHubPathCharacter);
    }

    private static bool IsGitHubPathCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value is '-' or '_' or '.';
    }

    private static AppUpdateCheckResult GitHubHttpFailure(HttpResponseMessage response, string repository)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Failed(
                AppVersionInfo.CurrentVersion,
                "检查失败",
                $"GitHub Releases 未找到：{repository}。请确认仓库存在且已发布 Release。");
        }

        return HttpFailure(response, "GitHub Releases");
    }

    private static AppUpdateCheckResult HttpFailure(HttpResponseMessage response, string sourceName)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var rateLimitDetail = BuildRateLimitDetail(response);
            return Failed(
                AppVersionInfo.CurrentVersion,
                "检查失败",
                string.IsNullOrWhiteSpace(rateLimitDetail)
                    ? $"{sourceName} 请求被限制，请稍后重试。"
                    : rateLimitDetail);
        }

        return Failed(
            AppVersionInfo.CurrentVersion,
            "检查失败",
            $"{sourceName} 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");
    }

    private static string BuildRateLimitDetail(HttpResponseMessage response)
    {
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            ? remainingValues.FirstOrDefault()
            : null;
        if (!string.Equals(remaining, "0", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            ? resetValues.FirstOrDefault()
            : null;
        if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetUnixSeconds))
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds).LocalDateTime;
            return $"GitHub API 速率限制已用尽，请在 {resetAt:yyyy-MM-dd HH:mm} 后重试。";
        }

        return "GitHub API 速率限制已用尽，请稍后重试。";
    }

    private static Uri? TryCreateHttpUri(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri;
        }

        throw new JsonException($"{fieldName} 不是有效的 http/https 地址。");
    }

    private static AppUpdateCheckResult NotConfigured(string currentVersion)
    {
        return new AppUpdateCheckResult
        {
            Status = AppUpdateCheckStatus.NotConfigured,
            Title = "未配置更新源",
            Detail = "当前没有配置 GitHub Releases、更新 manifest 或其它真实发布源。",
            CurrentVersion = currentVersion
        };
    }

    private static AppUpdateCheckResult Failed(string currentVersion, string title, string detail)
    {
        return new AppUpdateCheckResult
        {
            Status = AppUpdateCheckStatus.Failed,
            Title = title,
            Detail = detail,
            CurrentVersion = currentVersion
        };
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }

    private sealed class UpdateManifestDto
    {
        public string? LatestVersion { get; set; }

        public string? DownloadUrl { get; set; }

        public string? ReleaseNotesUrl { get; set; }

        public DateTimeOffset? PublishedAt { get; set; }
    }
}

internal sealed class SemanticAppVersion : IComparable<SemanticAppVersion>
{
    private SemanticAppVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    private int Major { get; }

    private int Minor { get; }

    private int Patch { get; }

    private IReadOnlyList<string> Prerelease { get; }

    public static bool TryParse(string? value, out SemanticAppVersion version)
    {
        version = new SemanticAppVersion(0, 0, 0, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        var prerelease = Array.Empty<string>();
        var prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseIndex >= 0)
        {
            var prereleaseText = normalized[(prereleaseIndex + 1)..];
            normalized = normalized[..prereleaseIndex];
            prerelease = prereleaseText.Split('.', StringSplitOptions.None);
            if (prerelease.Length == 0 || prerelease.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }
        }

        var segments = normalized.Split('.', StringSplitOptions.None).ToList();
        if (segments.Count == 0 || segments.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        while (segments.Count > 3 && segments[^1] == "0")
        {
            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count > 3)
        {
            return false;
        }

        while (segments.Count < 3)
        {
            segments.Add("0");
        }

        if (!TryParseSegment(segments[0], out var major)
            || !TryParseSegment(segments[1], out var minor)
            || !TryParseSegment(segments[2], out var patch))
        {
            return false;
        }

        version = new SemanticAppVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticAppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        coreComparison = Minor.CompareTo(other.Minor);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        coreComparison = Patch.CompareTo(other.Patch);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        return ComparePrerelease(other);
    }

    private static bool TryParseSegment(string value, out int segment)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out segment) && segment >= 0;
    }

    private int ComparePrerelease(SemanticAppVersion other)
    {
        if (Prerelease.Count == 0 && other.Prerelease.Count == 0)
        {
            return 0;
        }

        if (Prerelease.Count == 0)
        {
            return 1;
        }

        if (other.Prerelease.Count == 0)
        {
            return -1;
        }

        var limit = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var index = 0; index < limit; index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                continue;
            }

            var leftIsNumeric = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumeric = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            if (leftIsNumeric && rightIsNumeric)
            {
                return leftNumber.CompareTo(rightNumber);
            }

            if (leftIsNumeric)
            {
                return -1;
            }

            if (rightIsNumeric)
            {
                return 1;
            }

            return string.Compare(left, right, StringComparison.Ordinal);
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }
}
