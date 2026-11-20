using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Index;
using Waio.Feature.Launcher.Infrastructure.Ntfs;

namespace Waio.Feature.Launcher.Tests;

/// <summary>卷索引:搜索、拼音、过滤、USN 增量更新、存盘读盘。用手工搭的小目录树(记录号同 NTFS:5 是根,0~15 是元数据)。</summary>
public class IndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Recent = Now.AddHours(-1).ToFileTime();
    private static readonly long Old = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime();

    private static VolumeIndex Tree()
    {
        var index = new VolumeIndex(@"C:\", 0xABCD, 32, @"C:\Users\user01");
        void Add(int record, int parent, string name, bool dir = false, uint attributes = 0x20, long size = 10, long? time = null)
        {
            index.SetRecord(record, 1, parent, dir, attributes, size, time ?? Old);
            index.SetName(record, name);
        }

        Add(5, 5, ".", dir: true);
        Add(0, 5, "$MFT");
        Add(11, 5, "$Extend", dir: true);
        Add(16, 5, "Users", dir: true);
        Add(17, 16, "user01", dir: true);
        Add(18, 17, "Documents", dir: true);
        Add(20, 18, "年度报告.docx", size: 100, time: Recent);
        Add(21, 18, "report.docx");
        Add(22, 5, "Windows", dir: true);
        Add(23, 22, "notepad.exe");
        Add(24, 18, "node_modules", dir: true);
        Add(25, 24, "report.js");
        Add(26, 18, "report.hidden.txt", attributes: 0x22);
        Add(27, 11, "$UsnJrnl");
        Add(28, 5, "report.docx");
        index.Derive();
        return index;
    }

    private static List<string> Find(VolumeIndex index, string text, bool foldersOnly = false, FileFilter? filter = null) =>
        index.Search(FileQuery.Parse(text, foldersOnly)!, 20, filter ?? FileFilter.None, Now).Hits.Select(h => index.PathOf(h.Record)).ToList();

    [Fact]
    public void 路径_沿父目录拼出来_原文大小写()
    {
        var index = Tree();

        Assert.Equal(@"C:\Users\user01\Documents\年度报告.docx", index.PathOf(20));
        Assert.Equal("Documents", index.NameOf(18));
        Assert.Equal(11, index.Entries); // 不含根和元数据($MFT、$Extend 和它下面的 $UsnJrnl)
    }

    [Fact]
    public void 搜索_不分大小写_用户目录里的排前面_依赖目录排后面()
    {
        var found = Find(Tree(), "REPORT");

        Assert.Equal(@"C:\Users\user01\Documents\report.docx", found[0]); // 用户目录 +60,压过路径更浅的 C:\report.docx
        Assert.Equal(4, found.Count);
        var dependency = found.FindIndex(p => p.Contains("node_modules"));
        var plain = found.FindIndex(p => p.EndsWith("report.docx") && !p.Contains("Users"));
        Assert.True(dependency > plain, "依赖目录 −350 应排在普通位置后面");
    }

    [Fact]
    public void 搜索_多个词都要出现()
    {
        Assert.Equal([@"C:\Users\user01\Documents\report.docx", @"C:\report.docx"], Find(Tree(), "docx rep"));
    }

    [Fact]
    public void 搜索_汉字和拼音全拼首字母()
    {
        var index = Tree();
        var expected = @"C:\Users\user01\Documents\年度报告.docx";

        Assert.Equal([expected], Find(index, "报告"));
        Assert.Equal([expected], Find(index, "ndbg"));
        Assert.Equal([expected], Find(index, "niandubaogao"));
    }

    [Fact]
    public void 元数据文件不出现()
    {
        var index = Tree();

        Assert.Empty(Find(index, "$mft"));
        Assert.Empty(Find(index, "usnjrnl"));
    }

    [Fact]
    public void 只要文件夹()
    {
        Assert.Equal([@"C:\Users\user01\Documents"], Find(Tree(), "doc", foldersOnly: true));
    }

    [Fact]
    public void 过滤_隐藏文件和排除的文件夹_默认都不过滤()
    {
        var index = Tree();

        Assert.Contains(Find(index, "hidden"), p => p.EndsWith("report.hidden.txt"));
        Assert.Empty(Find(index, "hidden", filter: new FileFilter(true, false, new HashSet<int>())));
        Assert.DoesNotContain(Find(index, "report", filter: new FileFilter(false, false, new HashSet<int> { index.FindFolder(@"C:\Users\user01\Documents\node_modules") })), p => p.Contains("node_modules"));
    }

    [Fact]
    public void 按路径找文件夹()
    {
        var index = Tree();

        Assert.Equal(18, index.FindFolder(@"C:\users\USER01\Documents\"));
        Assert.Equal(-1, index.FindFolder(@"C:\Users\nobody"));
        Assert.Equal(VolumeIndex.RootRecord, index.FindFolder(@"C:\"));
    }

    [Fact]
    public void 日志_新建_改名_删除_移动文件夹()
    {
        var index = Tree();
        var details = new Dictionary<long, FileDetails> { [Ref(30, 1)] = new(4096, Recent, 0x20) };
        UsnChange Change(int record, int parent, string name, uint reason, uint attributes = 0x20) =>
            new(Ref(record, 1), Ref(parent, 1), 0, reason, attributes, name);

        index.Apply(
        [
            Change(30, 18, "新建文本.txt", UsnChange.FileCreate),
            Change(30, 18, "新建文本.txt", UsnChange.DataExtend | UsnChange.Close),
            Change(21, 18, "report.docx", UsnChange.RenameOldName),
            Change(21, 18, "summary.docx", UsnChange.RenameNewName),
            Change(20, 18, "年度报告.docx", UsnChange.FileDelete | UsnChange.Close),
            Change(24, 17, "node_modules", UsnChange.RenameNewName, attributes: 0x10),
        ], reference => details.TryGetValue(reference, out var d) ? d : null);

        Assert.Equal([@"C:\Users\user01\Documents\新建文本.txt"], Find(index, "xjwb"));
        Assert.Equal(4096, index.SizeOf(30));
        Assert.Equal([@"C:\Users\user01\Documents\summary.docx"], Find(index, "summary"));
        Assert.Empty(Find(index, "报告"));
        Assert.Equal(@"C:\Users\user01\node_modules\report.js", index.PathOf(25)); // 子项跟着走,不用逐个更新
        Assert.Equal(11, index.Entries); // +1 新建 −1 删除
        Assert.True(index.Dirty);
    }

    [Fact]
    public void 日志_序列号不同的删除不算_记录号已经给了别的文件()
    {
        var index = Tree();

        index.Apply([new UsnChange(Ref(21, 9), Ref(18, 1), 0, UsnChange.FileDelete, 0x20, "report.docx")], _ => null);

        Assert.True(index.IsInUse(21));
    }

    [Fact]
    public void 存盘读盘_原样回来_卷不对就不认()
    {
        var index = Tree();
        index.JournalId = 42;
        index.NextUsn = 1234;
        for (var i = 0; i < 50; i++)
        {
            index.SetName(23, $"notepad{i}.exe"); // 制造死字节,存盘时会压缩
        }

        var file = Path.Combine(Path.GetTempPath(), $"waio-index-{Guid.NewGuid():N}.bin");
        try
        {
            index.Save(file);
            var loaded = VolumeIndex.Load(file, @"C:\", 0xABCD, @"C:\Users\user01");

            Assert.NotNull(loaded);
            Assert.Equal(42ul, loaded.JournalId);
            Assert.Equal(1234, loaded.NextUsn);
            Assert.Equal(Find(index, "report"), Find(loaded, "report"));
            Assert.Equal(Find(index, "ndbg"), Find(loaded, "ndbg"));
            Assert.Equal("notepad49.exe", loaded.NameOf(23));
            Assert.Null(VolumeIndex.Load(file, @"C:\", 0x9999, null));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void 读盘后_日志里新建记录号更大的文件_能加进来()
    {
        // 读盘出来的数组正好是 Count 大小,没有余量(2026-11-11 真机上这里越界,日志位置卡住不前进)
        var file = Path.Combine(Path.GetTempPath(), $"waio-index-{Guid.NewGuid():N}.bin");
        var root = Tree().Root;
        try
        {
            Tree().Save(file);
            var loaded = VolumeIndex.Load(file, root, 0xABCD, null)!;
            var record = loaded.Count + 100;

            loaded.Apply([new UsnChange(Ref(record, 1), Ref(18, 1), 0, UsnChange.FileCreate, 0x20, "迟到的文件.txt")], _ => new FileDetails(7, Recent, 0x20));

            var hit = Assert.Single(loaded.Search(FileQuery.Parse("cddwj", false)!, 5, FileFilter.None, Now).Hits);
            Assert.Equal(record, hit.Record);
            Assert.EndsWith(Path.Combine("Documents", "迟到的文件.txt"), loaded.PathOf(record));
            Assert.Equal(12, loaded.Entries);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void 匹配质量_同名大于开头大于词边界大于包含()
    {
        static int Q(string name, string term) => NameMatch.Quality(FoldedText.Encode(name), FoldedText.Encode(term));

        Assert.Equal(4, Q("Report.docx", "report"));
        Assert.Equal(3, Q("reports.docx", "report"));
        Assert.Equal(2, Q("my-report.docx", "report"));
        Assert.Equal(1, Q("myreport.docx", "report"));
        Assert.Equal(0, Q("other.txt", "report"));
    }

    [Fact]
    public void 状态说明()
    {
        static VolumeStatus S(VolumeState state, long read = 0, long total = 0) =>
            new(@"C:\", "NTFS", state, 0, 0, null, read, total, null);

        Assert.Null(IndexStatusReport.Summary([S(VolumeState.Ready)]));
        Assert.Equal("正在建立文件索引:C: 25%", IndexStatusReport.Summary([S(VolumeState.Ready), S(VolumeState.Scanning, 25, 100)]));
        Assert.Equal("文件搜索需要管理员权限", IndexStatusReport.Summary([S(VolumeState.NeedsAdmin)]));
        Assert.Equal("没有可以索引的磁盘(只支持 NTFS 格式)", IndexStatusReport.Summary([]));
    }

    [Fact]
    public void 路径词_文件夹名加反斜杠_限定在该文件夹下_其他词仍只比名称()
    {
        var found = Find(Tree(), @"documents\ report");

        Assert.Equal(3, found.Count);
        Assert.All(found, p => Assert.StartsWith(@"C:\Users\user01\Documents\", p));
        Assert.DoesNotContain(@"C:\report.docx", found);
    }

    [Fact]
    public void 路径词_只有路径词时_列出路径里含它的全部条目_文件夹本身不带结尾反斜杠()
    {
        var index = Tree();

        Assert.Equal([@"C:\Windows\notepad.exe"], Find(index, @"c:\windows\"));
        var users = Find(index, @"\users");
        Assert.Contains(@"C:\Users", users);
        Assert.Contains(@"C:\Users\user01\Documents\年度报告.docx", users);
        Assert.DoesNotContain(@"C:\Windows\notepad.exe", users);
        Assert.Empty(Find(index, "d:"));
        Assert.Equal(11, Find(index, "c:").Count); // 整个卷(不含根和元数据)
    }

    [Fact]
    public void 路径词_斜杠当反斜杠_可以跨到名称开头()
    {
        var found = Find(Tree(), "c:/users/user01/documents/rep");

        Assert.Equal(2, found.Count); // report.docx、report.hidden.txt;node_modules 下的 report.js 不算
        Assert.DoesNotContain(found, p => p.Contains("node_modules"));
    }

    [Fact]
    public void 双引号括起来算一个词_可以含空格()
    {
        var query = FileQuery.Parse("\"c:\\program files\\\" setup", false)!;

        Assert.Equal(["c:\\program files\\"], query.PathTerms.Select(t => System.Text.Encoding.UTF8.GetString(t)));
        Assert.Equal(["setup"], query.Terms.Select(t => System.Text.Encoding.UTF8.GetString(t)));
        Assert.Equal([@"C:\Users\user01\Documents\年度报告.docx"], Find(Tree(), "\"user01\\documents\\年度\""));
    }

    [Fact]
    public void 路径词_和拼音一起用()
    {
        Assert.Equal([@"C:\Users\user01\Documents\年度报告.docx"], Find(Tree(), @"documents\ ndbg"));
        Assert.Empty(Find(Tree(), @"windows\ ndbg"));
    }

    private static long Ref(int record, ushort sequence) => ((long)sequence << 48) | (uint)record;
}
