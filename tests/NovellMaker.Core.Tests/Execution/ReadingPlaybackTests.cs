using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Text;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class ReadingPlaybackTests
{
    private sealed class Store : IPlayerProfileStore
    {
        public PlayerProfileData? Saved;
        public Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default) => Task.FromResult(new PlayerProfileRead(defaults, PlayerProfileStatus.Missing, ""));
        public Task SaveAsync(PlayerProfileData profile, CancellationToken token = default) { Saved = profile; return Task.CompletedTask; }
    }
    private static NovelProject Project(params string[] texts) => NovelProject.Create("Чтение") with
    { Scenes = [NovelScene.Create("Сцена") with { Lines = [.. texts.Select(t => DialogueLine.Create("ru", t))] }] };
    private static PlayerSettingsSession Profile(double speed = 2, double delay = 1) => new(new Store(),
        new(PlayerSettings.Defaults(new()) with { TextSpeed = speed, AutoDelay = delay }, PlayerProfileStatus.Missing, ""));
    private static ScenePlayer Play(NovelProject project) => new(project, project.Scenes[0].Id, "ru", mode: PlaybackMode.Game);

    [TestMethod]
    public async Task RevealUsesUnicodeElementsAndFirstInputOnlyCompletesText()
    {
        var project = Project("А😀е\u0301§#FF0000Б§r", "Дальше"); using var player = Play(project);
        var profile = Profile(); var reading = new ReadingPlayback(profile);
        reading.Update(player, TimeSpan.FromSeconds(1), true, false);
        Assert.AreEqual("А😀", reading.VisibleText); Assert.IsEmpty(profile.ReadLines);
        Assert.IsTrue(reading.Advance(player)); Assert.IsTrue(reading.FullyRevealed);
        Assert.AreEqual(project.Scenes[0].Lines[0].Id, player.Frame.LineId); Assert.HasCount(1, profile.ReadLines);
        Assert.IsTrue(reading.Advance(player)); Assert.AreEqual(project.Scenes[0].Lines[1].Id, player.Frame.LineId);
        Assert.HasCount(2, player.Execution.State.History); await profile.FlushAsync();
        Assert.AreEqual("§#FF0000А§r", DialogueText.Prefix("§#FF0000АБ§r", 1));
        Assert.AreEqual("е\u0301", string.Concat(DialogueText.Parse(DialogueText.Prefix("е§#FF0000\u0301§rБ", 1)).Select(r => r.Text)));
    }

    [TestMethod]
    public async Task AutoWaitsForRevealDelayAndActualVoiceAndAdvancesOncePerFrame()
    {
        using var player = Play(Project("1234", "5678", "9012")); var profile = Profile(); var reading = new ReadingPlayback(profile);
        Assert.IsTrue(reading.Toggle(player, ReadingMode.Auto));
        Assert.IsFalse(reading.Update(player, TimeSpan.FromSeconds(2), true, true)); Assert.IsTrue(reading.FullyRevealed);
        Assert.IsFalse(reading.Update(player, TimeSpan.FromSeconds(100), true, true));
        Assert.IsTrue(reading.Update(player, TimeSpan.Zero, true, false)); Assert.AreEqual("5678", player.Frame.Text);
        Assert.AreEqual(ReadingMode.Auto, reading.Mode); Assert.IsFalse(reading.FullyRevealed);
        Assert.IsTrue(reading.Update(player, TimeSpan.FromSeconds(100), true, false)); Assert.AreEqual("9012", player.Frame.Text);
        await profile.FlushAsync();
    }

    [TestMethod]
    public async Task SkipUsesReadSetFromBeforeVisitAndStopsBeforeUnknownLine()
    {
        var project = Project("one", "two", "new"); var profile = Profile();
        foreach (var line in project.Scenes[0].Lines.Take(2)) profile.MarkRead(ReadingPlayback.ReadKey(project.Scenes[0].Id, line.Id, "ru", line.Texts["ru"]));
        using var player = Play(project); var reading = new ReadingPlayback(profile);
        Assert.IsTrue(reading.Toggle(player, ReadingMode.SkipRead));
        Assert.IsTrue(reading.Update(player, TimeSpan.FromSeconds(1), true, true)); Assert.AreEqual("two", player.Frame.Text);
        Assert.IsTrue(reading.Update(player, TimeSpan.FromSeconds(1), true, true)); Assert.AreEqual("new", player.Frame.Text);
        Assert.AreEqual(ReadingMode.Manual, reading.Mode); Assert.IsFalse(reading.FullyRevealed);
        Assert.IsFalse(reading.Toggle(player, ReadingMode.SkipRead)); Assert.HasCount(2, profile.ReadLines);
        var lineId = project.Scenes[0].Lines[0].Id;
        Assert.IsFalse(profile.HasRead(ReadingPlayback.ReadKey(project.Scenes[0].Id, lineId, "en", "one")));
        Assert.IsFalse(profile.HasRead(ReadingPlayback.ReadKey(project.Scenes[0].Id, lineId, "ru", "edited")));
        await profile.FlushAsync();
    }

    [TestMethod]
    public async Task PausesAndPendingLuaDoNotRevealOrAdvanceAndManualInputStopsAuto()
    {
        using var player = Play(Project("123456", "next")); var profile = Profile(); var reading = new ReadingPlayback(profile);
        reading.Toggle(player, ReadingMode.Auto); reading.Update(player, TimeSpan.FromSeconds(100), false, false);
        Assert.AreEqual("", reading.VisibleText); Assert.IsEmpty(profile.ReadLines);
        player.Execution.IsPaused = true; Assert.IsFalse(reading.Advance(player)); Assert.AreEqual(ReadingMode.Manual, reading.Mode);
        player.Execution.IsPaused = false; Assert.IsTrue(reading.Advance(player)); Assert.IsTrue(reading.FullyRevealed);
        await profile.FlushAsync();
    }

    [TestMethod]
    public async Task LoadRestoresHistoryRevealsCurrentTextAndResetsAutoWithoutReplayingCommands()
    {
        var project = Project("one", "two"); using var player = Play(project); var profile = Profile(0); var reading = new ReadingPlayback(profile);
        reading.Update(player, TimeSpan.Zero, true, false); var save = GameSave.Serialize(player.Execution);
        Assert.IsTrue(reading.Advance(player)); reading.Update(player, TimeSpan.Zero, true, false);
        Assert.HasCount(2, profile.ReadLines); reading.Toggle(player, ReadingMode.Auto);
        player.LoadSave(save); reading.Synchronize(player);
        Assert.HasCount(1, player.Execution.State.History); Assert.HasCount(2, profile.ReadLines);
        Assert.AreEqual(ReadingMode.Manual, reading.Mode); Assert.IsTrue(reading.FullyRevealed);
        Assert.IsFalse(player.DrainAudioEvents().Any(e => e.Kind == NovellMaker.Core.Audio.AudioEventKind.Line));
        foreach (var version in new[] { 1, 2 })
        {
            var legacy = JsonNode.Parse(save)!; legacy["Version"] = version; legacy["State"]!.AsObject().Remove("History");
            player.LoadSave(legacy.ToJsonString()); Assert.IsEmpty(player.Execution.State.History);
        }
        var broken = JsonNode.Parse(save)!; broken["State"]!["History"]![0]!["LineId"] = Guid.NewGuid();
        var before = player.Execution.State; Assert.Throws<ArgumentException>(() => player.LoadSave(broken.ToJsonString())); Assert.AreSame(before, player.Execution.State);
        await profile.FlushAsync();
    }

    [TestMethod]
    public async Task TimerIsNotSkippedAndChoiceAndInteractionStopAutomation()
    {
        var project = Project("one", "two"); var scene = project.Scenes[0]; var target = Guid.NewGuid(); var first = scene.Lines[0].Id;
        project = project with { Scenes = [scene with { Actions = [new TextAction(Guid.NewGuid(), first), new WaitAction(Guid.NewGuid(), "timer", 5),
            new WaitAction(Guid.NewGuid(), "input"), new ChoiceAction(Guid.NewGuid(), [new(Guid.NewGuid(), "Выбрать", scene.Id, target)]),
            new LineEntryAction(target, scene.Lines[1].Id), new TextAction(Guid.NewGuid(), scene.Lines[1].Id), new WaitAction(Guid.NewGuid(), "input")] }] };
        using var player = Play(project); var profile = Profile(0, 0); var reading = new ReadingPlayback(profile);
        reading.Toggle(player, ReadingMode.Auto); Assert.IsFalse(reading.Update(player, TimeSpan.FromSeconds(100), true, false));
        Assert.AreEqual("timer", player.Execution.State.Wait!.Mode); player.Tick(TimeSpan.FromSeconds(5));
        Assert.IsTrue(reading.Update(player, TimeSpan.Zero, true, false)); Assert.IsNotNull(player.Execution.State.Choice);
        Assert.AreEqual(ReadingMode.Manual, reading.Mode); Assert.IsFalse(reading.Toggle(player, ReadingMode.Auto));
        await profile.FlushAsync();
    }

    [TestMethod]
    public void HistoryPreservesRepeatedVisitsAndSceneNavigationAndEvictsOldest()
    {
        var project = Project("one"); using var player = Play(project); var scene = project.Scenes[0].Id;
        var first = player.Execution.State.History[0];
        for (var i = 0; i < DialogueHistory.MaximumEntries; i++) player.EnterScene(scene);
        Assert.HasCount(DialogueHistory.MaximumEntries, player.Execution.State.History);
        Assert.IsFalse(player.Execution.State.History.Contains(first));
        GameSave.Validate(player.Execution.State, project);
    }

    [TestMethod]
    [DataRow(3)] [DataRow(4)]
    public void HistoryRemainsRequiredInV4AndV3LoadsWithoutAnimationTables(int version)
    {
        var project = Project("one"); using var player = Play(project);
        var visit = player.Execution.State.History.Single();
        var save = JsonNode.Parse(GameSave.Serialize(player.Execution))!;
        Assert.AreEqual(4, save["Version"]!.GetValue<int>());
        save["Version"] = version;
        if (version == 3)
        {
            save["State"]!.AsObject().Remove("CharacterClips");
            save["State"]!.AsObject().Remove("CharacterStartedAt");
        }
        player.EnterScene(project.Scenes[0].Id);
        player.LoadSave(save.ToJsonString());
        Assert.AreEqual(visit, player.Execution.State.History.Single());
        Assert.IsFalse(player.DrainAudioEvents().Any(e => e.Kind == NovellMaker.Core.Audio.AudioEventKind.Line));
        var before = player.Execution.State;
        var fields = version == 4 ? new[] { "History", "CharacterClips", "CharacterStartedAt" } : new[] { "History" };
        foreach (var field in fields)
        {
            var invalid = save.DeepClone(); invalid["State"]!.AsObject().Remove(field);
            Assert.ThrowsExactly<ArgumentException>(() => player.LoadSave(invalid.ToJsonString()));
            Assert.AreSame(before, player.Execution.State);
        }
    }

    [TestMethod]
    public async Task MinigameAndInteractionRequireExplicitReenableAndLuaNeverConsumesReadingTime()
    {
        var project = MinigameTests.Project(); var scene = project.Scenes[0]; var line = scene.Lines[0];
        var launch = scene.ScriptBlocks[0].Action;
        project = project with { Scenes = project.Scenes.SetItem(0, scene with { ScriptBlocks = [new(line.Id, ScriptPhase.AfterLine, launch)] }) };
        var profile = Profile(0, 0); var reading = new ReadingPlayback(profile);
        using (var player = Play(project))
        {
            reading.Toggle(player, ReadingMode.Auto); Assert.IsTrue(reading.Update(player, TimeSpan.FromSeconds(1), true, false));
            Assert.IsNotNull(player.Execution.State.Minigame); Assert.AreEqual(ReadingMode.Manual, reading.Mode);
            Assert.IsFalse(reading.Toggle(player, ReadingMode.SkipRead));
            player.Execution.FinishMinigame(player.Execution.State.Minigame.RunId, new(NovellMaker.Core.Minigames.MinigamePhase.Completed, MinigameTests.Data, ""));
            player.SynchronizePresentation(); reading.Update(player, TimeSpan.FromSeconds(20), true, false);
            Assert.AreEqual(ReadingMode.Manual, reading.Mode);
        }
        var plain = Project("one", "two"); scene = plain.Scenes[0];
        plain = plain with { Scenes = [scene with { ScriptBlocks = [new(scene.Lines[0].Id, ScriptPhase.AfterLine, new HotspotAction(Guid.NewGuid(), Guid.NewGuid(), "Предмет", 0, 0, 1, 1)),
            new(scene.Lines[0].Id, ScriptPhase.AfterLine, new WaitAction(Guid.NewGuid(), "interaction"))] }] };
        using (var player = Play(plain))
        {
            reading.Toggle(player, ReadingMode.Auto); reading.Update(player, TimeSpan.FromSeconds(1), true, false);
            Assert.AreEqual("interaction", player.Execution.State.Wait!.Mode); Assert.AreEqual(ReadingMode.Manual, reading.Mode);
            Assert.IsFalse(reading.Toggle(player, ReadingMode.Auto));
        }
        plain = Project("pending"); scene = plain.Scenes[0];
        plain = plain with { Scenes = [scene with { ScriptBlocks = [new(scene.Lines[0].Id, ScriptPhase.DuringLine, new ScriptAction(Guid.NewGuid(), "", "pending"))] }] };
        var completion = new TaskCompletionSource<ImmutableArray<ScenarioAction>>();
        using (var player = new ScenePlayer(plain, scene.Id, "ru", null, PlaybackMode.Game, true, (_, _, _, _) => completion.Task))
        {
            var slow = Profile(1); var pendingReading = new ReadingPlayback(slow);
            Assert.IsTrue(player.Execution.HasPendingLua);
            pendingReading.Update(player, TimeSpan.FromSeconds(100), true, false); Assert.AreEqual("", pendingReading.VisibleText);
            Assert.IsFalse(pendingReading.Advance(player)); completion.SetResult([]);
            for (var i = 0; i < 1000 && player.Execution.HasPendingLua; i++) { player.PollLua(); await Task.Delay(1); }
            Assert.IsFalse(player.Execution.HasPendingLua);
            pendingReading.Update(player, TimeSpan.FromSeconds(1), true, false); Assert.AreEqual("p", pendingReading.VisibleText);
            await slow.FlushAsync();
        }
        await profile.FlushAsync();
    }
}
