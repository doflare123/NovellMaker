using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeSettingsServicesTests
{
    private sealed class Store : IPlayerProfileStore
    {
        internal TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PlayerSettings? Written;
        internal PlayerProfileData? Profile;
        internal int WriteCount;
        public Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default) => Task.FromResult(new PlayerProfileRead(defaults, PlayerProfileStatus.Missing, ""));
        public Task SaveAsync(PlayerProfileData profile, CancellationToken token = default) { Written = profile.Settings; Profile = profile; WriteCount++; return Saved.Task; }
    }
    private static GameSession Session()
    {
        var story = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("ru", "line")] };
        return new(NovelProject.Create("Settings") with { Scenes = [story] }, story.Id, "ru");
    }
    private static PlayerSettingsSession Settings(Store store) => new(store, new(PlayerSettings.Defaults(new()), PlayerProfileStatus.Missing, ""));
    private static GameServiceCommand Set(string key, double? number = null, bool? flag = null) =>
        new(Guid.NewGuid(), GameServiceAction.SetSetting) { Setting = new(key, number, flag) };
    private static GameServiceCommand Persistent(string id, bool unlock = true) =>
        new(Guid.NewGuid(), unlock ? GameServiceAction.SetPersistent : GameServiceAction.GetPersistent) { Key = id, DataValue = unlock ? "value" : null };

    [TestMethod]
    public async Task PersistentStatusWaitsForDiskRetriesFailureAndSettingsPreserveTheFullProfile()
    {
        using var session = Session(); var store = new Store(); var profile = Settings(store);
        var bridge = new RuntimeSettingsServices(session, profile, _ => Assert.Fail("Persistent must not apply window settings."));
        Assert.IsTrue(bridge.Request(null, Persistent("first", false), session.Generation));
        Assert.AreEqual(true, bridge.LastResult!.ProfileSaved);
        Assert.IsNull(bridge.LastResult!.DataValue); Assert.IsFalse(profile.IsBusy);
        Assert.IsTrue(bridge.Request(null, Persistent("first"), session.Generation)); Assert.IsNull(bridge.LastResult);
        Assert.IsFalse(bridge.Request(null, Persistent("second"), session.Generation)); Assert.IsNull(profile.GetPersistent("second"));
        Assert.IsTrue(bridge.Request(null, Persistent("first", false), session.Generation)); Assert.AreEqual("value", bridge.LastResult!.DataValue);
        Assert.AreEqual(false, bridge.LastResult.ProfileSaved);
        store.Saved.SetException(new IOException("private/path")); await Drain(bridge, profile);
        Assert.IsFalse(bridge.LastResult!.Success); Assert.AreEqual("value", bridge.LastResult.DataValue);
        Assert.AreEqual(false, bridge.LastResult.ProfileSaved);
        store.Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(bridge.Request(null, Persistent("first"), session.Generation));
        store.Saved.SetResult(); await Drain(bridge, profile); Assert.IsTrue(bridge.LastResult!.Success);
        Assert.AreEqual(true, bridge.LastResult.ProfileSaved);
        var writes = store.WriteCount; Assert.IsTrue(bridge.Request(null, Persistent("first"), session.Generation));
        Assert.IsFalse(profile.IsBusy); Assert.AreEqual(writes, store.WriteCount); Assert.IsNull(bridge.LastResult!.Settings);
        bridge = new(session, profile, _ => { });
        Assert.IsTrue(bridge.Request(null, Set("master_volume", .4), session.Generation)); await Drain(bridge, profile);
        CollectionAssert.AreEqual(new[] { "first" }, store.Profile!.Data.Keys.Order(StringComparer.Ordinal).ToArray()); Assert.AreEqual(.4f, store.Profile.Settings.Master.Volume);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task PersistentCommitSurvivesOwnerRetirementButOldResultCannotReachNewHandler(bool load)
    {
        using var session = Session(); var store = new Store(); var profile = Settings(store);
        var stack = new ScreenStack(Guid.NewGuid()); var origin = stack.Open(Guid.NewGuid(), ScreenFixture.Create("Persistents").Definition());
        var bridge = new RuntimeSettingsServices(session, profile, _ => { }) { IsCurrentOrigin = value => stack.Instances.Contains(value) };
        var delivered = new List<ScreenInstance?>(); bridge.Completed += (who, _) => delivered.Add(who);
        var oldGeneration = session.Generation;
        Assert.IsTrue(bridge.Request(origin, Persistent("first"), oldGeneration));
        if (load) session.LoadSave(GameSave.Serialize(session.Player.Execution)); else stack.Close(origin);
        Assert.IsFalse(bridge.Request(origin, Persistent("stale"), load ? oldGeneration : session.Generation));
        var next = stack.Open(Guid.NewGuid(), ScreenFixture.Create("New achievements").Definition());
        Assert.IsTrue(bridge.Request(next, Persistent("second"), session.Generation));
        store.Saved.SetResult(); await Drain(bridge, profile);
        CollectionAssert.AreEqual(new[] { "first", "second" }, store.Profile!.Data.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.HasCount(1, delivered); Assert.AreSame(next, delivered[0]); Assert.IsNull(profile.GetPersistent("stale"));
    }

    [TestMethod]
    public void PersistentQuotaFailureIsReportedWithoutChangingGameplayOrPreferences()
    {
        using var session = Session(); var state = session.Player.Execution.State; var store = new Store();
        var profile = new PlayerSettingsSession(store, new(PlayerSettings.Defaults(new()), PlayerProfileStatus.Loaded, "")
        { Data = Enumerable.Range(0, PlayerProfileData.MaximumEntries).ToImmutableDictionary(i => "a" + i, _ => "value") });
        var bridge = new RuntimeSettingsServices(session, profile, _ => Assert.Fail());
        Assert.IsFalse(bridge.Request(null, Persistent("extra"), session.Generation));
        Assert.IsFalse(bridge.LastResult!.Success); Assert.IsNull(bridge.LastResult.DataValue);
        Assert.IsFalse(profile.IsBusy); Assert.AreSame(state, session.Player.Execution.State);
        Assert.IsTrue(bridge.Request(null, Persistent("a0"), session.Generation)); Assert.IsTrue(bridge.LastResult!.Success);
    }

    [TestMethod]
    public async Task SettingsApplyOnHostAndResultWaitsForDiskWithBusyAndCurrentSnapshot()
    {
        using var session = Session(); var store = new Store(); var settings = Settings(store);
        PlayerSettings? applied = null; var bridge = new RuntimeSettingsServices(session, settings, value => applied = value);
        var results = new List<GameServiceResult>(); bridge.Completed += (_, result) => results.Add(result);
        Assert.IsFalse(bridge.Poll());
        Assert.IsTrue(bridge.Request(null, new(Guid.NewGuid(), GameServiceAction.GetSettings), session.Generation));
        Assert.AreEqual(settings.Current, results.Single().Settings); results.Clear();
        Assert.IsTrue(bridge.Request(null, Set("voice_volume", .25), session.Generation));
        Assert.AreEqual(.25f, applied!.Voice.Volume); Assert.IsEmpty(results);
        Assert.IsFalse(bridge.Request(null, Set("fullscreen", flag: true), session.Generation));
        Assert.IsFalse(results.Single().Success); Assert.IsFalse(settings.Current.Fullscreen);
        store.Saved.SetResult(); await Drain(bridge, settings);
        Assert.HasCount(2, results); Assert.IsTrue(results[1].Success); Assert.AreEqual(.25f, results[1].Settings!.Voice.Volume);
        Assert.AreEqual(settings.Current, store.Written);
        Assert.Throws<ArgumentException>(() => bridge.Request(null, new(Guid.NewGuid(), GameServiceAction.ListSlots), session.Generation));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task AcceptedProfileWriteSurvivesClosedOriginOrGameplayResetButCannotCallOldHandler(bool resetSession)
    {
        using var session = Session(); var store = new Store(); var settings = Settings(store);
        var bridge = new RuntimeSettingsServices(session, settings, _ => { });
        var stack = new ScreenStack(Guid.NewGuid()); var origin = stack.Open(Guid.NewGuid(), ScreenFixture.Create("Settings").Definition());
        bridge.IsCurrentOrigin = value => stack.Instances.Any(i => ReferenceEquals(i, value));
        var results = 0; bridge.Completed += (_, _) => results++;
        Assert.IsTrue(bridge.Request(origin, Set("master_muted", flag: true), session.Generation));
        if (resetSession) session.LoadSave(GameSave.Serialize(session.Player.Execution)); else stack.Close(origin);
        Assert.IsFalse(bridge.Request(origin, Set("fullscreen", flag: true), 0));
        store.Saved.SetResult(); await Drain(bridge, settings);
        Assert.IsTrue(store.Written!.Master.Muted); Assert.AreEqual(0, results); Assert.IsTrue(settings.Current.Master.Muted);
        Assert.IsTrue(bridge.Request(null, new(Guid.NewGuid(), GameServiceAction.GetSettings), session.Generation)); Assert.AreEqual(1, results);
    }

    [TestMethod]
    public async Task WindowFailureKeepsOldSettingsAndDiskFailureKeepsNewSettingsAndGameplay()
    {
        using var session = Session(); var store = new Store(); var settings = Settings(store); var state = session.Player.Execution.State;
        var bridge = new RuntimeSettingsServices(session, settings, value => { if (value.Fullscreen) throw new InvalidOperationException("graphics backend"); });
        Assert.IsFalse(bridge.Request(null, Set("fullscreen", flag: true), session.Generation));
        Assert.IsFalse(settings.IsBusy); Assert.IsFalse(settings.Current.Fullscreen); Assert.IsFalse(bridge.LastResult!.Success);
        Assert.AreSame(state, session.Player.Execution.State);
        Assert.IsTrue(bridge.Request(null, Set("effect_volume", .2), session.Generation));
        store.Saved.SetException(new IOException("secret/profile/path")); await Drain(bridge, settings);
        Assert.IsFalse(bridge.LastResult!.Success); Assert.IsFalse(bridge.LastResult.Message.Contains("secret", StringComparison.Ordinal));
        Assert.AreEqual(.2f, settings.Current.Effect.Volume); Assert.AreSame(state, session.Player.Execution.State);
        store.Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(bridge.Request(null, Set("effect_volume", .2), session.Generation));
        store.Saved.SetResult(); await Drain(bridge, settings); Assert.IsTrue(bridge.LastResult!.Success);
    }

    [TestMethod]
    public async Task NewOriginCanWriteWhileOldCallbackIsRetiredAndNewestProfileIsFlushed()
    {
        using var session = Session(); var store = new Store(); var settings = Settings(store);
        var bridge = new RuntimeSettingsServices(session, settings, _ => { });
        var stack = new ScreenStack(Guid.NewGuid()); var origin = stack.Open(Guid.NewGuid(), ScreenFixture.Create("Settings").Definition());
        bridge.IsCurrentOrigin = value => stack.Instances.Contains(value);
        var delivered = new List<ScreenInstance?>(); bridge.Completed += (who, _) => delivered.Add(who);
        bridge.Request(origin, Set("sound_volume", .4), session.Generation); stack.Close(origin);
        var current = stack.Open(Guid.NewGuid(), ScreenFixture.Create("New settings").Definition());
        Assert.IsTrue(bridge.Request(current, Set("sound_volume", .6), session.Generation));
        store.Saved.SetResult(); await Drain(bridge, settings);
        Assert.AreEqual(.6f, store.Written!.Sound.Volume); Assert.HasCount(1, delivered); Assert.AreSame(current, delivered[0]);
    }

    private static async Task Drain(RuntimeSettingsServices bridge, PlayerSettingsSession settings)
    { for (var i = 0; i < 300 && settings.IsBusy; i++) { bridge.Poll(); await Task.Delay(5); } Assert.IsFalse(settings.IsBusy); }
}
