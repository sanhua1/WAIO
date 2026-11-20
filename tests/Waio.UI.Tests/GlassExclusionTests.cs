using Waio.UI.Theming;

namespace Waio.UI.Tests;

public class GlassExclusionTests
{
    [Fact]
    public void 暂停期间不取背景_恢复后可以取()
    {
        using (GlassExclusion.Suspend())
        {
            Assert.True(GlassExclusion.IsSuspended);
            Assert.False(GlassExclusion.Begin([]));
        }

        Assert.False(GlassExclusion.IsSuspended);
        Assert.True(GlassExclusion.Begin([]));
        GlassExclusion.End();
    }

    [Fact]
    public void 嵌套暂停_全部释放后才恢复_重复释放不出错()
    {
        var outer = GlassExclusion.Suspend();
        var inner = GlassExclusion.Suspend();
        inner.Dispose();
        inner.Dispose();
        Assert.True(GlassExclusion.IsSuspended);
        outer.Dispose();
        Assert.False(GlassExclusion.IsSuspended);
    }
}
