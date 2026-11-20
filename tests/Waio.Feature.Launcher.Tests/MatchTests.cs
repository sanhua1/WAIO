using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Tests;

/// <summary>拼音、拆词、匹配打分(档次同 MAIO 的 MatchScore)。</summary>
public class MatchTests
{
    private static int? Score(string query, params string[] names) => MatchScore.Score(query, SearchKeys.From(names));

    [Fact]
    public void 拼音表_覆盖常用汉字()
    {
        Assert.True(Pinyin.Count > 20_000);
        Assert.Equal(["wei"], Pinyin.Readings('微'));
        Assert.Equal(["xin"], Pinyin.Readings('信'));
        Assert.Equal("lv", Pinyin.Readings('绿')[0]); // ü 写成 v
        Assert.Empty(Pinyin.Readings('a'));
    }

    [Fact]
    public void 多音字_主读音在前_补上其他读音()
    {
        var readings = Pinyin.Readings('乐');

        Assert.Equal("le", readings[0]);
        Assert.Contains("yue", readings);
    }

    [Fact]
    public void 拆词_英文按空格大小写和数字_汉字逐字带拼音()
    {
        var tokens = SearchKeys.Tokenize("VisualStudio Code2024 微信");

        Assert.Equal(["visual", "studio", "code", "2024", "微", "信"], tokens.Select(t => t[0]));
        Assert.Equal(["微", "wei"], tokens[4]);
    }

    [Fact]
    public void 统一形式_小写_合并空白_全角兼容的NFC()
    {
        Assert.Equal("visual studio", SearchKeys.Normalize("  Visual \t STUDIO "));
        Assert.Equal("é", SearchKeys.Normalize("é")); // NFD → NFC
    }

    [Theory]
    [InlineData("微信", 100)]
    [InlineData("微", 90)]
    [InlineData("wx", 75)]
    [InlineData("weixin", 75)]
    [InlineData("wxin", 75)]
    [InlineData("微x", 75)]
    [InlineData("xin", 60)]
    public void 微信_中文拼音首字母都能搜到(string query, int expected) => Assert.Equal(expected, Score(query, "微信"));

    [Theory]
    [InlineData("vsc", 75)]
    [InlineData("visual", 90)]
    [InlineData("code", 60)]
    [InlineData("stu", 60)]
    [InlineData("al st", 50)]
    public void 英文名_按词匹配(string query, int expected) => Assert.Equal(expected, Score(query, "Visual Studio Code"));

    [Fact]
    public void 按顺序出现_最低一档_至少两个字符()
    {
        Assert.Equal(20, Score("slk", "Slack"));
        Assert.Null(Score("x", "Slack"));
    }

    [Fact]
    public void 多段输入_每段都要匹配_取最低一档减5()
    {
        Assert.Equal(55, Score("studio vis", "Visual Studio Code"));
        Assert.Null(Score("studio xyz", "Visual Studio Code"));
    }

    [Fact]
    public void 多音字_其他读音也能搜到() => Assert.Equal(75, Score("yy", "音乐"));

    [Fact]
    public void 多个名称_取最好的一个()
    {
        // 计算器的程序名 / 包名里的 WindowsCalculator 让 calc 也能搜到
        Assert.Equal(60, Score("calc", "计算器", "WindowsCalculator"));
        Assert.Equal(75, Score("jsq", "计算器", "WindowsCalculator"));
    }

    [Fact]
    public void 空输入和不匹配_返回null()
    {
        Assert.Null(Score("  ", "微信"));
        Assert.Null(Score("qq", "微信"));
    }
}
