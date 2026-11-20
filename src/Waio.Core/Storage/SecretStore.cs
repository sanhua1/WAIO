using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waio.Kit.Services;

namespace Waio.Core.Storage;

/// <summary>
/// 功能密钥(ADR-0014):每个值用 DPAPI(<c>CryptProtectData</c>,当前用户范围)加密后存进
/// <c>%LOCALAPPDATA%\WAIO\data\&lt;featureId&gt;\secrets.json</c>(名字 → Base64 密文)。
/// 只有同一 Windows 用户能解开:文件被拷到别的机器/别的用户下也读不出明文。设置文件里永远不出现密钥。
/// 对应 MAIO 的钥匙串存储(MAIO ADR-0009)。
/// </summary>
public sealed class SecretStore : ISecretStore
{
    private static readonly byte[] Entropy = "WAIO.secrets.v1"u8.ToArray(); // 附加熵:别的程序用同一 DPAPI 调用也解不开

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cipher;

    public SecretStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public string? Get(string name)
    {
        string? encoded;
        lock (_gate)
        {
            Load().TryGetValue(name, out encoded);
        }

        if (encoded is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(encoded)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            _logger.LogWarning("密钥 {Name} 无法解密(可能换了 Windows 用户),当作没有设置", name);
            return null;
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var all = Load();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!all.Remove(name))
                {
                    return;
                }
            }
            else
            {
                all[name] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value.Trim())));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cipher is not null)
        {
            return _cipher;
        }

        try
        {
            _cipher = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "密钥文件 {Path} 读取失败,当作空的", _path);
            _cipher = [];
        }

        return _cipher;
    }

    internal static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    internal static byte[] Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var data = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref data, null, ref entropy, 0, 0, UiForbidden, out var output)
                : CryptUnprotectData(ref data, 0, ref entropy, 0, 0, UiForbidden, out output);
            if (!ok)
            {
                throw new InvalidOperationException($"DPAPI 失败,Win32 错误码 {Marshal.GetLastWin32Error()}");
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob data, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob data, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
