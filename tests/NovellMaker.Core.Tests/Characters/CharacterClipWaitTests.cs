using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Characters;

[TestClass]
public sealed class CharacterClipWaitTests
{
    private static NovelProject Project(bool loop = false, int repeats = 0, bool launch = true,
        bool flipbook = false, double moveSeconds = 0)
    {
        var image = new ImageAsset(Guid.NewGuid(), "Часть", "Assets/Images/part.png", 100, 100);
        var part = new CharacterPart(Guid.NewGuid(), "Тело", image.Id, null, 0, 0, 0, new());
        var clip = new CharacterClip(Guid.NewGuid(), "Клип", .3, loop,
            flipbook ? [] : [new(part.Id, [new(0, new()), new(.3, new(30))])],
            flipbook ? [new(image.Id, .3)] : [], repeats);
        var pose = new CharacterState(Guid.NewGuid(), "Поза", image.Id)
        { Visual = new(100, 100, flipbook ? [] : [part], [clip], clip.Id) };
        var actor = NovelCharacter.Create("Герой") with { States = [pose], DefaultStateId = pose.Id };
        var line = DialogueLine.Create("ru", "После анимации") with { SpeakerId = actor.Id };
        var commands = new List<ScenarioAction>();
        if (launch) commands.Add(new PlayCharacterClipAction(Guid.NewGuid(), actor.Id, pose.Id, clip.Id));
        if (moveSeconds > 0) commands.Add(new MoveAction(Guid.NewGuid(), actor.Id, new(.8, 1, 1, 0), moveSeconds));
        commands.Add(new WaitAction(Guid.NewGuid(), "actions"));
        commands.Add(new SetAction(Guid.NewGuid(), "finished", "yes"));
        var scene = NovelScene.Create("Сцена") with { Lines = [line], ScriptBlocks =
            [new(null, ScriptPhase.SceneStart, new WaitAction(Guid.NewGuid(), "timer", .2)),
             .. commands.Select(a => new ScriptBlock(line.Id, ScriptPhase.BeforeLine, a))] };
        var project = NovelProject.Create("Ожидание клипа") with { Images = [image], Characters = [actor], Scenes = [scene] };
        ScenarioProgram.Validate(project);
        return project;
    }

    [TestMethod]
    [DataRow(false, false, 0)] [DataRow(true, false, 0)]
    [DataRow(false, true, 2)] [DataRow(true, true, 2)]
    [DataRow(false, true, 65535)]
    public void FiniteClipsHoldQueueThroughAllPassesAndRestoreWithoutReplay(bool flipbook, bool loop, int repeats)
    {
        var project = Project(loop, repeats, flipbook: flipbook);
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru");
        player.Tick(TimeSpan.FromSeconds(.3)); // .2 start delay, then .1 of clip.
        Assert.AreEqual("actions", player.State.Wait?.Mode);
        Assert.IsFalse(player.State.Variables.ContainsKey("finished"));
        var saved = GameSave.Serialize(player);
        player.IsPaused = true; player.Tick(TimeSpan.FromSeconds(10));
        Assert.AreEqual(.3, player.State.Time, 1e-12);
        player.IsPaused = false;
        using var restored = new ScenarioPlayer(project, project.Scenes[0].Id, "ru");
        GameSave.Load(restored, saved);
        Assert.AreEqual(player.State.Time, restored.State.Time);
        Assert.AreEqual(player.State.CharacterStartedAt.Single(), restored.State.CharacterStartedAt.Single());
        var left = .2 + .3 * (repeats + 1) - .3;
        restored.Tick(TimeSpan.FromSeconds(left - .01));
        Assert.AreEqual("actions", restored.State.Wait?.Mode);
        restored.Tick(TimeSpan.FromSeconds(.02));
        Assert.IsNull(restored.State.Error);
        Assert.AreEqual("input", restored.State.Wait?.Mode);
        Assert.AreEqual("yes", restored.State.Variables["finished"]);
        Assert.AreEqual(.2 + .3 * (repeats + 1), restored.State.TextStartedAt, 1e-12);
    }

    [TestMethod]
    [DataRow(.1)] [DataRow(.8)]
    public void ParallelMovesAndClipsFinishAtLatestBoundaryRegardlessOfFrameSize(double moveSeconds)
    {
        var project = Project(moveSeconds: moveSeconds);
        using var large = new ScenarioPlayer(project, project.Scenes[0].Id, "ru");
        using var small = new ScenarioPlayer(project, project.Scenes[0].Id, "ru");
        large.Tick(TimeSpan.FromSeconds(1.2));
        for (var i = 0; i < 120; i++) small.Tick(TimeSpan.FromSeconds(.01));
        Assert.IsNull(large.State.Error); Assert.IsNull(small.State.Error);
        Assert.AreEqual(.2 + Math.Max(.3, moveSeconds), large.State.TextStartedAt, 1e-12);
        Assert.AreEqual(large.State.TextStartedAt, small.State.TextStartedAt, 1e-12);
        Assert.AreEqual(large.State.Placements.Single(), small.State.Placements.Single());
        Assert.AreEqual("input", large.State.Wait?.Mode);
    }

