using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeScreensTests
{
    [TestMethod]
    public void PauseContinueConsumesEnterAndMouseInsteadOfAdvancingStory()
    {
        var menus = new RuntimeScreens(Guid.NewGuid());
        Assert.IsFalse(menus.Route(new(ScreenInputKind.Activate)).Consumed);
        menus.TogglePause(); Assert.IsTrue(menus.Stack.PausesScenario);
        var result = menus.Route(new(ScreenInputKind.Activate));
        Assert.IsTrue(result.Consumed); Assert.AreEqual(RuntimeScreens.ContinueId, result.Activation?.NodeId);
        Assert.IsTrue(menus.Stack.Instances.IsEmpty);
        menus.TogglePause();
        Assert.IsNull(menus.Route(new(ScreenInputKind.PointerUp, 450, 320)).Activation);
        menus.Route(new(ScreenInputKind.PointerDown, 450, 320));
        Assert.IsTrue(menus.Route(new(ScreenInputKind.PointerUp, 450, 320)).Consumed);
        Assert.IsTrue(menus.Stack.Instances.IsEmpty);
    }

    [TestMethod]
    public void NestedMixerReturnsToPauseAndEveryRealButtonUsesSameRouting()
    {
        var menus = new RuntimeScreens(Guid.NewGuid()); menus.TogglePause();
        var pause = menus.Stack.Instances.Single();
        menus.Route(new(ScreenInputKind.FocusNext)); menus.Route(new(ScreenInputKind.FocusNext));
        menus.Route(new(ScreenInputKind.Activate));
        Assert.IsTrue(menus.ShowMixer); Assert.AreEqual(2, menus.Stack.Instances.Length);
        foreach (var (id, command) in RuntimeScreens.MixerCommands)
        {
            var x = command.Change switch { -1 => 760, 1 => 870, _ => 1000 };
            var y = 170 + (int)command.Group * 70;
            Assert.IsTrue(menus.Route(new(ScreenInputKind.PointerDown, x, y)).Consumed);
            Assert.AreEqual(id, menus.Route(new(ScreenInputKind.PointerUp, x, y)).Activation?.NodeId);
        }
        Assert.IsTrue(menus.Route(new(ScreenInputKind.Back)).Consumed);
        Assert.AreSame(pause, menus.Stack.Instances.Single()); Assert.IsTrue(menus.Stack.PausesScenario);
        menus.TogglePause(); Assert.IsFalse(menus.Stack.PausesScenario);
        Assert.IsFalse(menus.Route(new(ScreenInputKind.Back)).Consumed);
        menus.ToggleMixer(); menus.ToggleMixer(); Assert.IsFalse(menus.ShowMixer);
        menus.TogglePause(); menus.ToggleMixer(); menus.TogglePause();
        Assert.AreEqual(1, menus.Stack.Instances.Length);
        menus.Stack.Clear(); Assert.IsFalse(menus.ShowMixer);
    }
}
