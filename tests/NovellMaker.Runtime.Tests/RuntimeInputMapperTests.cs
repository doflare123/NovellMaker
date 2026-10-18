using Microsoft.Xna.Framework;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeInputMapperTests
{
    [TestMethod]
    public void LetterboxAndMapping_WideWindow_IgnoreBlackBarsAndPreserveLogicalCoordinates()
    {
        var letterbox = RuntimeInputMapper.CalculateLetterbox(1600, 1200);

        Assert.AreEqual(new Rectangle(0, 150, 1600, 900), letterbox);
        Assert.IsFalse(RuntimeInputMapper.TryMapWindowToLogical(new Point(800, 100), letterbox, out _));
        Assert.IsTrue(RuntimeInputMapper.TryMapWindowToLogical(new Point(800, 600), letterbox, out var center));
        Assert.AreEqual(new Point(640, 360), center);
    }

    [TestMethod]
    public void NavigationHitTest_UsesEnabledButtonBoundsOnly()
    {
        var backPoint = RuntimeInputMapper.BackButtonBounds.Center;
        var nextPoint = RuntimeInputMapper.NextButtonBounds.Center;

        Assert.AreEqual(RuntimeAction.Previous, RuntimeInputMapper.HitTestNavigation(backPoint, true, false));
        Assert.AreEqual(RuntimeAction.Next, RuntimeInputMapper.HitTestNavigation(nextPoint, false, true));
        Assert.IsNull(RuntimeInputMapper.HitTestNavigation(backPoint, false, true));
        Assert.IsNull(RuntimeInputMapper.HitTestNavigation(nextPoint, true, false));
        Assert.IsNull(RuntimeInputMapper.HitTestNavigation(new Point(10, 10), true, true));
    }
}
