namespace Waio.Kit.Commands;

public enum CommandErrorKind
{
    /// <summary>命令不存在:提供方功能未启用或已故障。调用方应自行降级(如隐藏按钮)。</summary>
    Unavailable,

    /// <summary>参数缺失或类型不符。</summary>
    InvalidArguments,

    /// <summary>handler 执行中抛出的业务异常,已被总线包装。</summary>
    Failed,
}

public sealed class CommandException : Exception
{
    public CommandException(CommandId commandId, CommandErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        CommandId = commandId;
        Kind = kind;
    }

    public CommandId CommandId { get; }

    public CommandErrorKind Kind { get; }
}
