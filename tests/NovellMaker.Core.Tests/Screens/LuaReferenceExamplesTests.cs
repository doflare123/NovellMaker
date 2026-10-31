using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Tests.Execution;

namespace NovellMaker.Core.Tests.Screens;

/// <summary>Execute the published Markdown examples, not a second set of copied source strings.</summary>
[TestClass]
public sealed class LuaReferenceExamplesTests
{
    private static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty;
    private static string Example(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NovellMaker.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory, "Repository root not found.");
        var markdown = File.ReadAllText(Path.Combine(directory.FullName, "docs", "lua-reference", "examples.md"));
        var match = Regex.Match(markdown, @"<!-- example:" + Regex.Escape(name) + @" -->\s*```lua\r?\n([\s\S]*?)\r?\n```");
        Assert.IsTrue(match.Success, "Missing example: " + name);
        return match.Groups[1].Value;
    }
    private static ImmutableDictionary<string, Guid> Refs(ScreenDefinition definition) =>
        ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id).Add("add", definition.Nodes[1].Id)
            .Add("mark", definition.Nodes[1].Id).Add("check", definition.Nodes[1].Id).Add("cancel", definition.Nodes[3].Id);
    private static (ScreenStack Stack, ScreenInstance Instance) Fixture()
    {
        var definition = ScreenLuaProtocolTests.Definition();
        definition = new(definition.Id, definition.Nodes.Add(new(Guid.NewGuid(), definition.Nodes[0].Id,
            ScreenNodeKind.Button, new(65, 10, 30, 30), "cancel")));
        var stack = new ScreenStack(Guid.NewGuid());
        return (stack, stack.Open(Guid.NewGuid(), definition));
    }
    private static async Task Click(ScreenLuaSession session, ScreenInstance instance)
    {
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(instance, new("click", instance.Definition.Nodes[1].Id), Empty));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public void ScenarioQueueUsesTheActualWorkerAndPausesCommandsRatherThanLua()
    {
        var scene = NovelScene.Create("Reference");
        scene = scene with { Actions = [new ScriptAction(Guid.NewGuid(), Example("scenario-queue"), "reference")] };
        var project = NovelProject.Create("Reference") with { Scenes = [scene] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        Assert.AreEqual("started", player.State.Variables["stage"]);
        Assert.AreEqual("timer", player.State.Wait?.Mode);
        player.TickSettled(TimeSpan.FromSeconds(1));
        Assert.AreEqual("done", player.State.Variables["stage"]);
        Assert.AreEqual("input", player.State.Wait?.Mode);
        Assert.IsNull(player.State.Error);
    }

    [TestMethod]
    public async Task CounterCommitsStateAndTextAndReopeningStartsAtZero()
    {
        var (stack, instance) = Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, Example("counter"), Refs(instance.Definition));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("Нажатий: 0", instance.Definition.Nodes[2].Text);
        await Click(session, instance); await Click(session, instance);
        Assert.AreEqual(2d, session.GetState(instance).Properties!["count"].Number);
        Assert.AreEqual("Нажатий: 2", instance.Definition.Nodes[2].Text);
        var definition = instance.Definition;
        stack.Close(instance);
        var reopened = stack.Open(Guid.NewGuid(), definition);
        session.Attach(reopened, Example("counter"), Refs(definition));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("Нажатий: 0", reopened.Definition.Nodes[2].Text);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task ModuleExampleRunsThroughRealWorkerWithDeclaredReferences()
    {
        var (stack, instance) = Fixture(); var root = Guid.NewGuid(); var helper = Guid.NewGuid();
        var source = Example("module-root");
        var modules = new LuaModuleLibrary(root, [new(root, "root", source)
            { References = ImmutableDictionary<string, Guid>.Empty.Add("helper", helper) },
            new(helper, "helper", Example("module-helper"))]);
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, Refs(instance.Definition), modules: modules);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("Счёт: 2", instance.Definition.Nodes[2].Text);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task RandomCheckpointReplaysTheSameSampleThroughWorker()
    {
        var (stack, instance) = Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, Example("random-checkpoint"), Refs(instance.Definition));
        await ScreenLuaSessionTests.Drain(session);
        var state = session.GetState(instance).Properties!;
        Assert.AreEqual(state["first"].Number, state["replay"].Number);
        Assert.IsTrue(state["first"].Number is >= 1 and <= 100);
        Assert.IsEmpty(session.Diagnostics);
    }

    private static MinigameDefinition Definition => new()
    {
        ParametersSchema = new() { Fields = ImmutableDictionary<string, LuaValueKind>.Empty.Add("target", LuaValueKind.Number) },
        DefaultParameters = LuaValue.FromJson("{\"target\":3}"),
        StateSchema = new() { Fields = ImmutableDictionary<string, LuaValueKind>.Empty.Add("score", LuaValueKind.Number) },
        InitialState = LuaValue.FromJson("{\"score\":0}"),
        ResultSchema = new() { Fields = ImmutableDictionary<string, LuaValueKind>.Empty.Add("score", LuaValueKind.Number) },
    };

    [TestMethod]
    public async Task MinigameRebuildsFromSavedStateAndCompletesOnceWithSchemaCheckedData()
    {
        var (stack, instance) = Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, Example("minigame-handler"), Refs(instance.Definition), state: Definition.InitialState,
            minigame: Definition, invocation: new(Definition.DefaultParameters, false));
        await ScreenLuaSessionTests.Drain(session);
        await Click(session, instance);
        var state = session.GetState(instance); var random = session.GetRandomState(instance); var definition = instance.Definition;
        Assert.AreEqual(1d, state.Properties!["score"].Number);
        stack.Close(instance);
        var restored = stack.Open(Guid.NewGuid(), definition);
        session.Attach(restored, Example("minigame-handler"), Refs(definition), state: state, randomState: random,
            minigame: Definition, invocation: new(Definition.DefaultParameters, true));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(state.ToJson(), session.GetState(restored).ToJson());
        Assert.AreEqual(random, session.GetRandomState(restored));
        Assert.AreEqual("Счёт: 1", restored.Definition.Nodes[2].Text);
        await Click(session, restored); Assert.IsNull(session.GetMinigameResult(restored));
        await Click(session, restored);
        Assert.AreEqual(MinigamePhase.Completed, session.GetMinigameResult(restored)!.Phase);
        Assert.AreEqual(3d, session.GetMinigameResult(restored)!.Data.Properties!["score"].Number);
    }

    [TestMethod]
    public void ScenarioLaunchWaitsForOutcomeAndThenExecutesTheDocumentedAfterResource()
    {
        var puzzle = NovelScene.Create("Puzzle");
        puzzle = puzzle with { Canvas = puzzle.Canvas with { Minigame = Definition } };
        var after = new LuaScriptAsset(Guid.NewGuid(), "after", Example("minigame-after"));
        var scene = NovelScene.Create("Story");
        scene = scene with { Actions = [new ScriptAction(Guid.NewGuid(), Example("minigame-launch"), "launch")
            { References = ImmutableDictionary<string, Guid>.Empty.Add("puzzle", puzzle.Id).Add("after", after.Id) }] };
        var project = NovelProject.Create("Reference") with { Scenes = [scene, puzzle], Scripts = [after] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        Assert.AreEqual("minigame", player.State.Wait?.Mode);
        Assert.IsFalse(player.State.Variables.ContainsKey("puzzle.won"));
        Assert.IsTrue(player.FinishMinigame(player.State.Minigame!.RunId,
            new(MinigamePhase.Completed, LuaValue.FromJson("{\"score\":3}"), "")));
        player.Settled();
        Assert.AreEqual("true", player.State.Variables["puzzle.won"]);
        Assert.IsNull(player.State.Error);
    }

    [TestMethod]
    public async Task AnimationExampleRunsOnHostAndCorrelatesCompletion()
    {
        var (stack, instance) = Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, Example("animation"), Refs(instance.Definition));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(1, session.ActiveAnimations);
        session.AdvanceAnimations(TimeSpan.FromSeconds(1), _ => true);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(200d, instance.Definition.Nodes[2].Bounds.X);
        Assert.AreEqual(0.5d, instance.Definition.Nodes[2].Geometry.Opacity);
        Assert.AreEqual("completed:finished", instance.Definition.Nodes[2].Text);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task ProfileExampleHandlesCorrelatedReadAndWriteResults()
    {
        var (stack, instance) = Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, Example("profile"), Refs(instance.Definition));
        await ScreenLuaSessionTests.Drain(session);
        var read = session.DrainServices().Single().Command;
        Assert.AreEqual(GameServiceAction.GetPersistent, read.Action);
        session.Enqueue(instance, new("service_result") { Result = new(read.RequestId, read.Action, true, "", null, [])
            { Key = read.Key, ProfileSaved = true } }, Empty);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("Ещё не отмечено", instance.Definition.Nodes[2].Text);
        await Click(session, instance);
        var write = session.DrainServices().Single().Command;
        Assert.AreEqual(GameServiceAction.SetPersistent, write.Action); Assert.AreEqual("true", write.DataValue);
        session.Enqueue(instance, new("service_result") { Result = new(write.RequestId, write.Action, true, "", null, [])
            { Key = write.Key, DataValue = "true", ProfileSaved = true } }, Empty);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("Подтверждено", instance.Definition.Nodes[2].Text);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task ReleaseExampleHandlesDisabledServiceThroughActualHostCoordinator()
    {
        var (stack, instance) = Fixture(); await using var session = new ScreenLuaSession(stack);
        await using var releases = new ReleaseCheckService(Guid.NewGuid(), true, new NeverCalledProvider());
        session.ConnectReleaseChecks(releases);
        session.Attach(instance, Example("release"), Refs(instance.Definition));
        await ScreenLuaSessionTests.Drain(session); await Click(session, instance);
        // Service reservations are not runnable Lua work until the next host Poll.
        session.Poll();
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("Проверка выпуска не настроена.", instance.Definition.Nodes[2].Text);
        Assert.IsEmpty(session.Diagnostics);
    }
    private sealed class NeverCalledProvider : IReleaseCheckProvider
    {
        public Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration configuration, CancellationToken cancellationToken)
            => throw new AssertFailedException("A disabled configuration must never start the provider.");
    }

    [TestMethod]
    public void AudioExampleBuildsOwnedCommandAndHandlesCorrelatedCompletionInVm()
    {
        var definition = Fixture().Instance.Definition; var asset = Guid.NewGuid();
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), Example("audio"), Refs(definition),
            audioReferences: ImmutableDictionary<string, Guid>.Empty.Add("click", asset));
        var command = ScreenLuaProtocol.Prepare(definition, vm.Handle(null, Empty)).AnimationChanges.Single().Audio!;
        Assert.AreEqual("play", command.Kind); Assert.AreEqual(asset, command.AssetId);
        var changes = vm.Handle(new("audio_result") { Audio = new(command.Id, "completed") }, Empty);
        Assert.AreEqual("completed", changes.Single().Text);
    }

    [TestMethod]
    public void ImportantUnregisteredGlobalsRemainAbsentInActualComponentVm()
    {
        string[] names = ["_G", "_VERSION", "assert", "error", "pcall", "xpcall", "print", "next", "select", "unpack",
            "rawequal", "rawget", "rawset", "getmetatable", "setmetatable", "load", "loadstring", "loadfile", "dofile",
            "require", "collectgarbage", "io", "os", "debug", "package", "coroutine", "bit32", "utf8", "clr", "luanet"];
        var checks = string.Join(" and ", names.Select(n => n + "==nil"));
        var definition = Fixture().Instance.Definition;
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), "return {on_load=function() ui.set_state(" + checks + ") end,on_event=function() end}",
            ImmutableDictionary<string, Guid>.Empty);
        Assert.IsTrue(vm.Handle(null, Empty).Single().State!.Boolean);
    }

    [TestMethod]
    public void LegacyFinishDoesNotChangeTheCurrentLuaGetSnapshot()
    {
        var scene = NovelScene.Create("Story");
        var project = NovelProject.Create("Reference") with { Scenes = [scene] };
        var state = new GameState { ProjectId = project.Id, SceneId = scene.Id, Revision = "reference" };
        var commands = LuaActionScript.Run(new(Guid.NewGuid(),
            "game.finish('new'); game.set('observed',game.get('minigame.result') or 'missing')", "finish-snapshot"), state, project);
        Assert.AreEqual("new", ((SetAction)commands[0]).Value);
        Assert.AreEqual("missing", ((SetAction)commands[1]).Value);
    }

    [TestMethod]
    public void ExplicitNilAndMissingWaitClickArgumentAreDifferentInWorker()
    {
        var scene = NovelScene.Create("Story");
        var project = NovelProject.Create("Reference") with { Scenes = [scene] };
        var state = new GameState { ProjectId = project.Id, SceneId = scene.Id, Revision = "reference" };
        var commands = LuaActionScript.Run(new(Guid.NewGuid(), "game.wait_click()", "missing"), state, project);
        Assert.IsNull(((WaitAction)commands.Single()).Token);
        Assert.Throws<InvalidOperationException>(() => LuaActionScript.Run(
            new(Guid.NewGuid(), "game.wait_click(nil)", "explicit-nil"), state, project));
        var converted = LuaActionScript.Run(new(Guid.NewGuid(), "game.wait('1'); game.set(12,34)", "legacy-conversion"), state, project);
        Assert.AreEqual(1d, ((WaitAction)converted[0]).Seconds);
        Assert.AreEqual("12", ((SetAction)converted[1]).Variable);
        Assert.AreEqual("34", ((SetAction)converted[1]).Value);
    }

    [TestMethod]
    public void CancelledMinigameDataIsANullMarkerRatherThanLuaNilInScenarioWorker()
    {
        var scene = NovelScene.Create("Story");
        var project = NovelProject.Create("Reference") with { Scenes = [scene] };
        var state = new GameState { ProjectId = project.Id, SceneId = scene.Id, Revision = "reference",
            MinigameOutcome = new(Guid.NewGuid(), new(MinigamePhase.Cancelled, LuaValue.Null, "")) };
        var commands = LuaActionScript.Run(new(Guid.NewGuid(),
            "local r=game.minigame_result(); game.set('observed', r.status=='cancelled' and r.data~=nil and 'marker' or 'unexpected')",
            "null-projection"), state, project);
        Assert.AreEqual("marker", ((SetAction)commands.Single()).Value);
    }
}
