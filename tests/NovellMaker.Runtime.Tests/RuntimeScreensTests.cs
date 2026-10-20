using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeScreensTests
{
    private static async Task Drain(RuntimeScreens menus)
    {
        for (var i = 0; i < 1000 && menus.HasPending; i++)
        {
            menus.Poll();
            if (menus.Scripts?.PendingTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(10));
            else await Task.Delay(1);
            menus.Poll();
        }
        Assert.IsFalse(menus.HasPending);
    }

    [TestMethod]
    public async Task ActualLuaContinuationClosesOnceAndEscapeWorksBeforeQueuedHandler()
    {
        await using var menus = new RuntimeScreens(Guid.NewGuid(), enableLua: true);
        menus.TogglePause(); await Drain(menus);
        Assert.AreEqual("Пауза · Esc — назад", menus.Stack.Instances.Single().Definition.Nodes[1].Text);
        Assert.IsTrue(menus.Route(new(ScreenInputKind.Activate)).Consumed);
        Assert.AreEqual(1, menus.Stack.Instances.Length, "The handler must finish before its batch is applied.");
        await Drain(menus); Assert.IsTrue(menus.Stack.Instances.IsEmpty);
        Assert.AreEqual(2, menus.Scripts!.AppliedBatches);
        menus.TogglePause(); await Drain(menus);
        menus.Route(new(ScreenInputKind.Activate));
        Assert.IsTrue(menus.Route(new(ScreenInputKind.Back)).Consumed);
        await Drain(menus); Assert.IsTrue(menus.Stack.Instances.IsEmpty);
        Assert.IsTrue(menus.Scripts.Diagnostics.IsEmpty);
        menus.Reset(); Assert.AreEqual(2L, menus.Scripts.Generation);
    }

    [TestMethod]
    public async Task HandlerFailurePreservesViewAndNativeContinueAndBackRemainUsable()
    {
        await using var menus = new RuntimeScreens(Guid.NewGuid(), enableLua: true, pauseHandler: """
            return {on_event=function() ui.set_text(refs.title,'partial'); local fail; fail() end}
            """);
        menus.TogglePause(); await Drain(menus); var instance = menus.Stack.Instances.Single(); var before = instance.Definition;
        menus.Route(new(ScreenInputKind.Activate)); await Drain(menus);
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(1, menus.Stack.Instances.Length);
        Assert.IsFalse(menus.Scripts!.Diagnostics.IsEmpty);
        Assert.IsTrue(menus.Route(new(ScreenInputKind.Activate)).Consumed);
        Assert.IsTrue(menus.Stack.Instances.IsEmpty);
        menus.TogglePause(); await Drain(menus);
        Assert.IsTrue(menus.Route(new(ScreenInputKind.Back)).Consumed);
        await Drain(menus); Assert.IsTrue(menus.Stack.Instances.IsEmpty);
    }

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
