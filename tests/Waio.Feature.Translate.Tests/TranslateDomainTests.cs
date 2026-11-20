using Waio.Feature.Translate.Domain;
using Waio.Kit.Text;

namespace Waio.Feature.Translate.Tests;

/// <summary>纯规则(移植自 MAIO TranslateDomainTests / TextCleanerTests),外加 Windows 版的语种识别与窗口定位。</summary>
public class TranslateDomainTests
{
    // ---- 删除换行(MAIO TextCleanerTests) ----

    [Fact]
    public void 删除换行_英文行之间加空格() => Assert.Equal("The quick brown fox jumps", TextCleaner.RemovingLineBreaks("The quick brown\nfox jumps"));

    [Fact]
    public void 删除换行_中文行之间直接拼接() => Assert.Equal("今天天气很好", TextCleaner.RemovingLineBreaks("今天天气\n很好"));

    [Fact]
    public void 删除换行_保留空行分隔的段落() => Assert.Equal("a b\n\nc d", TextCleaner.RemovingLineBreaks("a\nb\n\n\nc\r\nd"));

    [Fact]
    public void 删除换行_拼回连字符断开的单词_大写开头不是断词()
    {
        Assert.Equal("translation works", TextCleaner.RemovingLineBreaks("trans-\nlation works"));
        Assert.Equal("Wi- Fi", TextCleaner.RemovingLineBreaks("Wi-\nFi"));
    }

    // ---- 目标语言规则 ----

    [Fact]
    public void 目标语言_外文译成首选语言()
    {
        Assert.Equal(Language.SimplifiedChinese, TargetLanguagePolicy.Target(Language.English, Language.SimplifiedChinese, Language.English));
    }

    [Fact]
    public void 目标语言_原文已是首选语言_改译成第二语言_简繁都算中文()
    {
        Assert.Equal(Language.English, TargetLanguagePolicy.Target(Language.SimplifiedChinese, Language.SimplifiedChinese, Language.English));
        Assert.Equal(Language.English, TargetLanguagePolicy.Target(Language.TraditionalChinese, Language.SimplifiedChinese, Language.English));
    }

    [Fact]
    public void 目标语言_识别不出来_用首选语言()
    {
        Assert.Equal(Language.Japanese, TargetLanguagePolicy.Target(null, Language.Japanese, Language.English));
    }

    // ---- 提示词 ----

    [Fact]
    public void 提示词_填入占位符_原文里的占位符不被替换()
    {
        var rendered = new PromptTemplate("to $to from $from", "$text").Render(new TranslationRequest("hi $to", Language.English, Language.SimplifiedChinese));

        Assert.Equal("to Simplified Chinese from English", rendered.System);
        Assert.Equal("hi $to", rendered.User);
    }

    // ---- 历史 ----

    private static HistoryEntry Entry(string text, Language target = Language.English) =>
        new() { Date = DateTimeOffset.Now, Text = text, Source = Language.SimplifiedChinese, Target = target };

    [Fact]
    public void 历史_新的在前_同样的原文只留最新一条()
    {
        var list = new List<HistoryEntry>();
        list = HistoryList.Adding(Entry("a"), list, 10);
        list = HistoryList.Adding(Entry("b"), list, 10);
        list = HistoryList.Adding(Entry("a"), list, 10);

        Assert.Equal(["a", "b"], list.Select(e => e.Text));
    }

