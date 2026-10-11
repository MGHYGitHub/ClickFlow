using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClickFlow.Core.Updates;

public sealed record UpdateRelease(Version Version, string Tag, string Name, string Notes,
    Uri ReleasePage, Uri DownloadUrl, Uri ChecksumsUrl, string FileName, long SizeBytes);

public sealed record DownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes > 0 ? Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100) : 0;
}

/// <summary>Downloads only stable releases belonging to this application's public repository.</summary>
public sealed partial class GitHubUpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/MGHYGitHub/ClickFlow";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private const string LatestUrl = "https://api.github.com/repos/MGHYGitHub/ClickFlow/releases/latest";
    private const string LatestManifestUrl = ReleasesUrl + "/latest/download/update.json";
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubUpdateService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            byte[] bytes;
            try
            {
                using var response = await SendAsync(new Uri(LatestUrl), false, timeout.Token);
                bytes = await ReadBoundedAsync(response, 1024 * 1024, timeout.Token);
            }
            catch (RateLimitedException)
            {
                // Release assets are public downloads and do not consume GitHub API quota.
                using var response = await SendAsync(new Uri(LatestManifestUrl), true, timeout.Token, allowLatestManifest: true);
                bytes = await ReadBoundedAsync(response, 1024 * 1024, timeout.Token);
            }
            return ParseRelease(bytes, currentVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("检查更新超时，请检查网络后重试。"); }
        catch (HttpRequestException ex) { throw new IOException("无法连接 GitHub，请检查网络后重试。", ex); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("GitHub 返回的更新信息不完整或格式不正确。", ex); }
    }

    private static UpdateRelease? ParseRelease(byte[] bytes, Version currentVersion)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("更新信息不是正式版本，请稍后重试。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        if (version <= current) return null;
        var fileName = $"ClickFlow-{version}-win-x64.exe";
        var releasePage = RequireExactUri(root.GetProperty("html_url").GetString(), $"{ReleasesUrl}/tag/{tag}");
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var executable = RequireAsset(assets, fileName);
        var checksums = RequireAsset(assets, "SHA256SUMS.txt");
        var size = executable.GetProperty("size").GetInt64();
        if (size is <= 0 or > MaxDownloadBytes) throw new InvalidDataException("安装文件大小超出允许范围。");
        return new UpdateRelease(version, tag,
            root.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            releasePage,
            RequireExactUri(executable.GetProperty("browser_download_url").GetString(), $"{ReleasesUrl}/download/{tag}/{fileName}"),
            RequireExactUri(checksums.GetProperty("browser_download_url").GetString(), $"{ReleasesUrl}/download/{tag}/SHA256SUMS.txt"),
            fileName, size);
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string destinationDirectory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        string? partialPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifest;
            using (var checksumTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                checksumTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var manifestResponse = await SendAsync(release.ChecksumsUrl, true, checksumTimeout.Token);
                manifest = Encoding.UTF8.GetString(await ReadBoundedAsync(manifestResponse, 128 * 1024, checksumTimeout.Token));
            }
            var expectedHash = ReadExpectedHash(manifest, release.FileName);
            var destination = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destination);
            var finalPath = Path.Combine(destination, release.FileName);
            partialPath = Path.Combine(destination, $".{release.FileName}.{Guid.NewGuid():N}.partial");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var response = await SendAsync(release.DownloadUrl, true, timeout.Token);
            if (response.Content.Headers.ContentLength is long declared && declared != release.SizeBytes)
                throw new InvalidDataException("下载文件大小与发布信息不一致，已中止下载。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            progress?.Report(new DownloadProgress(0, release.SizeBytes));
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    received += count;
                    if (received > release.SizeBytes || received > MaxDownloadBytes)
                        throw new InvalidDataException("下载文件超过预期大小，已中止下载。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report(new DownloadProgress(received, release.SizeBytes));
                }
                if (received != release.SizeBytes) throw new InvalidDataException("下载未完成，文件大小不正确，请重试。");
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
                    throw new InvalidDataException("SHA-256 校验失败，文件可能不完整，已删除本次下载。");
                await output.FlushAsync(timeout.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Keep any existing copy untouched until the new download has passed all checks.
            File.Move(partialPath, finalPath, overwrite: true);
            partialPath = null;
            return finalPath;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("下载更新超时，请检查网络后重试。"); }
        catch (HttpRequestException ex) { throw new IOException("下载更新失败，请检查网络后重试。", ex); }
        finally
        {
            if (partialPath is not null)
            {
                try { File.Delete(partialPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static Version ParseVersion(string tag)
    {
        var match = StableVersionPattern().Match(tag);
        if (!match.Success || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("更新版本号必须使用 v主版本.次版本.修订版本 格式。");
        return version;
    }

    private static JsonElement RequireAsset(JsonElement[] assets, string name)
    {
        var matches = assets.Where(a => a.TryGetProperty("name", out var n) && n.GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"发布文件缺失或重复：{name}");
        return matches[0];
    }

    private static Uri RequireExactUri(string? text, string expected)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !StringComparer.Ordinal.Equals(uri.AbsoluteUri, expected))
            throw new InvalidDataException("更新地址不属于 ClickFlow 官方仓库，已中止操作。");
        return uri;
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        var version = ParseVersion(release.Tag);
        var fileName = $"ClickFlow-{version}-win-x64.exe";
        if (version != release.Version || release.FileName != fileName || release.SizeBytes is <= 0 or > MaxDownloadBytes)
            throw new InvalidDataException("更新文件信息不正确。");
        RequireExactUri(release.ReleasePage.AbsoluteUri, $"{ReleasesUrl}/tag/{release.Tag}");
        RequireExactUri(release.DownloadUrl.AbsoluteUri, $"{ReleasesUrl}/download/{release.Tag}/{fileName}");
        RequireExactUri(release.ChecksumsUrl.AbsoluteUri, $"{ReleasesUrl}/download/{release.Tag}/SHA256SUMS.txt");
    }

    private static byte[] ReadExpectedHash(string manifest, string fileName)
    {
        string? found = null;
        foreach (var line in manifest.TrimStart('\uFEFF').Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            var match = ChecksumPattern().Match(trimmed);
            if (!match.Success) throw new InvalidDataException("SHA-256 校验清单格式不正确。");
            if (match.Groups[2].Value != fileName) continue;
            if (found is not null) throw new InvalidDataException("SHA-256 校验清单包含重复的安装文件。");
            found = match.Groups[1].Value;
        }
        if (found is null) throw new InvalidDataException("SHA-256 校验清单缺少当前安装文件。");
        return Convert.FromHexString(found);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool asset, CancellationToken cancellationToken,
        bool allowLatestManifest = false)
    {
        var original = uri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClickFlow", "3"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var actual = response.RequestMessage?.RequestUri ?? uri;
            if (!IsPermittedAddress(actual, original, asset, allowLatestManifest))
            {
                response.Dispose();
                throw new InvalidDataException("下载被重定向到非 GitHub 地址，已中止操作。");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("GitHub 下载重定向缺少地址。");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsPermittedAddress(next, original, asset, allowLatestManifest))
                    throw new InvalidDataException("下载被重定向到非 GitHub 地址，已中止操作。");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    throw new RateLimitedException();
                throw new IOException(status switch
                {
                    HttpStatusCode.NotFound => "尚未发布可用版本，或发布文件已被移除。",
                    _ => $"GitHub 请求失败（HTTP {(int)status}），请稍后重试。"
                });
            }
            return response;
        }
        throw new InvalidDataException("GitHub 下载重定向次数过多。");
    }

    private static bool IsPermittedAddress(Uri candidate, Uri original, bool asset, bool allowLatestManifest) =>
        candidate.Scheme == Uri.UriSchemeHttps && candidate.IsDefaultPort &&
        string.IsNullOrEmpty(candidate.UserInfo) && string.IsNullOrEmpty(candidate.Fragment) &&
        (candidate == original || (asset && candidate.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com") ||
        (allowLatestManifest && IsStableManifestAddress(candidate)));

    private static bool IsStableManifestAddress(Uri candidate)
    {
        const string prefix = ReleasesUrl + "/download/";
        const string suffix = "/update.json";
        var address = candidate.AbsoluteUri;
        if (!address.StartsWith(prefix, StringComparison.Ordinal) || !address.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var tag = address[prefix.Length..^suffix.Length];
        return StableVersionPattern().IsMatch(tag) && Version.TryParse(tag[1..], out _);
    }

    private sealed class RateLimitedException() : IOException("GitHub 暂时限制了访问频率，请稍后再试。");

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("更新信息超出允许大小。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("更新信息超出允许大小。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    [GeneratedRegex(@"\Av(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
    [GeneratedRegex(@"\A([a-fA-F0-9]{64}) [ *](.+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();
}
