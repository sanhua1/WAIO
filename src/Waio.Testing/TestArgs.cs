using System.Text.Json;
using Waio.Kit.Commands;

namespace Waio.Testing;

/// <summary>测试里快速构造命令参数:<c>TestArgs.Json("""{"target":"notepad"}""")</c>。</summary>
public static class TestArgs
{
    public static CommandArguments Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        return new CommandArguments(values);
    }
}
