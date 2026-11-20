namespace Waio.UI.Theming;

/// <summary>
/// "短暂地把 WAIO 窗口排除在截屏之外"(ADR-0027 修订):
/// 平时 WAIO 窗口和普通窗口一样能被任何截图 / 录屏工具截到(截图时设置页不能消失);
/// 只有玻璃取背景的那一下(几十毫秒)才设 <c>WDA_EXCLUDEFROMCAPTURE</c>,取完立即恢复。
/// WAIO 自己截图 / 长截图 / 录屏期间调 <see cref="Glass.SuspendBackgroundRefresh"/>,保证这期间不排除。
/// 可在任意线程调用(截图在后台线程读屏)。
/// </summary>
internal static class GlassExclusion
{
    private static readonly object Gate = new();

    /// <summary>本次由我们设成排除的窗口 → 原来的显示亲和性(原本就排除的窗口,如录屏控制条,不动)。</summary>
    private static readonly Dictionary<nint, uint> Changed = [];

    private static int _suspended;

    /// <summary>开始一次取背景:把这些窗口排除在截屏外。暂停中返回 false。</summary>
    public static bool Begin(IEnumerable<nint> windows)
    {
        lock (Gate)
        {
            if (_suspended > 0)
            {
                return false;
            }

            foreach (var hwnd in windows)
            {
                // 只动可见的窗口:给隐藏的窗口设显示亲和性,DWM 会把它的系统描边重新画出来,
                // 屏幕上留下一个空的圆角框(本机复现:隐藏的启动台每 5 秒"冒"出来一次)
                if (Changed.ContainsKey(hwnd) || !GlassNative.IsWindowVisible(hwnd) || !GlassNative.GetWindowDisplayAffinity(hwnd, out var original)
                    || original == GlassNative.WdaExcludeFromCapture)
                {
                    continue;
                }

                if (GlassNative.SetWindowDisplayAffinity(hwnd, GlassNative.WdaExcludeFromCapture))
                {
                    Changed[hwnd] = original;
                }
            }

            return true;
        }
    }

    /// <summary>取完背景:恢复原来的显示亲和性。</summary>
    public static void End()
    {
        lock (Gate)
        {
            RestoreLocked();
        }
    }

    /// <summary>暂停取背景(WAIO 自己截图 / 录屏期间)。若正在取背景,立即恢复并等 DWM 合成一帧,保证随后的截屏里有 WAIO 窗口。</summary>
    public static IDisposable Suspend()
    {
        bool restored;
        lock (Gate)
        {
            _suspended++;
            restored = Changed.Count > 0;
            RestoreLocked();
        }

        if (restored)
        {
            _ = GlassNative.DwmFlush();
        }

        return new Resume();
    }

    public static bool IsSuspended
    {
        get
        {
            lock (Gate)
            {
                return _suspended > 0;
            }
        }
    }

    private static void RestoreLocked()
    {
        foreach (var (hwnd, original) in Changed)
        {
            _ = GlassNative.SetWindowDisplayAffinity(hwnd, original);
        }

        Changed.Clear();
    }

    private sealed class Resume : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (Gate)
                {
                    _suspended--;
                }
            }
        }
    }
}
