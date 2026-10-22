using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Profiles;

[TestClass]
public sealed class PlayerProfileTests
{
    private static PlayerSettings Defaults => PlayerSettings.Defaults(new());
    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nm-profile-" + Guid.NewGuid().ToString("N"));
        internal string File => System.IO.Path.Combine(Path, "profile.json");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    [TestMethod]
    public async Task MissingProfileUsesAuthorMixerWithoutCreatingFilesAndRoundTripsAcrossNewStore()
    {
        using var folder = new Folder(); var id = Guid.NewGuid(); var store = new PlayerProfileStore(id, folder.File);
        var defaults = PlayerSettings.Defaults(new() { Mixer = ImmutableDictionary<AudioGroup, AudioBus>.Empty.Add(AudioGroup.Voice, new(.4f, true)) });
        Assert.AreEqual(new(.4f, true), defaults.Voice); Assert.AreEqual(new(1, false), defaults.Master);
        var read = await store.ReadAsync(defaults); Assert.AreEqual(PlayerProfileStatus.Missing, read.Status);
        Assert.AreEqual(defaults, read.Settings); Assert.IsFalse(Directory.Exists(folder.Path));
        var changed = defaults with { Fullscreen = true, Master = new(.7f, true), Sound = new(.3f, false), Effect = new(0, true), Voice = new(1, false) };
        await store.SaveAsync(changed);
        read = await new PlayerProfileStore(id, folder.File).ReadAsync(Defaults);
        Assert.AreEqual(PlayerProfileStatus.Loaded, read.Status); Assert.AreEqual(changed, read.Settings);
        Assert.IsEmpty(Directory.GetFiles(folder.Path, "*.tmp"));
        var other = new PlayerProfileStore(Guid.NewGuid(), folder.File); var bytes = System.IO.File.ReadAllBytes(folder.File);
        Assert.AreEqual(PlayerProfileStatus.ForeignProject, (await other.ReadAsync(Defaults)).Status);
        await Assert.ThrowsAsync<IOException>(() => other.SaveAsync(Defaults));
        CollectionAssert.AreEqual(bytes, System.IO.File.ReadAllBytes(folder.File));
        var separate = new PlayerProfileStore(Guid.NewGuid(), System.IO.Path.Combine(folder.Path, "other.json"));
        Assert.AreEqual(Defaults, (await separate.ReadAsync(Defaults)).Settings);
    }

