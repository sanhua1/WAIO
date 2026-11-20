using System.Globalization;
using System.Text.Json;

namespace Waio.Core.Update;

/// <summary>GitHub 上的一个发布版本(只取更新需要的字段)。</summary>
/// <param name="Version">版本号(标签去掉开头的 v)。</param>
/// <param name="Notes">发布说明(Markdown 原文)。</param>
/// <param name="PageUrl">发布页面,给"查看更新说明"用。</param>
/// <param name="InstallerUrl">安装包下载地址(<c>WAIO-Setup-&lt;版本&gt;.exe</c>)。</param>
/// <param name="InstallerName">安装包文件名。</param>
/// <param name="InstallerSize">安装包字节数。</param>
/// <param name="Sha256">安装包的 SHA-256(小写十六进制);取自 GitHub 给资产算的 digest,没有时取同名 <c>.sha256</c> 资产。</param>
/// <param name="Sha256Url">没有 digest 时,<c>.sha256</c> 资产的下载地址(<see cref="Sha256"/> 为 null)。</param>
public sealed record ReleaseInfo(
    Version Version, string Tag, string Notes, string PageUrl, DateTimeOffset? PublishedAt,
    string InstallerUrl, string InstallerName, long InstallerSize, string? Sha256, string? Sha256Url);

/// <summary>
/// 解析 GitHub 的 <c>GET /repos/{owner}/{repo}/releases/latest</c>(ADR-0031)。纯逻辑,可单测。
/// 只认 HTTPS 的 github.com 下载地址;格式不对一律当"没有可用的更新包"。
/// </summary>
public static class ReleaseFeed
{
    /// <summary>官方仓库。自己 fork 发布时改这里。</summary>
    public const string DefaultRepository = "sanhua1/WAIO";

    public static Uri LatestReleaseApi(string repository) => new($"https://api.github.com/repos/{repository}/releases/latest");

    public static string InstallerNameFor(Version version) => $"WAIO-Setup-{Format(version)}.exe";

    /// <summary>显示用的版本号:0.1.0(第四段为 0 时省略)。</summary>
    public static string Format(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(version.Build >= 0 ? 3 : 2);

    /// <summary>标签 → 版本:接受 <c>v0.2.0</c> / <c>0.2.0</c>,预发布后缀(<c>-beta</c>)不接受。</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        return Version.TryParse(text, out var version) && version.Major >= 0 && text.All(c => char.IsAsciiDigit(c) || c == '.')
            ? version
            : null;
    }

    /// <summary>解析 releases/latest 的 JSON;草稿、预发布、没有安装包资产的返回 null。</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Bool(root, "draft") || Bool(root, "prerelease"))
        {
            return null;
        }

        var tag = Str(root, "tag_name");
        if (ParseTag(tag) is not { } version || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var installerName = InstallerNameFor(version);
        JsonElement? installer = null;
        string? sha256Url = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name");
            if (string.Equals(name, installerName, StringComparison.OrdinalIgnoreCase))
            {
                installer = asset;
            }
            else if (string.Equals(name, installerName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                sha256Url = TrustedUrl(Str(asset, "browser_download_url"));
            }
        }

        if (installer is not { } found || TrustedUrl(Str(found, "browser_download_url")) is not { } url)
        {
            return null;
        }

        var digest = Str(found, "digest");
        var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? NormalizeSha256(digest["sha256:".Length..])
            : null;
        if (sha256 is null && sha256Url is null)
        {
            return null; // 没有任何办法校验,不接受
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(
            Str(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        var size = found.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
        return new ReleaseInfo(
            version, tag!, Str(root, "body") ?? "", TrustedUrl(Str(root, "html_url")) ?? "", published,
            url, installerName, size, sha256, sha256 is null ? sha256Url : null);
    }

    /// <summary><c>.sha256</c> 文件内容("哈希" 或 "哈希  文件名")→ 小写哈希;格式不对返回 null。</summary>
    public static string? ParseSha256File(string text) =>
        NormalizeSha256(text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");

    public static string? NormalizeSha256(string text)
    {
        var hash = text.Trim().ToLowerInvariant();
        return hash.Length == 64 && hash.All(char.IsAsciiHexDigit) ? hash : null;
    }

    /// <summary>只接受 HTTPS 的 github.com(发布页、资产下载;下载会再跳到 GitHub 自己的存储域名)。</summary>
    public static string? TrustedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
