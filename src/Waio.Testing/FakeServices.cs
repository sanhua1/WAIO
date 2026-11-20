using System.Text.Json;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Testing;

/// <summary>内存版设置:Get 返回独立副本(与真实实现一致),SaveAsync 触发 Changed。</summary>
public sealed class InMemoryFeatureSettings : IFeatureSettings
{
    private string? _json;

    public event Action? Changed;

    public T Get<T>()
        where T : class, new() =>
        _json is null ? new T() : JsonSerializer.Deserialize<T>(_json) ?? new T();

    public ValueTask SaveAsync<T>(T value, CancellationToken cancellationToken = default)
        where T : class, new()
    {
        _json = JsonSerializer.Serialize(value);
        Changed?.Invoke();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeFeatureStorage(string dataDirectory) : IFeatureStorage
{
    public string DataDirectory { get; } = dataDirectory;
}

/// <summary>记录所有 HUD 文本,便于断言用户看到了什么。</summary>
public sealed class RecordingOverlay : IOverlayService
{
    public List<string> HudMessages { get; } = [];

    public ValueTask ShowHudAsync(string text, TimeSpan? duration = null)
    {
        HudMessages.Add(text);
        return ValueTask.CompletedTask;
    }
}

/// <summary>记录所有启动请求,不真的启动任何东西。设置 <see cref="FailWith"/> 可模拟启动失败。</summary>
public sealed class RecordingLauncher : ILauncher
{
    public List<LaunchRequest> Requests { get; } = [];

    public string? FailWith { get; set; }

    public ValueTask LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return FailWith is null ? ValueTask.CompletedTask : throw new LaunchException(FailWith);
    }
}

/// <summary>
/// 内存版剪贴板:<see cref="Copy"/> 模拟其他程序复制(触发 Changed),<see cref="SetAsync"/> 记录 WAIO 自己的写入(不触发 Changed,与真实行为一致)。
/// </summary>
public sealed class FakeClipboard : IClipboardService
{
    public event Action<ClipboardChange>? Changed;

    public bool RespectExclusionFormats { get; set; } = true;

    public List<ClipboardContent> Written { get; } = [];

    /// <summary>设为 false 模拟"剪贴板一直被占用"。</summary>
    public bool CanWrite { get; set; } = true;

    public ClipboardContent? Current { get; private set; }

    public void Copy(string text, string? sourceApp = "notepad") =>
        Copy(ClipboardContent.FromText(text), sourceApp);

    public void Copy(ClipboardContent content, string? sourceApp = "notepad")
    {
        Current = content;
        Changed?.Invoke(new ClipboardChange(content, sourceApp));
    }

    public ValueTask<bool> SetAsync(ClipboardContent content, CancellationToken cancellationToken = default)
    {
        if (!CanWrite)
        {
            return ValueTask.FromResult(false);
        }

        Written.Add(content);
        Current = content;
        ChangeCount++;
        return ValueTask.FromResult(true);
    }

    /// <summary>和真实实现一样:写入后触发 Changed(来源 "Waio"),剪贴板历史会收到。</summary>
    public async ValueTask<bool> CopyAsync(ClipboardContent content, CancellationToken cancellationToken = default)
    {
        if (!await SetAsync(content, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Changed?.Invoke(new ClipboardChange(content, "Waio"));
        return true;
    }

    event Action<ClipboardChange> IClipboardService.Changed
    {
        add => Changed += value;
        remove => Changed -= value;
    }

    public uint ChangeCount { get; private set; }

    /// <summary>当前有几个 <see cref="SuppressChanges"/> 没释放。</summary>
    public int ActiveSuppressions { get; private set; }

    /// <summary>
    /// 模拟"有程序响应 Ctrl+C 写入了剪贴板":更新内容与序号;没有 <see cref="SuppressChanges"/> 时才触发 Changed。
    /// </summary>
    public void ExternalWrite(ClipboardContent content, string? sourceApp = "notepad")
    {
        Current = content;
        ChangeCount++;
        if (ActiveSuppressions == 0)
        {
            Changed?.Invoke(new ClipboardChange(content, sourceApp));
        }
    }

    public ValueTask<ClipboardContent?> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);

    public IDisposable SuppressChanges()
    {
        ActiveSuppressions++;
        return new Release(() => ActiveSuppressions--);
    }

    private sealed class Release(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

/// <summary>同步执行投递的工作,当作"调用线程就是 UI 线程"。需要模拟线程切换的测试自己写假实现。</summary>
public sealed class ImmediateUiThread : IUiThread
{
    public bool IsCurrent => true;

    public bool Post(Action action)
    {
        action();
        return true;
    }
}

public sealed class FakeSystemEvents : ISystemEvents
{
    public ForegroundWindowInfo? Foreground { get; private set; }

    public event Action<ForegroundWindowInfo>? ForegroundChanged;

    /// <summary>模拟前台窗口变化。</summary>
    public void SetForeground(ForegroundWindowInfo info)
    {
        Foreground = info;
        ForegroundChanged?.Invoke(info);
    }
}

public sealed class FakeShortcutDirectory : IShortcutDirectory
{
    private readonly Dictionary<KeyChord, string> _owners = [];

    public bool TryClaim(KeyChord chord, string owner, out string? conflictOwner)
    {
        if (_owners.TryGetValue(chord, out var existing) && existing != owner)
        {
            conflictOwner = existing;
            return false;
        }

        _owners[chord] = owner;
        conflictOwner = null;
        return true;
    }

    public void Release(KeyChord chord, string owner)
    {
        if (_owners.TryGetValue(chord, out var existing) && existing == owner)
        {
            _owners.Remove(chord);
        }
    }

    public bool IsClaimed(KeyChord chord) => _owners.ContainsKey(chord);
}

/// <summary>内存版密钥存储(不加密)。</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string name)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    public void Set(string name, string? value)
    {
        lock (_values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _values.Remove(name);
            }
            else
            {
                _values[name] = value.Trim();
            }
        }
    }
}
