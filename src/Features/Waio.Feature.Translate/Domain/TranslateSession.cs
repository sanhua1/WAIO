using Waio.Kit.Text;

namespace Waio.Feature.Translate.Domain;

internal enum CardPhase
{
    Loading,

    /// <summary>大模型逐字输出中。</summary>
    Streaming,

    Done,
    Failed,
}

/// <summary>一个翻译源的结果卡片。</summary>
internal sealed class ResultCard(string id, string name, string mark)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Mark { get; } = mark;

    public CardPhase Phase { get; set; } = CardPhase.Loading;

    /// <summary>流式中间结果或最终译文。</summary>
    public string? Text { get; set; }

    public DictionaryEntry? Dictionary { get; set; }

    public string? Error { get; set; }

    public bool IsCollapsed { get; set; }

    public bool IsWorking => Phase is CardPhase.Loading or CardPhase.Streaming;
}

/// <summary>
/// 翻译窗口的状态(MAIO <c>TranslateViewModel</c>,逻辑一一对应):原文、语言、各翻译源的结果卡片。
/// 所有启用的源并行请求,先返回的先显示;全部结束且没被取消时记一条历史。
/// <para>
/// 线程:公开方法都在 UI 线程调用;翻译在线程池上跑,结果经 <c>post</c> 回到 UI 线程再改状态,然后触发 <see cref="Changed"/>。
/// 视图在 Changed 里重画即可。
/// </para>
/// </summary>
internal sealed class TranslateSession(
    Func<TranslateSettings> settings,
    Func<ServiceConfig, ITranslationProvider> makeProvider,
    Action<HistoryEntry, int> recordHistory,
    Action<Action> post)
{
    private CancellationTokenSource? _run;
    private int _generation;

    public event Action? Changed;

    /// <summary>窗口要关闭(Esc、关闭按钮)。</summary>
    public event Action? CloseRequested;

    public string SourceText { get; set; } = "";

    /// <summary>用户选择的源语言(Auto 为自动检测)。</summary>
    public Language SourceLanguage { get; set; } = Language.Auto;

    /// <summary>用户选择的目标语言(null 表示按设置自动选择)。</summary>
    public Language? TargetOverride { get; set; }

    public Language? Detected { get; private set; }

    public Language EffectiveTarget { get; private set; } = Language.SimplifiedChinese;

    public List<ResultCard> Cards { get; private set; } = [];

    public bool IsPinned { get; set; }

    public double FontSize { get; private set; } = 14;

    /// <summary>每次打开窗口加一,用于让输入框重新获得焦点。</summary>
    public int FocusToken { get; private set; }

    public void RequestClose() => CloseRequested?.Invoke();

    /// <summary>窗口打开前调用。<paramref name="text"/> 为 null 时保留上次的内容。</summary>
    public void Prepare(string? text)
    {
        var current = settings();
        FontSize = current.FontSize;
        FocusToken++;
        if (text is not null)
        {
            SourceText = current.RemoveLineBreaks ? TextCleaner.RemovingLineBreaks(text) : text;
        }

        Changed?.Invoke();
    }

    public void RemoveLineBreaks()
    {
        SourceText = TextCleaner.RemovingLineBreaks(SourceText);
        Translate();
    }

    public void SwapLanguages()
    {
        var currentSource = SourceLanguage == Language.Auto ? Detected : SourceLanguage;
        if (currentSource is not { } source)
        {
            return;
        }

        SourceLanguage = EffectiveTarget;
        TargetOverride = source;
        Translate();
    }

    public void ToggleCollapsed(string id)
    {
        if (Cards.Find(c => c.Id == id) is { } card)
        {
            card.IsCollapsed = !card.IsCollapsed;
            Changed?.Invoke();
        }
    }

    public void Cancel()
    {
        _run?.Cancel();
        _run = null;
        _generation++;
    }

    public void Retry(string id)
    {
        var current = settings();
        if (current.Services.Find(s => s.Id == id) is not { } config || CurrentRequest(current) is not { } request
            || Cards.Find(c => c.Id == id) is not { } card)
        {
            return;
        }

        card.Phase = CardPhase.Loading;
        card.Text = card.Error = null;
        card.Dictionary = null;
        Changed?.Invoke();
        var generation = _generation;
        var token = (_run ??= new CancellationTokenSource()).Token;
        _ = PerformAsync(makeProvider(config), id, request, generation, token);
    }

    public void Translate()
    {
        Cancel();
        var current = settings();
        if (CurrentRequest(current) is not { } request)
        {
            Cards = [];
            Changed?.Invoke();
            return;
        }

        var services = current.Services.Where(s => s.IsEnabled).ToList();
        var collapsed = Cards.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().IsCollapsed);
        Cards = [.. services.Select(s => new ResultCard(s.Id, s.Name, s.Spec.Mark) { IsCollapsed = collapsed.GetValueOrDefault(s.Id) })];
        Changed?.Invoke();

        var generation = _generation;
        _run = new CancellationTokenSource();
        var token = _run.Token;
        var jobs = services.Select(s => PerformAsync(makeProvider(s), s.Id, request, generation, token)).ToArray();
        _ = FinishAsync(jobs, request, current, generation);
    }

    private async Task FinishAsync(Task[] jobs, TranslationRequest request, TranslateSettings current, int generation)
    {
        await Task.WhenAll(jobs).ConfigureAwait(false);
        post(() =>
        {
            if (generation == _generation)
            {
                RecordHistory(request, current);
            }
        });
    }

    private TranslationRequest? CurrentRequest(TranslateSettings current)
    {
        var text = SourceText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        Detected = LanguageDetector.Detect(text);
        var known = SourceLanguage == Language.Auto ? Detected : SourceLanguage;
        EffectiveTarget = TargetOverride ?? TargetLanguagePolicy.Target(known, current.TargetLanguage, current.SecondTargetLanguage);
        return new TranslationRequest(text, SourceLanguage, EffectiveTarget, Detected);
    }

    private async Task PerformAsync(ITranslationProvider provider, string id, TranslationRequest request, int generation, CancellationToken token)
    {
        try
        {
            var result = await Task.Run(
                () => provider.TranslateAsync(request, partial => Update(id, generation, card =>
                {
                    if (card.IsWorking)
                    {
                        card.Phase = CardPhase.Streaming;
                        card.Text = partial;
                    }
                }), token),
                token).ConfigureAwait(false);
            Update(id, generation, card =>
            {
                card.Phase = CardPhase.Done;
                card.Text = result.Text;
                card.Dictionary = result.Dictionary;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新的翻译开始了或窗口关了:旧结果丢弃
        }
        catch (Exception ex)
        {
            var message = ex is TranslationException ? ex.Message : $"出错了:{ex.Message}";
            Update(id, generation, card =>
            {
                card.Phase = CardPhase.Failed;
                card.Error = message;
            });
        }
    }

    private void Update(string id, int generation, Action<ResultCard> change) => post(() =>
    {
        if (generation == _generation && Cards.Find(c => c.Id == id) is { } card)
        {
            change(card);
            Changed?.Invoke();
        }
    });

    private void RecordHistory(TranslationRequest request, TranslateSettings current)
    {
        if (!current.HistoryEnabled)
        {
            return;
        }

        var items = Cards.Where(c => c.Phase == CardPhase.Done && !string.IsNullOrEmpty(c.Text))
            .Select(c => new HistoryItem(c.Name, c.Text!)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        recordHistory(new HistoryEntry
        {
            Date = DateTimeOffset.Now, Text = request.Text, Source = request.Detected ?? request.Source, Target = request.Target, Results = items,
        }, current.HistoryLimit);
    }
}
