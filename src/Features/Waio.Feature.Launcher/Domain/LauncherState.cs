namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 面板的状态:输入、来源标签、结果、选中项(对应 MAIO 的 LauncherViewModel,去掉了界面部分)。
/// 纯逻辑,只在 UI 线程上使用。每次结果变化 <see cref="Version"/> 加一,视图据此决定要不要重建列表。
/// </summary>
internal sealed class LauncherState
{
    private IReadOnlyList<LauncherItem> _apps = [];

    public string Query { get; private set; } = "";

    /// <summary>标签选中的来源(输入了前缀时,实际来源以前缀为准,见 <see cref="Parsed"/>)。</summary>
    public LauncherSource SelectedSource { get; private set; } = LauncherSource.All;

    public LauncherQuery Parsed { get; private set; } = new(LauncherSource.All, "", null);

    public IReadOnlyList<LauncherSection> Sections { get; private set; } = [];

    /// <summary>所有分组的条目按显示顺序排成一列(上下键、Ctrl+数字用)。</summary>
    public IReadOnlyList<LauncherItem> Items { get; private set; } = [];

    public int SelectedIndex { get; private set; }

    public int Version { get; private set; }

    public UsageBook Usage { get; set; } = new();

    public LauncherSettings Settings { get; set; } = new();

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public LauncherItem? Selected => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    /// <summary>标签上高亮的来源。</summary>
    public LauncherSource EffectiveSource => Parsed.Source;

    /// <summary>标签是"全部",但因为没有匹配的应用自动当作"文件"(见 <see cref="ShouldAutoSwitchToFiles"/>)。</summary>
    public bool AutoFiles { get; private set; }

    /// <summary>输入里写了来源前缀或引擎关键词(切换标签时要先去掉)。自动切到"文件"不算。</summary>
    public bool HasPrefix => (Parsed.Source != SelectedSource && !AutoFiles) || Parsed.Engine is not null;

    public IReadOnlyList<LauncherItem> Apps => _apps;

    /// <summary>Tab 能切换的来源(关掉文件搜索时没有"文件""文件夹")。</summary>
    public IReadOnlyList<LauncherSource> Sources => LauncherSources.For(Settings);

    /// <summary>最近一次到达的文件搜索结果(输入变了以后、新结果到达之前,先显示上一次的)。</summary>
    public FileResults Files { get; private set; } = FileResults.None;

    /// <summary>文件索引还没准备好时的说明("正在建立文件索引…");准备好了为 null。</summary>
    public string? FileStatus { get; set; }

    /// <summary>文件搜索的结果到了:是当前输入的结果才用,尽量保持选中项。返回是否用上了。</summary>
    public bool SetFiles(FileResults results)
    {
        if (!Parsed.WantsFiles || results.Query != Parsed.Text)
        {
            return false;
        }

        Files = results;
        Rebuild(resetSelection: false);
        return true;
    }

    /// <summary>应用列表更新了(后台扫描完成):尽量保持选中项。</summary>
    public void SetApps(IReadOnlyList<LauncherItem> apps)
    {
        _apps = apps;
        Rebuild(resetSelection: false);
    }

    /// <summary>打开面板时调用。<paramref name="keepQuery"/>:刚关闭不久,保留上次的输入和选中项。</summary>
    public void PrepareForOpen(bool keepQuery, LauncherSource source)
    {
        if (!keepQuery)
        {
            Query = "";
            SelectedSource = LauncherSource.All;
            Files = FileResults.None;
            _autoOff = false;
        }

        if (source != LauncherSource.All)
        {
            SelectedSource = source;
        }

        Rebuild(resetSelection: !keepQuery);
    }

    public void SetQuery(string text)
    {
        if (text == Query)
        {
            return;
        }

        Query = text;
        if (text.Trim().Length == 0)
        {
            _autoOff = false;
        }

        Rebuild(resetSelection: true);
    }

    public void SetSource(LauncherSource source)
    {
        var leaveAuto = source == LauncherSource.All && AutoFiles; // 自动切到"文件"后点了"全部":回去,并且这次不再自动切
        if (source == SelectedSource && !leaveAuto)
        {
            return;
        }

        _autoOff |= leaveAuto;
        SelectedSource = source;
        Rebuild(resetSelection: true);
    }

    /// <summary>设置变了(引擎、条数):重新排,尽量保持选中项。</summary>
    public void Refresh() => Rebuild(resetSelection: false);

