namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 把输入变成分好组、排好序的结果(规则同 MAIO 的 LauncherRanking;命令组在 P4)。
/// <list type="bullet">
/// <item>"全部":应用(最多 <see cref="LauncherSettings.MaxAppsInAll"/> 个)→ 文件 → 文件夹(两组合计最多 <see cref="LauncherSettings.MaxFilesInAll"/> 个)→ 网页搜索
/// (软件 > 文件 > 文件夹 > 网页搜索);</item>
/// <item>"应用":最多 <see cref="MaxAppsInSource"/> 个;"文件""文件夹":文件搜索给的全部(最多 <see cref="MaxFilesInSource"/>);</item>
/// <item>输入为空:最近使用;写了引擎关键词(<c>g 关键词</c>):只有那一项网页搜索。</item>
/// </list>
/// 文件按索引给的顺序(匹配质量等,见 VolumeIndex.Search),从启动台打开过的往前提。
/// </summary>
internal sealed class LauncherRanking(
    IReadOnlyList<LauncherItem> apps, UsageBook usage, LauncherSettings settings, DateTimeOffset now, FileResults? files = null,
    bool webFallback = true)
{
    public const int MaxAppsInSource = 50;
    public const int MaxFilesInSource = 300;
    public const int RecentLimit = 8;

    private readonly FileResults _files = files ?? FileResults.None;

    public IReadOnlyList<LauncherSection> Sections(LauncherQuery query)
    {
        if (query.Engine is { } engine)
        {
            return LauncherItem.Web(engine, query.Text) is { } item ? [new LauncherSection("网页搜索", [item])] : [];
        }

        if (query.IsEmpty)
        {
            return Recents(query.Source);
        }

        var sections = new List<LauncherSection>();
        if (query.Source is LauncherSource.All or LauncherSource.Apps)
        {
            var limit = query.Source == LauncherSource.Apps ? MaxAppsInSource : Math.Max(1, settings.MaxAppsInAll);
            var found = Rank(apps, query.Text, limit);
            if (found.Count > 0)
            {
                sections.Add(new LauncherSection("应用", found));
            }
        }

        if (query.Source.IncludesFiles())
        {
            var inAll = query.Source == LauncherSource.All;
            sections.AddRange(FileSections(inAll ? Math.Max(1, settings.MaxFilesInAll) : MaxFilesInSource, offerMore: inAll));
        }

        // webFallback = false:文件结果还没到,先不放网页搜索(否则它会暂时排第一、被选中)
        if (query.Source == LauncherSource.All && settings.ShowWebFallback && webFallback
            && settings.FallbackEngine is { } fallback && LauncherItem.Web(fallback, query.Text) is { } web)
        {
            sections.Add(new LauncherSection("网页搜索", [web]));
        }

        return sections;
    }

    /// <summary>匹配的条目按"匹配质量 + 常用度"排序;同分时名称短的在前,再按拼音顺序。</summary>
    public IReadOnlyList<LauncherItem> Rank(IReadOnlyList<LauncherItem> items, string text, int limit)
    {
        var scored = new List<(LauncherItem Item, int Score)>();
        foreach (var item in items)
        {
            if (MatchScore.Score(text, item.Keys) is { } match)
            {
                scored.Add((item, match + usage.Bonus(item.Id, text, now)));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Item.Title.Length)
            .ThenBy(s => s.Item.Title, CultureText.Ordering)
            .Take(limit)
            .Select(s => s.Item)
            .ToList();
    }

    /// <summary>
    /// 文件组和文件夹组(先文件后文件夹):各自保持索引给的顺序,从启动台打开过的往前提(MAIO 的 fileSections)。
    /// "共 N 个"写在第一组的标题上(N 是文件和文件夹合计的命中数)。
    /// <paramref name="offerMore"/>:没列全时在最后一组末尾加"查看全部 N 个"(只在"全部"里,打开 = 切到"文件"标签)。
    /// </summary>
    private IEnumerable<LauncherSection> FileSections(int limit, bool offerMore)
    {
        if (_files.Items.Count == 0)
        {
            yield break;
        }

        var ordered = _files.Items
            .Select((item, order) => (item, rank: (usage.Bonus(item.Id, _files.Query, now) * 100) - order))
            .OrderByDescending(x => x.rank)
            .Select(x => x.item)
            .Take(limit)
            .ToList();
        var files = ordered.Where(i => i.Kind == LauncherItemKind.File).ToList();
        var folders = ordered.Where(i => i.Kind == LauncherItemKind.Folder).ToList();
        var hidden = _files.Total > ordered.Count;
        var more = hidden ? $"(共 {_files.Total:N0} 个)" : "";
        if (hidden && offerMore)
        {
            (folders.Count > 0 ? folders : files).Add(LauncherItem.MoreFiles(_files.Total, MaxFilesInSource));
        }

        if (files.Count > 0)
        {
            yield return new LauncherSection("文件" + more, files);
            more = "";
        }

        if (folders.Count > 0)
        {
            yield return new LauncherSection("文件夹" + more, folders);
        }
    }
    private IReadOnlyList<LauncherSection> Recents(LauncherSource source)
    {
        if (!settings.ShowRecents || source is LauncherSource.Files or LauncherSource.Folders || apps.Count == 0)
        {
            return [];
        }

        var byId = new Dictionary<string, LauncherItem>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            byId.TryAdd(app.Id, app);
        }

        var items = usage.Recent(RecentLimit * 2, now)
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<LauncherItem>()
            .Take(RecentLimit)
            .ToList();
        return items.Count == 0 ? [] : [new LauncherSection("最近使用", items)];
    }
}
