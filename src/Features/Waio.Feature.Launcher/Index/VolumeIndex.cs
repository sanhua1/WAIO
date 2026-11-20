using System.Text;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Index;

/// <summary>
/// 一个卷的文件索引(ADR-0024)。按 MFT 记录号存成一组数组(每个字段一个数组,同 MAIO / oil-find 的 SoA):
/// 记录号就是编号,父目录也存记录号,所以 USN 日志里的"文件号 / 父文件号"可以直接定位,目录改名也不用动子项。
/// 名称存在 <see cref="NameBlob"/> 里:折叠成小写的一份用来搜,原文大小写不同时另存一份用来显示,含汉字的另存拼音键。
/// <para>
/// 线程:构建在后台线程上单独进行,建好才交出去;之后搜索拿读锁(可以多个同时搜),USN 更新拿写锁(一批几毫秒)。
/// 本文件:存储和派生标志;<c>.Search.cs</c> 搜索;<c>.Update.cs</c> 增量更新;<c>.Persist.cs</c> 存盘 / 读盘。
/// </para>
/// </summary>
internal sealed partial class VolumeIndex
{
    public const int RootRecord = 5;
    private const int ExtendRecord = 11;
    private const int FirstUserRecord = 16;

    public const byte InUse = 1;
    public const byte Directory = 2;
    public const byte Hidden = 4;
    public const byte SystemFile = 8;

    /// <summary>NTFS 自己的元数据文件($MFT、$Extend 下的 $UsnJrnl 等),不出现在结果里。</summary>
    public const byte Meta = 16;

    /// <summary>在用户目录(%USERPROFILE%)下:排序加分。</summary>
    public const byte InHome = 32;

    /// <summary>在依赖 / 系统目录下(node_modules、.git、C:\Windows、回收站……):排序扣分(仍然索引,同 Everything)。</summary>
    public const byte Clutter = 64;

