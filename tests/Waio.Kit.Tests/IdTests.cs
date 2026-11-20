using Waio.Kit.Commands;
using Waio.Kit.Features;

namespace Waio.Kit.Tests;

public class IdTests
{
    [Theory]
    [InlineData("screenshot.captureRegion", "screenshot")]
    [InlineData("clipboard.showPanel", "clipboard")]
    [InlineData("translate.text", "translate")]
    public void CommandId_合法格式(string value, string featurePart)
    {
        var id = new CommandId(value);

        Assert.Equal(value, id.ToString());
        Assert.Equal(featurePart, id.FeaturePart);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noverb")]
    [InlineData("Clipboard.show")]
    [InlineData("clipboard.Show")]
    [InlineData("clipboard.show.panel")]
    [InlineData("clip-board.show")]
    public void CommandId_非法格式抛异常(string value)
    {
        Assert.Throws<ArgumentException>(() => new CommandId(value));
    }

    [Theory]
    [InlineData("clipboard")]
    [InlineData("keymap2")]
    public void FeatureId_合法(string value)
    {
        Assert.Equal(value, new FeatureId(value).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Clipboard")]
    [InlineData("key-map")]
    [InlineData("2fa")]
    public void FeatureId_非法抛异常(string value)
    {
        Assert.Throws<ArgumentException>(() => new FeatureId(value));
    }
}
