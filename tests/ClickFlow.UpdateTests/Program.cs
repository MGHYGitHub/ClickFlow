using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClickFlow.Core.Updates;

var tests = new (string Name, Func<Task> Run)[]
{
    ("版本按数字比较：3.10.0 > 3.9.0", async () =>
    {
        using var service = Service(_ => Json(Release("v3.10.0")));
        Equal(new Version(3, 10, 0), (await service.CheckAsync(new Version(3, 9, 0)))!.Version);
    }),
    ("当前和更高版本不提示更新", async () =>
    {
        using var service = Service(_ => Json(Release()));
        Equal<UpdateRelease?>(null, await service.CheckAsync(new Version(3, 1, 0, 0)));
        Equal<UpdateRelease?>(null, await service.CheckAsync(new Version(4, 0, 0)));
    }),
    ("拒绝预发行及草稿", async () =>
    {
        using var draft = Service(_ => Json(Release(draft: true)));
        using var preview = Service(_ => Json(Release(prerelease: true)));
        await Throws<InvalidDataException>(() => draft.CheckAsync(new Version(3, 0, 0)));
        await Throws<InvalidDataException>(() => preview.CheckAsync(new Version(3, 0, 0)));
    }),
    ("拒绝非标准版本号", async () =>
    {
        foreach (var tag in new[] { "3.1.0", "v3.1", "v3.1.0-beta", "v3.1.0.0", "v03.1.0", "v9999999999999999.1.0" })
        {
            using var service = Service(_ => Json(Release(tag)));
            await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
        }
    }),
    ("拒绝不完整 JSON", async () =>
    {
        using var service = Service(_ => Json("{\"tag_name\":\"v3.1.0\"}"));
        await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
    }),
    ("拒绝外部资产 URL 和仿冒仓库 URL", async () =>
    {
        foreach (var url in new[] { "http://github.com/MGHYGitHub/ClickFlow/releases/download/v3.1.0/ClickFlow-3.1.0-win-x64.exe",
            "https://github.com.evil.test/a.exe", "https://github.com/Someone/ClickFlow/releases/download/v3.1.0/ClickFlow-3.1.0-win-x64.exe" })
        {
            using var service = Service(_ => Json(Release(downloadUrl: url)));
            await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
        }
    }),
    ("缺少校验资产时拒绝更新", async () =>
    {
        using var service = Service(_ => Json(Release(includeChecksum: false)));
        await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
    }),
    ("大小限制在下载前生效", async () =>
    {
        using var service = Service(_ => Json(Release(size: 513L * 1024 * 1024)));
        await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
    }),
    ("离线错误可理解", async () =>
    {
        using var service = Service(_ => throw new HttpRequestException("offline"));
        var error = await Throws<IOException>(() => service.CheckAsync(new Version(3, 0, 0)));
        Assert(error.Message.Contains("网络"), "缺少网络错误提示");
    }),
    ("限流错误可理解", async () =>
    {
        using var service = Service(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var error = await Throws<IOException>(() => service.CheckAsync(new Version(3, 0, 0)));
        Assert(error.Message.Contains("频率"), "缺少限流提示");
    }),
    ("API 403/429 自动回退至官方更新清单", async () =>
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            var requests = new List<string>();
            using var service = Service(request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                requests.Add(url);
                if (request.RequestUri.Host == "api.github.com") return new HttpResponseMessage(status);
                if (url == GitHubUpdateService.ReleasesUrl + "/latest/download/update.json")
                    return Redirect(GitHubUpdateService.ReleasesUrl + "/download/v3.1.0/update.json");
                if (request.RequestUri.Host == "github.com")
                    return Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/update?token=example");
                return Json(Release());
            });
            Equal(new Version(3, 1, 0), (await service.CheckAsync(new Version(3, 0, 0)))!.Version);
            Equal(4, requests.Count);
            Equal("https://api.github.com/repos/MGHYGitHub/ClickFlow/releases/latest", requests[0]);
            Equal<UpdateRelease?>(null, await service.CheckAsync(new Version(3, 1, 0)));
        }
    }),
    ("其他 API 错误不回退", async () =>
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var requests = 0;
            using var service = Service(_ => { requests++; return new HttpResponseMessage(status); });
            await Throws<IOException>(() => service.CheckAsync(new Version(3, 0, 0)));
            Equal(1, requests);
        }
    }),
    ("回退清单拒绝外部仓库、预览版和非清单重定向", async () =>
    {
        foreach (var url in new[] { "https://evil.test/update.json",
            "https://github.com/Someone/ClickFlow/releases/download/v3.1.0/update.json",
            GitHubUpdateService.ReleasesUrl + "/download/v3.1.0-beta/update.json",
            GitHubUpdateService.ReleasesUrl + "/download/v3.1.0/update.json?other=true",
            GitHubUpdateService.ReleasesUrl + "/download/v3.1.0/payload.exe" })
        {
            var requests = 0;
            using var service = Service(request =>
            {
                requests++;
                return request.RequestUri!.Host == "api.github.com"
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Redirect(url);
            });
            await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
            Equal(2, requests);
        }
    }),
    ("回退清单仍验证内容来源和大小", async () =>
    {
        foreach (var manifest in new[] { Release(downloadUrl: "https://evil.test/payload.exe"), new string(' ', 1024 * 1024 + 1) })
        {
            using var service = Service(request => request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(manifest));
            await Throws<InvalidDataException>(() => service.CheckAsync(new Version(3, 0, 0)));
        }
    }),
    ("合法下载校验并原子替换，报告进度", async () => await InDirectory(async directory =>
    {
        var data = Encoding.UTF8.GetBytes("test executable payload; never executed");
        using var service = DownloadService(data);
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        var target = Path.Combine(directory, update.FileName);
        await File.WriteAllTextAsync(target, "old file");
        var progress = new List<DownloadProgress>();
        Equal(target, await service.DownloadAsync(update, directory, new ImmediateProgress(p => progress.Add(p))));
        var downloaded = await File.ReadAllBytesAsync(target);
        Assert(data.SequenceEqual(downloaded), "下载内容不同");
        Equal(100d, progress[^1].Percentage);
        Equal(0, Directory.GetFiles(directory, "*.partial").Length);
    })),
    ("校验失败清理临时文件，保留旧文件", async () => await InDirectory(async directory =>
    {
        using var service = DownloadService([1, 2, 3], checksum: new string('0', 64));
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        var target = Path.Combine(directory, update.FileName);
        await File.WriteAllTextAsync(target, "keep me");
        await Throws<InvalidDataException>(() => service.DownloadAsync(update, directory));
        Equal("keep me", await File.ReadAllTextAsync(target));
        Equal(0, Directory.GetFiles(directory, "*.partial").Length);
    })),
    ("校验清单缺失、畸形和重复行均拒绝", async () => await InDirectory(async directory =>
    {
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));
        foreach (var manifest in new[] { hash + "  wrong.exe", "not a sha256sum", $"{hash}  ClickFlow-3.1.0-win-x64.exe\n{hash}  ClickFlow-3.1.0-win-x64.exe" })
        {
            using var service = DownloadService([1], manifest: manifest);
            var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
            await Throws<InvalidDataException>(() => service.DownloadAsync(update, directory));
            Equal(0, Directory.GetFiles(directory).Length);
        }
    })),
    ("取消下载清理所有临时文件", async () => await InDirectory(async directory =>
    {
        using var service = DownloadService(new byte[200000]);
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        using var cancel = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => service.DownloadAsync(update, directory,
            new ImmediateProgress(p => { if (p.BytesReceived > 0) cancel.Cancel(); }), cancel.Token));
        Equal(0, Directory.GetFiles(directory).Length);
    })),
    ("提前取消不请求网络", async () => await InDirectory(async directory =>
    {
        using var service = DownloadService([1]);
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => service.DownloadAsync(update, directory, cancellationToken: canceled.Token));
        Equal(0, Directory.GetFiles(directory).Length);
    })),
    ("允许 GitHub 资产 CDN 重定向", async () => await InDirectory(async directory =>
    {
        var data = new byte[] { 42, 18 };
        var hash = Convert.ToHexString(SHA256.HashData(data));
        using var service = Service(request => request.RequestUri!.Host switch
        {
            "api.github.com" => Json(Release(size: data.Length)),
            "release-assets.githubusercontent.com" => Bytes(data),
            _ when request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt") => Bytes(Encoding.UTF8.GetBytes($"{hash}  ClickFlow-3.1.0-win-x64.exe")),
            _ => Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/abc?token=value")
        });
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        var path = await service.DownloadAsync(update, directory);
        var downloaded = await File.ReadAllBytesAsync(path);
        Assert(data.SequenceEqual(downloaded), "CDN 下载失败");
    })),
    ("拒绝非 GitHub 和 HTTP 重定向", async () => await InDirectory(async directory =>
    {
        foreach (var url in new[] { "https://evil.test/file", "http://release-assets.githubusercontent.com/file" })
        {
            using var service = Service(request => request.RequestUri!.Host == "api.github.com" ? Json(Release()) : Redirect(url));
            var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
            await Throws<InvalidDataException>(() => service.DownloadAsync(update, directory));
        }
    })),
    ("拒绝伪造下载记录，文件名不能逃逸目录", async () => await InDirectory(async directory =>
    {
        using var service = DownloadService([1]);
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        await Throws<InvalidDataException>(() => service.DownloadAsync(update with { FileName = "../escape.exe" }, directory));
        Equal(0, Directory.GetFiles(directory).Length);
    })),
    ("下载截断拒绝并清理", async () => await InDirectory(async directory =>
    {
        using var service = DownloadService([1, 2], declaredSize: 3);
        var update = (await service.CheckAsync(new Version(3, 0, 0)))!;
        await Throws<InvalidDataException>(() => service.DownloadAsync(update, directory));
        Equal(0, Directory.GetFiles(directory).Length);
    })),
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} updater tests passed.");
return failures == 0 ? 0 : 1;

static GitHubUpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
    new(new HttpClient(new FakeHandler(responder)));

static GitHubUpdateService DownloadService(byte[] data, string? checksum = null, string? manifest = null, long? declaredSize = null) =>
    Service(request => request.RequestUri!.Host == "api.github.com"
        ? Json(Release(size: declaredSize ?? data.Length))
        : request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? Bytes(Encoding.UTF8.GetBytes(manifest ?? $"{checksum ?? Convert.ToHexString(SHA256.HashData(data))}  ClickFlow-3.1.0-win-x64.exe\n"))
            : Bytes(data));

static string Release(string tag = "v3.1.0", bool draft = false, bool prerelease = false,
    string? downloadUrl = null, bool includeChecksum = true, long size = 123)
{
    var filename = $"ClickFlow-{tag.TrimStart('v')}-win-x64.exe";
    var assets = new List<object> { new { name = filename, size,
        browser_download_url = downloadUrl ?? $"{GitHubUpdateService.ReleasesUrl}/download/{tag}/{filename}" } };
    if (includeChecksum) assets.Add(new { name = "SHA256SUMS.txt", size = 150,
        browser_download_url = $"{GitHubUpdateService.ReleasesUrl}/download/{tag}/SHA256SUMS.txt" });
    return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, name = "ClickFlow " + tag,
        body = "Release notes", html_url = $"{GitHubUpdateService.ReleasesUrl}/tag/{tag}", assets });
}

static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
static HttpResponseMessage Redirect(string location)
{
    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
    response.Headers.Location = new Uri(location);
    return response;
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task InDirectory(Func<string, Task> action)
{
    var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClickFlow.UpdateTests." + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    try { await action(directory); }
    finally
    {
        if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("ClickFlow.UpdateTests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        Directory.Delete(directory, recursive: true);
    }
}
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request));
    }
}
sealed class ImmediateProgress(Action<DownloadProgress> callback) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => callback(value);
}
