namespace Waio.Core.Storage;

/// <summary>app.json 里的检查更新设置(ADR-0031)。字段只增不删。</summary>
public sealed class UpdateSettings
{
    /// <summary>自动检查更新(每天一次,只访问 GitHub 的公开发布接口)。默认开。</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>上次检查的时间(不管成功与否),用来控制每天一次。</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>已经在托盘提示过的版本:同一个版本只提示一次。</summary>
    public string? NotifiedVersion { get; set; }
}