    [Fact]
    public void 历史_同样的原文但目标语言不同_分别保留()
    {
        var list = HistoryList.Adding(Entry("a", Language.English), [], 10);
        list = HistoryList.Adding(Entry("a", Language.Japanese), list, 10);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void 历史_超过上限丢弃最旧的()
    {
        var list = new List<HistoryEntry>();
        for (var i = 0; i < 5; i++)
        {
            list = HistoryList.Adding(Entry(i.ToString()), list, 3);
        }

        Assert.Equal(["4", "3", "2"], list.Select(e => e.Text));
    }

    [Fact]
    public void 历史_搜索也匹配译文_不区分大小写()
    {
        var item = Entry("hello");
        item.Results = [new HistoryItem("Google", "你好")];

        Assert.True(item.Matches("你好"));
        Assert.True(item.Matches("HELLO"));
        Assert.False(item.Matches("再见"));
    }

    // ---- 翻译源配置 ----

    [Fact]
    public void 出厂配置_只启用三个免费源()
    {
        Assert.Equal([ProviderKind.Google, ProviderKind.Bing, ProviderKind.Transmart],
            ServiceConfig.Defaults().Where(s => s.IsEnabled).Select(s => s.Kind));
    }

    [Fact]
    public void 配置值_没填或空白时用默认值()
    {
        var config = ServiceConfig.Make(ProviderKind.Google, "g");
        Assert.Equal("https://translate.googleapis.com", config.Option("baseURL"));
        config.Options["baseURL"] = "  ";
        Assert.Equal("https://translate.googleapis.com", config.Option("baseURL"));
        config.Options["baseURL"] = "https://mirror.example";
        Assert.Equal("https://mirror.example", config.Option("baseURL"));
    }

    [Fact]
    public void 每种翻译源都有说明()
    {
        Assert.All(ProviderCatalog.All, kind => Assert.False(string.IsNullOrEmpty(kind.Spec().DisplayName)));
    }

    [Fact]
    public void 设置_手写的坏值被修正()
    {
        var settings = new TranslateSettings
        {
            Services = [null!, ServiceConfig.Make(ProviderKind.Bing, "x"), ServiceConfig.Make(ProviderKind.Google, "x"), new() { Id = "" }],
            FontSize = 99, HistoryLimit = 0, TargetLanguage = Language.Auto,
        }.Normalized();

        Assert.Equal([ProviderKind.Bing], settings.Services.Select(s => s.Kind)); // null、空 id、重复 id 都丢掉
        Assert.Equal(22, settings.FontSize);
        Assert.Equal(1, settings.HistoryLimit);
        Assert.Equal(Language.SimplifiedChinese, settings.TargetLanguage);
    }

    // ---- 语种识别(Windows 版:按文字系统) ----

    [Theory]
    [InlineData("今天天气很好", "SimplifiedChinese")]
    [InlineData("這個問題很難說", "TraditionalChinese")]
    [InlineData("今日はいい天気ですね", "Japanese")]
    [InlineData("안녕하세요", "Korean")]
    [InlineData("Привет, как дела?", "Russian")]
    [InlineData("Привіт, як справи? Їжак", "Ukrainian")]
    [InlineData("The quick brown fox", "English")]
    [InlineData("¿Dónde está la biblioteca? Mañana", "Spanish")]
    [InlineData("Größe und Straße", "German")]
    [InlineData("สวัสดีครับ", "Thai")]
    public void 语种识别(string text, string expected) => Assert.Equal(Enum.Parse<Language>(expected), LanguageDetector.Detect(text));

    [Fact]
    public void 语种识别_只有数字符号_识别不出()
    {
        Assert.Null(LanguageDetector.Detect("12345 !!!"));
    }

    // ---- 窗口定位 ----

    private static readonly Box Work = new(0, 0, 1920, 1040);

    private static (int, int) Place(PlacementKind kind, Box? selection = null, (int, int)? mouse = null, int[]? remembered = null) =>
        WindowPlacement.TopLeft(kind, Work, 460, 200, 420, 8, mouse ?? (500, 500), selection, remembered);

    [Fact]
    public void 定位_屏幕正中间() => Assert.Equal((730, 420), Place(PlacementKind.Center));

    [Fact]
    public void 定位_鼠标处_窗口在鼠标下方一点() => Assert.Equal((470, 512), Place(PlacementKind.Mouse));

    [Fact]
    public void 定位_所选文字下方_放不下时放到上方()
    {
        Assert.Equal((100, 128), Place(PlacementKind.Below, new Box(100, 100, 300, 120)));
        Assert.Equal((100, 1000 - 8 - 420), Place(PlacementKind.Below, new Box(100, 1000, 300, 1020)));
    }

    [Fact]
    public void 定位_贴着屏幕边缘时_整个窗口留在屏幕内()
    {
        Assert.Equal((1920 - 460 - 8, 1040 - 420), Place(PlacementKind.Mouse, mouse: (1900, 1030)));
    }

    [Fact]
    public void 定位_记住的位置_没有记录时放在上方五分之一处()
    {
        Assert.Equal((300, 400), Place(PlacementKind.Remembered, remembered: [300, 400]));
        Assert.Equal((730, 208), Place(PlacementKind.Remembered));
    }

    [Fact]
    public void 内容变高_正中间模式保持中心_其他模式保持左上角()
    {
        Assert.Equal((730, 320), WindowPlacement.AfterResize((730, 420), (960, 520), 460, 400, Work));
        Assert.Equal((100, 128), WindowPlacement.AfterResize((100, 128), null, 460, 400, Work));
        Assert.Equal((100, 640), WindowPlacement.AfterResize((100, 900), null, 460, 400, Work)); // 底部不超出屏幕
    }

    [Fact]
    public void 选择定位方式_按触发方式和设置()
    {
        var settings = new TranslateSettings { WindowPosition = WindowPosition.FollowMouse, SelectionWindowPosition = SelectionPosition.NearSelection };

        Assert.Equal(PlacementKind.Mouse, WindowPlacement.Kind(ShowTrigger.Input, settings));
        Assert.Equal(PlacementKind.Below, WindowPlacement.Kind(new ShowTrigger(true, new Box(0, 0, 1, 1)), settings));
        Assert.Equal(PlacementKind.Mouse, WindowPlacement.Kind(new ShowTrigger(true), settings)); // 拿不到选中位置时用鼠标处
    }
}