    [TestMethod]
    public async Task BackupRecoversCorruptOrMissingPrimaryWithoutImplicitRepairAndSurvivesNextWrite()
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File);
        var first = Defaults with { Master = new(.3f, true) }; var second = Defaults with { Master = new(.6f, false) };
        await store.SaveAsync(first); await store.SaveAsync(second);
        System.IO.File.WriteAllText(folder.File, "broken");
        var recovered = await store.ReadAsync(Defaults);
        Assert.AreEqual(PlayerProfileStatus.Recovered, recovered.Status); Assert.AreEqual(first, recovered.Settings);
        Assert.AreEqual("broken", System.IO.File.ReadAllText(folder.File)); Assert.IsFalse(string.IsNullOrEmpty(recovered.Diagnostic));
        await store.SaveAsync(second); System.IO.File.WriteAllText(folder.File, "broken again");
        Assert.AreEqual(first, (await store.ReadAsync(Defaults)).Settings);
        System.IO.File.Delete(folder.File);
        Assert.AreEqual(PlayerProfileStatus.Recovered, (await store.ReadAsync(Defaults)).Status);
        System.IO.File.WriteAllText(folder.File + ".bak", "broken backup");
        Assert.AreEqual(PlayerProfileStatus.Corrupt, (await store.ReadAsync(Defaults)).Status);
        System.IO.File.WriteAllText(folder.File, "broken primary");
        Assert.AreEqual(PlayerProfileStatus.Corrupt, (await store.ReadAsync(Defaults)).Status);
        await store.SaveAsync(second); Assert.AreEqual(second, (await store.ReadAsync(Defaults)).Settings);
    }

    [TestMethod]
    [DataRow("unknown")] [DataRow("missing")] [DataRow("duplicate")] [DataRow("null")]
    [DataRow("negative")] [DataRow("large")] [DataRow("missingBusField")] [DataRow("invalidBoolean")]
    [DataRow("tooBig")] [DataRow("invalidRoot")] [DataRow("invalidVersion")] [DataRow("nullBus")]
    public async Task StrictProfileRejectsMalformedDocumentsAndKeepsThemUnchanged(string issue)
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File); await store.SaveAsync(Defaults);
        var node = JsonNode.Parse(System.IO.File.ReadAllText(folder.File))!;
        switch (issue)
        {
            case "unknown": node["Extra"] = 1; break;
            case "missing": node["Settings"]!.AsObject().Remove("Fullscreen"); break;
            case "null": node["Settings"] = null; break;
            case "negative": node["Settings"]!["Master"]!["Volume"] = -.1; break;
            case "large": node["Settings"]!["Voice"]!["Volume"] = 1.01; break;
            case "nullBus": node["Settings"]!["Effect"] = null; break;
            case "missingBusField": node["Settings"]!["Sound"]!.AsObject().Remove("Muted"); break;
            case "invalidBoolean": node["Settings"]!["Fullscreen"] = "yes"; break;
            case "invalidVersion": node["Version"] = "one"; break;
        }
        var text = issue switch
        {
            "duplicate" => node.ToJsonString().Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal),
            "tooBig" => new string(' ', PlayerProfileStore.MaxFileBytes + 1),
            "invalidRoot" => "[]", _ => node.ToJsonString(),
        };
        System.IO.File.WriteAllText(folder.File, text); var read = await store.ReadAsync(Defaults);
        Assert.AreEqual(PlayerProfileStatus.Corrupt, read.Status); Assert.AreEqual(Defaults, read.Settings);
        Assert.AreEqual(text, System.IO.File.ReadAllText(folder.File)); Assert.IsFalse(read.Diagnostic.Contains(folder.Path, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(0)] [DataRow(2)]
    public async Task UnsupportedVersionIsNotMigratedOrOverwritten(int version)
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File); await store.SaveAsync(Defaults);
        var node = JsonNode.Parse(System.IO.File.ReadAllText(folder.File))!; node["Version"] = version;
        var text = node.ToJsonString(); System.IO.File.WriteAllText(folder.File, text);
        Assert.AreEqual(PlayerProfileStatus.UnsupportedVersion, (await store.ReadAsync(Defaults)).Status);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Defaults));
        Assert.AreEqual(text, System.IO.File.ReadAllText(folder.File));
    }

    [TestMethod]
    [DataRow("missingFullscreen", false)] [DataRow("missingFullscreen", true)]
    [DataRow("invalidVolume", false)] [DataRow("invalidVolume", true)]
    [DataRow("nullSettings", false)] [DataRow("nullSettings", true)]
    [DataRow("missingVersion", false)] [DataRow("missingVersion", true)]
    [DataRow("invalidVersion", false)] [DataRow("invalidVersion", true)]
    [DataRow("foreignIdFirst", false)] [DataRow("foreignIdFirst", true)]
    [DataRow("foreignIdLast", false)] [DataRow("foreignIdLast", true)]
    public async Task ReadableForeignIdProtectsPrimaryAndBackupEvenWithDamagedSettingsOrHeader(string issue, bool inBackup)
    {
        using var folder = new Folder(); var id = Guid.NewGuid(); var foreign = Guid.NewGuid();
        var store = new PlayerProfileStore(id, folder.File);
        await store.SaveAsync(Defaults); await store.SaveAsync(Defaults);
        var file = inBackup ? folder.File + ".bak" : folder.File;
        var document = JsonNode.Parse(System.IO.File.ReadAllText(file))!;
        document["ProjectId"] = foreign;
        switch (issue)
        {
            case "missingFullscreen": document["Settings"]!.AsObject().Remove("Fullscreen"); break;
            case "invalidVolume": document["Settings"]!["Master"]!["Volume"] = -1; break;
            case "nullSettings": document["Settings"] = null; break;
            case "missingVersion": document.AsObject().Remove("Version"); break;
            case "invalidVersion": document["Version"] = "invalid"; break;
        }
        var text = document.ToJsonString();
        var foreignProperty = "\"ProjectId\":\"" + foreign + "\"";
        var ownProperty = "\"ProjectId\":\"" + id + "\"";
        if (issue == "foreignIdFirst") text = text.Replace(foreignProperty, foreignProperty + "," + ownProperty, StringComparison.Ordinal);
        if (issue == "foreignIdLast") text = text.Replace(foreignProperty, ownProperty + "," + foreignProperty, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file, text);
        var primary = System.IO.File.ReadAllBytes(folder.File); var backup = System.IO.File.ReadAllBytes(folder.File + ".bak");
        if (!inBackup) Assert.AreEqual(PlayerProfileStatus.ForeignProject, (await store.ReadAsync(Defaults)).Status);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Defaults with { Fullscreen = true }));
            CollectionAssert.AreEqual(primary, System.IO.File.ReadAllBytes(folder.File));
            CollectionAssert.AreEqual(backup, System.IO.File.ReadAllBytes(folder.File + ".bak"));
            Assert.IsEmpty(Directory.GetFiles(folder.Path, "*.tmp"));
        }
    }

    [TestMethod]
    [DataRow("missing", 0)] [DataRow("missing", 2)]
    [DataRow("loaded", 0)] [DataRow("loaded", 2)]
    [DataRow("corrupt", 0)] [DataRow("corrupt", 2)]
    public async Task UnsupportedBackupVersionBlocksEveryWriteBeforeChangingEitherFile(string primaryState, int version)
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File);
        await store.SaveAsync(Defaults); await store.SaveAsync(Defaults);
        var document = JsonNode.Parse(System.IO.File.ReadAllText(folder.File + ".bak"))!; document["Version"] = version;
        // A future schema need not contain any settings known by this engine.
        document.AsObject().Remove("Settings");
        System.IO.File.WriteAllText(folder.File + ".bak", document.ToJsonString());
        if (primaryState == "missing") System.IO.File.Delete(folder.File);
        if (primaryState == "corrupt") System.IO.File.WriteAllText(folder.File, "broken primary");
        var primary = primaryState == "missing" ? null : System.IO.File.ReadAllBytes(folder.File);
        var backup = System.IO.File.ReadAllBytes(folder.File + ".bak");
        if (primaryState == "missing") Assert.AreEqual(PlayerProfileStatus.UnsupportedVersion, (await store.ReadAsync(Defaults)).Status);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Defaults with { Fullscreen = true }));
            if (primary is null) Assert.IsFalse(System.IO.File.Exists(folder.File));
            else CollectionAssert.AreEqual(primary, System.IO.File.ReadAllBytes(folder.File));
            CollectionAssert.AreEqual(backup, System.IO.File.ReadAllBytes(folder.File + ".bak"));
            Assert.IsEmpty(Directory.GetFiles(folder.Path, "*.tmp"));
        }
    }

    [TestMethod]
    public async Task UnreadableBackupBlocksWriteWithoutChangingPrimary()
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File);
        await store.SaveAsync(Defaults); await store.SaveAsync(Defaults);
        var primary = System.IO.File.ReadAllBytes(folder.File); var backup = System.IO.File.ReadAllBytes(folder.File + ".bak");
        using (var locked = new FileStream(folder.File + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Defaults with { Fullscreen = true }));
        CollectionAssert.AreEqual(primary, System.IO.File.ReadAllBytes(folder.File));
        CollectionAssert.AreEqual(backup, System.IO.File.ReadAllBytes(folder.File + ".bak"));
    }

    [TestMethod]
    public async Task LockedFileAndUnavailableDirectoryAreDiagnosedAndCancellationDoesNotPublish()
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File); await store.SaveAsync(Defaults);
        var bytes = System.IO.File.ReadAllBytes(folder.File);
        using (var locked = new FileStream(folder.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.AreEqual(PlayerProfileStatus.Unavailable, (await store.ReadAsync(Defaults)).Status);
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Defaults));
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(Defaults with { Fullscreen = true }, cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync(Defaults, cancelled.Token));
        CollectionAssert.AreEqual(bytes, System.IO.File.ReadAllBytes(folder.File));
        Assert.IsEmpty(Directory.GetFiles(folder.Path, "*.tmp"));
        var blocked = new PlayerProfileStore(Guid.NewGuid(), System.IO.Path.Combine(folder.File, "profile.json"));
        await Assert.ThrowsAsync<IOException>(() => blocked.SaveAsync(Defaults));
    }

    [TestMethod]
    public void AllSettingsUseTypedBoundedChangesAndNoInvalidBusCanBeApplied()
    {
        foreach (var group in Enum.GetValues<AudioGroup>())
        {
            var key = group.ToString().ToLowerInvariant();
            foreach (var volume in new[] { 0d, .5, 1 })
            {
                var value = Defaults.Apply(new(key + "_volume", volume, null)).Apply(new(key + "_muted", null, true));
                Assert.AreEqual(new((float)volume, true), value.Bus(group));
            }
        }
        Assert.IsTrue(Defaults.Apply(new("fullscreen", null, true)).Fullscreen);
        Assert.IsFalse(Defaults.Apply(new("fullscreen", null, false)).Fullscreen);
        foreach (var change in new PlayerSettingChange[] {
            new("unknown",0,null), new(null!,null,true), new("master_volume",null,null), new("voice_muted",null,null),
            new("sound_volume",0,true), new("effect_muted",0,true), new("fullscreen",1,null), new("master_volume",-1,null),
            new("sound_volume",1.01,null), new("effect_volume",double.NaN,null), new("voice_volume",double.PositiveInfinity,null) })
            Assert.Throws<ArgumentException>(() => Defaults.Apply(change));
        foreach (var invalid in new[] { -1f, 2, float.NaN, float.NegativeInfinity })
            foreach (var group in Enum.GetValues<AudioGroup>()) Assert.Throws<ArgumentException>(() => Defaults.SetBus(group, new(invalid)));
        Assert.Throws<ArgumentException>(() => Defaults.Bus((AudioGroup)999));
        Assert.Throws<ArgumentException>(() => Defaults.SetBus((AudioGroup)999, new()));
        foreach (var value in new[] { Defaults with { Master = null! }, Defaults with { Sound = null! }, Defaults with { Effect = null! }, Defaults with { Voice = null! } })
            Assert.Throws<ArgumentException>(value.Validate);
        Assert.Throws<ArgumentException>(() => new PlayerProfileStore(Guid.Empty, "profile.json"));
    }

    private sealed class ControlledStore : IPlayerProfileStore
    {
        internal readonly List<PlayerSettings> Writes = [];
        internal readonly Queue<TaskCompletionSource> Gates = new();
        public Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default) => Task.FromResult(new PlayerProfileRead(defaults, PlayerProfileStatus.Missing, ""));
        public Task SaveAsync(PlayerSettings settings, CancellationToken token = default)
        { lock (Writes) { Writes.Add(settings); return Gates.Dequeue().Task; } }
        internal async Task WaitWrites(int count)
        { for (var i = 0; i < 300 && Writes.Count < count; i++) await Task.Delay(5); Assert.HasCount(count, Writes); }
    }

    [TestMethod]
    public async Task UnsupportedWindowStartupUsesEffectiveModeWithoutWritingOrBlockingLaterAudioChanges()
    {
        var store = new ControlledStore(); var written = new TaskCompletionSource(); store.Gates.Enqueue(written);
        var host = new PlayerSettingsSession(store, new(Defaults with { Fullscreen = true }, PlayerProfileStatus.Loaded, ""));
        host.UseStartupFallback(host.Current with { Fullscreen = false });
        Assert.IsFalse(host.Current.Fullscreen); Assert.IsFalse(host.IsBusy); Assert.IsEmpty(store.Writes);
        Assert.Throws<ArgumentException>(() => host.UseStartupFallback(Defaults with { Sound = new(2, false) }));
        host.Set(host.Current with { Voice = new(.5f, false) }); await store.WaitWrites(1);
        Assert.Throws<InvalidOperationException>(() => host.UseStartupFallback(Defaults));
        written.SetResult(); await host.FlushAsync(); Assert.IsFalse(store.Writes[0].Fullscreen);
    }

    [TestMethod]
    public async Task HostCoalescesRapidChangesRetainsCurrentOnFailureAndExplicitRetryFlushesLatest()
    {
        var store = new ControlledStore(); var first = new TaskCompletionSource(); var second = new TaskCompletionSource(); var retry = new TaskCompletionSource();
        store.Gates.Enqueue(first); store.Gates.Enqueue(second); store.Gates.Enqueue(retry);
        var host = new PlayerSettingsSession(store, new(Defaults, PlayerProfileStatus.Missing, "initial"));
        Assert.IsFalse(host.Poll()); Assert.IsFalse(host.IsBusy); Assert.AreEqual("initial", host.Diagnostic);
        host.Set(Defaults); await store.WaitWrites(1);
        for (var i = 0; i < 100; i++) host.Set(Defaults with { Master = new(i / 100f, i % 2 == 0) });
        Assert.HasCount(1, store.Writes); Assert.IsFalse(host.Poll());
        first.SetResult(); for (var i = 0; i < 300 && !host.Poll(); i++) await Task.Delay(5);
        await store.WaitWrites(2); Assert.AreEqual(host.Current, store.Writes[1]);
        second.SetException(new UnauthorizedAccessException("private/path")); await host.FlushAsync();
        Assert.IsFalse(host.IsBusy); Assert.IsFalse(host.LastWrite!.Success); Assert.IsFalse(host.Diagnostic.Contains("private/path", StringComparison.Ordinal));
        Assert.AreEqual(.99f, host.Current.Master.Volume); Assert.HasCount(2, store.Writes);
        host.Set(host.Current); await store.WaitWrites(3); retry.SetResult(); await host.FlushAsync();
        Assert.IsTrue(host.LastWrite!.Success); Assert.AreEqual("", host.Diagnostic);
        Assert.Throws<ArgumentException>(() => host.Set(Defaults with { Master = new(-1, false) }));
        Assert.AreEqual(.99f, host.Current.Master.Volume);
    }

    [TestMethod]
    public async Task GameplayLoadNewGameAndMenuNeverReadOrResetPreferences()
    {
        using var folder = new Folder(); var scene = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("ru", "one"), DialogueLine.Create("ru", "two")] };
        var menu = NovelScene.Create("Menu");
        var project = NovelProject.Create("Profile") with { Scenes = [scene, menu], StartScreenSceneId = menu.Id };
        using var game = new GameSession(project, scene.Id, "ru"); Assert.IsTrue(game.NewGame(game.Generation));
        var save = GameSave.Serialize(game.Player.Execution);
        var store = new PlayerProfileStore(project.Id, folder.File); var settings = new PlayerSettingsSession(store, await store.ReadAsync(Defaults));
        var originalRevision = game.Player.Execution.State.Revision;
        var preferences = Defaults with { Sound = new(.2f, true), Fullscreen = true }; settings.Set(preferences);
        game.Player.Next(); game.LoadSave(save); Assert.AreEqual(preferences, settings.Current);
        Assert.AreEqual(originalRevision, game.Player.Execution.State.Revision);
        Assert.IsTrue(game.ReturnToMenu(game.Generation)); Assert.AreEqual(preferences, settings.Current);
        Assert.IsTrue(game.NewGame(game.Generation)); Assert.AreEqual(preferences, settings.Current);
        Assert.IsTrue(game.End(game.Generation)); Assert.AreEqual(preferences, settings.Current);
        await settings.FlushAsync(); Assert.AreEqual(preferences, (await store.ReadAsync(Defaults)).Settings);
        Assert.IsFalse(save.Contains("Fullscreen", StringComparison.Ordinal));
    }
}
