using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Update;

namespace Waio.Core.Tests;

/// <summary>检查更新(ADR-0031):解析 GitHub 发布、只认可校验的安装包、版本比较。</summary>
public class UpdateFeedTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(
        string tag = "v0.2.0", bool prerelease = false, string? digest = "sha256:" + Hash, bool sha256Asset = false,
        string host = "github.com")
    {
        var name = $"WAIO-Setup-{tag.TrimStart('v')}.exe";
        var digestJson = digest is null ? "" : $"\"digest\": \"{digest}\",";
        var extra = sha256Asset
            ? $$""", { "name": "{{name}}.sha256", "size": 90, "browser_download_url": "https://github.com/sanhua1/WAIO/releases/download/{{tag}}/{{name}}.sha256" }"""
            : "";
        return $$"""
            {
              "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://github.com/sanhua1/WAIO/releases/tag/{{tag}}",
              "published_at": "2026-11-20T08:00:00Z", "body": "修了一些问题",
              "assets": [
                { "name": "{{name}}", "size": 85000000, {{digestJson}}
                  "browser_download_url": "https://{{host}}/sanhua1/WAIO/releases/download/{{tag}}/{{name}}" }{{extra}}
              ]
            }
            """;
    }

    [Fact]
    public void 解析发布_取安装包地址和GitHub给的SHA256()
    {
        var release = ReleaseFeed.Parse(Release())!;

        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("WAIO-Setup-0.2.0.exe", release.InstallerName);
        Assert.Equal(Hash, release.Sha256);
        Assert.Null(release.Sha256Url);
        Assert.StartsWith("https://github.com/", release.InstallerUrl);
        Assert.Equal("修了一些问题", release.Notes);
    }

    [Fact]
    public void 没有digest时_用同名sha256资产()
    {
        var release = ReleaseFeed.Parse(Release(digest: null, sha256Asset: true))!;

        Assert.Null(release.Sha256);
        Assert.EndsWith("WAIO-Setup-0.2.0.exe.sha256", release.Sha256Url);
    }

    [Fact]
    public void 无法校验_预发布_非GitHub地址_都不接受()
    {
        Assert.Null(ReleaseFeed.Parse(Release(digest: null)));
        Assert.Null(ReleaseFeed.Parse(Release(prerelease: true)));
        Assert.Null(ReleaseFeed.Parse(Release(host: "evil.example.com")));
        Assert.Null(ReleaseFeed.Parse(Release(digest: "md5:abc")));
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.1", "0.2.1")]
    [InlineData("V1.10.3", "1.10.3")]
    public void 标签解析(string tag, string expected) => Assert.Equal(Version.Parse(expected), ReleaseFeed.ParseTag(tag));

    [Theory]
    [InlineData("v1.0.0-beta")]
    [InlineData("latest")]
    [InlineData("")]
    public void 预发布和乱写的标签不算版本(string tag) => Assert.Null(ReleaseFeed.ParseTag(tag));

    [Fact]
    public void sha256文件_取第一段()
    {
        Assert.Equal(Hash, ReleaseFeed.ParseSha256File(Hash.ToUpperInvariant() + "  WAIO-Setup-0.2.0.exe\n"));
        Assert.Null(ReleaseFeed.ParseSha256File("not-a-hash"));
    }

    [Fact]
    public void 版本显示_省略第四段() => Assert.Equal("0.1.0", ReleaseFeed.Format(new Version(0, 1, 0, 0)));

    [Fact]
    public async Task 检查_新版本是Available_同版本是UpToDate_没有发布是NoRelease()
    {
        using var service = Service(HttpStatusCode.OK, Release());

        Assert.Equal(UpdateStatus.Available, (await service.CheckAsync(new Version(0, 1, 0), default)).Status);
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync(new Version(0, 2, 0), default)).Status);

        using var empty = Service(HttpStatusCode.NotFound, "{}");
        Assert.Equal(UpdateStatus.NoRelease, (await empty.CheckAsync(new Version(0, 1, 0), default)).Status);

        using var limited = Service(HttpStatusCode.Forbidden, "{}");
        Assert.Equal(UpdateStatus.Failed, (await limited.CheckAsync(new Version(0, 1, 0), default)).Status);
    }

    private static UpdateService Service(HttpStatusCode status, string body) =>
        new(NullLogger.Instance, ReleaseFeed.DefaultRepository, new FixedHandler(status, body));

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
