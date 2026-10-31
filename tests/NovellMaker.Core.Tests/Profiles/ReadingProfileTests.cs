using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;

namespace NovellMaker.Core.Tests.Profiles;

[TestClass]
public sealed class ReadingProfileTests
{
    [TestMethod]
    public async Task OldProfilesDefaultReadingOptionsAndNewProfilePreservesReadMarksAcrossSettingsAndRecovery()
    {
        var folder = Path.Combine(TestPaths.TempDirectory, "reading-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(folder, "profile.json"); var id = Guid.NewGuid(); var defaults = PlayerSettings.Defaults(new());
            var store = new PlayerProfileStore(id, file);
            await store.SaveAsync(defaults);
            var legacy = JsonNode.Parse(File.ReadAllText(file))!; legacy["Version"] = 3; legacy.AsObject().Remove("ReadLines");
            foreach (var key in new[] { "TextSpeed", "AutoDelay", "AutoWaitVoice" }) legacy["Settings"]!.AsObject().Remove(key);
            var original = legacy.ToJsonString(); File.WriteAllText(file, original);
            var read = await store.ReadAsync(defaults); Assert.AreEqual(PlayerProfileStatus.Loaded, read.Status);
            Assert.AreEqual(40d, read.Settings.TextSpeed); Assert.AreEqual(2d, read.Settings.AutoDelay); Assert.IsTrue(read.Settings.AutoWaitVoice);
            Assert.AreEqual(original, File.ReadAllText(file));
            var session = new PlayerSettingsSession(store, read);
            var keyRead = ReadingPlayback.ReadKey(Guid.NewGuid(), Guid.NewGuid(), "ru", "Текст");
            session.MarkRead(keyRead); session.Set(session.Current with { TextSpeed = 0, AutoDelay = .5, AutoWaitVoice = false }); await session.FlushAsync();
            read = await new PlayerProfileStore(id, file).ReadAsync(defaults);
            CollectionAssert.AreEqual(new[] { keyRead }, read.ReadLines.ToArray()); Assert.AreEqual(0d, read.Settings.TextSpeed);
            await store.SaveAsync(read.Settings with { Fullscreen = true });
            File.WriteAllText(file, "broken"); read = await store.ReadAsync(defaults);
            Assert.AreEqual(PlayerProfileStatus.Recovered, read.Status); CollectionAssert.AreEqual(new[] { keyRead }, read.ReadLines.ToArray());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void ReadingPreferencesRejectWrongTypesAndNonFiniteOrOutOfRangeNumbers()
    {
        var defaults = PlayerSettings.Defaults(new());
        foreach (var change in new[] { new PlayerSettingChange("text_speed", -1, null), new("text_speed", 201, null), new("text_speed", double.NaN, null),
            new("text_speed", null, true), new("auto_delay", 31, null), new("auto_delay", double.PositiveInfinity, null), new("auto_wait_voice", 1, null) })
            Assert.Throws<ArgumentException>(() => defaults.Apply(change));
        Assert.AreEqual(0d, defaults.Apply(new("text_speed", 0, null)).TextSpeed);
        Assert.AreEqual(30d, defaults.Apply(new("auto_delay", 30, null)).AutoDelay);
        Assert.IsFalse(defaults.Apply(new("auto_wait_voice", null, false)).AutoWaitVoice);
        Assert.Throws<ArgumentException>(() => new PlayerProfileData(defaults, ImmutableDictionary<string, string>.Empty) { ReadLines = ["bad"] }.Validate());
    }
}
