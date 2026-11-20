using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Tests;

/// <summary>翻译窗口的状态逻辑(MAIO TranslateViewModel 的行为):并行、顺序、流式、失败与重试、取消、历史。</summary>
public class TranslateSessionTests
{
    /// <summary>可控的假翻译源:测试决定什么时候返回、返回什么。</summary>
    private sealed class FakeProvider : ITranslationProvider
    {
        public TaskCompletionSource<TranslationResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<string>? Partial { get; private set; }

        public List<TranslationRequest> Requests { get; } = [];

        public CancellationToken Token { get; private set; }

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Partial = onPartial;
            Token = cancellationToken;
            using (cancellationToken.Register(() => Result.TrySetCanceled(cancellationToken)))
            {
                return await Result.Task;
            }
        }
    }

    private sealed class Harness
    {
        private readonly object _gate = new();
        private readonly List<Action> _posted = [];

        public Harness(params ServiceConfig[] services)
        {
            Settings = new TranslateSettings { Services = [.. services] };
            Session = new TranslateSession(() => Settings, Make, (entry, limit) => History.Add((entry, limit)), Post);
        }

        public TranslateSettings Settings { get; }

        public TranslateSession Session { get; }

        public Dictionary<string, Queue<FakeProvider>> Providers { get; } = [];

        public List<(HistoryEntry Entry, int Limit)> History { get; } = [];

        public FakeProvider Next(string id)
        {
            var provider = new FakeProvider();
            if (!Providers.TryGetValue(id, out var queue))
            {
                Providers[id] = queue = new Queue<FakeProvider>();
            }

            queue.Enqueue(provider);
            return provider;
        }

        /// <summary>模拟 UI 线程:把后台投递过来的更新依次执行,直到没有新的。</summary>
        public async Task Drain()
        {
            for (var round = 0; round < 50; round++)
            {
                await Task.Delay(10);
                Action[] batch;
                lock (_gate)
                {
                    batch = [.. _posted];
                    _posted.Clear();
                }

                foreach (var action in batch)
                {
                    action();
                }

                if (batch.Length == 0 && round > 2)
                {
                    return;
                }
            }
        }

        public ResultCard Card(string id) => Session.Cards.Single(c => c.Id == id);

        private ITranslationProvider Make(ServiceConfig config) =>
            Providers.TryGetValue(config.Id, out var queue) && queue.Count > 0 ? queue.Dequeue() : throw new InvalidOperationException(config.Id);

        private void Post(Action action)
        {
            lock (_gate)
            {
                _posted.Add(action);
            }
        }
    }

    private static ServiceConfig Service(string id, bool enabled = true) =>
        new() { Id = id, Kind = ProviderKind.Google, Name = id.ToUpperInvariant(), IsEnabled = enabled };

    [Fact]
    public async Task 启用的源并行请求_卡片按设置顺序排列_先返回的先显示()
    {
        var h = new Harness(Service("a"), Service("off", enabled: false), Service("b"));
        var a = h.Next("a");
        var b = h.Next("b");
        h.Session.SourceText = "hello";

        h.Session.Translate();
        await h.Drain();
        Assert.Equal(["a", "b"], h.Session.Cards.Select(c => c.Id));
        Assert.All(h.Session.Cards, c => Assert.Equal(CardPhase.Loading, c.Phase));

        b.Result.SetResult(new TranslationResult("你好 B"));
        await h.Drain();
        Assert.Equal(CardPhase.Loading, h.Card("a").Phase);
        Assert.Equal("你好 B", h.Card("b").Text);
    }

    [Fact]
    public async Task 原文是英文_目标是中文_原文是中文_自动改译英文()
    {
        var h = new Harness(Service("a"));
        var first = h.Next("a");
        h.Session.SourceText = "hello world";
        h.Session.Translate();
        await h.Drain();
        Assert.Equal(Language.SimplifiedChinese, first.Requests.Single().Target);

        var second = h.Next("a");
        h.Session.SourceText = "今天天气很好";
        h.Session.Translate();
        await h.Drain();
        Assert.Equal(Language.English, second.Requests.Single().Target);
        Assert.Equal(Language.SimplifiedChinese, h.Session.Detected);
    }

    [Fact]
    public async Task 大模型逐字输出_卡片显示中间结果_完成后显示最终译文()
    {
        var h = new Harness(Service("llm"));
        var llm = h.Next("llm");
        h.Session.SourceText = "hello";
        h.Session.Translate();
        await h.Drain();

        llm.Partial!("你");
        await h.Drain();
        Assert.Equal((CardPhase.Streaming, "你"), (h.Card("llm").Phase, h.Card("llm").Text));

        llm.Result.SetResult(new TranslationResult("你好"));
        await h.Drain();
        Assert.Equal((CardPhase.Done, "你好"), (h.Card("llm").Phase, h.Card("llm").Text));
    }

    [Fact]
    public async Task 失败_卡片显示原因_重试只重新请求这一个源()
    {
        var h = new Harness(Service("a"), Service("b"));
        var a = h.Next("a");
        h.Next("b").Result.SetResult(new TranslationResult("ok"));
        h.Session.SourceText = "hello";
        h.Session.Translate();
        a.Result.SetException(new TranslationException("请求超时,请检查网络或代理"));
        await h.Drain();
        Assert.Equal((CardPhase.Failed, "请求超时,请检查网络或代理"), (h.Card("a").Phase, h.Card("a").Error));

        h.Next("a").Result.SetResult(new TranslationResult("你好"));
        h.Session.Retry("a");
        await h.Drain();

        Assert.Equal("你好", h.Card("a").Text);
        Assert.Equal("ok", h.Card("b").Text);
    }

    [Fact]
    public async Task 新的翻译开始_旧请求被取消_旧结果不会覆盖新卡片()
    {
        var h = new Harness(Service("a"));
        var old = h.Next("a");
        h.Session.SourceText = "one";
        h.Session.Translate();
        await h.Drain();

        var fresh = h.Next("a");
        h.Session.SourceText = "two";
        h.Session.Translate();
        old.Result.TrySetResult(new TranslationResult("旧的"));
        await h.Drain();

        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(CardPhase.Loading, h.Card("a").Phase);
        fresh.Result.SetResult(new TranslationResult("新的"));
        await h.Drain();
        Assert.Equal("新的", h.Card("a").Text);
    }

    [Fact]
    public async Task 全部完成后记一条历史_只含成功的结果_关闭历史时不记()
    {
        var h = new Harness(Service("a"), Service("b"));
        h.Next("a").Result.SetResult(new TranslationResult("你好"));
        h.Next("b").Result.SetException(new TranslationException("坏了"));
        h.Session.SourceText = "hello";
        h.Session.Translate();
        await h.Drain();

        var (entry, limit) = Assert.Single(h.History);
        Assert.Equal("hello", entry.Text);
        Assert.Equal([new HistoryItem("A", "你好")], entry.Results);
        Assert.Equal(500, limit);

        h.Settings.HistoryEnabled = false;
        h.Next("a").Result.SetResult(new TranslationResult("你好"));
        h.Next("b").Result.SetResult(new TranslationResult("你好"));
        h.Session.Translate();
        await h.Drain();
        Assert.Single(h.History);
    }

    [Fact]
    public async Task 被取消的翻译不记历史()
    {
        var h = new Harness(Service("a"));
        h.Next("a");
        h.Session.SourceText = "hello";
        h.Session.Translate();
        h.Session.Cancel();
        await h.Drain();

        Assert.Empty(h.History);
    }

    [Fact]
    public async Task 原文为空_清空卡片_不发请求()
    {
        var h = new Harness(Service("a"));
        h.Session.SourceText = "   ";
        h.Session.Translate();
        await h.Drain();

        Assert.Empty(h.Session.Cards);
    }

    [Fact]
    public async Task 交换语言_源语言变成目标_目标变成原来的源()
    {
        var h = new Harness(Service("a"));
        h.Next("a");
        h.Session.SourceText = "hello";
        h.Session.Translate();
        await h.Drain();

        var swapped = h.Next("a");
        h.Session.SwapLanguages();
        await h.Drain();

        Assert.Equal(Language.SimplifiedChinese, h.Session.SourceLanguage);
        Assert.Equal(Language.English, h.Session.TargetOverride);
        Assert.Equal((Language.SimplifiedChinese, Language.English), (swapped.Requests.Single().Source, swapped.Requests.Single().Target));
    }

    [Fact]
    public void 打开窗口时按设置删除换行_传null保留上次内容()
    {
        var h = new Harness();
        h.Session.Prepare("trans-\nlation");
        Assert.Equal("translation", h.Session.SourceText);

        h.Session.Prepare(null);
        Assert.Equal("translation", h.Session.SourceText);

        h.Settings.RemoveLineBreaks = false;
        h.Session.Prepare("a\nb");
        Assert.Equal("a\nb", h.Session.SourceText);
    }

    [Fact]
    public async Task 折叠状态在重新翻译后保留()
    {
        var h = new Harness(Service("a"));
        h.Next("a");
        h.Session.SourceText = "hello";
        h.Session.Translate();
        h.Session.ToggleCollapsed("a");

        h.Next("a");
        h.Session.Translate();
        await h.Drain();

        Assert.True(h.Card("a").IsCollapsed);
    }
}
