using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Tests;

/// <summary>常用度、输入解析、网页搜索、分组排序。</summary>
public class RankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.FromHours(8));

    internal static LauncherItem App(string name, string? exe = null) =>
        LauncherItem.App(new AppEntry("{GUID}\\" + (exe ?? name) + ".exe", name, exe is null ? null : $@"C:\Apps\{exe}.exe"));

    private static IReadOnlyList<WebSearchEngine> Engines => WebSearchEngine.Defaults();

    [Fact]
    public void 常用度_打开一次加分_一周减半()
    {
        var book = new UsageBook();
        book.Record("app:a", "", Now);
        book.Record("app:a", "", Now);

        Assert.Equal(2, book.Usage("app:a", Now), 3);
        Assert.Equal(1, book.Usage("app:a", Now + UsageBook.HalfLife), 3);
        Assert.Equal(0, book.Usage("app:b", Now));
    }

    [Fact]
    public void 常用度_同样的输入选过它_加分更多()
    {
        var book = new UsageBook();
        book.Record("app:a", "WX", Now);

        var same = book.Bonus("app:a", "wx", Now);
        var prefix = book.Bonus("app:a", "w", Now);
        var other = book.Bonus("app:a", "qq", Now);

        Assert.True(same > prefix && prefix > other, $"{same} {prefix} {other}");
        Assert.Equal(10, other); // 只有常用度:10·log2(1+1)
    }

    [Fact]
    public void 常用度_超过上限_删掉分数最低的()
    {
        var book = new UsageBook();
        for (var i = 0; i <= UsageBook.MaxItems; i++)
        {
            book.Record($"app:{i}", "", Now.AddMinutes(i));
        }

        Assert.Equal(UsageBook.MaxItems, book.Items.Count);
        Assert.DoesNotContain("app:0", book.Items.Keys); // 最早的那个衰减得最多
    }

    [Theory]
    [InlineData("a 微信", "Apps", "微信")]
    [InlineData("A 微信", "Apps", "微信")]
    [InlineData("a", "All", "a")]
    [InlineData("ab c", "All", "ab c")]
    public void 前缀_后面跟空格才算(string raw, string source, string text)
    {
        var query = LauncherQuery.Parse(raw, LauncherSource.All, Engines);

        Assert.Equal(Enum.Parse<LauncherSource>(source), query.Source);
        Assert.Equal(text, query.Text);
        Assert.Null(query.Engine);
    }

    [Fact]
    public void 引擎关键词_直接网页搜索()
    {
        var query = LauncherQuery.Parse("BD 天气 北京", LauncherSource.Apps, Engines);

        Assert.Equal("bd", query.Engine?.Keyword);
        Assert.Equal("天气 北京", query.Text);
    }

    [Fact]
    public void 网址_输入经过URL编码()
    {
        var google = Engines[0];

        Assert.Equal("https://www.google.com/search?q=c%23%20%26%20%E5%BE%AE", google.UrlFor(" c# & 微 "));
        Assert.Null(google.UrlFor("   "));
        Assert.Null(new WebSearchEngine { Name = "x", Keyword = "x", Template = "file:///{query}" }.UrlFor("a"));
    }

    [Fact]
    public void 全部_应用在前_最后是默认引擎搜索()
    {
        var apps = new[] { App("微信"), App("企业微信"), App("记事本") };
        var ranking = new LauncherRanking(apps, new UsageBook(), new LauncherSettings(), Now);

        var sections = ranking.Sections(LauncherQuery.Parse("wx", LauncherSource.All, Engines));

        Assert.Equal(["应用", "网页搜索"], sections.Select(s => s.Title));
        Assert.Equal(["微信", "企业微信"], sections[0].Items.Select(i => i.Title));
        Assert.Equal("用 Google 搜索“wx”", sections[1].Items[0].Title);
    }

    [Fact]
    public void 全部_应用条数受设置限制_应用来源不受()
    {
        var apps = Enumerable.Range(0, 20).Select(i => App($"Tool {i}")).ToList();
        var settings = new LauncherSettings { MaxAppsInAll = 3, ShowWebFallback = false };
        var ranking = new LauncherRanking(apps, new UsageBook(), settings, Now);

        Assert.Equal(3, ranking.Sections(new LauncherQuery(LauncherSource.All, "tool", null))[0].Items.Count);
        Assert.Equal(20, ranking.Sections(new LauncherQuery(LauncherSource.Apps, "tool", null))[0].Items.Count);
    }

    [Fact]
    public void 同样的输入常选的那项_排第一()
    {
        var apps = new[] { App("Code"), App("Visual Studio Code") };
        var book = new UsageBook();
        for (var i = 0; i < 3; i++)
        {
            book.Record(apps[1].Id, "code", Now); // 选过一次还压不过"完全相同",经常选才排第一(同 MAIO)
        }
        var ranking = new LauncherRanking(apps, book, new LauncherSettings(), Now);

        var found = ranking.Rank(apps, "code", 5);

        Assert.Equal("Visual Studio Code", found[0].Title); // 本来"完全相同"的 Code 排第一
    }

    [Fact]
    public void 同分_名称短的在前()
    {
        var apps = new[] { App("Notepad++"), App("Notepad") };

        var found = new LauncherRanking(apps, new UsageBook(), new LauncherSettings(), Now).Rank(apps, "note", 5);

        Assert.Equal(["Notepad", "Notepad++"], found.Select(i => i.Title));
    }

    [Fact]
    public void 输入为空_显示最近使用_关掉就什么都没有()
    {
        var apps = new[] { App("微信"), App("记事本") };
        var book = new UsageBook();
        book.Record(apps[1].Id, "", Now);

        var shown = new LauncherRanking(apps, book, new LauncherSettings(), Now).Sections(new(LauncherSource.All, "", null));
        var hidden = new LauncherRanking(apps, book, new LauncherSettings { ShowRecents = false }, Now)
            .Sections(new(LauncherSource.All, "", null));

        Assert.Equal("最近使用", shown.Single().Title);
        Assert.Equal(["记事本"], shown[0].Items.Select(i => i.Title));
        Assert.Empty(hidden);
    }

    [Fact]
    public void 引擎关键词_只有那一项网页搜索()
    {
        var ranking = new LauncherRanking([App("Google Chrome")], new UsageBook(), new LauncherSettings(), Now);

        var sections = ranking.Sections(LauncherQuery.Parse("gh waio", LauncherSource.All, Engines));

        var item = Assert.Single(Assert.Single(sections).Items);
        Assert.Equal("https://github.com/search?q=waio", item.Target);
        Assert.Equal("github.com", item.Subtitle);
    }

    [Fact]
    public void 默认引擎写错了_用第一个可用的()
    {
        var settings = new LauncherSettings { DefaultEngine = "nope" };

        Assert.Equal("g", settings.FallbackEngine?.Keyword);
    }
}
