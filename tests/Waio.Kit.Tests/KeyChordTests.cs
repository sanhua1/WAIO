using Waio.Kit.Input;

namespace Waio.Kit.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+shift+v", KeyModifiers.Ctrl | KeyModifiers.Shift, "v")]
    [InlineData("Alt+Space", KeyModifiers.Alt, "space")]
    [InlineData("win+f1", KeyModifiers.Win, "f1")]
    [InlineData("f13", KeyModifiers.None, "f13")]
    [InlineData(" Ctrl + Alt + Delete ", KeyModifiers.Ctrl | KeyModifiers.Alt, "delete")]
    public void Parse_接受大小写和空格(string text, KeyModifiers modifiers, string key)
    {
        var chord = KeyChord.Parse(text);

        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(key, chord.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl+")]
    [InlineData("ctrl+shift")]
    [InlineData("a+b+c")]
    [InlineData("f24+f24")]
    [InlineData("ctrl++v")]
    public void TryParse_拒绝非法输入(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Theory]
    [InlineData("f24+j", KeyModifiers.None, "f24", "j")]
    [InlineData("Ctrl+F24+J", KeyModifiers.Ctrl, "f24", "j")]
    [InlineData("shift+capslock+1", KeyModifiers.Shift, "capslock", "1")]
    public void Parse_两个非修饰键时_前一个是自定义修饰键(string text, KeyModifiers modifiers, string hold, string key)
    {
        var chord = KeyChord.Parse(text);

        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(hold, chord.Hold);
        Assert.Equal(key, chord.Key);
    }

    [Fact]
    public void 自定义修饰键_参与相等比较_格式化在标准修饰键之后()
    {
        var chord = new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Alt, "j", "f24");

        Assert.Equal("ctrl+alt+f24+j", chord.ToString());
        Assert.NotEqual(new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Alt, "j"), chord);
        Assert.Equal(KeyChord.Parse("alt+ctrl+f24+j"), chord);
    }

    [Fact]
    public void ToString_修饰键顺序固定()
    {
        var chord = new KeyChord(KeyModifiers.Win | KeyModifiers.Alt | KeyModifiers.Ctrl | KeyModifiers.Shift, "k");

        Assert.Equal("ctrl+shift+alt+win+k", chord.ToString());
    }

    [Theory]
    [InlineData("ctrl+shift+v")]
    [InlineData("alt+tab")]
    [InlineData("f5")]
    [InlineData("ctrl+f24+j")]
    public void 解析后再格式化保持不变(string text)
    {
        Assert.Equal(text, KeyChord.Parse(text).ToString());
    }
}
