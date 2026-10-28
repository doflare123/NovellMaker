using System.Collections.Immutable;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Tests.Audio;

internal sealed class ScriptAudioFixture : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "NovellMaker.ScriptAudio", Guid.NewGuid().ToString("N"));
    internal readonly Guid Asset = Guid.NewGuid();
    internal readonly AudioOwner Owner = new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid());
    internal readonly FakeBackend Backend = new();
    internal readonly AudioService Service;
    internal readonly NovelProject Project;
    internal readonly List<(AudioOwner Owner, ScriptAudioResult Result)> Results = [];
    internal ScriptAudioFixture()
    {
        Directory.CreateDirectory(Root);
        Project = NovelProject.Create("Lua audio") with { Audio = new() { Assets = [new(Asset, "sample.wav", $"Assets/Audio/{Asset:N}.wav")] } };
        Service = new(Project, Root, "ru", Backend);
        Service.PlaybackEnded += (owner, result) => Results.Add((owner, result));
    }
    internal ScriptAudioCommand Play(Guid? id = null, AudioGroup group = AudioGroup.Effect, double volume = .5, bool loop = false) => new("play", id ?? Guid.NewGuid(), Asset, group, loop, volume);
    internal sealed class FakeBackend : IAudioBackend
    {
        internal readonly List<Source> Sources = [];
        internal string? Failure;
        public IAudioSource Open(string path, bool loop)
        {
            if (Failure == "open") throw new IOException("missing audio");
            var source = new Source { Loop = loop, Failure = Failure }; Sources.Add(source); return source;
        }
    }
    internal sealed class Source : IAudioSource
    {
        internal string? Failure;
        internal bool Loop, Paused;
        internal int Disposals, Updates;
        private float volume;
        public float Volume { get => volume; set { if (Failure == "volume") throw new InvalidOperationException("volume failure"); volume = value; } }
        public bool Finished { get; set; }
        public void Update() { Updates++; if (Failure == "update") throw new IOException("update failure"); }
        public void Pause(bool value) { if (Failure == "pause") throw new InvalidOperationException("pause failure"); Paused = value; }
        public void Dispose() { Disposals++; if (Failure == "dispose") throw new IOException("dispose failure"); }
    }
    public void Dispose() { Service.Dispose(); Directory.Delete(Root, true); }
}

[TestClass]
public sealed class ScriptAudioTests
{
    [TestMethod]
    [DataRow(AudioGroup.Sound)] [DataRow(AudioGroup.Effect)] [DataRow(AudioGroup.Voice)]
    public void SourceGainMultipliesBusesAndLuaOwnershipSurvivesStoryContinuation(AudioGroup group)
    {
        using var f = new ScriptAudioFixture(); var command = f.Play(group: group, loop: true);
        f.Service.SetBus(AudioGroup.Master, new(.5f)); f.Service.SetBus(group, new(.4f));
        f.Service.Execute(f.Owner, command); var s = f.Backend.Sources.Single();
        Assert.AreEqual(.1f, s.Volume, .000001f); Assert.IsTrue(s.Loop); Assert.IsTrue(s.Paused);
        f.Service.PauseOwner(f.Owner, false); f.Service.Pause(true); Assert.IsFalse(s.Paused);
        f.Service.Handle(new(AudioEventKind.Continue, Guid.NewGuid())); f.Service.Handle(new(AudioEventKind.Jump, Guid.NewGuid()));
        Assert.AreEqual(1, f.Service.ActiveSources);
        f.Service.Execute(f.Owner, new("volume", command.Id, Volume: 1)); Assert.AreEqual(.2f, s.Volume, .000001f);
        f.Service.SetBus(group, new(.4f, true)); Assert.AreEqual(0, s.Volume);
        f.Service.SetBus(group, new(.4f)); f.Service.SetBus(AudioGroup.Master, new(.5f, true)); Assert.AreEqual(0, s.Volume);
        f.Service.PauseOwner(f.Owner, true); s.Finished = true; f.Service.Update(); Assert.AreEqual(0, s.Updates);
        f.Service.PauseOwner(f.Owner, false); f.Service.Update(); Assert.AreEqual(1, s.Disposals);
        Assert.AreEqual("completed", f.Results.Single().Result.Status); f.Service.Update(); Assert.HasCount(1, f.Results);
    }

