using System.Runtime.InteropServices;

namespace Waio.Feature.Launcher.Infrastructure;

/// <summary>
/// 按 vtable 槽位直接调用 Shell 的 COM 接口(只用到 IShellItem / IShellItem2 / IEnumShellItems / IShellItemImageFactory 的几个方法,
/// 做法同截图功能的 ComNative,不引入整套互操作程序集)。槽位都对照 Windows SDK 的 ShObjIdl_core.h 核对过;IUnknown 占 0~2。
/// 调用方必须在已经初始化 COM 的线程上(<see cref="StaWorker"/>)。
/// </summary>
internal static unsafe class ShellNative
{
    // IShellItem
    private const int BindToHandlerSlot = 3, GetDisplayNameSlot = 5;

    // IShellItem2(IShellItem 之后):GetProperty 13、GetCLSID 14、GetFileTime 15、GetInt32 16、GetString 17
    private const int GetStringSlot = 17;

    // IEnumShellItems
    private const int NextSlot = 3;

    // IShellItemImageFactory
    private const int GetImageSlot = 3;

    public const uint SigdnNormalDisplay = 0;
    public const uint SigdnParentRelativeParsing = 0x80018001;

    /// <summary>SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY:要图标不要缩略图,系统给的稍大一点也行。</summary>
    public const int ImageIconOnly = 0x1 | 0x4;

    public static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    public static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IidShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    public static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    public static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    /// <summary>System.Link.TargetParsingPath:快捷方式指向的文件。</summary>
    public static readonly PropertyKey LinkTargetParsingPath = new(new Guid("b9b4b3fc-2b51-4a42-b5d8-324146afcf25"), 2);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Size(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    private static void** Vtbl(nint obj) => *(void***)obj;

    public static void Release(ref nint obj)
    {
        if (obj != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(obj)[2])(obj);
            obj = 0;
        }
    }

    public static nint QueryInterface(nint obj, in Guid iid)
    {
        nint result = 0;
        fixed (Guid* id = &iid)
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(obj)[0])(obj, id, &result);
            return hr < 0 ? 0 : result;
        }
    }

    /// <summary>开始菜单的"所有应用"(shell:AppsFolder)。</summary>
    public static nint AppsFolder()
    {
        Check(SHGetKnownFolderItem(FolderIdAppsFolder, 0, 0, IidShellItem, out var item), "打开 shell:AppsFolder");
        return item;
    }

    /// <summary>按解析名找一项(例如 <c>shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>);找不到返回 0。</summary>
    public static nint ItemFromParsingName(string name) =>
        SHCreateItemFromParsingName(name, 0, IidShellItem, out var item) < 0 ? 0 : item;

    /// <summary>列出一个文件夹项下面的全部子项,逐个交给 <paramref name="visit"/>(用完自动释放)。</summary>
    public static void EnumerateChildren(nint folder, Action<nint> visit)
    {
        nint enumerator = 0;
        try
        {
            fixed (Guid* bhid = &BhidEnumItems)
            fixed (Guid* iid = &IidEnumShellItems)
            {
                var bind = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)Vtbl(folder)[BindToHandlerSlot];
                Check(bind(folder, 0, bhid, iid, &enumerator), "列出所有应用");
            }

            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Vtbl(enumerator)[NextSlot];
            while (true)
            {
                nint child = 0;
                uint fetched = 0;
                if (next(enumerator, 1, &child, &fetched) != 0 || fetched == 0)
                {
                    break; // S_FALSE = 没有更多了
                }

                try
                {
                    visit(child);
                }
                finally
                {
                    Release(ref child);
                }
            }
        }
        finally
        {
            Release(ref enumerator);
        }
    }

    public static string? DisplayName(nint item, uint sigdn)
    {
        nint text = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(item)[GetDisplayNameSlot])(item, sigdn, &text);
        return TakeString(hr, text);
    }

    /// <summary>IShellItem2::GetString;没有这个属性返回 null。</summary>
    public static string? StringProperty(nint item, in PropertyKey key)
    {
        var item2 = QueryInterface(item, IidShellItem2);
        if (item2 == 0)
        {
            return null;
        }

        try
        {
            nint text = 0;
            fixed (PropertyKey* k = &key)
            {
                var hr = ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, nint*, int>)Vtbl(item2)[GetStringSlot])(item2, k, &text);
                return TakeString(hr, text);
            }
        }
        finally
        {
            Release(ref item2);
        }
    }

    /// <summary>取图标位图(HBITMAP,调用方负责 DeleteObject);失败返回 0。</summary>
    public static nint IconBitmap(nint item, int size)
    {
        var factory = QueryInterface(item, IidShellItemImageFactory);
        if (factory == 0)
        {
            return 0;
        }

        try
        {
            nint bitmap = 0;
            var getImage = (delegate* unmanaged[Stdcall]<nint, Size, int, nint*, int>)Vtbl(factory)[GetImageSlot];
            return getImage(factory, new Size(size, size), ImageIconOnly, &bitmap) < 0 ? 0 : bitmap;
        }
        finally
        {
            Release(ref factory);
        }
    }

    private static string? TakeString(int hr, nint text)
    {
        if (hr < 0 || text == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            CoTaskMemFree(text);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"{what}失败(0x{hr:X8})", hr);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(in Guid folderId, uint flags, nint token, in Guid iid, out nint item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint pointer);
}