    [TestMethod]
    [DataRow(true, true)] [DataRow(false, false)] [DataRow(false, true)]
    public void InfiniteExplicitLoopsAndPoseDefaultsDoNotBlock(bool launch, bool loop)
    {
        var project = Project(loop: loop, launch: launch);
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru");
        player.Tick(TimeSpan.FromSeconds(.2));
        Assert.AreEqual("input", player.State.Wait?.Mode);
        Assert.AreEqual(.2, player.State.TextStartedAt);
    }

    [TestMethod]
    public void ReplacingPoseCancelsClipAndHiddenExplicitClipStillFinishes()
    {
        foreach (var resetPose in new[] { false, true })
        {
            var project = Project(); var scene = project.Scenes[0]; var actor = project.Characters[0];
            ScenarioAction change = resetPose ? new StateAction(Guid.NewGuid(), actor.Id, actor.DefaultStateId!.Value)
                : new HideAction(Guid.NewGuid(), actor.Id);
            scene = scene with { ScriptBlocks = scene.ScriptBlocks.Insert(2,
                new(scene.Lines[0].Id, ScriptPhase.BeforeLine, change)) };
            project = project with { Scenes = [scene] };
            using var player = new ScenarioPlayer(project, scene.Id, "ru");
            player.Tick(TimeSpan.FromSeconds(.2));
            Assert.AreEqual(resetPose ? "input" : "actions", player.State.Wait?.Mode);
            if (resetPose) continue;
            var saved = GameSave.Serialize(player);
            var invalid = player.State with { CharacterStartedAt = ImmutableDictionary<Guid, double>.Empty };
            Assert.ThrowsExactly<ArgumentException>(() => GameSave.Validate(invalid, project));
            GameSave.Load(player, saved);
            player.Tick(TimeSpan.FromSeconds(.3));
            Assert.AreEqual("input", player.State.Wait?.Mode);
            Assert.IsEmpty(player.State.VisibleObjects);
        }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void ReplacementClipUsesItsOwnStartAndDoesNotKeepOldLaunchInBarrier(bool infinite)
    {
        var project = Project(); var scene = project.Scenes[0]; var actor = project.Characters[0]; var pose = actor.States[0];
        var clip = pose.Visual!.Clips[0] with { Id = Guid.NewGuid(), Duration = .6, Loop = infinite };
        pose = pose with { Visual = pose.Visual with { Clips = pose.Visual.Clips.Add(clip) } };
        actor = actor with { States = [pose] };
        scene = scene with { ScriptBlocks = scene.ScriptBlocks.InsertRange(2, (ImmutableArray<ScriptBlock>)
            [new(scene.Lines[0].Id, ScriptPhase.BeforeLine, new WaitAction(Guid.NewGuid(), "timer", .1)),
             new(scene.Lines[0].Id, ScriptPhase.BeforeLine, new PlayCharacterClipAction(Guid.NewGuid(), actor.Id, pose.Id, clip.Id))]) };
        project = project with { Scenes = [scene], Characters = [actor] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru");
        player.Tick(TimeSpan.FromSeconds(.31));
        Assert.AreEqual(infinite ? "input" : "actions", player.State.Wait?.Mode);
        Assert.AreEqual(clip.Id, player.State.CharacterClips[actor.Id]);
        if (!infinite)
        {
            player.Tick(TimeSpan.FromSeconds(.58));
            Assert.AreEqual("actions", player.State.Wait?.Mode);
            player.Tick(TimeSpan.FromSeconds(.02));
        }
        Assert.IsNull(player.State.Error);
        Assert.AreEqual(infinite ? .3 : .9, player.State.TextStartedAt, 1e-12);
        Assert.AreEqual("input", player.State.Wait?.Mode);
    }

    [TestMethod]
    public async Task LuaWaitUsesSameBarrierAndCompletedClipCannotKeepSavedWaitAlive()
    {
        var project = Project(); var scene = project.Scenes[0];
        var wait = scene.ScriptBlocks[2];
        scene = scene with { ScriptBlocks = scene.ScriptBlocks.SetItem(2,
            wait with { Action = new ScriptAction(wait.Action.Id, "game.wait_actions()", "wait.lua") }) };
        project = project with { Scenes = [scene] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru");
        player.Tick(TimeSpan.FromSeconds(.2));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (player.HasPendingLua && DateTime.UtcNow < deadline) { await Task.Delay(10); player.PollLua(); }
        Assert.IsFalse(player.HasPendingLua); Assert.IsNull(player.State.Error);
        Assert.AreEqual("actions", player.State.Wait?.Mode);
        Assert.ThrowsExactly<ArgumentException>(() => GameSave.Validate(player.State with { Time = 1 }, project));
        player.Tick(TimeSpan.FromSeconds(.3));
        Assert.AreEqual("yes", player.State.Variables["finished"]);
    }
}