    [TestMethod]
    public void ForeignOwnersAndHandlesCannotChangeOrStopSourcesAndCleanupIsIdempotent()
    {
        using var f = new ScriptAudioFixture(); var play = f.Play(); f.Service.Execute(f.Owner, play);
        var foreign = f.Owner with { InstanceId = Guid.NewGuid() };
        f.Service.Execute(foreign, new("stop", play.Id)); f.Service.Execute(foreign, new("volume", play.Id, Volume: 0));
        f.Service.StopOwner(foreign); Assert.AreEqual(1, f.Service.ActiveSources); Assert.AreEqual(.5f, f.Backend.Sources[0].Volume);
        Assert.Throws<ArgumentException>(() => f.Service.Execute(f.Owner, play));
        f.Service.StopOwner(f.Owner); f.Service.StopOwner(f.Owner); Assert.AreEqual(1, f.Backend.Sources[0].Disposals); Assert.IsEmpty(f.Results);
        f.Service.Execute(f.Owner, f.Play()); f.Service.Stop(); f.Service.Stop(); Assert.AreEqual("stopped", f.Results.Single().Result.Status);
        f.Service.Dispose(); f.Service.Dispose();
    }

    [TestMethod]
    [DataRow("open")] [DataRow("volume")] [DataRow("pause")] [DataRow("update")] [DataRow("dispose")]
    public void BackendFailureReleasesSourceAndReportsOneBoundedTerminalResult(string phase)
    {
        using var f = new ScriptAudioFixture(); f.Backend.Failure = phase; var command = f.Play();
        f.Service.Execute(f.Owner, command);
        f.Service.PauseOwner(f.Owner, false); f.Service.Update();
        if (phase == "dispose") f.Service.Execute(f.Owner, new("stop", command.Id));
        Assert.AreEqual(0, f.Service.ActiveSources); Assert.HasCount(1, f.Results); Assert.AreEqual(command.Id, f.Results[0].Result.Id);
        Assert.AreEqual("failed", f.Results[0].Result.Status); Assert.IsTrue(f.Results[0].Result.Message.Length <= 1024);
        Assert.IsNotEmpty(f.Service.Diagnostics); Assert.IsTrue(f.Backend.Sources.All(s => s.Disposals == 1));
        f.Service.Stop(); Assert.HasCount(1, f.Results);
    }

    [TestMethod]
    [DataRow("pause")] [DataRow("volume")] [DataRow("update")]
    public void LateBackendFailureIsContainedByMixerAndPause(string phase)
    {
        using var f = new ScriptAudioFixture(); f.Service.Execute(f.Owner, f.Play()); var source = f.Backend.Sources.Single(); source.Failure = phase;
        if (phase == "volume") f.Service.SetBus(AudioGroup.Master, new(.8f));
        else if (phase == "pause") f.Service.Pause(true);
        else { f.Service.PauseOwner(f.Owner, false); f.Service.Update(); }
        Assert.AreEqual(0, f.Service.ActiveSources); Assert.AreEqual("failed", f.Results.Single().Result.Status);
    }

    [TestMethod]
    public void LimitIsSharedAndMissingAssetsAndDisposedServiceReportFailure()
    {
        using var f = new ScriptAudioFixture();
        for (var i = 0; i < 33; i++) f.Service.Execute(f.Owner, f.Play());
        Assert.AreEqual(32, f.Service.ActiveSources); Assert.HasCount(32, f.Backend.Sources); Assert.AreEqual("failed", f.Results.Single().Result.Status);
        f.Service.StopOwner(f.Owner); f.Service.Execute(f.Owner, f.Play() with { AssetId = Guid.NewGuid() }); Assert.HasCount(2, f.Results);
        f.Service.Dispose(); f.Service.Execute(f.Owner, f.Play()); Assert.HasCount(3, f.Results);
    }

    [TestMethod]
    public void CommandAndResultShapesAreStrict()
    {
        using var f = new ScriptAudioFixture(); var play = f.Play(); play.Validate(); new ScriptAudioCommand("stop", play.Id).Validate();
        foreach (var c in new[] { play with { Id = Guid.Empty }, play with { Kind = "bad" }, play with { AssetId = null }, play with { AssetId = Guid.Empty },
            play with { Group = AudioGroup.Master }, play with { Loop = null }, play with { Volume = -1 }, play with { Volume = double.NaN },
            play with { Volume = double.PositiveInfinity }, play with { Volume = 2 }, play with { Kind = "stop" }, play with { Kind = "volume" },
            new ScriptAudioCommand("volume", play.Id), new ScriptAudioCommand("stop", play.Id, Volume: 1) })
            Assert.Throws<ArgumentException>(c.Validate);
        foreach (var r in new[] { new ScriptAudioResult(Guid.Empty, "completed"), new ScriptAudioResult(play.Id, "bad"),
            new ScriptAudioResult(play.Id, "failed", new string('x', 1025)), new ScriptAudioResult(play.Id, "failed", null!) }) Assert.Throws<ArgumentException>(r.Validate);
        new ScriptAudioResult(play.Id, "stopped").Validate();
    }
}