    /// <summary>上下移动,到头了回到另一头。</summary>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        SelectedIndex = ((SelectedIndex + delta) % Items.Count + Items.Count) % Items.Count;
        _userMoved = true;
    }

    public void Select(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            SelectedIndex = index;
            _userMoved = true;
        }
    }

    /// <summary>
    /// 这次输入的文件结果还没到(结果异步到达)。这期间不显示"网页搜索",免得它暂时排第一被选中、⏎ 误开浏览器
    /// (想搜文件,默认却选中了联网搜索)。
    /// </summary>
    public bool FilesPending => Settings.FileSearchEnabled && Parsed.WantsFiles && Files.Query != Parsed.Text;

    /// <summary>Ctrl+1~9 对应的条目。</summary>
    public LauncherItem? QuickItem(int slot) => slot is >= 1 and <= 9 && slot <= Items.Count ? Items[slot - 1] : null;

    /// <summary>
    /// Tab / Shift+Tab:在已有的来源之间循环。输入里写了前缀或引擎关键词时,先去掉它们再切换。
    /// 返回新的输入文字(视图要同步到搜索框)。
    /// </summary>
    public string CycleSource(bool forward)
    {
        var sources = Sources;
        var current = Math.Max(0, IndexOf(sources, Parsed.Source));
        var next = sources[(current + (forward ? 1 : sources.Count - 1)) % sources.Count];
        if (HasPrefix)
        {
            Query = Parsed.Text;
        }

        _autoOff |= next == LauncherSource.All && AutoFiles;
        SelectedSource = next;
        Rebuild(resetSelection: true);
        return Query;
    }

    /// <summary>Esc:有输入先清空,不在"全部"先回到"全部",都没有才关闭(返回 true = 应当关闭)。</summary>
    public bool Escape()
    {
        if (Query.Length > 0)
        {
            SetQuery("");
            return false;
        }

        if (SelectedSource != LauncherSource.All)
        {
            SetSource(LauncherSource.All);
            return false;
        }

        return true;
    }

    /// <summary>输入为空时按退格:回到"全部"。返回 false 表示没处理(交给搜索框)。</summary>
    public bool BackspaceOnEmpty()
    {
        if (Query.Length > 0 || SelectedSource == LauncherSource.All)
        {
            return false;
        }

        SetSource(LauncherSource.All);
        return true;
    }

    /// <summary>打开了一项:记一次常用度。</summary>
    public void Record(LauncherItem item) => Usage.Record(item.Id, Parsed.Text, Clock());

    /// <summary>应用已经不在了(打开时发现文件没了):从列表拿掉,也不再出现在最近使用里。</summary>
    public void Remove(LauncherItem item)
    {
        Usage.Forget(item.Id);
        _apps = _apps.Where(a => a.Id != item.Id).ToList();
        if (Files.Items.Any(f => f.Id == item.Id))
        {
            Files = Files with { Items = Files.Items.Where(f => f.Id != item.Id).ToList(), Total = Math.Max(0, Files.Total - 1) };
        }
        Rebuild(resetSelection: false);
    }

    /// <summary>
    /// 用户在这次输入下自己移动过选中项(上下键、点击)。没有移动过时,结果怎么变(文件结果到达、应用列表刷新)都选中第一项;
    /// 移动过才尽量保持在同一项上。
    /// </summary>
    private bool _userMoved;

    /// <summary>这次输入下用户手动回到了"全部":不再自动切到"文件",直到输入清空或面板重新打开。</summary>
    private bool _autoOff;

    /// <summary>
    /// "全部"里没有匹配的应用、文件结果已经到了并且不为空 → 自动当作"文件"标签。
    /// 文件也没有时留在"全部",网页搜索还在;用户手动选了"全部"(<see cref="_autoOff"/>)就不再自动切。
    /// </summary>
    private bool ShouldAutoSwitchToFiles() =>
        Settings.AutoSwitchToFiles && Settings.FileSearchEnabled && !_autoOff
        && Parsed is { Source: LauncherSource.All, Engine: null, IsEmpty: false }
        && Files.Query == Parsed.Text && Files.Total > 0
        && !_apps.Any(app => MatchScore.Score(Parsed.Text, app.Keys) is not null);

    private void Rebuild(bool resetSelection)
    {
        var previous = Selected?.Id;
        if (resetSelection)
        {
            _userMoved = false;
        }

        Parsed = LauncherQuery.Parse(Query, SelectedSource, Settings.Engines, Sources);
        AutoFiles = ShouldAutoSwitchToFiles();
        if (AutoFiles)
        {
            Parsed = Parsed with { Source = LauncherSource.Files };
        }

        Sections = new LauncherRanking(_apps, Usage, Settings, Clock(), Parsed.WantsFiles ? Files : null, webFallback: !FilesPending)
            .Sections(Parsed);
        Items = Sections.SelectMany(s => s.Items).ToList();
        var kept = resetSelection || !_userMoved || previous is null ? -1 : IndexOf(Items, previous);
        SelectedIndex = kept >= 0 ? kept : 0;
        Version++;
    }

    private static int IndexOf(IReadOnlyList<LauncherSource> sources, LauncherSource source)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] == source)
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<LauncherItem> items, string id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
