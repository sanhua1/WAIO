using Waio.Kit.Features;

namespace Waio.Core.Host;

/// <summary>功能的运行状态机:Disabled → NeedsAttention / Running / Failed(ARCHITECTURE §8)。</summary>
public enum FeatureState
{
    /// <summary>未启用,或已被停用。</summary>
    Disabled,

    /// <summary>运行条件不满足(如需要管理员但当前不是),等待用户处理;不会启动。</summary>
    NeedsAttention,

    Running,

    /// <summary>激活抛了异常或超时。已被隔离,不影响其他功能。</summary>
    Failed,
}

public sealed record FeatureStatus(FeatureId Id, FeatureState State, string? Message = null, Exception? Error = null);
