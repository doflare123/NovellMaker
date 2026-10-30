using System.Text.Json.Nodes;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Tests.Projects;

[TestClass]
public sealed class ReleaseCheckProjectTests
{
    [TestMethod]
    [DataRow("ghp_")] [DataRow("github_pat_")] [DataRow("gho_")] [DataRow("ghu_")] [DataRow("ghs_")] [DataRow("ghr_")]
    [DataRow("GHP_")]
    public async Task TokenPrefixesAreRejectedBeforePersistenceWithoutEchoingTheValue(string prefix)
    {
        var value = prefix + new string('A', 36);
        var root = Directory.CreateTempSubdirectory("NovellMaker.TokenGuard.");
        try
        {
            var path = Path.Combine(root.FullName, "game.novell"); var store = new JsonProjectStore();
            var project = NovelProject.Create("Проверка"); await store.SaveAsync(path, project); var original = await File.ReadAllBytesAsync(path);
            var error = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(path, project with
            { ReleaseCheck = new("github_releases_v1", "author", value, "1.0.0") }));
            Assert.IsFalse(error.ToString().Contains(value, StringComparison.Ordinal));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["releaseCheck"] = new JsonObject { ["provider"] = "github_releases_v1", ["owner"] = "author", ["repository"] = value, ["currentVersion"] = "1.0.0" };
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var invalid = await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            Assert.IsFalse(invalid.ToString().Contains(value, StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
    }

    private static readonly ReleaseCheckSettings Settings = new(ReleaseCheckConfiguration.Provider, "author", "novel", "1.2.3");

    [TestMethod]
    public async Task DeclarationRoundTripsAndChangesContentAndSaveRevision()
    {
        var root = Directory.CreateTempSubdirectory("NovellMaker.ReleaseProject.");
        try
        {
            var path = Path.Combine(root.FullName, "game.novell"); var store = new JsonProjectStore();
            var original = NovelProject.Create("Версия"); var configured = original with { ReleaseCheck = Settings };
            await store.SaveAsync(path, configured); var loaded = await store.LoadAsync(path);
            Assert.IsTrue(configured.ContentEquals(loaded)); Assert.IsFalse(original.ContentEquals(loaded));
            Assert.AreNotEqual(ScenarioProgram.Fingerprint(original), ScenarioProgram.Fingerprint(loaded));
            Assert.AreNotEqual(ScenarioProgram.Fingerprint(loaded), ScenarioProgram.Fingerprint(loaded with { ReleaseCheck = Settings with { CurrentVersion = "1.2.4" } }));
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.AreEqual(20, json["formatVersion"]!.GetValue<int>());
            Assert.AreEqual(4, json["releaseCheck"]!.AsObject().Count);
            Assert.AreEqual("github_releases_v1", json["releaseCheck"]!["provider"]!.GetValue<string>());
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)]
    [DataRow(7)] [DataRow(8)] [DataRow(9)] [DataRow(10)] [DataRow(11)] [DataRow(12)]
    [DataRow(13)] [DataRow(14)] [DataRow(15)] [DataRow(16)] [DataRow(17)] [DataRow(18)] [DataRow(19)]
    public async Task OldFormatsMigrateDisabledWithoutWritingAndRejectNewDeclaration(int version)
    {
        var root = Directory.CreateTempSubdirectory("NovellMaker.ReleaseMigration.");
        try
        {
            var path = Path.Combine(root.FullName, "game.novell"); var store = new JsonProjectStore();
            await store.SaveAsync(path, NovelProject.Create("Старый проект"));
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["formatVersion"] = version; json.Remove("releaseCheck");
            if (version < 17) json.Remove("startScreenSceneId");
            if (version < 14) json.Remove("inputBindings");
            if (version is >= 11 and <= 13) json["pauseScreenId"] = null;
            if (version is 11 or 12) json["screens"] = new JsonArray();
            if (version < 10) json.Remove("scripts");
            if (version < 9) json.Remove("audio");
            if (version < 6) json.Remove("explorerShowsFiles");
            if (version < 4) json.Remove("characters");
            if (version < 3) json.Remove("images");
            if (version == 1) { json.Remove("scenes"); json.Remove("defaultLanguage"); json.Remove("dialogueUi"); }
            await File.WriteAllTextAsync(path, json.ToJsonString()); var bytes = await File.ReadAllBytesAsync(path);
            var loaded = await store.LoadAsync(path);
            Assert.IsNull(loaded.ReleaseCheck); Assert.AreEqual(20, loaded.FormatVersion);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            json["releaseCheck"] = JsonNode.Parse("""{"provider":"github_releases_v1","owner":"author","repository":"novel","currentVersion":"1.0.0"}""");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    [DataRow("missing")] [DataRow("unknown-provider")] [DataRow("null-owner")] [DataRow("wrong-type")]
    [DataRow("token")] [DataRow("headers")] [DataRow("url")] [DataRow("secret-key")]
    [DataRow("duplicate")] [DataRow("bad-version")] [DataRow("unknown-field")]
    public async Task InvalidDeclarationsNeverEchoSensitiveInputInExceptionChain(string mutation)
    {
        const string secret = "PRIVATE-CREDENTIAL-DO-NOT-LOG";
        var root = Directory.CreateTempSubdirectory("NovellMaker.ReleaseSecret.");
        try
        {
            var path = Path.Combine(root.FullName, "game.novell"); var store = new JsonProjectStore();
            await store.SaveAsync(path, NovelProject.Create("Проверка") with { ReleaseCheck = Settings });
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); var declaration = json["releaseCheck"]!.AsObject();
            switch (mutation)
            {
                case "missing": json.Remove("releaseCheck"); break;
                case "unknown-provider": declaration["provider"] = secret; break;
                case "null-owner": declaration["owner"] = null; break;
                case "wrong-type": declaration["repository"] = new JsonObject { [secret] = secret }; break;
                case "bad-version": declaration["currentVersion"] = secret; break;
                case "secret-key": declaration[secret] = secret; break;
                case "unknown-field": declaration["future"] = secret; break;
                case "duplicate": break;
                default: declaration[mutation] = secret; break;
            }
            var text = json.ToJsonString();
            if (mutation == "duplicate") text = text.Replace("\"owner\":\"author\"", "\"owner\":\"author\",\"owner\":\"" + secret + "\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, text);
            var error = await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            Assert.IsFalse(error.ToString().Contains(secret, StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public async Task InvalidDraftCannotOverwriteProjectAndNullDisablesExplicitly()
    {
        var root = Directory.CreateTempSubdirectory("NovellMaker.ReleaseDraft.");
        try
        {
            var path = Path.Combine(root.FullName, "game.novell"); var store = new JsonProjectStore();
            var project = NovelProject.Create("Проект"); await store.SaveAsync(path, project); var bytes = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(path, project with { ReleaseCheck = Settings with { Owner = "https://secret.invalid/token" } }));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            Assert.IsNull((await store.LoadAsync(path)).ReleaseCheck);
            Assert.AreEqual(nameof(ReleaseCheckSettings), (Settings with { Owner = "secret" }).ToString());
        }
        finally { root.Delete(true); }
    }
}
