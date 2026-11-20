using System.Security.Principal;
using Waio.Kit.Features;

namespace Waio.Core.Host;

public sealed record CapabilityCheck(bool Satisfied, string? Reason = null)
{
    public static CapabilityCheck Ok { get; } = new(true);
}

/// <summary>检查功能声明的运行条件是否满足。抽成接口,测试里可以随意模拟"没有管理员"。</summary>
public interface ICapabilityProbe
{
    CapabilityCheck Check(Capability required);
}

/// <summary>真实环境的探测。目前只有管理员权限需要主动检查(ADR-0004);其余条件由对应服务自己在运行时降级。</summary>
public sealed class WindowsCapabilityProbe : ICapabilityProbe
{
    public CapabilityCheck Check(Capability required)
    {
        if (required.HasFlag(Capability.Elevated) && !IsElevated())
        {
            return new CapabilityCheck(false, "需要管理员权限。请以管理员身份运行 WAIO(开机自启已配置为最高权限)。");
        }

        return CapabilityCheck.Ok;
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