    private static readonly HashSet<string> ClutterNames = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", ".svn", ".hg", "__pycache__", ".vs", ".gradle", ".nuget", ".cache",
        "$recycle.bin", "system volume information", "winsxs", "package cache", "$windows.~bt", "$winreagent",
        "temp", "tmp", "cache", "caches", "obj", "inetcache", "crashdumps",
    };

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private ushort[] _seq;
    private int[] _parent;
    private byte[] _flags;
    private byte[] _depth;
    private long[] _size;
    private uint[] _mtime;
    private int[] _name;
    private ushort[] _nameLength;
    private int[] _original;
    private NameBlob _names;
    private NameBlob _originals;
    private NameBlob _alt;
    private readonly Dictionary<int, int> _altOf = [];
    private readonly string _homeFolded;

    public VolumeIndex(string root, uint serial, int capacity, string? home, bool forLoad = false)
    {
        Root = root.EndsWith('\\') ? root : root + "\\";
        Serial = serial;
        capacity = Math.Max(capacity, FirstUserRecord + 1);
        _seq = new ushort[capacity];
        _parent = new int[capacity];
        _flags = new byte[capacity];
        _depth = new byte[capacity];
        _size = new long[capacity];
        _mtime = new uint[capacity];
        _name = new int[capacity];
        _nameLength = new ushort[capacity];
        _original = new int[capacity];
        Array.Fill(_original, -1);
        Array.Fill(_name, -1);
        _names = new NameBlob(forLoad ? 16 : capacity * 16);
        _originals = new NameBlob(forLoad ? 16 : capacity * 4);
        _alt = new NameBlob(1 << 16);
        _homeFolded = home is null ? "" : home.TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>挂载路径,如 <c>C:</c>。</summary>
    public string Root { get; }

    public uint Serial { get; }

    public ulong JournalId { get; set; }

    /// <summary>下一次从哪个 USN 开始读日志。</summary>
    public long NextUsn { get; set; }

    /// <summary>最高的记录号 + 1。</summary>
    public int Count { get; private set; }

    /// <summary>在用、会出现在结果里的条目数(不含元数据文件)。</summary>
    public int Entries { get; private set; }

    /// <summary>上次完整扫描的时间。</summary>
    public DateTimeOffset ScannedAt { get; set; }

    /// <summary>有了还没存盘的变化。</summary>
    public bool Dirty { get; set; }

    /// <summary>大致的内存占用(数组 + 名称块)。</summary>
    public long MemoryBytes =>
        ((long)_seq.Length * (2 + 4 + 1 + 1 + 8 + 4 + 4 + 2 + 4)) + _names.Raw.Length + _originals.Raw.Length + _alt.Raw.Length
        + (_altOf.Count * 12L);

    // ---- 构建 / 更新共用 ----

    /// <summary>设置一条记录的基本信息(不含名称)。</summary>
    public void SetRecord(int record, ushort sequence, int parent, bool directory, uint attributes, long size, long lastWrite)
    {
        EnsureCapacity(record);
        var wasInUse = (_flags[record] & InUse) != 0;
        _seq[record] = sequence;
        _parent[record] = parent;
        _flags[record] = (byte)((_flags[record] & (Meta | InHome | Clutter)) | InUse | (directory ? Directory : 0)
            | ((attributes & 0x2) != 0 ? Hidden : 0) | ((attributes & 0x4) != 0 ? SystemFile : 0));
        _size[record] = directory ? 0 : size;
        _mtime[record] = ToUnixSeconds(lastWrite);
        if (!wasInUse && record >= Count)
        {
            Count = record + 1;
        }
    }

    /// <summary>换名称:旧名称变成死字节,新的追加到名称块末尾;含汉字时生成拼音键。</summary>
    public void SetName(int record, ReadOnlySpan<char> name)
    {
        EnsureCapacity(record);
        DropName(record);
        Span<char> folded = name.Length <= 512 ? stackalloc char[name.Length] : new char[name.Length];
        var differs = FoldedText.Fold(name, folded);
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(Math.Min(name.Length, 1024))];
        var length = Encoding.UTF8.GetBytes(folded, utf8);
        _name[record] = _names.Append(utf8[..length], record);
        _nameLength[record] = (ushort)length;
        if (differs)
        {
            var originalLength = Encoding.UTF8.GetBytes(name, utf8);
            _original[record] = _originals.Append(utf8[..originalLength], record);
        }

        if (Pinyin.ContainsHan(folded))
        {
            AddAltKeys(record, folded);
        }

        Count = Math.Max(Count, record + 1);
    }

    /// <summary>删除:清掉"在用",名称变成死字节。</summary>
    public void Remove(int record)
    {
        if (record < 0 || record >= _flags.Length || (_flags[record] & InUse) == 0)
        {
            return;
        }

        if (IsVisible(record))
        {
            Entries--;
        }

        _flags[record] = 0;
        DropName(record);
    }

    // ---- 构建时用(扩展记录可能比基本记录先读到,名称 / 大小先放进基本记录的位置) ----

    public bool HasName(int record) => record < _name.Length && _name[record] >= 0;

    public int ParentOf(int record) => record < _parent.Length ? _parent[record] : 0;

    public long PendingSize(int record) => record < _size.Length ? _size[record] : 0;

    public void SetParent(int record, int parent)
    {
        EnsureCapacity(record);
        _parent[record] = parent;
    }

    public void SetSize(int record, long size)
    {
        EnsureCapacity(record);
        _size[record] = (_flags[record] & Directory) != 0 ? 0 : size;
    }

    public bool IsInUse(int record) => record >= 0 && record < _flags.Length && (_flags[record] & InUse) != 0;

    public ushort SequenceOf(int record) => _seq[record];

    public byte FlagsOf(int record) => _flags[record];

    public long SizeOf(int record) => _size[record];

    public DateTimeOffset ModifiedOf(int record) => DateTimeOffset.FromUnixTimeSeconds(_mtime[record]);

    /// <summary>显示用的名称(原文大小写)。</summary>
    public string NameOf(int record)
    {
        if (_name[record] < 0)
        {
            return "";
        }

        if (_original[record] >= 0 && _originals.OwnerAt(_original[record], out var end) == record)
        {
            return Encoding.UTF8.GetString(_originals.At(_original[record], end - _original[record]));
        }

        return Encoding.UTF8.GetString(_names.At(_name[record], _nameLength[record]));
    }

    /// <summary>完整路径:沿父目录链拼起来。</summary>
    public string PathOf(int record)
    {
        var parts = new List<string>();
        var current = record;
        for (var guard = 0; current != RootRecord && guard < 256; guard++)
        {
            if (current < 0 || current >= Count || (_flags[current] & InUse) == 0)
            {
                return "";
            }

            parts.Add(NameOf(current));
            current = _parent[current];
        }

        parts.Reverse();
        return Root + string.Join('\\', parts);
    }

    /// <summary>
    /// 构建完以后算一遍派生标志和深度:元数据(记录号 &lt; 16,和 $Extend 下面的)、是否在用户目录 / 依赖目录下。
    /// 每条记录沿父目录链往上走到已经算过的那一层,所以整遍是线性的。
    /// </summary>
    public void Derive()
    {
        var state = new byte[Count]; // 0 未算,1 算过,2 正在路径上(用来发现成环的父目录链)
        var path = new List<int>();
        Entries = 0;
        for (var record = 0; record < Count; record++)
        {
            if ((_flags[record] & InUse) == 0 || state[record] != 0)
            {
                continue;
            }

            path.Clear();
            var current = record;
            while (current >= 0 && current < Count && state[current] == 0 && (_flags[current] & InUse) != 0
                && current != RootRecord && path.Count < 256)
            {
                state[current] = 2;
                path.Add(current);
                current = _parent[current];
            }

            // 从最上面一层往下算:每一层的父目录都已经算过(或是根、不在用、成环)
            for (var i = path.Count - 1; i >= 0; i--)
            {
                DeriveOne(path[i]);
                state[path[i]] = 1;
            }
        }

        for (var record = 0; record < Count; record++)
        {
            if (IsVisible(record))
            {
                Entries++;
            }
        }
    }

    /// <summary>按父目录算一条记录的深度和继承标志(父目录必须已经算过;更新时新增的条目也用它)。</summary>
    private void DeriveOne(int record)
    {
        var parent = _parent[record];
        var inherited = (byte)0;
        var depth = 1;
        if (parent != RootRecord && parent >= 0 && parent < Count)
        {
            inherited = (byte)(_flags[parent] & (Meta | InHome | Clutter));
            depth = Math.Min(255, _depth[parent] + 1);
        }

        var own = (byte)0;
        if ((record < FirstUserRecord && record != RootRecord) || record == ExtendRecord)
        {
            own |= Meta;
        }

        if (_name[record] >= 0)
        {
            var name = Encoding.UTF8.GetString(_names.At(_name[record], _nameLength[record]));
            if (ClutterNames.Contains(name) || (depth == 1 && name == "windows"))
            {
                own |= Clutter;
            }

            if (depth <= 3 && _homeFolded.Length > 0 && (inherited & InHome) == 0
                && _homeFolded.StartsWith(Root.ToLowerInvariant(), StringComparison.Ordinal)
                && PathOf(record).Equals(_homeFolded, StringComparison.OrdinalIgnoreCase))
            {
                own |= InHome;
            }
        }

        _flags[record] = (byte)((_flags[record] & (InUse | Directory | Hidden | SystemFile)) | inherited | own);
        _depth[record] = (byte)depth;
    }

    /// <summary>会出现在结果里:在用、有名称、不是元数据。</summary>
    private bool IsVisible(int record) =>
        record >= 0 && record < _flags.Length && record != RootRecord && (_flags[record] & (InUse | Meta)) == InUse && _name[record] >= 0;

    private void DropName(int record)
    {
        if (_name[record] >= 0)
        {
            _names.Dead += _nameLength[record] + 5;
            _name[record] = -1;
        }

        if (_original[record] >= 0)
        {
            _originals.Dead += _nameLength[record] + 5;
            _original[record] = -1;
        }

        if (_altOf.Remove(record))
        {
            _alt.Dead += 16;
        }
    }

    /// <summary>拼音键(同 MAIO 的 AltKeys):全拼连写、首字母,非汉字部分原样(已经是小写)。"年度报告.docx" → niandubaogao.docx、ndbg.docx。</summary>
    private void AddAltKeys(int record, ReadOnlySpan<char> folded)
    {
        var full = new StringBuilder(folded.Length * 3);
        var initials = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (Pinyin.IsHan(c) && Pinyin.Readings(c) is [var reading, ..])
            {
                full.Append(reading);
                initials.Append(reading[0]);
            }
            else
            {
                full.Append(c);
                initials.Append(c);
            }
        }

        _altOf[record] = _alt.Append(Encoding.UTF8.GetBytes(full.ToString()), record);
        if (initials.Length != full.Length)
        {
            _alt.Append(Encoding.UTF8.GetBytes(initials.ToString()), record);
        }
    }

    private void EnsureCapacity(int record)
    {
        if (record < _seq.Length)
        {
            return;
        }

        var size = Math.Max(record + 1, _seq.Length + (_seq.Length / 4));
        Array.Resize(ref _seq, size);
        Array.Resize(ref _parent, size);
        Array.Resize(ref _flags, size);
        Array.Resize(ref _depth, size);
        Array.Resize(ref _size, size);
        Array.Resize(ref _mtime, size);
        var old = _name.Length;
        Array.Resize(ref _name, size);
        Array.Resize(ref _nameLength, size);
        Array.Resize(ref _original, size);
        _name.AsSpan(old).Fill(-1);
        _original.AsSpan(old).Fill(-1);
    }

    /// <summary>FILETIME(1601 年起的 100 纳秒)→ Unix 秒;1970 年以前的算 0。</summary>
    internal static uint ToUnixSeconds(long fileTime)
    {
        const long UnixEpoch = 116444736000000000;
        return fileTime <= UnixEpoch ? 0 : (uint)Math.Min(uint.MaxValue, (fileTime - UnixEpoch) / 10_000_000);
    }
}
