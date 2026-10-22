using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Styling;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Tests.Screens;

namespace NovellMaker.Core.Tests.Scenes;

[TestClass]
public sealed class DialogueCanvasTests
{
    [TestMethod]
    public void DynamicDialogueGeometryControlsPointerFocusAndCaptureWithoutCommittingHostState()
    {
        var scene = Scene(); var panel = scene.Canvas.Nodes[1];
        var button = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Button, new(30, 220, 200, 50), "Нажать");
        var definition = new ScreenDefinition(scene.Id, scene.Canvas.Nodes.Add(button));
        Assert.IsEmpty(definition.Buttons);
        var show = true; var stack = new ScreenStack(Guid.NewGuid())
        { Presentation = (_, source) => DialogueCanvas.Present(source, Style, "Имя", "Текст", show, 350, 30) };
        var instance = stack.Open(Guid.NewGuid(), definition, new(false, false));
        var point = stack.VisualDefinition(instance).Visual(button.Id).World.Apply(20, 20);
        Assert.AreEqual(button.Id, stack.Route(new(ScreenInputKind.FocusNext)).Consumed ? stack.Focus?.NodeId : null);
        Assert.AreEqual(button.Id, stack.Route(new(ScreenInputKind.Activate)).Activation?.NodeId);
        stack.Route(new(ScreenInputKind.PointerDown, point.X, point.Y));
        var updated = new ScreenDefinition(scene.Id, definition.Nodes.Replace(button, button with { Text = "Другая подпись" }));
        stack.Commit(instance, 0, updated);
        Assert.AreEqual(button.Id, stack.Route(new(ScreenInputKind.PointerUp, point.X, point.Y)).Activation?.NodeId);
        Assert.AreEqual(1L, instance.Version); Assert.AreSame(updated, instance.Definition);
        stack.Route(new(ScreenInputKind.PointerDown, point.X, point.Y)); show = false;
        Assert.IsNull(stack.Route(new(ScreenInputKind.PointerUp, point.X, point.Y)).Activation);
        Assert.IsNull(stack.Route(new(ScreenInputKind.Activate)).Activation);
        stack.Presentation = (_, source) => new(Guid.NewGuid(), source.Nodes);
        Assert.Throws<ArgumentException>(() => stack.Route(new(ScreenInputKind.FocusNext)));
    }

    [TestMethod]
    public async Task LuaClickUsesPresentedAdmissionAndStaleCheckButWorkerReceivesOnlyAuthoringNodes()
    {
        var scene = Scene(); var button = new ScreenNode(Guid.NewGuid(), scene.Canvas.Nodes[1].Id, ScreenNodeKind.Button, new(30, 220, 200, 50));
        var stack = new ScreenStack(Guid.NewGuid()) { Presentation = (_, source) => DialogueCanvas.Present(source, Style, "Имя", "Подставленный текст", true, 350, 30) };
        var instance = stack.Open(Guid.NewGuid(), new(scene.Id, scene.Canvas.Nodes.Add(button)));
        var connection = new ScreenLuaSessionTests.Connection(automatic: true);
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function() end}"); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(instance, new("click", button.Id), ImmutableDictionary<string, string>.Empty));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.IsTrue(connection.Seen.Any(r => r.Kind == "event"));
        var load = connection.Seen.Single(r => r.Kind == "load"); Assert.IsTrue(load.Nodes.All(n => n.Text != "Подставленный текст"));
        ScreenLuaProtocol.ValidateEvent(instance.Definition, new("click", button.Id), worker: true);
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(instance.Definition, new("click", button.Id)));
        var disabled = new ScreenDefinition(scene.Id, instance.Definition.Nodes.Replace(button, button with { Enabled = false }));
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(disabled, new("click", button.Id), worker: true));
        var outside = new ScreenNode(Guid.NewGuid(), scene.Canvas.Nodes[0].Id, ScreenNodeKind.Button, new(2000, 0, 200, 50));
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(new(scene.Id, instance.Definition.Nodes.Add(outside)), new("click", outside.Id), worker: true));
    }
    private static readonly DialogueStyle Style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
    private static NovelScene Scene(bool legacy = true)
    {
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Реплика §#FF0000красным§r")] };
        return scene with { Canvas = scene.Canvas with { Nodes = DialogueCanvas.Add(scene.Canvas.Nodes, scene.Id, Style, legacy) } };
    }

    [TestMethod]
    public void FullLegacyCanvasKeepsBoundedMigrationReserveAndOrdinaryTreesKeepTheirLimit()
    {
        var scene = NovelScene.Create("Полный холст"); var root = scene.Canvas.Nodes[0];
        var nodes = (ImmutableArray<ScreenNode>)[root, .. Enumerable.Range(0, 1023).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(0, 0, 1, 1)))];
        var migrated = DialogueCanvas.Add(nodes, scene.Id, Style, legacy: true, deterministic: true);
        Assert.AreEqual(1028, new ScreenDefinition(scene.Id, migrated).Nodes.Length);
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(scene.Id, nodes.Add(new(Guid.NewGuid(), root.Id, ScreenNodeKind.Text, new(0, 0, 1, 1)))));
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(scene.Id, migrated.Add(new(Guid.NewGuid(), migrated.Single(n => n.ParentId is null).Id, ScreenNodeKind.Text, new(0, 0, 1, 1)))));
        var deep = (ImmutableArray<ScreenNode>)[root]; var parent = root.Id;
        for (var i = 0; i < 31; i++) { var child = new ScreenNode(Guid.NewGuid(), parent, ScreenNodeKind.Container, new(0, 0, 1, 1)); deep = deep.Add(child); parent = child.Id; }
        _ = new ScreenDefinition(scene.Id, DialogueCanvas.Add(deep, scene.Id, Style, legacy: true, deterministic: true));
    }

    [TestMethod]
    public void LegacyMigrationKeepsViewportDialogueBelowTransformedOldCanvasWithoutChangingItsWorldGeometry()
    {
        var scene = NovelScene.Create("Старая сцена"); var root = scene.Canvas.Nodes[0] with { Bounds = new(400, 200, 300, 240), Geometry = new() { Rotation = 12, Opacity = .4 }, ClipChildren = false };
        var child = new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Panel, new(20, 20, 100, 100));
        var source = new ScreenDefinition(scene.Id, [root, child]);
        var migrated = DialogueCanvas.Add(source.Nodes, scene.Id, Style, legacy: true, deterministic: true);
        var presentation = DialogueCanvas.Present(new(scene.Id, migrated), Style, "Имя", "Текст", true, 260, 30);
        Assert.AreEqual(source.Visual(child.Id).World, presentation.Visual(child.Id).World);
        Assert.AreEqual(source.Visual(child.Id).Opacity, presentation.Visual(child.Id).Opacity);
        var panel = presentation.Nodes.Single(n => n.Dialogue == DialogueBinding.CssPanel);
        Assert.AreEqual(Style.Left, presentation.Visual(panel.Id).World.X);
        Assert.AreEqual(1d, presentation.Visual(panel.Id).Opacity);
        var paintOrder = presentation.Visuals(ScreenDefinition.CanvasViewport).Select(n => n.Node.Id).ToArray();
        Assert.IsTrue(Array.IndexOf(paintOrder, panel.Id) < Array.IndexOf(paintOrder, child.Id));
        Assert.IsTrue(migrated.SequenceEqual(DialogueCanvas.Add(source.Nodes, scene.Id, Style, legacy: true, deterministic: true)));
    }

    [TestMethod]
    [DataRow(true)] [DataRow(false)]
    public void AdapterBindsTextAndSpeakerWithoutChangingSourceOrExecution(bool legacy)
    {
        var scene = Scene(legacy); var source = scene.AsScreen().Definition();
        var player = new ScenePlayer(NovelProject.Create("Новелла") with { Scenes = [scene] }, scene.Id, "ru", mode: PlaybackMode.Game);
        var before = player.Execution.State;
        var result = DialogueCanvas.Present(source, Style, "Анна", scene.Lines[0].Texts["ru"], true, 260, 32);
        Assert.AreEqual("Анна", result.Nodes.Single(n => n.Dialogue == DialogueBinding.Speaker).Text);
        Assert.AreEqual(scene.Lines[0].Texts["ru"], result.Nodes.Single(n => n.Dialogue == DialogueBinding.Text).Text);
        Assert.AreEqual("", source.Nodes.Single(n => n.Dialogue == DialogueBinding.Text).Text);
        Assert.AreSame(before, player.Execution.State); Assert.IsNotNull(player.Execution.State.Wait);
        Assert.IsTrue(player.Next()); Assert.IsTrue(player.Execution.State.Completed);
    }

    [TestMethod]
    public void LegacyLayoutFollowsCssAndMaterializingFreezesOnlyBoundsAndKeepsStableIds()
    {
        var scene = Scene(); var source = scene.AsScreen().Definition();
        var shown = DialogueCanvas.Present(source, Style, "", "Текст", true, 320, 0);
        var panel = shown.Nodes.Single(n => n.Dialogue == DialogueBinding.CssPanel);
        Assert.AreEqual(new ScreenRect(Style.Left, 720 - Style.Bottom - 320, Math.Clamp(Math.Ceiling(Style.Width), 64, 1280), 320), panel.Bounds);
        var speaker = shown.Nodes.Single(n => n.Dialogue == DialogueBinding.Speaker); Assert.IsFalse(speaker.Visible);
        Assert.AreEqual(Style.PaddingTop, shown.Nodes.Single(n => n.Dialogue == DialogueBinding.Text).Bounds.Y);
        var frozen = DialogueCanvas.Materialize(source, shown);
        Assert.AreEqual(DialogueBinding.Panel, frozen.Single(n => n.Id == panel.Id).Dialogue);
        Assert.IsTrue(source.Nodes.Select(n => n.Id).SequenceEqual(frozen.Select(n => n.Id)));
        var manual = new ScreenDefinition(scene.Id, frozen);
        var other = DialogueCanvas.Present(manual, Style, "Другой", "Другой текст", true, 600, 100);
        Assert.IsTrue(manual.Nodes.Select(n => n.Bounds).SequenceEqual(other.Nodes.Select(n => n.Bounds)));
        Assert.IsTrue(frozen.All(n => n.Text == ""));
    }

    [TestMethod]
    public void BindingIsDecorativeWhileOrdinaryButtonsRemainInteractiveAndLayeringAndClippingAreShared()
    {
        var scene = Scene(false); var nodes = scene.Canvas.Nodes;
        var panel = nodes.Single(n => n.Dialogue == DialogueBinding.Panel) with { Bounds = new(100, 100, 300, 200), Geometry = new() { Rotation = 20, Opacity = .5 } };
        nodes = nodes.SetItem(1, panel);
        var area = new ScreenNode(Guid.NewGuid(), nodes[0].Id, ScreenNodeKind.Area, new(100, 100, 300, 300)) { Order = -1 };
        var button = new ScreenNode(Guid.NewGuid(), panel.Id, ScreenNodeKind.Button, new(250, 150, 100, 100));
        var definition = new ScreenDefinition(scene.Id, nodes.Add(area).Add(button));
        var result = DialogueCanvas.Present(definition, Style, "Имя", "Текст", true, 200, 25);
        var p = result.Visual(panel.Id).World.Apply(20, 20);
        Assert.AreEqual(area.Id, result.HitTest(p.X, p.Y), "Dialogue decoration must pass through to an authored area below it.");
        var visual = result.Visual(button.Id); var inside = visual.World.Apply(10, 10); var outside = visual.World.Apply(80, 80);
        Assert.AreEqual(button.Id, result.HitTest(inside.X, inside.Y)); Assert.AreNotEqual(button.Id, result.HitTest(outside.X, outside.Y));
        Assert.AreEqual(.5, visual.Opacity); Assert.IsTrue(Math.Abs(ScreenPolygon.Area(visual.Polygon)) < 10000);
        var hidden = DialogueCanvas.Present(definition, Style, "Имя", "Текст", false, 200, 25);
        Assert.IsEmpty(hidden.Visuals(ScreenDefinition.CanvasViewport).Where(v => v.Node.ParentId == panel.Id).ToArray());
    }

    [TestMethod]
    [DataRow(0)] [DataRow(.5)] [DataRow(1)]
    public void CssAnimationComposesWithAuthoredGeometryAndZeroScaleDoesNotCreateSingularMatrix(double seconds)
    {
        var scene = Scene(); var nodes = scene.Canvas.Nodes;
        nodes = nodes.SetItem(1, nodes[1] with { Geometry = new() { PivotX = .3, PivotY = .2, Rotation = 15, ScaleX = -1 } });
        var style = DialogueCssParser.Parse(".dialogue { animation-name: appear; animation-duration: 1s; } @keyframes appear { from { opacity:0; transform:scale(0); } to { opacity:1; transform:scale(1) translateX(20px); } }");
        var source = new ScreenDefinition(scene.Id, nodes);
        var result = DialogueCanvas.Present(source, style, "Имя", "Текст", true, 200, 25, TimeSpan.FromSeconds(seconds), animate: true);
        var panel = result.Nodes[1];
        Assert.AreEqual(seconds, panel.Geometry.Opacity, 1e-8); Assert.AreEqual(15d, panel.Geometry.Rotation);
        if (seconds == 0) Assert.IsEmpty(result.Visual(panel.Id).Polygon);
        else Assert.AreEqual(-seconds, panel.Geometry.ScaleX, 1e-8);
        Assert.IsFalse(DialogueCanvas.Present(source, style, "Имя", "Текст", true, 200, 25).Visual(panel.Id).Polygon.IsEmpty,
            "Static authoring must show the resting dialogue even when the animation starts transparent.");
    }

    [TestMethod]
    public void PlainTreesAreUnchangedAndLongScenarioTextDoesNotChangeAuthoringTextQuotas()
    {
        var empty = NovelScene.Create("Пустая сцена").AsScreen().Definition();
        Assert.AreSame(empty, DialogueCanvas.Present(empty, Style, "", "", false, 150, 0));
        var source = Scene().AsScreen().Definition(); var text = new string('я', 10000);
        var shown = DialogueCanvas.Present(source, Style, "", text, true, 720, 0);
        Assert.AreEqual(text, shown.Nodes.Single(n => n.Dialogue == DialogueBinding.Text).Text);
        Assert.Throws<ArgumentException>(() => new ScreenDefinition(shown.Id, shown.Nodes));
    }

    [TestMethod]
    public void InvalidBindingAndWrongKindOrRootAreRejectedAndCopyRetainsBindingsWithNewIds()
    {
        var source = Scene().AsScreen().Definition(); var node = source.Nodes[1];
        foreach (var invalid in new[] { node with { Dialogue = (DialogueBinding)99 }, node with { Kind = ScreenNodeKind.Button }, node with { Dialogue = DialogueBinding.Speaker }, source.Nodes[0] with { Dialogue = DialogueBinding.Panel } })
            Assert.Throws<ArgumentException>(() => new ScreenDefinition(source.Id, source.Nodes.Replace(source.Nodes.Single(n => n.Id == invalid.Id), invalid)));
        var (copy, ids) = ScreenTree.Copy(source, node.Id, source.Nodes[0].Id);
        Assert.HasCount(3, ids); Assert.AreEqual(node.Dialogue, copy.Nodes.Single(n => n.Id == ids[node.Id]).Dialogue);
        Assert.IsTrue(copy.Nodes.Where(n => ids.Values.Contains(n.Id) && n.Id != ids[node.Id]).All(n => n.ParentId == ids[node.Id]));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task Version15MigrationIsDeterministicReadOnlyAndCurrentSchemaIsStrict(bool invalidCss)
    {
        var scene = NovelScene.Create("Старая сцена") with { Lines = [DialogueLine.Create("ru", "Фраза")], DialogueCssOverride = invalidCss ? "черновик" : null };
        var empty = NovelScene.Create("Меню");
        var project = NovelProject.Create("Старый проект") with { Scenes = [scene, empty] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["formatVersion"] = 15; json.AsObject().Remove("startScreenSceneId");
            foreach (var s in json["scenes"]!.AsArray()) foreach (var node in s!["canvas"]!["nodes"]!.AsArray()) node!.AsObject().Remove("dialogue");
            File.WriteAllText(path, json.ToJsonString()); var bytes = File.ReadAllBytes(path);
            var migrated = await store.LoadAsync(path); Assert.HasCount(4, migrated.Scenes[0].Canvas.Nodes); Assert.HasCount(1, migrated.Scenes[1].Canvas.Nodes);
            Assert.IsTrue(migrated.ContentEquals(await store.LoadAsync(path))); CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Assert.AreEqual(scene.DialogueCssOverride, migrated.Scenes[0].DialogueCssOverride);
            await store.SaveAsync(path, migrated); Assert.IsTrue(migrated.ContentEquals(await store.LoadAsync(path)));
            json = JsonNode.Parse(File.ReadAllText(path))!; json["formatVersion"] = 15; json.AsObject().Remove("startScreenSceneId"); File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            json["formatVersion"] = 16; json.AsObject().Remove("startScreenSceneId"); json["scenes"]![0]!["canvas"]!["nodes"]![0]!.AsObject().Remove("dialogue"); File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }
}
