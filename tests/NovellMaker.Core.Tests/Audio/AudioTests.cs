using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Testing;

namespace NovellMaker.Core.Tests.Audio;

[TestClass]
public sealed class AudioTests
{
    private string root = null!;
    private NovelProject project = null!;
    [TestInitialize] public void Init()
    {
        root = Path.Combine(Path.GetTempPath(), "NovellMaker.Audio.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        project = AudioFixture.Attach(NovelProject.Create("Аудио") with { Scenes = [NovelScene.Create("Первая") with { Lines = [DialogueLine.Create("ru", "Один"), DialogueLine.Create("ru", "Два")] }] }, root);
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [TestMethod] public void MixerUpdatesLiveSourcesAndHonorsMasterMuteAndPause()
    {
        var backend = new FakeBackend(); using var audio = new AudioService(project, root, "ru", backend);
        audio.Handle(new(AudioEventKind.Scene, project.Scenes[0].Id));
        audio.Handle(new(AudioEventKind.Line, project.Scenes[0].Id, project.Scenes[0].Lines[0].Id));
        Assert.AreEqual(3, audio.ActiveSources);
        audio.SetBus(AudioGroup.Master, new(0.5f)); audio.SetBus(AudioGroup.Voice, new(0.4f));
        Assert.AreEqual(0.5f, backend.Sources[0].Volume); Assert.AreEqual(0.2f, backend.Sources[1].Volume);
        audio.SetBus(AudioGroup.Effect, new(1, true)); Assert.AreEqual(0f, backend.Sources[2].Volume);
        audio.SetBus(AudioGroup.Master, new(1, true)); Assert.IsTrue(backend.Sources.All(s => s.Volume == 0));
        audio.Pause(true); Assert.IsTrue(backend.Sources.All(s => s.Paused));
        audio.Pause(false); Assert.IsTrue(backend.Sources.All(s => !s.Paused));
        audio.Handle(new(AudioEventKind.Continue, project.Scenes[0].Id));
        Assert.IsTrue(backend.Sources[1].Disposed); Assert.IsFalse(backend.Sources[0].Disposed); Assert.IsFalse(backend.Sources[2].Disposed);
        backend.Sources[2].Finished = true; audio.Update(); Assert.AreEqual(1, audio.ActiveSources);
    }
    [TestMethod] public void InterpreterContinuationJumpAndRestoreHaveOrderedAudioPolicy()
    {
        var scene = project.Scenes[0];
        var second = NovelScene.Create("Вторая") with { Lines = [DialogueLine.Create("ru", "Три")] };
        project = project with { Scenes = project.Scenes.Add(second) };
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        CollectionAssert.AreEqual(new[] { AudioEventKind.Scene, AudioEventKind.Line }, player.DrainAudioEvents().Select(e => e.Kind).ToArray());
        var save = GameSave.Serialize(player);
        Assert.IsTrue(player.AdvanceSettled());
        CollectionAssert.AreEqual(new[] { AudioEventKind.Continue, AudioEventKind.Line }, player.DrainAudioEvents().Select(e => e.Kind).ToArray());
        GameSave.Load(player, save);
        CollectionAssert.AreEqual(new[] { AudioEventKind.Restore }, player.DrainAudioEvents().Select(e => e.Kind).ToArray());
        var state = player.State;
        Assert.Throws<ArgumentException>(() => GameSave.Load(player, "{}"));
        Assert.AreEqual(state, player.State); Assert.AreEqual(0, player.DrainAudioEvents().Length);
        // A real branch into another scene stops transient sounds before starting its background and voice.
        scene = scene with { ScriptBlocks = [new(scene.Lines[0].Id, ScriptPhase.AfterLine, new JumpAction(Guid.NewGuid(), second.Id, ScenarioProgram.LineEntryId(second.Lines[0].Id)))] };
        project = project with { Scenes = project.Scenes.SetItem(0, scene) };
        player = new(project, scene.Id, "ru"); player.DrainAudioEvents(); player.AdvanceSettled();
        CollectionAssert.AreEqual(new[] { AudioEventKind.Continue, AudioEventKind.Jump, AudioEventKind.Scene, AudioEventKind.Line }, player.DrainAudioEvents().Select(e => e.Kind).ToArray());
    }
    [TestMethod] public void RestoreRestartsOnlyBackgroundAndLanguageNeverFallsBackToWrongVoice()
    {
        var backend = new FakeBackend(); using var audio = new AudioService(project, root, "en", backend);
        audio.Handle(new(AudioEventKind.Line, project.Scenes[0].Id, project.Scenes[0].Lines[0].Id));
        Assert.AreEqual(1, audio.ActiveSources); // effect, no Russian speech in English
        audio.Handle(new(AudioEventKind.Restore, project.Scenes[0].Id));
        Assert.AreEqual(1, audio.ActiveSources); Assert.IsTrue(backend.Sources[0].Disposed); Assert.IsTrue(backend.Sources[1].Loop);
        audio.Handle(new(AudioEventKind.Stop, project.Scenes[0].Id)); Assert.AreEqual(0, audio.ActiveSources);
    }
    [TestMethod] public async Task Version9RoundTripsAndVersion8MigrationDoesNotWrite()
    {
        var path = Path.Combine(root, "project.novell"); var store = new JsonProjectStore();
        await store.SaveAsync(path, project); Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); node["formatVersion"] = 8;
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        node.Remove("audio"); node.Remove("scripts"); await File.WriteAllTextAsync(path, node.ToJsonString()); var before = File.ReadAllBytes(path);
        var migrated = await store.LoadAsync(path); Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion); Assert.AreEqual(0, migrated.Audio.Assets.Length);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }
    [TestMethod] public void WaveParserRejectsTruncationCompressionAndBadAlignmentWithoutReadingPcm()
    {
        var wave = AudioFixture.Wave(); using var stream = new MemoryStream(wave);
        var info = PcmWave.Inspect(stream); Assert.AreEqual(24000, info.SampleRate); Assert.AreEqual(48000, info.Length);
        foreach (var bytes in new[] { wave[..^1], wave.ToArray(), wave.ToArray(), wave.ToArray() }.Select((b, i) => { if (i == 1) b[20] = 3; if (i == 2) b[32] = 4; if (i == 3) b[40] = 1; return b; }))
            Assert.Throws<InvalidDataException>(() => PcmWave.Inspect(new MemoryStream(bytes)));
    }
    [TestMethod] public void MissingAudioDiagnosesAndDoesNotBreakScenario()
    {
        File.Delete(Path.Combine(root, project.Audio.Assets[0].RelativePath));
        using var audio = new AudioService(project, root, "ru", new CheckingBackend());
        audio.Handle(new(AudioEventKind.Scene, project.Scenes[0].Id));
        Assert.AreEqual(1, audio.Diagnostics.Count); Assert.AreEqual(0, audio.ActiveSources);
    }
    private sealed class CheckingBackend : IAudioBackend { public IAudioSource Open(string path, bool loop) { using var s = File.OpenRead(path); throw new InvalidOperationException(); } }
    private sealed class FakeBackend : IAudioBackend
    {
        public List<FakeSource> Sources { get; } = [];
        public IAudioSource Open(string path, bool loop) { var s = new FakeSource { Loop = loop }; Sources.Add(s); return s; }
    }
    private sealed class FakeSource : IAudioSource
    {
        public bool Loop { get; init; } public float Volume { get; set; } public bool Paused { get; private set; }
        public bool Disposed { get; private set; } public bool Finished { get; set; }
        public void Update() { } public void Pause(bool paused) => Paused = paused; public void Dispose() => Disposed = true;
    }
}
