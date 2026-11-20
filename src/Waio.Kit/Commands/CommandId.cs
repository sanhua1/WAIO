using System.Text.RegularExpressions;

namespace Waio.Kit.Commands;

/// <summary>命令标识,格式 "&lt;featureId&gt;.&lt;verb&gt;",如 "screenshot.captureRegion"。</summary>
public readonly partial record struct CommandId
{
    public string Value { get; }

    public CommandId(string value)
    {
        if (!Pattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"CommandId 格式必须是 '<feature>.<verb>'(feature 全小写,verb 为 camelCase),实际为 '{value}'",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>命令所属功能的 id 部分。</summary>
    public string FeaturePart => Value[..Value.IndexOf('.')];

    public override string ToString() => Value;

    public static implicit operator CommandId(string value) => new(value);

    [GeneratedRegex("^[a-z][a-z0-9]*\\.[a-z][A-Za-z0-9]*$")]
    private static partial Regex Pattern();
}
