namespace Waio.Kit.Commands;

/// <summary>命令的静态描述,绑定编辑器据此展示名字并(未来)自动生成参数表单。</summary>
/// <param name="Id">"&lt;featureId&gt;.&lt;verb&gt;"。</param>
/// <param name="Title">用户在绑定编辑器里看到的名字,如 "显示剪贴板历史"。</param>
/// <param name="Parameters">参数声明,无参数时为空。</param>
public sealed record CommandDescriptor(CommandId Id, string Title, IReadOnlyList<ParameterSpec>? Parameters = null)
{
    public IReadOnlyList<ParameterSpec> Parameters { get; init; } = Parameters ?? [];
}

public enum ParameterKind
{
    String,
    Bool,
    Number,
    FilePath,
    Url,
}

public sealed record ParameterSpec(string Name, string Title, ParameterKind Kind, bool Required = false);
