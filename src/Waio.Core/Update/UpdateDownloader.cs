using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Waio.Core.Update;

/// <summary>下载并校验好的安装包。持有一个"拒绝他人写入 / 删除"的句柄:运行安装包之前文件不可能被替换。</summary>
public sealed class VerifiedInstaller(string path, FileStream guard) : IDisposable
{
    public string Path => path;

    public void Dispose() => guard.Dispose();
}

/// <summary>
/// 下载安装包到只有管理员能写的目录并校验 SHA-256(ADR-0031)。WAIO 以管理员身份运行安装包,
/// 所以下载目录、文件都不能让普通进程有机会替换:
/// <list type="bullet">
/// <item>目录 <c>%ProgramData%\WAIO\updates</c>(两级都处理):所有者设为 Administrators,
/// 只允许 Administrators 和 SYSTEM 访问,不继承上级权限;是联接 / 符号链接就拒绝。</item>
/// <item>边下载边算哈希;写完以"只读、拒绝写入 / 删除"重新打开再算一遍,句柄一直持有到安装包启动之后。</item>
/// </list>
/// </summary>
public sealed class UpdateDownloader(HttpClient http, ILogger logger)
{
    /// <summary>安装包大小上限(防止异常响应写满磁盘)。</summary>
    private const long MaxInstallerBytes = 1L << 30;

    public static string UpdatesDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WAIO", "updates");

    public async Task<VerifiedInstaller> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellation)
    {
        var expected = release.Sha256;
        if (expected is null && release.Sha256Url is { } sha256Url)
        {
            expected = ReleaseFeed.ParseSha256File(await http.GetStringAsync(sha256Url, cancellation).ConfigureAwait(false));
        }

        if (expected is null)
        {
            throw new InvalidDataException("发布里没有可用的 SHA-256,不安装");
        }

        var directory = EnsureSecureDirectory();
        foreach (var old in Directory.EnumerateFiles(directory))
        {
            TryDelete(old); // 上次留下的安装包和日志
        }

        var path = System.IO.Path.Combine(directory, release.InstallerName);
        using (var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("下载被重定向到了非 HTTPS 地址");
            }

            var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > MaxInstallerBytes)
                {
                    throw new InvalidDataException("安装包大小异常");
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                if (total > 0)
                {
                    progress?.Report(Math.Min(1, (double)written / total));
                }
            }

            if (!Matches(hash.GetHashAndReset(), expected))
            {
                file.Close();
                TryDelete(path);
                throw new InvalidDataException("安装包的 SHA-256 对不上,已删除");
            }
        }

        // 再以"拒绝写入 / 删除"打开并重新校验:从这里到安装包启动,文件内容不可能再变
        var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Matches(await SHA256.HashDataAsync(guard, cancellation).ConfigureAwait(false), expected))
            {
                throw new InvalidDataException("安装包在下载后被改动,已中止");
            }

            logger.LogInformation("更新:已下载并校验 {Name}({Bytes} 字节)", release.InstallerName, guard.Length);
            return new VerifiedInstaller(path, guard);
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    /// <summary>建好(或修正)两级目录的权限,返回 updates 目录。需要管理员权限。</summary>
    private static string EnsureSecureDirectory()
    {
        var updates = UpdatesDirectory;
        var parent = System.IO.Path.GetDirectoryName(updates)!;
        foreach (var directory in new[] { parent, updates })
        {
            var info = new DirectoryInfo(directory);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException($"{directory} 是联接或符号链接,拒绝使用");
            }

            if (!info.Exists)
            {
                info.Create(AdminsOnly());
            }
            else
            {
                info.SetAccessControl(AdminsOnly());
            }
        }

        return updates;
    }

    private static DirectorySecurity AdminsOnly()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool Matches(byte[] actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉(比如上次的安装包还在运行)不影响这次
        }
    }
}
