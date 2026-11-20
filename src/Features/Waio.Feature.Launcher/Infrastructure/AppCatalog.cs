using System.Runtime.InteropServices;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Infrastructure;

/// <summary>
/// 应用目录:开始菜单"所有应用"(<c>shell:AppsFolder</c>)里的全部条目——传统程序的快捷方式和商店 / 打包应用都在这里,
/// 名称是系统按当前语言给的("计算器"),和开始菜单看到的一致(ADR-0023)。
/// 不自己扫开始菜单文件夹:那样拿不到商店应用,也拿不到本地化名称。
/// 必须在 <see cref="StaWorker"/> 上调用。
/// </summary>
internal static class AppCatalog
{
    public static List<AppEntry> Scan()
    {
        var apps = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = ShellNative.AppsFolder();
        try
        {
            ShellNative.EnumerateChildren(folder, item =>
            {
                var parsing = ShellNative.DisplayName(item, ShellNative.SigdnParentRelativeParsing);
                var name = ShellNative.DisplayName(item, ShellNative.SigdnNormalDisplay);
                if (string.IsNullOrWhiteSpace(parsing) || string.IsNullOrWhiteSpace(name) || parsing.Contains('"')
                    || !seen.Add(parsing))
                {
                    return;
                }

                var target = ShellNative.StringProperty(item, ShellNative.LinkTargetParsingPath);
                apps.Add(new AppEntry(parsing, name.Trim(), IsLocalFile(target) ? target : null));
            });
        }
        finally
        {
            ShellNative.Release(ref folder);
        }

        return apps;
    }

    /// <summary>"卸载 xxx""Uninstall xxx"、unins000.exe 这类条目(设置里可以选择不显示)。</summary>
    public static bool IsUninstaller(AppEntry entry)
    {
        if (entry.DisplayName.Contains("卸载", StringComparison.Ordinal)
            || entry.DisplayName.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var file = entry.TargetPath is { } path ? Path.GetFileName(path) : "";
        return file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("uninst", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只认本机磁盘上的完整路径(快捷方式也可能指向网址、Shell 位置)。</summary>
    private static bool IsLocalFile(string? path) =>
        path is { Length: > 3 } && path[1] == ':' && path[2] == '\\' && !path.Contains('"');
}

/// <summary>图标:Shell 给出的位图 → 预乘透明度的 BGRA 像素(WinUI 的 WriteableBitmap 要这种格式)。</summary>
internal static class AppIcons
{
    /// <summary>取图标;<paramref name="parsingName"/> 是 Shell 解析名(<c>shell:AppsFolder…</c> 或文件路径)。拿不到返回 null。必须在 <see cref="StaWorker"/> 上调用。</summary>
    public static IconPixels? Load(string parsingName, int size)
    {
        var item = ShellNative.ItemFromParsingName(parsingName);
        if (item == 0)
        {
            return null;
        }

        nint bitmap = 0;
        try
        {
            bitmap = ShellNative.IconBitmap(item, size);
            return bitmap == 0 ? null : Read(bitmap);
        }
        finally
        {
            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }

            ShellNative.Release(ref item);
        }
    }

    private static IconPixels? Read(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BitmapHeader>(), out var header) == 0 || header.Width <= 0 || header.Height <= 0)
        {
            return null;
        }

        var width = header.Width;
        var height = Math.Abs(header.Height);
        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = CreateCompatibleDC(0);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            _ = DeleteDC(dc);
        }

        FixAlpha(pixels);
        return new IconPixels(width, height, pixels);
    }

    /// <summary>
    /// 没有透明通道的位图(alpha 全是 0)改成不透明;没有预乘过的(有颜色分量大于 alpha)按 alpha 预乘。
    /// Shell 在不同系统版本、不同图标来源下两种都可能给出。
    /// </summary>
    internal static void FixAlpha(byte[] bgra)
    {
        var anyAlpha = false;
        var straight = false;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            anyAlpha |= a != 0;
            straight |= bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a;
        }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (!anyAlpha)
            {
                bgra[i + 3] = 255;
            }
            else if (straight)
            {
                var a = bgra[i + 3];
                bgra[i] = (byte)(bgra[i] * a / 255);
                bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
                bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(nint obj, int size, out BitmapHeader header);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern int DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(nint obj);
}

internal sealed record IconPixels(int Width, int Height, byte[] Bgra);
