using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Testing;

namespace NovellMaker.Core.Tests.Audio;

[TestClass]
public sealed class AudioPlaybackTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        internal void Advance(double seconds) => ticks += (long)(seconds * 1000);
    }
    private sealed class Source : IAudioSource
    {
        public float Volume { get; set; }
        public bool Finished { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(4);
        internal bool Paused, Disposed;
        public void Pause(bool value) => Paused = value;
        public void Update() { }
        public void Dispose() => Disposed = true;
    }
    private sealed class Backend : IAudioBackend
    {
        internal readonly List<Source> Sources = [];
        internal bool Fail;
        public IAudioSource Open(string path, bool loop)
        {
            if (Fail) throw new IOException("Cannot open replacement");
            var s = new Source(); Sources.Add(s); return s;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "NovellMaker.Playback", Guid.NewGuid().ToString("N"));
        internal readonly NovelProject Project;
        internal readonly Clock Clock = new();
        internal readonly Backend Backend = new();
        internal readonly AudioService Service;
        internal Guid Scene => Project.Scenes[0].Id;
        internal Guid Line => Project.Scenes[0].Lines[0].Id;
        internal Fixture(AudioMixSettings? mix = null)
        {
            var scene = NovelScene.Create("Scene") with { Lines = [DialogueLine.Create("ru", "Voice"), DialogueLine.Create("ru", "Silent")] };
            var p = AudioFixture.Attach(NovelProject.Create("Playback") with { Scenes = [scene] }, Root);
            Project = p with { Audio = p.Audio with { Playback = mix ?? new(), LineEffects = ImmutableDictionary<Guid, Guid>.Empty } };
            Service = new(Project, Root, "ru", Backend, Clock);
        }
        internal void SceneStart() => Service.Handle(new(AudioEventKind.Scene, Scene));
        internal void VoiceStart() => Service.Handle(new(AudioEventKind.Line, Scene, Line));
        internal void Step(double seconds) { Clock.Advance(seconds); Service.Update(); }
        public void Dispose() { Service.Dispose(); Directory.Delete(Root, true); }
    }

    [TestMethod]
    public void VoiceProgressUsesBackendCursorAndPausesIndependentlyOfGameAndHistory()
    {
        using var f = new Fixture(); f.SceneStart(); f.VoiceStart();
        var music = f.Backend.Sources[0]; var voice = f.Backend.Sources[1];
        f.Step(20); Assert.AreEqual(0d, f.Service.Voice.Progress, "Elapsed time cannot invent PCM consumption.");
        voice.Position = TimeSpan.FromSeconds(1.25); f.Service.Update();
        Assert.AreEqual(.3125, f.Service.Voice.Progress); Assert.AreEqual(TimeSpan.FromSeconds(4), f.Service.Voice.Duration);
        f.Service.PauseVoice(true); Assert.IsTrue(voice.Paused); Assert.IsFalse(music.Paused);
        f.Service.Pause(true); f.Service.Pause(false); Assert.IsTrue(voice.Paused);
        f.Step(10); Assert.AreEqual(.3125, f.Service.Voice.Progress);
        f.Service.ReplayVoice(f.Line, "ru"); Assert.IsFalse(f.Backend.Sources[2].Paused);
        f.Service.StopReplayVoice(); Assert.IsTrue(voice.Paused);
        f.Service.PauseVoice(false); Assert.IsFalse(voice.Paused);
        voice.Finished = true; f.Service.Update(); Assert.IsFalse(f.Service.Voice.Available);
        Assert.IsTrue(voice.Disposed); Assert.AreEqual(0d, f.Service.Voice.Progress);
    }

    [TestMethod]
    public void ContinueJumpSilentLineAndRestoreNeverRetainOrReplayVoice()
    {
        using var f = new Fixture(new() { FadeOutSeconds = 2 });
        foreach (var cue in new[] { AudioEventKind.Continue, AudioEventKind.Jump, AudioEventKind.Restore })
        {
            f.VoiceStart(); f.Service.PauseVoice(true); var voice = f.Backend.Sources[^1];
            f.Service.Handle(new(cue, f.Scene)); Assert.IsFalse(f.Service.Voice.Available); Assert.IsTrue(voice.Disposed);
            f.VoiceStart(); Assert.IsFalse(f.Service.Voice.Paused, "A fresh line must not inherit manual voice pause.");
        }
        f.Service.Handle(new(AudioEventKind.Line, f.Scene, f.Project.Scenes[0].Lines[1].Id));
        Assert.IsFalse(f.Service.Voice.Available);
        f.Backend.Fail = true; f.VoiceStart(); Assert.IsFalse(f.Service.Voice.Available); Assert.HasCount(1, f.Service.Diagnostics);
    }

    [TestMethod]
    public void FadesFreezeOnPauseAndNaturalTailUsesRealPosition()
    {
        using var f = new Fixture(new() { FadeInSeconds = 1, FadeOutSeconds = 1 }); f.VoiceStart();
        var voice = f.Backend.Sources.Single(); Assert.AreEqual(0f, voice.Volume);
        f.Step(.5); Assert.AreEqual(.5f, voice.Volume, .00001);
        f.Service.PauseVoice(true); f.Step(20); Assert.AreEqual(.5f, voice.Volume, .00001);
        f.Service.PauseVoice(false); f.Step(.5); Assert.AreEqual(1f, voice.Volume, .00001);
        voice.Position = TimeSpan.FromSeconds(3.5); f.Service.Update(); Assert.AreEqual(.5f, voice.Volume, .00001);
        f.Service.SetBus(AudioGroup.Master, new(.5f)); Assert.AreEqual(.25f, voice.Volume, .00001);
        f.Service.Handle(new(AudioEventKind.Continue, f.Scene)); Assert.IsTrue(voice.Disposed, "Skipping a line is immediate even with a release envelope.");
    }

    [TestMethod]
    public void CrossfadeOverlapsMusicDisposesOutgoingAndRestoreCutsAllTails()
    {
        using var f = new Fixture(new() { CrossfadeSeconds = 1 }); f.SceneStart(); var old = f.Backend.Sources.Single();
        f.Service.Handle(new(AudioEventKind.Scene, f.Scene)); var next = f.Backend.Sources[^1];
        Assert.IsFalse(old.Disposed); Assert.AreEqual(0f, next.Volume);
        f.Step(.5); Assert.AreEqual(.5f, old.Volume, .00001); Assert.AreEqual(.5f, next.Volume, .00001);
        f.Service.Pause(true); f.Step(10); Assert.IsFalse(old.Disposed);
        f.Service.Pause(false); f.Step(.5); Assert.IsTrue(old.Disposed); Assert.AreEqual(1, f.Service.ActiveSources);
        f.Service.Handle(new(AudioEventKind.Scene, f.Scene));
        f.Service.Handle(new(AudioEventKind.Restore, f.Scene)); Assert.AreEqual(1, f.Service.ActiveSources);
        Assert.IsTrue(next.Disposed); Assert.IsFalse(f.Service.Voice.Available);
    }

    [TestMethod]
    public void SceneReplacementStillFadesInWhenCrossfadeIsDisabled()
    {
        using var f = new Fixture(new() { FadeInSeconds = 1 }); f.SceneStart(); f.Step(1);
        var old = f.Backend.Sources[0]; f.SceneStart();
        Assert.IsTrue(old.Disposed); Assert.AreEqual(0f, f.Backend.Sources[1].Volume);
        f.Step(.5); Assert.AreEqual(.5f, f.Backend.Sources[1].Volume, .00001);
    }

    [TestMethod]
    public void RestoreDoesNotRetainOldVoiceDucking()
    {
        using var f = new Fixture(new() { DuckingEnabled = true, DuckingVolume = .25f, DuckingAttackSeconds = 0, DuckingReleaseSeconds = 2 });
        f.SceneStart(); f.VoiceStart(); f.Service.Update(); Assert.AreEqual(.25f, f.Backend.Sources[0].Volume);
        f.Service.Handle(new(AudioEventKind.Restore, f.Scene));
        Assert.IsFalse(f.Service.Voice.Available); Assert.AreEqual(1f, f.Backend.Sources[^1].Volume);
    }

    [TestMethod]
    public void SilentSceneFadesOutMusicAndFailedReplacementPreservesIt()
    {
        using var f = new Fixture(new() { FadeOutSeconds = 1, CrossfadeSeconds = 1 }); f.SceneStart();
        var music = f.Backend.Sources.Single(); f.Backend.Fail = true;
        f.SceneStart(); f.Step(2); Assert.IsFalse(music.Disposed); Assert.AreEqual(1f, music.Volume);
        f.Service.Handle(new(AudioEventKind.Scene, Guid.NewGuid())); f.Step(.5); Assert.AreEqual(.5f, music.Volume, .00001);
        f.Step(.5); Assert.IsTrue(music.Disposed); Assert.AreEqual(0, f.Service.ActiveSources);
    }

    [TestMethod]
    public void DuckingMultipliesBusesAndReleasesForPausedMutedAndCompletedVoice()
    {
        using var f = new Fixture(new() { DuckingEnabled = true, DuckingVolume = .25f, DuckingAttackSeconds = 1, DuckingReleaseSeconds = 1 });
        f.SceneStart(); var music = f.Backend.Sources[0];
        f.Service.SetBus(AudioGroup.Master, new(.5f)); f.Service.SetBus(AudioGroup.Sound, new(.5f));
        f.VoiceStart(); f.Step(.5); Assert.AreEqual(.25f * .625f, music.Volume, .00001);
        f.Step(.5); Assert.AreEqual(.25f * .25f, music.Volume, .00001);
        f.Service.PauseVoice(true); f.Step(1); Assert.AreEqual(.25f, music.Volume, .00001);
        f.Service.PauseVoice(false); f.Step(1); Assert.AreEqual(.0625f, music.Volume, .00001);
        f.Service.SetBus(AudioGroup.Voice, new(1, true)); f.Step(1); Assert.AreEqual(.25f, music.Volume, .00001);
        f.Service.SetBus(AudioGroup.Voice, new()); f.Step(1); Assert.AreEqual(.0625f, music.Volume, .00001);
        f.Backend.Sources[1].Finished = true; f.Step(1); Assert.AreEqual(.25f, music.Volume, .00001);
        Assert.AreEqual(.5f, f.Service.Bus(AudioGroup.Sound).Volume, "Ducking never rewrites player settings.");
    }

    [TestMethod]
    public void LuaVoiceAlsoDucksAndOwnerPauseReleasesWithoutCreatingDialogueProgress()
    {
        using var f = new Fixture(new() { DuckingEnabled = true, DuckingVolume = .2f, DuckingAttackSeconds = 0, DuckingReleaseSeconds = 0 });
        f.SceneStart(); var owner = new AudioOwner(Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid());
        f.Service.Execute(owner, new("play", Guid.NewGuid(), f.Project.Audio.Assets[0].Id, AudioGroup.Voice, false, 1));
        f.Service.PauseOwner(owner, false); f.Service.Update(); Assert.AreEqual(.2f, f.Backend.Sources[0].Volume, .00001);
        Assert.IsFalse(f.Service.Voice.Available);
        f.VoiceStart(); f.Service.PauseOwner(owner, true); f.Service.Update();
        Assert.AreEqual(.2f, f.Backend.Sources[0].Volume, .00001, "Another active voice retains ducking.");
        f.Service.PauseVoice(true); f.Service.Update(); Assert.AreEqual(1f, f.Backend.Sources[0].Volume);
        f.Service.PauseOwner(owner, false); f.Service.Update(); Assert.AreEqual(.2f, f.Backend.Sources[0].Volume, .00001);
        f.Service.StopOwner(owner); f.Service.Update(); Assert.AreEqual(1f, f.Backend.Sources[0].Volume);
        Assert.AreEqual(2, f.Service.ActiveSources);
    }

    [TestMethod]
    public async Task SettingsRoundTripOldV21DefaultsAndValidationAreExplicit()
    {
        using var f = new Fixture(new() { FadeInSeconds = .2, FadeOutSeconds = .4, CrossfadeSeconds = 1.5,
            DuckingEnabled = true, DuckingVolume = .3f, DuckingAttackSeconds = .1, DuckingReleaseSeconds = .6 });
        var path = Path.Combine(f.Root, "audio.novell"); var store = new JsonProjectStore();
        await store.SaveAsync(path, f.Project); var copy = await store.LoadAsync(path);
        Assert.IsTrue(AudioContent.Equals(f.Project.Audio, copy.Audio));
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!; node["audio"]!.AsObject().Remove("playback");
        await File.WriteAllTextAsync(path, node.ToJsonString()); copy = await store.LoadAsync(path);
        Assert.AreEqual(new AudioMixSettings(), copy.Audio.Playback);
        foreach (var invalid in new[] { new AudioMixSettings { CrossfadeSeconds = double.NaN }, new() { FadeOutSeconds = -1 },
            new() { DuckingVolume = 1.1f }, new() { DuckingAttackSeconds = double.PositiveInfinity }, new() { FadeInSeconds = 31 } })
            Assert.Throws<ArgumentException>(() => AudioProject.Validate(f.Project with { Audio = f.Project.Audio with { Playback = invalid } }));
    }
}
