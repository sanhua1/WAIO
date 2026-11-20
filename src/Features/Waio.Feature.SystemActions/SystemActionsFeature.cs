using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Services;

namespace Waio.Feature.SystemActions;

/// <summary>
/// 系统动作:一个快捷键 → 打开程序/文件/文件夹/链接。M0 的第一个真实功能。
/// 所有启动都经 <see cref="ILauncher"/>,默认降权(ADR-0006);本功能自己不创建进程。
/// </summary>
public sealed class SystemActionsFeature(IFeatureContext context) : IFeature
{
    public static readonly CommandId OpenApp = "system.openApp";
    public static readonly CommandId OpenUrl = "system.openUrl";

    public static FeatureManifest Manifest { get; } = new(
        "system",
        "系统动作",
        "用快捷键打开程序、文件、文件夹或网页",
        "\uE7E8", // Segoe Fluent Icons: PowerButton
        Capability.None,
        [
            new CommandDescriptor(OpenApp, "打开程序或文件",
            [
                new ParameterSpec("target", "程序/文件/文件夹路径", ParameterKind.FilePath, Required: true),
                new ParameterSpec("arguments", "命令行参数(仅程序)", ParameterKind.String),
                new ParameterSpec("workingDirectory", "工作目录", ParameterKind.FilePath),
                new ParameterSpec("runElevated", "以管理员权限运行(有安全风险,默认关闭)", ParameterKind.Bool),
            ]),
            new CommandDescriptor(OpenUrl, "打开网页",
            [
                new ParameterSpec("url", "网址", ParameterKind.Url, Required: true),
            ]),
        ],
        []);

    public static FeatureDescriptor Descriptor { get; } = new(Manifest, ctx => new SystemActionsFeature(ctx));

    private readonly List<IDisposable> _registrations = [];

    public ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        _registrations.Add(context.Commands.Register(OpenApp, OpenAppAsync));
        _registrations.Add(context.Commands.Register(OpenUrl, OpenUrlAsync));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        return ValueTask.CompletedTask;
    }

    private ValueTask OpenAppAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var target = args.GetString("target");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new CommandException(OpenApp, CommandErrorKind.InvalidArguments, "缺少参数 target");
        }

        // runElevated 只在显式为 true 时才保持高权限;缺省、类型不符都按 false 处理
        var request = new LaunchRequest(
            target,
            args.GetString("arguments"),
            args.GetString("workingDirectory"),
            RunElevated: args.GetBool("runElevated") == true);
        return LaunchAsync(OpenApp, request, cancellationToken);
    }

    private ValueTask OpenUrlAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var url = args.GetString("url");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new CommandException(OpenUrl, CommandErrorKind.InvalidArguments, "缺少参数 url");
        }

        return LaunchAsync(OpenUrl, new LaunchRequest(url), cancellationToken);
    }

    private async ValueTask LaunchAsync(CommandId command, LaunchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await context.Launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (LaunchException ex)
        {
            // 用户配置错误(路径不存在等)不是程序 bug:HUD 提示用户,同时让总线记录为命令失败
            await context.Overlay.ShowHudAsync(ex.Message).ConfigureAwait(false);
            throw new CommandException(command, CommandErrorKind.Failed, ex.Message, ex);
        }
    }
}
