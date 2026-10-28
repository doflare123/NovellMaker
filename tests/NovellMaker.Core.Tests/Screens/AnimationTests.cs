using System.Collections.Immutable;
using System.Text.Json;
using MoonSharp.Interpreter;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Animations;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class AnimationTests
{
    [TestMethod]
    [DataRow("requested")] [DataRow("property_set")] [DataRow("removed")] [DataRow("reparented")] [DataRow("replaced")]
    public void CancellationPreservesBothLaunchAndCancellingCauseAndRejectsOverBudgetAtomically(string reason)
    {
        var d = ScreenLuaProtocolTests.Definition(); var id = Guid.NewGuid();
        var start = new ScreenChange("animate", null, null, null) { Animation = new(id, [new(d.Nodes[1].Id, "x", 50, 0, 1, "linear")]) };
        var command = reason switch
        {
            "requested" => new ScreenChange("cancel_animation", null, null, null) { AnimationId = id },
            "property_set" => new ScreenChange("geometry", d.Nodes[1].Id, null, null) { Patch = new() { X = 20 } },
            "removed" => new ScreenChange("remove", d.Nodes[1].Id, null, null),
            "reparented" => new ScreenChange("parent", d.Nodes[1].Id, null, null) { ParentId = d.Nodes[0].Id },
            _ => start with { Animation = start.Animation! with { Id = Guid.NewGuid() } }
        };
        var a = new ScreenAnimator().Prepare(d, [start], 2);
        Assert.AreEqual(32, a.Prepare(d, [command], 31).Drain().Single().ChainDepth);
        Assert.Throws<InvalidOperationException>(() => a.Prepare(d, [command], 32));
        Assert.AreEqual(1, a.ActiveCount); Assert.IsEmpty(a.Drain());
        var deep = new ScreenAnimator().Prepare(d, [start], 31);
        Assert.AreEqual(32, deep.Prepare(d, [command], 0).Drain().Single().ChainDepth);
    }

    [TestMethod]
    public void CompletedAnimationRetainsOriginThroughPreparationAndDepthIsHostOnly()
    {
        var d = ScreenLuaProtocolTests.Definition(); var start = Start(Animation(d));
        var a = new ScreenAnimator().Prepare(d, [start], 31);
        a = a.Prepare(d, [new("text", d.Nodes[1].Id, "unrelated", null)], 0);
        a.Tick(d, 2); var result = a.Drain().Single(); Assert.AreEqual(32, result.ChainDepth);
        var json = JsonSerializer.Serialize(result, ScreenLuaProtocol.Json); Assert.IsFalse(json.Contains("ChainDepth", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScreenAnimationResult>(json[..^1] + ",\"ChainDepth\":0}", ScreenLuaProtocol.Json));
        Assert.Throws<InvalidOperationException>(() => new ScreenAnimator().Prepare(d, [start], 32));
    }

    [TestMethod]
    public async Task ActualWorkerAnimationOnlyChainHasTheSameBudget()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "local function go() ui.animate(ui.tween(refs.target,{x=20},0.001)) end; return {on_load=go,on_event=go}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", instance.Definition.Nodes[1].Id));
        await ScreenLuaSessionTests.Drain(session);
        for (var i = 0; i < 40 && session.IsAvailable(instance); i++)
        { session.AdvanceAnimations(TimeSpan.FromSeconds(1), _ => true); await ScreenLuaSessionTests.Drain(session); }
        Assert.IsFalse(session.IsAvailable(instance)); StringAssert.Contains(session.Diagnostics.Single(), "глубина"); Assert.AreEqual(0, session.ActiveAnimations);
    }
    private static ScreenDefinition LargeScaledNode()
    {
        var root = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Panel, new(0, 0, 100, 100)) { Geometry = new() { ScaleX = 1000 } };
        return new(Guid.NewGuid(), [root, new(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(90000, 0, 10000, 30))]);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void JointGeometryValidationAndPlaybackAreIndependentOfPropertyDeclarationOrder(bool reversed)
    {
        var d = LargeScaledNode(); var id = d.Nodes[1].Id;
        ImmutableArray<ScreenTween> tracks = [new(id, "x", 0, 0, 1, "linear"), new(id, "width", 100000, 0, 1, "linear")];
        var animation = new ScreenAnimation(Guid.NewGuid(), reversed ? [.. tracks.Reverse()] : tracks);
        animation.Validate(d); var player = Play(d, animation); var frame = d;
        for (var i = 1; i <= 4; i++)
        {
            frame = player.Tick(frame, .25);
            Assert.AreEqual(90000 * (1 - i / 4d), frame.Nodes[1].Bounds.X, 1e-8);
            Assert.AreEqual(10000 + 90000 * i / 4d, frame.Nodes[1].Bounds.Width, 1e-8);
            Assert.AreEqual(100000000, frame.AbsoluteBounds(id).X + frame.AbsoluteBounds(id).Width, 1e-6);
        }
        Assert.AreEqual("completed", player.Drain().Single().Status);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void ValidationUsesActualTimeAtStaggeredBoundariesInsteadOfApplyingFutureTargets(bool reversed)
    {
        var d = LargeScaledNode(); var id = d.Nodes[1].Id;
        ImmutableArray<ScreenTween> valid = [new(id, "x", 0, 0, 2, "linear"), new(id, "width", 100000, 1, 1, "linear")];
        new ScreenAnimation(Guid.NewGuid(), reversed ? [.. valid.Reverse()] : valid).Validate(d);
        // Both final values are legal together, but at t=1 the slower x track is only half complete.
        var invalid = valid.SetItem(1, valid[1] with { Start = 0 });
        Assert.Throws<ArgumentException>(() => new ScreenAnimation(Guid.NewGuid(), reversed ? [.. invalid.Reverse()] : invalid).Validate(d));
    }

    [TestMethod]
    [DataRow("ui.tween(refs.target,{x=0,width=100000},1)")]
    [DataRow("ui.tween(refs.target,{width=100000,x=0},1)")]
    [DataRow("ui.parallel({ui.tween(refs.target,{width=100000},1),ui.tween(refs.target,{x=0},1)})")]
    [DataRow("ui.parallel({ui.tween(refs.target,{x=0},1),ui.tween(refs.target,{width=100000},1)})")]
    public void LuaDescriptionsDeferWorldValidationUntilTheWholeParallelPlanIsKnown(string expression)
    {
        var d = LargeScaledNode(); var vm = Vm(d, "ui.animate(" + expression + ")");
        var changes = vm.Handle(null, ImmutableDictionary<string, string>.Empty);
        ScreenLuaProtocol.Prepare(d, changes);
        var animator = new ScreenAnimator().Prepare(d, changes); var frame = animator.Tick(d, .5);
        Assert.AreEqual(45000, frame.Nodes[1].Bounds.X); Assert.AreEqual(55000, frame.Nodes[1].Bounds.Width);
        Assert.AreEqual("completed", AfterCompletion().Status);
        ScreenAnimationResult AfterCompletion() { animator.Tick(frame, .5); return animator.Drain().Single(); }
    }
    private static ScreenAnimation Animation(ScreenDefinition d, string property = "x", double to = 90, double start = 0, double seconds = 1, string easing = "linear") =>
        new(Guid.NewGuid(), [new(d.Nodes[1].Id, property, to, start, seconds, easing)]);
    private static ScreenChange Start(ScreenAnimation animation) => new("animate", null, null, null) { Animation = animation };
    private static ScreenAnimator Play(ScreenDefinition d, ScreenAnimation animation) => new ScreenAnimator().Prepare(d, [Start(animation)]);

    [TestMethod]
    [DataRow("linear", .25)] [DataRow("in_quad", .0625)] [DataRow("out_quad", .4375)]
    [DataRow("in_out_quad", .125)] [DataRow("in_cubic", .015625)] [DataRow("out_cubic", .578125)] [DataRow("in_out_cubic", .0625)]
    public void EasingHasExactEndpointsAndExpectedQuarter(string easing, double quarter)
    {
        Assert.AreEqual(0, ScreenAnimation.Ease(easing, 0)); Assert.AreEqual(1, ScreenAnimation.Ease(easing, 1));
        Assert.AreEqual(quarter, ScreenAnimation.Ease(easing, .25), 1e-10);
        if (easing.StartsWith("in_out", StringComparison.Ordinal)) Assert.AreEqual(1 - quarter, ScreenAnimation.Ease(easing, .75), 1e-10);
        var d = ScreenLuaProtocolTests.Definition(); var a = Play(d, Animation(d, easing: easing));
        var frame = a.Tick(d, .25); Assert.AreEqual(10 + 80 * quarter, frame.Nodes[1].Bounds.X, 1e-10);
        frame = a.Tick(frame, 10000); Assert.AreEqual(90, frame.Nodes[1].Bounds.X);
        Assert.AreEqual("completed", a.Drain().Single().Status); Assert.IsEmpty(a.Drain()); Assert.IsFalse(a.Busy);
    }

    [TestMethod]
    public void LongFrameMatchesSubstepsAcrossNestedSequenceAndParallelBoundaries()
    {
        var d = ScreenLuaProtocolTests.Definition(); var id = d.Nodes[1].Id;
        var animation = new ScreenAnimation(Guid.NewGuid(), [new(id, "x", 90, 0, 1, "linear"), new(id, "y", 50, 0, 2, "linear"),
            new(id, "x", 30, 2, 1, "in_out_quad"), new(id, "opacity", .5, 2, 1, "linear")]);
        var fast = Play(d, animation); var slow = Play(d, animation);
        var actual = fast.Tick(d, 2.5); var expected = d;
        for (var i = 0; i < 25; i++) expected = slow.Tick(expected, .1);
        Assert.AreEqual(60, actual.Nodes[1].Bounds.X, 1e-8); Assert.AreEqual(.75, actual.Nodes[1].Geometry.Opacity, 1e-8);
        Assert.AreEqual(expected.Nodes[1].Bounds.X, actual.Nodes[1].Bounds.X, 1e-8);
        actual = fast.Tick(actual, .5); Assert.AreEqual(30, actual.Nodes[1].Bounds.X); Assert.AreEqual(50, actual.Nodes[1].Bounds.Y);
        Assert.AreEqual(1, fast.Drain().Count()); Assert.AreEqual(0, fast.ActiveCount);
    }

    [TestMethod]
    public void ParallelChildrenWithDisjointIntervalsApplyByTimeRatherThanDeclarationOrder()
    {
        var d = ScreenLuaProtocolTests.Definition(); var id = d.Nodes[1].Id;
        var animation = new ScreenAnimation(Guid.NewGuid(), [new(id, "opacity", .5, 0, 1, "linear"),
            new(id, "x", 90, 1, 1, "linear"), new(id, "x", 50, 0, 1, "linear")]);
        var fast = Play(d, animation); var frame = fast.Tick(d, 1.5);
        Assert.AreEqual(70, frame.Nodes[1].Bounds.X, 1e-10);
        var slow = Play(d, animation); var split = slow.Tick(d, 1); split = slow.Tick(split, .5);
        Assert.AreEqual(frame.Nodes[1], split.Nodes[1]);
        Assert.AreEqual(90, fast.Tick(frame, .5).Nodes[1].Bounds.X);
    }

    [TestMethod]
    [DataRow("x", 90d)] [DataRow("y", 90d)] [DataRow("width", 90d)] [DataRow("height", 90d)]
    [DataRow("anchor_x", .5)] [DataRow("anchor_y", .5)] [DataRow("pivot_x", .5)] [DataRow("pivot_y", .5)]
    [DataRow("scale_x", 2d)] [DataRow("scale_y", 2d)] [DataRow("rotation", 180d)] [DataRow("opacity", .25)]
    public void AllWhitelistedPropertiesInterpolate(string property, double target)
    {
        var d = ScreenLuaProtocolTests.Definition(); var a = Play(d, Animation(d, property, target));
        var frame = a.Tick(d, .5); Assert.AreEqual((ScreenAnimation.Read(d.Nodes[1], property) + target) / 2, ScreenAnimation.Read(frame.Nodes[1], property), 1e-10);
        frame = a.Tick(frame, .5); Assert.AreEqual(target, ScreenAnimation.Read(frame.Nodes[1], property), 1e-10);
        Assert.AreEqual("property_set", Play(d, Animation(d, property, target)).Prepare(d,
            [new("geometry", d.Nodes[1].Id, null, null) { Patch = ScreenAnimation.Patch(property, target) }]).Drain().Single().Reason);
    }

    [TestMethod]
    [DataRow("x", double.NaN)] [DataRow("x", double.PositiveInfinity)] [DataRow("x", 100001d)]
    [DataRow("width", 0d)] [DataRow("height", -1d)] [DataRow("scale_x", -1d)] [DataRow("scale_y", 0d)]
    [DataRow("opacity", 2d)] [DataRow("anchor_x", -1d)] [DataRow("pivot_y", 2d)] [DataRow("rotation", 360001d)]
    [DataRow("color", 1d)] [DataRow("order", 1d)] [DataRow("clip_children", 1d)]
    public void InvalidPropertiesCannotStart(string property, double value)
    { var d = ScreenLuaProtocolTests.Definition(); Assert.Throws<ArgumentException>(() => Animation(d, property, value).Validate(d)); }

    [TestMethod]
    [DataRow(-1d, 1d)] [DataRow(0d, 0d)] [DataRow(0d, -1d)] [DataRow(0d, double.NaN)]
    [DataRow(double.PositiveInfinity, 1d)] [DataRow(3599d, 2d)]
    public void InvalidTimeIsRejected(double start, double seconds)
    { var d = ScreenLuaProtocolTests.Definition(); Assert.Throws<ArgumentException>(() => Animation(d, start: start, seconds: seconds).Validate(d)); }

    [TestMethod]
    public void InvalidShapesForeignNodesAndConflictingParallelWritesAreRejected()
    {
        var d = ScreenLuaProtocolTests.Definition(); var a = Animation(d);
        foreach (var invalid in new[] { a with { Id = Guid.Empty }, a with { Tracks = [] }, a with { Tracks = default },
            a with { Tracks = [null!] }, a with { Tracks = [a.Tracks[0] with { NodeId = Guid.NewGuid() }] },
            a with { Tracks = [a.Tracks[0] with { Easing = "bad" }] }, a with { Tracks = [.. Enumerable.Repeat(a.Tracks[0], 65)] },
            a with { Tracks = [a.Tracks[0], a.Tracks[0] with { Start = .5 }] } })
            Assert.Throws<ArgumentException>(() => invalid.Validate(d));
        Assert.Throws<ArgumentException>(() => ScreenAnimation.Ease("bad", .5));
    }

    [TestMethod]
    public void NewGroupCancelsEntireConflictingGroupAndRetainsCurrentPosition()
    {
        var d = ScreenLuaProtocolTests.Definition(); var first = Animation(d); var a = Play(d, first); d = a.Tick(d, .5);
        var second = Animation(d, to: 70); var next = a.Prepare(d, [Start(second)]);
        Assert.AreEqual(1, a.ActiveCount); Assert.AreEqual(1, next.ActiveCount);
        var result = next.Drain().Single(); Assert.AreEqual(first.Id, result.Id); Assert.AreEqual("replaced", result.Reason);
        d = next.Tick(d, .5); Assert.AreEqual(60, d.Nodes[1].Bounds.X, 1e-10);
        next = next.Prepare(d, [new("cancel_animation", null, null, null) { AnimationId = second.Id }]);
        Assert.AreEqual("requested", next.Drain().Single().Reason); var same = next.Tick(d, 10); Assert.AreSame(d, same);
        Assert.IsEmpty(next.Prepare(d, [new("cancel_animation", null, null, null) { AnimationId = second.Id }]).Drain());
    }

    [TestMethod]
    public void SetterRemovalAndReparentCancelButUnrelatedPropertiesAndTextDoNot()
    {
        var d = ScreenLuaProtocolTests.Definition(); var id = d.Nodes[1].Id; var a = Play(d, Animation(d));
        Assert.AreEqual(1, a.Prepare(d, [new("text", id, "new", null), new("geometry", id, null, null) { Patch = new() { Opacity = .5 } }]).ActiveCount);
        Assert.AreEqual("property_set", a.Prepare(d, [new("geometry", id, null, null) { Patch = new() { X = 20 } }]).Drain().Single().Reason);
        Assert.AreEqual("removed", a.Prepare(d, [new("remove", id, null, null)]).Drain().Single().Reason);
        Assert.AreEqual("reparented", a.Prepare(d, [new("parent", id, null, null) { ParentId = d.Nodes[0].Id }]).Drain().Single().Reason);
        Assert.Throws<ArgumentException>(() => a.Prepare(d, [Start(Animation(d) with { Id = Guid.Empty })]));
    }

    [TestMethod]
    public void LiveGroupAndTrackQuotasAreAtomic()
    {
        var d = ScreenLuaProtocolTests.Definition(); var animator = new ScreenAnimator();
        var changes = Enumerable.Range(0, 17).Select(i => Start(Animation(d, to: 20 + i))).ToImmutableArray();
        var replaced = animator.Prepare(d, changes); Assert.AreEqual(17, replaced.Reserved); Assert.AreEqual(0, animator.Reserved);
        var excessive = Enumerable.Range(0, 129).Select(i => Start(Animation(d, to: 20))).ToImmutableArray();
        Assert.Throws<InvalidOperationException>(() => animator.Prepare(d, excessive)); Assert.AreEqual(0, animator.Reserved);
        Assert.Throws<ArgumentOutOfRangeException>(() => animator.Tick(d, -1)); Assert.Throws<ArgumentOutOfRangeException>(() => animator.Tick(d, double.NaN));
        var root = d.Nodes[0]; var many = new ScreenDefinition(d.Id, [root, .. Enumerable.Range(0, 320).Select(_ =>
            new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(0, 0, 10, 10)))]);
        Assert.Throws<InvalidOperationException>(() => animator.Prepare(many,
            [.. many.Nodes.Skip(1).Take(17).Select(n => Start(new(Guid.NewGuid(), [new(n.Id, "x", 20, 0, 1, "linear")])))]));
        Assert.Throws<InvalidOperationException>(() => animator.Prepare(many,
            [.. Enumerable.Range(0, 5).Select(i => Start(new(Guid.NewGuid(), [.. many.Nodes.Skip(1 + i * 64).Take(64).Select(n => new ScreenTween(n.Id, "x", 20, 0, 1, "linear"))])))]));
        var one = Animation(d); Assert.Throws<InvalidOperationException>(() => Play(d, one).Prepare(d, [Start(one)]));
    }

    [TestMethod]
    public void InvalidIntermediateWorldGeometryFailsOnceAndRetainsLastValidFrame()
    {
        var d = ScreenLuaProtocolTests.Definition(); var root = d.Nodes[0] with { Geometry = new() { ScaleX = 1000 } };
        var child = d.Nodes[1] with { Bounds = new(0, 0, 10000, 30), Geometry = new() { ScaleX = .001 } };
        d = new(d.Id, [root, child]);
        var a = new ScreenAnimator().Prepare(d, [Start(new(Guid.NewGuid(), [new(root.Id, "scale_x", .001, 0, 1, "linear"), new(child.Id, "scale_x", 1000, 0, 1, "linear")]))], 17);
        var after = a.Tick(d, .5); Assert.AreEqual(d.Nodes[0], after.Nodes[0]); Assert.AreEqual(d.Nodes[1], after.Nodes[1]);
        var result = a.Drain().Single(); Assert.AreEqual("failed", result.Status); Assert.AreEqual(18, result.ChainDepth);
        Assert.IsEmpty(a.Drain()); Assert.AreEqual(0, a.ActiveCount);
    }

    private static ScreenLuaVm Vm(ScreenDefinition d, string body) => new(d, Guid.NewGuid(), "return {on_load=function() " + body + " end,on_event=function() end}",
        ImmutableDictionary<string, Guid>.Empty.Add("target", d.Nodes[1].Id));

    [TestMethod]
    public void LuaBuildsNestedDescriptionsAndRoundTripsStrictWire()
    {
        var d = ScreenLuaProtocolTests.Definition(); var vm = Vm(d, """
            local a=ui.tween(refs.target,{x=50},1)
            local b=ui.tween(refs.target,{opacity=0.5},2,'out_quad')
            local c=ui.tween(refs.target,{x=20},1,'in_cubic')
            local id=ui.animate(ui.sequence({ui.parallel({a,b}),c}))
            ui.cancel_animation(id)
            """);
        var batch = vm.Handle(null, ImmutableDictionary<string, string>.Empty);
        var copy = JsonSerializer.Deserialize<ImmutableArray<ScreenChange>>(JsonSerializer.Serialize(batch, ScreenLuaProtocol.Json), ScreenLuaProtocol.Json);
        Assert.AreEqual(3, copy[0].Animation!.Tracks.Length); Assert.AreEqual(2, copy[0].Animation!.Tracks[2].Start);
        Assert.AreEqual(copy[0].Animation!.Id, copy[1].AnimationId); ScreenLuaProtocol.Prepare(d, copy);
    }

    [TestMethod]
    public void DeletedTableEntriesDoNotBecomePropertiesOrSequenceHoles()
    {
        var d = ScreenLuaProtocolTests.Definition();
        var vm = Vm(d, """
            local props={x=50,color='old'};props.color=nil
            local a=ui.tween(refs.target,props,1)
            local children={a,a,a};children[3]=nil
            ui.animate(ui.sequence(children))
            """);
        Assert.AreEqual(2, vm.Handle(null, ImmutableDictionary<string, string>.Empty).Single().Animation!.Tracks.Length);
        Assert.Throws<Exception>(() => Vm(d, "local a=ui.tween(refs.target,{x=50},1);local t={a,a,a};t[2]=nil;ui.sequence(t)")
            .Handle(null, ImmutableDictionary<string, string>.Empty));
    }

    [TestMethod]
    [DataRow("ui.tween(refs.target,{},1)")] [DataRow("ui.tween(refs.target,{x='2'},1)")]
    [DataRow("ui.tween(refs.target,{x=2},'1')")] [DataRow("ui.tween(refs.target,{x=2},1,'bad')")]
    [DataRow("ui.tween(refs.target,{x=2},0)")] [DataRow("ui.tween(refs.target,{x=2},1,nil)")]
    [DataRow("ui.tween(refs.target,{x=2},1,'linear',1)")] [DataRow("ui.tween('foreign',{x=2},1)")]
    [DataRow("ui.sequence({})")] [DataRow("ui.parallel(1)")] [DataRow("ui.sequence({[2]='bad'})")]
    [DataRow("ui.sequence({a='bad'})")] [DataRow("ui.animate('unknown')")] [DataRow("ui.cancel_animation('bad')")]
    [DataRow("ui.animate()")] [DataRow("ui.sequence({},1)")]
    [DataRow("for i=1,129 do ui.tween(refs.target,{x=2},1) end")]
    [DataRow("local a=ui.tween(refs.target,{x=2},1);ui.animate(ui.parallel({a,a}))")]
    [DataRow("local a=ui.tween(refs.target,{x=2},3600);ui.sequence({a,a})")]
    [DataRow("local a=ui.tween(refs.target,{x=2},1);for i=1,7 do a=ui.sequence({a,a}) end")]
    public void LuaRejectsInvalidArgumentsAndQuotas(string body)
    {
        var d = ScreenLuaProtocolTests.Definition();
        Assert.Throws<Exception>(() => Vm(d, body).Handle(null, ImmutableDictionary<string, string>.Empty));
    }

    [TestMethod]
    public void ProtocolRejectsMixedPayloadsAndInvalidAnimationEvents()
    {
        var d = ScreenLuaProtocolTests.Definition(); var a = Animation(d);
        foreach (var c in new[] { Start(a) with { NodeId = d.Nodes[1].Id }, Start(a) with { AnimationId = Guid.NewGuid() },
            new ScreenChange("cancel_animation", null, null, null), new ScreenChange("text", d.Nodes[1].Id, "x", null) { Animation = a } })
            Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(d, [c]));
        var e = new ScreenLuaEvent("animation_result") { Animation = new(a.Id, "completed", "finished") };
        ScreenLuaProtocol.ValidateEvent(d, e);
        foreach (var invalid in new[] { e with { Animation = null }, e with { NodeId = d.Nodes[1].Id }, e with { Kind = "update" },
            e with { Animation = new(Guid.Empty, "completed", "finished") }, e with { Animation = new(a.Id, "bad", "finished") } })
            Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(d, invalid));
    }

    [TestMethod]
    public async Task ActualWorkerCompletesOncePausesAndSeesCurrentAnimatedGeometry()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var target = instance.Definition.Nodes[1].Id;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            local count=0
            return {on_load=function() ui.animate(ui.tween(refs.target,{x=90},1)) end,
              on_event=function(e)
                if e.kind=='animation_result' then count=count+1;ui.set_text(refs.target,e.status..':'..count)
                  ui.animate(ui.tween(refs.target,{x=50},1))
                end
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("target", target));
        await ScreenLuaSessionTests.Drain(session); Assert.IsTrue(session.HasAnimations(instance));
        session.AdvanceAnimations(TimeSpan.FromSeconds(.5), _ => true); Assert.AreEqual(50, instance.Definition.Nodes[1].Bounds.X);
        session.Suspend(instance, true); session.AdvanceAnimations(TimeSpan.FromSeconds(20), _ => true);
        session.Suspend(instance, false); session.AdvanceAnimations(TimeSpan.FromSeconds(20), _ => false);
        Assert.AreEqual(50, instance.Definition.Nodes[1].Bounds.X);
        session.AdvanceAnimations(TimeSpan.FromSeconds(.5), _ => true); Assert.AreEqual(90, instance.Definition.Nodes[1].Bounds.X);
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual("completed:1", instance.Definition.Nodes[1].Text); Assert.IsEmpty(session.Diagnostics);
        session.AdvanceAnimations(TimeSpan.FromSeconds(.5), _ => true); Assert.AreEqual(70, instance.Definition.Nodes[1].Bounds.X);
        stack.Close(instance); session.Poll(); await ScreenLuaSessionTests.Drain(session); Assert.AreEqual(0, session.ActiveAnimations);
        session.Reset(); Assert.IsFalse(session.HasAnimations(instance));
    }

    [TestMethod]
    public async Task AdmissionReservesCompletionSpaceAndPendingOrFailureCannotAdvanceAnimation()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        var animation = Animation(instance.Definition); load.Reply(Start(animation)); await session.PendingTask!;
        Assert.AreEqual(0, session.ActiveAnimations); session.Poll(); Assert.AreEqual(1, session.ActiveAnimations);
        for (var i = 0; i < 127; i++) Assert.AreEqual(ScreenEventAdmission.Accepted, session.EnqueueUsingInstanceSnapshot(instance, new("update")));
        Assert.AreEqual(ScreenEventAdmission.Busy, session.EnqueueUsingInstanceSnapshot(instance, new("update")));
        var before = instance.Definition; session.AdvanceAnimations(TimeSpan.FromSeconds(5), _ => true); Assert.AreSame(before, instance.Definition);
        session.Poll(); var call = await connection.Next(); session.AdvanceAnimations(TimeSpan.FromSeconds(5), _ => true); Assert.AreSame(before, instance.Definition);
        call.Completion.SetResult(new(ScreenLuaProtocol.Version, "error", call.Request.Stamp, "failure", []));
        await session.PendingTask!; session.Poll(); Assert.AreEqual(0, session.ActiveAnimations); Assert.IsFalse(session.HasAnimations(instance));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AdvanceAnimations(TimeSpan.FromSeconds(-1), _ => true));
        session.Dispose(); Assert.Throws<ObjectDisposedException>(() => session.AdvanceAnimations(TimeSpan.Zero, _ => true));
    }

    [TestMethod]
    public async Task LateStartAfterCloseAndRejectedOverCapacityBatchCannotLeakAnimationOrVisualMutation()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var connection = new ScreenLuaSessionTests.Connection { IgnoreCancellation = true };
        await using var session = new ScreenLuaSession(stack, () => connection); var before = instance.Definition;
        session.Attach(instance, "return {}"); session.Poll(); var load = await connection.Next();
        stack.Close(instance); session.Poll(); load.Reply(Start(Animation(before))); await session.PendingTask!; session.Poll();
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual(0, session.ActiveAnimations); Assert.AreSame(before, instance.Definition);
        var other = stack.Open(Guid.NewGuid(), before); session.Attach(other, "return {}"); session.Poll(); var next = await connection.Next();
        next.Reply(); await session.PendingTask!; session.Poll();
        session.EnqueueUsingInstanceSnapshot(other, new("update")); session.Poll(); var work = await connection.Next();
        for (var i = 0; i < 128; i++) session.EnqueueUsingInstanceSnapshot(other, new("update"));
        work.Reply(new ScreenChange("text", before.Nodes[1].Id, "must not commit", null), Start(Animation(before)));
        await session.PendingTask!; session.Poll(); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(0, session.ActiveAnimations); Assert.AreSame(before, other.Definition); Assert.IsFalse(session.IsAvailable(other));
    }

    [TestMethod]
    public void WorkerProjectsAnimationResultAndDescriptionsExpireBetweenHandlers()
    {
        var d = ScreenLuaProtocolTests.Definition(); var id = Guid.NewGuid();
        var vm = new ScreenLuaVm(d, Guid.NewGuid(), """
            local description
            return {on_load=function() description=ui.tween(refs.target,{x=90},1) end,
              on_event=function(e)
                ui.set_text(refs.target,e.animation_id..':'..e.status..':'..e.reason)
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("target", d.Nodes[1].Id));
        vm.Handle(null, ImmutableDictionary<string, string>.Empty);
        var changes = vm.Handle(new("animation_result") { Animation = new(id, "cancelled", "requested") }, ImmutableDictionary<string, string>.Empty);
        Assert.AreEqual(id.ToString("N") + ":cancelled:requested", changes.Single().Text);
        var expired = new ScreenLuaVm(d, Guid.NewGuid(), "local a;return {on_load=function() a=ui.tween(refs.target,{x=20},1) end,on_event=function() ui.animate(a) end}",
            ImmutableDictionary<string, Guid>.Empty.Add("target", d.Nodes[1].Id));
        expired.Handle(null, ImmutableDictionary<string, string>.Empty);
        Assert.Throws<InterpreterException>(() => expired.Handle(new("update"), ImmutableDictionary<string, string>.Empty));
        Assert.Throws<InterpreterException>(() => new ScreenLuaVm(d, Guid.NewGuid(), "ui.sequence({});return {on_event=function() end}", ImmutableDictionary<string, Guid>.Empty));
        Assert.Throws<ArgumentException>(() => vm.Synchronize([d.Nodes[0]]));
        Assert.Throws<ArgumentException>(() => vm.Synchronize(d.Nodes.SetItem(0, d.Nodes[0] with { Name = "forged" })));
    }
}
