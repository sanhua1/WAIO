using System.Text.RegularExpressions;

namespace Waio.Kit.Features;

/// <summary>功能的全局唯一标识,如 "clipboard"。一经发布永不改名(配置文件和数据目录都依赖它)。</summary>
public readonly partial record struct FeatureId
{
    public string Value { get; }

    public FeatureId(string value)
    {
        if (!Pattern().IsMatch(value))
        {
            throw new ArgumentException($"FeatureId 必须是小写字母/数字组成的单个词,实际为 '{value}'", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value;

    public static implicit operator FeatureId(string value) => new(value);

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex Pattern();
}
