using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ExtendedInputTests
{
    private static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty;
    private static (ScreenStack Stack, ScreenInstance Instance, List<ScreenRoutedInput> Events) Fixture()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
        var button = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Area, new(10, 20, 100, 60));
        var other = button with { Id = Guid.NewGuid(), Bounds = new(200, 20, 100, 60) };
        var stack = new ScreenStack(Guid.NewGuid());
        var instance = stack.Open(Guid.NewGuid(), new(Guid.NewGuid(), [root, button, other]), new(false, false));
        var events = new List<ScreenRoutedInput>(); stack.InputRouted += events.Add;
        return (stack, instance, events);
    }

    [TestMethod]
    public void HoverTransitionsUseClippedHitGeometryAndCoherentFocus()
    {
        var (s, i, events) = Fixture();
        s.Route(new(ScreenInputKind.PointerMove, 20, 30)); s.Route(new(ScreenInputKind.PointerMove, 21, 31));
        s.Route(new(ScreenInputKind.PointerMove, 220, 30)); s.Route(new(ScreenInputKind.PointerMove, -1, 30));
        CollectionAssert.AreEqual(new[] { "hover_enter", "move", "move", "hover_leave", "hover_enter", "move", "hover_leave" }, events.Select(e => e.Kind).ToArray());
        Assert.IsNull(s.Hover); events.Clear();
        s.Route(new(ScreenInputKind.FocusNext)); s.Route(new(ScreenInputKind.FocusNext)); s.Route(new(ScreenInputKind.FocusPrevious));
        CollectionAssert.AreEqual(new[] { "focus", "blur", "focus", "blur", "focus" }, events.Select(e => e.Kind).ToArray());
        Assert.AreEqual(i.Definition.Nodes[1].Id, s.Focus!.NodeId);
    }

    [TestMethod]
    public void DragRetainsOwnerOutsideViewportEndsOnceAndSuppressesClick()
    {
        var (s, i, events) = Fixture(); s.Route(new(ScreenInputKind.PointerDown, 20, 30));
        s.Route(new(ScreenInputKind.PointerMove, 22, 30)); Assert.IsFalse(events.Any(e => e.Kind == "drag_start"));
        s.Route(new(ScreenInputKind.PointerMove, 2000, -50)); s.Route(new(ScreenInputKind.PointerMove, 20, 30));
        Assert.IsNull(s.Route(new(ScreenInputKind.PointerUp, 20, 30)).Activation);
        s.Route(new(ScreenInputKind.PointerUp, 20, 30));
        Assert.AreEqual(1, events.Count(e => e.Kind == "drag_start")); Assert.AreEqual(1, events.Count(e => e.Kind == "drag_end"));
        Assert.IsTrue(events.Where(e => e.Kind == "drag").All(e => e.NodeId == i.Definition.Nodes[1].Id));
        Assert.IsNull(s.PointerCapture);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30)); Assert.IsNotNull(s.Route(new(ScreenInputKind.PointerUp, 20, 30)).Activation);
    }

    [TestMethod]
    [DataRow("modal")] [DataRow("focus")] [DataRow("remove")] [DataRow("disable")] [DataRow("close")] [DataRow("clear")] [DataRow("replace")]
    public void LifecycleInvalidatesCaptureAndLateCommands(string transition)
    {
        var (s, i, events) = Fixture(); s.Route(new(ScreenInputKind.PointerDown, 20, 30));
        var epoch = s.InputEpoch; var sequence = s.PointerSequence;
        switch (transition)
        {
            case "modal": s.Open(Guid.NewGuid(), i.Definition); break;
            case "focus": s.SetActive(false); s.SetActive(true); break;
            case "remove": s.Commit(i, i.Version, ScreenTree.Remove(i.Definition, i.Definition.Nodes[1].Id)); break;
            case "disable": s.Commit(i, i.Version, new(i.Definition.Id, i.Definition.Nodes.Replace(i.Definition.Nodes[1], i.Definition.Nodes[1] with { Enabled = false }))); break;
            case "close": s.Close(i); break;
            case "clear": s.Clear(); break;
            case "replace": s.Replace(i, i.Definition); break;
        }
        Assert.IsNull(s.PointerCapture); Assert.IsNull(s.Focus);
        s.ApplyInputCommands(i, [new("capture_pointer", i.Definition.Nodes[0].Id)], epoch, sequence);
        Assert.IsNull(s.PointerCapture); Assert.IsNull(s.Route(new(ScreenInputKind.PointerUp, 20, 30)).Activation);
        Assert.IsLessThanOrEqualTo(1, events.Count(e => e.Kind == "pointer_cancel"));
    }

    [TestMethod]
    public void CaptureReleaseFocusCommandsRequireOwnLivePointerAndValidNodes()
    {
        var (s, i, events) = Fixture(); var node = i.Definition.Nodes[1].Id; var other = i.Definition.Nodes[2].Id;
        s.ValidateInputCommands(i.Definition, [new("focus", node), new("release_pointer")]);
        foreach (var bad in new[] { new ScreenInputCommand("focus", Guid.NewGuid()), new("capture_pointer", i.Definition.Nodes[0].Id), new("release_pointer", node), new("bad") })
            Assert.Throws<InvalidOperationException>(() => s.ValidateInputCommands(i.Definition, [bad]));
        s.Route(new(ScreenInputKind.PointerDown, 20, 30)); var sequence = s.PointerSequence;
        s.ApplyInputCommands(i, [new("capture_pointer", other), new("focus", other)], s.InputEpoch, sequence);
        Assert.AreEqual(other, s.PointerCapture!.NodeId); Assert.AreEqual(other, s.Focus!.NodeId);
        s.ApplyInputCommands(i, [new("release_pointer")], s.InputEpoch, sequence); Assert.IsNull(s.PointerCapture);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30));
        s.ApplyInputCommands(i, [new("release_pointer")], s.InputEpoch, sequence); Assert.IsNotNull(s.PointerCapture);
    }

    [TestMethod]
    public void KeysAreEdgeTriggeredAndModalResetSuppressesHeldKeyRelease()
    {
        var (s, i, events) = Fixture(); s.Route(new(ScreenInputKind.FocusNext));
        s.Route(new(ScreenInputKind.KeyDown, Key: "A", Shift: true)); s.Route(new(ScreenInputKind.KeyDown, Key: "A"));
        s.Route(new(ScreenInputKind.KeyUp, Key: "A")); s.Route(new(ScreenInputKind.KeyUp, Key: "A"));
        Assert.AreEqual(1, events.Count(e => e.Kind == "key_down")); Assert.AreEqual(1, events.Count(e => e.Kind == "key_up"));
        Assert.IsTrue(events.Single(e => e.Kind == "key_down").Input.Shift);
        s.Route(new(ScreenInputKind.KeyDown, Key: "A")); var modal = s.Open(Guid.NewGuid(), i.Definition); events.Clear();
        s.Route(new(ScreenInputKind.KeyUp, Key: "A")); Assert.IsEmpty(events);
        s.Route(new(ScreenInputKind.KeyDown, Key: "B")); Assert.AreSame(modal, events.Single().Instance);
        Assert.Throws<ArgumentException>(() => s.Route(new(ScreenInputKind.KeyDown, Key: "bad")));
        Assert.Throws<ArgumentException>(() => s.Route(new(ScreenInputKind.PointerMove, double.NaN)));
        s.SetActive(false); Assert.IsTrue(s.Route(new(ScreenInputKind.KeyDown, Key: "C")).Consumed);
    }

    [TestMethod]
    public void LuaBuildsAtomicInputCommandsAndProjectsPhysicalKeys()
    {
        var (_, i, _) = Fixture(); var d = i.Definition; var target = d.Nodes[1].Id;
        var vm = new ScreenLuaVm(d, i.Id, "return {input_events=true,on_event=function(e) ui.capture_pointer(refs.target);ui.focus(refs.target);ui.release_pointer();ui.set_text(refs.target,e.key..':'..tostring(e.shift)..':'..tostring(e.control)..':'..tostring(e.alt)) end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", target));
        var e = new ScreenLuaEvent("key_down", target) { Key = "A", Shift = true, Control = true };
        ScreenLuaProtocol.ValidateEvent(d, e); var changes = vm.Handle(e, Empty);
        Assert.AreEqual("A:true:true:false", changes.Last().Text); Assert.AreEqual(3, ScreenLuaProtocol.Prepare(d, changes).InputCommands.Length);
        foreach (var bad in new[] { e with { Key = null }, e with { Key = "bad" }, e with { Kind = "update" }, e with { Seconds = .1 }, e with { NodeId = null }, e with { InputEpoch = -1 }, e with { PointerSequence = -1 } })
            Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(d, bad));
    }

    [TestMethod]
    [DataRow("ui.capture_pointer(refs.target,1)")]
    [DataRow("ui.release_pointer(refs.target)")]
    [DataRow("ui.focus(nil)")]
    [DataRow("ui.capture_pointer('bad')")]
    public void LuaRejectsInvalidInputArguments(string body)
    {
        var (_, i, _) = Fixture();
        var vm = new ScreenLuaVm(i.Definition, i.Id, "return {input_events=true,on_event=function() " + body + " end}", ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id));
        Assert.Throws<InterpreterException>(() => vm.Handle(new("click", i.Definition.Nodes[1].Id), Empty));
    }

    [TestMethod]
    public async Task RealWorkerReceivesDragKeyboardResetAndCannotRecoverReleasedCapture()
    {
        var (s, i, _) = Fixture(); await using var session = new ScreenLuaSession(s);
        s.InputRouted += e => session.EnqueueInput(e, Empty);
        session.Attach(i, "return {input_events=true,on_event=function(e) if e.kind=='pointer_down' then ui.capture_pointer(refs.target) elseif e.kind=='drag' then ui.set_text(refs.target,'drag:'..e.x) elseif e.kind=='key_down' then ui.set_text(refs.target,e.key) elseif e.kind=='input_reset' then ui.set_text(refs.target,'reset') end end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id));
        await ScreenLuaSessionTests.Drain(session);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30)); s.Route(new(ScreenInputKind.PointerUp, 20, 30));
        await ScreenLuaSessionTests.Drain(session); Assert.IsNull(s.PointerCapture);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30)); await ScreenLuaSessionTests.Drain(session);
        s.Route(new(ScreenInputKind.PointerMove, 1500, 50)); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("drag:1500", i.Definition.Nodes[1].Text);
        s.Route(new(ScreenInputKind.KeyDown, Key: "Left")); await ScreenLuaSessionTests.Drain(session); Assert.AreEqual("Left", i.Definition.Nodes[1].Text);
        s.SetActive(false); await ScreenLuaSessionTests.Drain(session); Assert.AreEqual("reset", i.Definition.Nodes[1].Text);
        Assert.IsNull(s.PointerCapture); Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public void StationaryPointerDoesNotFloodWorkerButDetectsMovedGeometry()
    {
        var (s, i, events) = Fixture(); s.Route(new(ScreenInputKind.PointerMove, 20, 30)); events.Clear();
        for (var n = 0; n < 100; n++) s.Route(new(ScreenInputKind.PointerMove, 20, 30));
        Assert.IsEmpty(events);
        s.Commit(i, i.Version, new(i.Definition.Id, i.Definition.Nodes.Replace(i.Definition.Nodes[1], i.Definition.Nodes[1] with { Bounds = new(400, 20, 100, 60) })));
        s.Route(new(ScreenInputKind.PointerMove, 20, 30)); Assert.AreEqual("hover_leave", events.Single().Kind);
        Assert.IsNull(s.Hover);
    }

    [TestMethod]
    public void KeyUpReturnsToOriginalFocusAndRemovalResetsHeldKeys()
    {
        var (s, i, events) = Fixture(); s.Route(new(ScreenInputKind.FocusNext));
        s.Route(new(ScreenInputKind.KeyDown, Key: "A")); s.Route(new(ScreenInputKind.FocusNext));
        s.Route(new(ScreenInputKind.KeyUp, Key: "A"));
        Assert.AreEqual(i.Definition.Nodes[1].Id, events.Single(e => e.Kind == "key_up").NodeId);
        s.Route(new(ScreenInputKind.KeyDown, Key: "B"));
        s.Commit(i, i.Version, ScreenTree.Remove(i.Definition, i.Definition.Nodes[2].Id));
        Assert.AreEqual(1, events.Count(e => e.Kind == "input_reset")); events.Clear();
        s.Route(new(ScreenInputKind.KeyUp, Key: "B")); Assert.IsEmpty(events);
        var empty = new ScreenStack(Guid.NewGuid()); Assert.IsFalse(empty.Route(new(ScreenInputKind.KeyDown, Key: "A")).Consumed);
    }

    [TestMethod]
    public async Task LegacyHandlersOptOutWithoutChangingClickContract()
    {
        var (s, i, _) = Fixture(); await using var session = new ScreenLuaSession(s);
        session.Attach(i, "return {on_event=function(e) ui.set_text(refs.target,'click only') end}", ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id));
        s.InputRouted += e => session.EnqueueInput(e, Empty);
        await ScreenLuaSessionTests.Drain(session); Assert.IsFalse(session.AcceptsInput(i));
        s.Route(new(ScreenInputKind.FocusNext)); s.Route(new(ScreenInputKind.PointerDown, 20, 30));
        Assert.IsFalse(session.HasPending); Assert.AreEqual("", i.Definition.Nodes[1].Text);
        session.Enqueue(i, new("click", i.Definition.Nodes[1].Id), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("click only", i.Definition.Nodes[1].Text);
    }

    [TestMethod]
    public async Task OverflowStopsHandlerAndClearsPressWithoutReentrantRoutingFailure()
    {
        var (s, i, _) = Fixture(); var connection = new ScreenLuaSessionTests.Connection(true)
        { Response = request => ScreenLuaProtocolTests.Reply(request) with { InputEvents = request.Kind == "load" } };
        await using var session = new ScreenLuaSession(s, () => connection); session.Attach(i, "return {}");
        await ScreenLuaSessionTests.Drain(session); s.InputRouted += e => session.EnqueueInput(e, Empty);
        for (var n = 0; n < ScreenLuaSession.MaximumQueuedEvents; n++) session.Enqueue(i, new("update"), Empty);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30)); Assert.IsFalse(session.IsAvailable(i));
        Assert.IsNull(s.PointerCapture); Assert.IsNull(s.Focus); Assert.AreEqual(0, session.QueuedEvents);
        await ScreenLuaSessionTests.Drain(session); Assert.IsTrue(session.Diagnostics.Any(t => t.Contains("бюджет")));
    }

    [TestMethod]
    public async Task AdjacentDragCoalescesWithoutCrossingSignificantEdges()
    {
        var (s, i, _) = Fixture(); var connection = new ScreenLuaSessionTests.Connection(true)
        { Response = request => ScreenLuaProtocolTests.Reply(request) with { InputEvents = request.Kind == "load" } };
        await using var session = new ScreenLuaSession(s, () => connection); session.Attach(i, "return {}");
        await ScreenLuaSessionTests.Drain(session); s.InputRouted += e => session.EnqueueInput(e, Empty);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30));
        for (var n = 0; n < 1000; n++) s.Route(new(ScreenInputKind.PointerMove, 400 + n, 400));
        Assert.IsLessThan(10, session.QueuedEvents);
        s.Route(new(ScreenInputKind.PointerUp, 2000, 400)); await ScreenLuaSessionTests.Drain(session);
        var kinds = connection.Seen.Where(r => r.Event is not null).Select(r => r.Event!.Kind).ToArray();
        Assert.AreEqual(1, kinds.Count(k => k == "drag")); Assert.IsTrue(Array.IndexOf(kinds, "pointer_up") > Array.IndexOf(kinds, "drag"));
        Assert.AreEqual("drag_end", kinds.Last()); Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task FocusEventFeedbackCannotResetChainBudget()
    {
        var (s, i, _) = Fixture(); await using var session = new ScreenLuaSession(s); s.InputRouted += e => session.EnqueueInput(e, Empty);
        session.Attach(i, "return {input_events=true,on_event=function(e) if e.kind=='focus' then ui.focus(e.node==refs.a and refs.b or refs.a) end end}",
            ImmutableDictionary<string, Guid>.Empty.Add("a", i.Definition.Nodes[1].Id).Add("b", i.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); s.Route(new(ScreenInputKind.FocusNext)); await ScreenLuaSessionTests.Drain(session);
        Assert.IsFalse(session.IsAvailable(i)); Assert.IsNull(s.Focus); Assert.IsTrue(session.Diagnostics.Any(t => t.Contains("глубины")));
    }

    [TestMethod]
    [DataRow("transport")] [DataRow("reset")] [DataRow("dispose")]
    public async Task WorkerAndSessionFailureAlwaysReleaseInput(string transition)
    {
        var (s, i, _) = Fixture(); var connection = new ScreenLuaSessionTests.Connection(true)
        { Response = request => ScreenLuaProtocolTests.Reply(request) with { InputEvents = request.Kind == "load" } };
        await using var session = new ScreenLuaSession(s, () => connection); session.Attach(i, "return {}");
        await ScreenLuaSessionTests.Drain(session); s.InputRouted += e => session.EnqueueInput(e, Empty);
        s.Route(new(ScreenInputKind.PointerDown, 20, 30)); Assert.IsNotNull(s.PointerCapture);
        if (transition == "reset") session.Reset();
        else if (transition == "dispose") session.Dispose();
        else { connection.Response = _ => throw new IOException("broken pipe"); await ScreenLuaSessionTests.Drain(session); }
        Assert.IsNull(s.PointerCapture); Assert.IsNull(s.Focus); Assert.IsNull(s.Hover);
    }

    [TestMethod]
    public async Task RapidFocusRegainCannotDiscardTerminalCancellation()
    {
        var (s, i, _) = Fixture(); await using var session = new ScreenLuaSession(s); s.InputRouted += e => session.EnqueueInput(e, Empty);
        session.Attach(i, "local held=false;return {input_events=true,on_event=function(e) if e.kind=='pointer_down' then held=true elseif e.kind=='pointer_cancel' then held=false elseif e.kind=='key_down' then ui.set_text(refs.target,tostring(held)) end end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id));
        await ScreenLuaSessionTests.Drain(session); s.Route(new(ScreenInputKind.PointerDown, 20, 30)); await ScreenLuaSessionTests.Drain(session);
        s.SetActive(false); s.SetActive(true); await ScreenLuaSessionTests.Drain(session);
        s.Route(new(ScreenInputKind.KeyDown, Key: "A")); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("false", i.Definition.Nodes[1].Text); Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public void PhysicalKeyWhitelistAndRootRoutingAreBoundedAndExact()
    {
        foreach (var key in new[] { "A", "Z", "D0", "D9", "F1", "F12", "Tab", "Enter", "Escape", "Space", "Left", "Right", "Up", "Down",
            "Back", "Delete", "Home", "End", "PageUp", "PageDown", "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt" })
            Assert.IsTrue(ScreenStack.ValidInputKey(key), key);
        foreach (var key in new[] { null, "", "AA", "DX", "D/", "D:", "F13", "a", "TextInput" }) Assert.IsFalse(ScreenStack.ValidInputKey(key));
        var (s, i, events) = Fixture(); Assert.IsFalse(s.Route(new(ScreenInputKind.KeyDown, Key: "A")).Consumed);
        foreach (var bad in new[] { new ScreenInput(ScreenInputKind.PointerDown, Key: "A"), new(ScreenInputKind.PointerDown, Shift: true),
            new(ScreenInputKind.PointerDown, Control: true), new(ScreenInputKind.PointerDown, Alt: true) })
            Assert.Throws<ArgumentException>(() => s.Route(bad));
        Assert.AreEqual(i.Definition.Nodes[0].Id, events.Single().NodeId); s.Route(new(ScreenInputKind.KeyUp, Key: "A"));
        s.Route(new(ScreenInputKind.PointerMove, 20, 30)); s.Route(new(ScreenInputKind.PointerDown, 20, 30));
        s.ReleaseInput(i with { }); Assert.IsNotNull(s.PointerCapture);
        s.ReleaseInput(i); Assert.IsNull(s.Hover); Assert.IsNull(s.PointerCapture);
    }

    [TestMethod]
    public void InputCommandsRejectEveryExtraneousPayloadAndInvalidFinalTarget()
    {
        var (s, i, _) = Fixture(); var d = i.Definition; var id = d.Nodes[1].Id;
        var valid = new ScreenChange("focus", id, null, null);
        foreach (var bad in new[] { valid with { Text = "x" }, valid with { Value = true }, valid with { Patch = new() },
            valid with { ParentId = id }, valid with { Ids = ImmutableDictionary<Guid, Guid>.Empty },
            valid with { Service = new(Guid.NewGuid(), NovellMaker.Core.Execution.GameServiceAction.MainMenu) },
            valid with { Trigger = new("test") }, valid with { State = NovellMaker.Core.Scripting.LuaValue.Null },
            valid with { RandomState = NovellMaker.Core.Scripting.LuaRandomState.Initial },
            valid with { MinigameResult = new(NovellMaker.Core.Minigames.MinigamePhase.Cancelled, NovellMaker.Core.Scripting.LuaValue.Null, "") },
            valid with { Animation = new(Guid.NewGuid(), []) }, valid with { AnimationId = Guid.NewGuid() },
            valid with { Audio = new("stop", Guid.NewGuid()) }, valid with { Kind = "release_pointer" }, valid with { NodeId = Guid.NewGuid() } })
            Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(d, [bad]));
        var batch = ScreenLuaProtocol.Prepare(d, [valid, new("remove", id, null, null)]);
        Assert.Throws<InvalidOperationException>(() => s.ValidateInputCommands(batch.Definition, batch.InputCommands));
        Assert.AreSame(d, i.Definition);
        Assert.AreEqual(1, ScreenLuaProtocol.Prepare(d, [new("release_pointer", null, null, null)]).InputCommands.Length);
        s.ValidateInputCommands(d, [new("capture_pointer", id)]);
    }

    [TestMethod]
    public void OptInHasExactBooleanTypeAndTopLevelInputCannotRun()
    {
        var (_, i, _) = Fixture(); var refs = ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id);
        foreach (var flag in new[] { "1", "'true'", "{}" })
            Assert.Throws<InvalidOperationException>(() => new ScreenLuaVm(i.Definition, i.Id, "return {input_events=" + flag + ",on_event=function()end}", refs));
        Assert.Throws<InterpreterException>(() => new ScreenLuaVm(i.Definition, i.Id, "ui.focus(refs.target);return {input_events=true,on_event=function()end}", refs));
        var vm = new ScreenLuaVm(i.Definition, i.Id, "return {on_event=function()ui.release_pointer()end}", refs);
        Assert.Throws<InterpreterException>(() => vm.Handle(new("click", i.Definition.Nodes[1].Id), Empty));
    }

    [TestMethod]
    [DataRow("ui.capture_pointer(refs.target)")]
    [DataRow("ui.release_pointer()")]
    [DataRow("ui.focus(refs.target)")]
    public async Task RestoringMinigameRejectsInputEffectsAtomically(string code)
    {
        var (s, i, _) = Fixture(); var before = i.Definition; await using var session = new ScreenLuaSession(s);
        var definition = new NovellMaker.Core.Minigames.MinigameDefinition();
        session.Attach(i, "return {input_events=true,on_load=function()ui.set_text(refs.target,'bad');" + code + " end,on_event=function()end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id), state: definition.InitialState,
            minigame: definition, invocation: new(definition.DefaultParameters, true));
        await ScreenLuaSessionTests.Drain(session); Assert.IsFalse(session.IsAvailable(i)); Assert.AreSame(before, i.Definition);
        Assert.IsNull(s.Focus); Assert.IsNull(s.PointerCapture);
    }

    [TestMethod]
    public async Task RemovedFinalFocusTargetRejectsWholeVisualAndStateBatch()
    {
        var (s, i, _) = Fixture(); var before = i.Definition; await using var session = new ScreenLuaSession(s);
        session.Attach(i, "return {input_events=true,on_event=function()ui.focus(refs.target);ui.remove(refs.target);ui.set_state({bad=true})end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", i.Definition.Nodes[1].Id));
        await ScreenLuaSessionTests.Drain(session); session.Enqueue(i, new("click", i.Definition.Nodes[1].Id), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsFalse(session.IsAvailable(i)); Assert.AreSame(before, i.Definition);
        Assert.AreEqual(NovellMaker.Core.Scripting.LuaValueKind.Null, session.GetState(i).Kind);
    }

    [TestMethod]
    public async Task FocusNotificationsReserveSpaceBeforeVisualAndStateCommit()
    {
        var (s, i, _) = Fixture(); var before = i.Definition;
        var connection = new ScreenLuaSessionTests.Connection(true) { Response = r => r.Event?.Kind == "click"
            ? ScreenLuaProtocolTests.Reply(r, new("focus", before.Nodes[2].Id, null, null), new("text", before.Nodes[0].Id, "bad", null),
                new("state", null, null, null) { State = NovellMaker.Core.Scripting.LuaValue.FromBoolean(true) })
            : ScreenLuaProtocolTests.Reply(r) with { InputEvents = r.Kind == "load" } };
        await using var session = new ScreenLuaSession(s, () => connection); session.Attach(i, "return {}"); await ScreenLuaSessionTests.Drain(session);
        s.Route(new(ScreenInputKind.FocusNext)); session.Enqueue(i, new("click", before.Nodes[1].Id), Empty);
        for (var n = 1; n < ScreenLuaSession.MaximumQueuedEvents; n++) session.Enqueue(i, new("update"), Empty);
        await ScreenLuaSessionTests.Drain(session); Assert.IsFalse(session.IsAvailable(i)); Assert.AreSame(before, i.Definition);
        Assert.AreEqual(NovellMaker.Core.Scripting.LuaValueKind.Null, session.GetState(i).Kind);
    }

    [TestMethod]
    public void InputBudgetIncludesRemovalHoverKeysAndCommandTransitions()
    {
        var (s, i, _) = Fixture(); var d = i.Definition; var id = d.Nodes[1].Id;
        s.Route(new(ScreenInputKind.PointerMove, 20, 30)); s.Route(new(ScreenInputKind.PointerDown, 20, 30)); s.Route(new(ScreenInputKind.KeyDown, Key: "A"));
        Assert.AreEqual(4, s.InputEventBudget(i, ScreenTree.Remove(d, id), []));
        Assert.AreEqual(1, s.InputEventBudget(i, d, [new("release_pointer"), new("release_pointer")]));
        Assert.AreEqual(2, s.InputEventBudget(i, d, [new("focus", id), new("focus", d.Nodes[2].Id)]));
        s.ReleaseInput(i); Assert.AreEqual(1, s.InputEventBudget(i, d, [new("focus", id)]));
    }
}
