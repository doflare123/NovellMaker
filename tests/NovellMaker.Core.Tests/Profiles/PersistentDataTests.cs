using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Profiles;

[TestClass]
public sealed class PersistentDataTests
{
    private static PlayerSettings Defaults => PlayerSettings.Defaults(new());
    private static ImmutableDictionary<string,string> Data(params string[] keys) => keys.ToImmutableDictionary(k=>k,_=>"true",StringComparer.Ordinal);
    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"nm-data-"+Guid.NewGuid().ToString("N"));
        internal string File => System.IO.Path.Combine(Path,"profile.json");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path,true); }
    }
    [TestMethod]
    [DataRow(1,false)] [DataRow(1,true)] [DataRow(2,false)] [DataRow(2,true)]
    public async Task LegacyMigrationKeepsDataAndPreferencesWithoutWritingUntilExplicitChange(int version,bool backup)
    {
        using var folder=new Folder(); var store=new PlayerProfileStore(Guid.NewGuid(),folder.File);
        var settings=Defaults with {Master=new(.25f,true),Fullscreen=true}; await store.SaveAsync(settings);
        var json=JsonNode.Parse(File.ReadAllText(folder.File))!; json["Version"]=version; json.AsObject().Remove("Data");
        if(version==2) json["Achievements"]=new JsonArray("first_steps",new string('z',64));
        var text=json.ToJsonString(); File.WriteAllText(folder.File,text);
        if(backup) { File.Move(folder.File,folder.File+".bak"); File.WriteAllText(folder.File,"broken"); }
        var read=await store.ReadAsync(Defaults); Assert.AreEqual(settings,read.Settings);
        Assert.AreEqual(backup?PlayerProfileStatus.Recovered:PlayerProfileStatus.Loaded,read.Status);
        Assert.AreEqual(version==2?"true":null,read.Data.GetValueOrDefault("first_steps"));
        Assert.AreEqual(text,File.ReadAllText(backup?folder.File+".bak":folder.File));
        var session=new PlayerSettingsSession(store,read); session.SetPersistent(new("custom_note","anything")); await session.FlushAsync();
        var loaded=await store.ReadAsync(Defaults); Assert.AreEqual(settings,loaded.Settings); Assert.AreEqual("anything",loaded.Data["custom_note"]);
        Assert.AreEqual(version==2?"true":null,loaded.Data.GetValueOrDefault("first_steps"));
        Assert.AreEqual(3,JsonNode.Parse(File.ReadAllText(folder.File))!["Version"]!.GetValue<int>());
    }
    [TestMethod]
    [DataRow("missing")] [DataRow("null")] [DataRow("duplicate")] [DataRow("invalidKey")] [DataRow("nullValue")]
    [DataRow("longValue")] [DataRow("tooMany")] [DataRow("totalSize")] [DataRow("wrongType")]
    [DataRow("v1Extra")] [DataRow("v2Missing")] [DataRow("v2Null")] [DataRow("v2Duplicate")] [DataRow("v2Invalid")] [DataRow("v2Large")] [DataRow("legacySize")]
    public async Task AllSchemasAreStrictAndReadNeverRepairsFiles(string issue)
    {
        using var folder=new Folder(); var store=new PlayerProfileStore(Guid.NewGuid(),folder.File);
        await store.SaveAsync(new PlayerProfileData(Defaults,Data("custom")));
        var json=JsonNode.Parse(File.ReadAllText(folder.File))!;
        switch(issue)
        {
            case "missing": json.AsObject().Remove("Data"); break;
            case "null": json["Data"]=null; break;
            case "invalidKey": json["Data"]=new JsonObject{["../bad"]="x"}; break;
            case "nullValue": json["Data"]!["custom"]=null; break;
            case "longValue": json["Data"]!["custom"]=new string('x',1025); break;
            case "tooMany": json["Data"]=JsonSerializer.SerializeToNode(Enumerable.Range(0,257).ToDictionary(i=>"a"+i,_=>"")); break;
            case "totalSize": json["Data"]=JsonSerializer.SerializeToNode(Enumerable.Range(0,17).ToDictionary(i=>"a"+i,_=>new string('x',1024))); break;
            case "wrongType": json["Data"]=true; break;
            case "v1Extra": json["Version"]=1; break;
            case "v2Missing": json["Version"]=2; json.AsObject().Remove("Data"); break;
            case "v2Null": case "v2Duplicate": case "v2Invalid": case "v2Large":
                json["Version"]=2; json.AsObject().Remove("Data");
                json["Achievements"]=issue switch {"v2Null"=>null,"v2Duplicate"=>new JsonArray("a","a"),"v2Invalid"=>new JsonArray("A"),
                    _=>new JsonArray(Enumerable.Range(0,129).Select(i=>JsonValue.Create("a"+i) as JsonNode).ToArray())}; break;
            case "legacySize": json["Version"]=1; json.AsObject().Remove("Data"); break;
        }
        var text=json.ToJsonString();
        if(issue=="duplicate") text=text.Replace("\"custom\":\"true\"","\"custom\":\"true\",\"custom\":\"other\"",StringComparison.Ordinal);
        if(issue=="legacySize") text+=new string(' ',16384);
        File.WriteAllText(folder.File,text); var read=await store.ReadAsync(Defaults);
        Assert.AreEqual(PlayerProfileStatus.Corrupt,read.Status); Assert.IsEmpty(read.Data); Assert.AreEqual(text,File.ReadAllText(folder.File));
    }
    [TestMethod]
    [DataRow(false,false)] [DataRow(false,true)] [DataRow(true,false)] [DataRow(true,true)]
    public async Task SupportedDuplicateCannotHideFutureVersionInPrimaryOrBackup(bool backup,bool futureFirst)
    {
        using var folder=new Folder(); var store=new PlayerProfileStore(Guid.NewGuid(),folder.File); await store.SaveAsync(Defaults); await store.SaveAsync(Defaults);
        var file=backup?folder.File+".bak":folder.File;
        var text=JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Replace("\"Version\":3",futureFirst?"\"Version\":4,\"Version\":3":"\"Version\":3,\"Version\":4",StringComparison.Ordinal);
        File.WriteAllText(file,text); var primary=File.ReadAllBytes(folder.File); var old=File.ReadAllBytes(folder.File+".bak");
        if(!backup) Assert.AreEqual(PlayerProfileStatus.UnsupportedVersion,(await store.ReadAsync(Defaults)).Status);
        await Assert.ThrowsAsync<IOException>(()=>store.SaveAsync(new PlayerProfileData(Defaults,Data("custom"))));
        CollectionAssert.AreEqual(primary,File.ReadAllBytes(folder.File)); CollectionAssert.AreEqual(old,File.ReadAllBytes(folder.File+".bak")); Assert.IsEmpty(Directory.GetFiles(folder.Path,"*.tmp"));
    }
    [TestMethod]
    [DataRow("future")] [DataRow("foreign")] [DataRow("corrupt")]
    public async Task DeletingAnUnknownKeyInAnUnreadableProfileCannotPretendTheDefaultsWerePersisted(string issue)
    {
        using var folder = new Folder(); var store = new PlayerProfileStore(Guid.NewGuid(), folder.File);
        await store.SaveAsync(new PlayerProfileData(Defaults, Data("note")));
        var document = JsonNode.Parse(File.ReadAllText(folder.File))!;
        if (issue == "future") document["Version"] = 4;
        if (issue == "foreign") document["ProjectId"] = Guid.NewGuid();
        var text = issue == "corrupt" ? "broken" : document.ToJsonString(); File.WriteAllText(folder.File, text);
        var session = new PlayerSettingsSession(store, await store.ReadAsync(Defaults));
        Assert.IsEmpty(session.Data); session.SetPersistent(new("note", null));
        Assert.IsTrue(session.IsBusy); await session.FlushAsync();
        Assert.AreEqual(issue == "corrupt", session.LastWrite!.Success);
        if (issue == "corrupt") Assert.IsEmpty((await store.ReadAsync(Defaults)).Data);
        else Assert.AreEqual(text, File.ReadAllText(folder.File));
    }

    [TestMethod]
    public async Task MaximumEscapedDataFitsFileAndSettingsOnlyWriteAndRecoveryPreserveIt()
    {
        using var folder=new Folder(); var store=new PlayerProfileStore(Guid.NewGuid(),folder.File);
        var data=Enumerable.Range(0,16).ToImmutableDictionary(i=>("a"+i).PadRight(64,'z'),_=>new string('\0',960));
        await store.SaveAsync(new PlayerProfileData(Defaults,data)); Assert.IsLessThanOrEqualTo(PlayerProfileStore.MaxFileBytes,new FileInfo(folder.File).Length);
        await store.SaveAsync(Defaults with {Fullscreen=true}); File.WriteAllText(folder.File,"broken");
        var recovered=await store.ReadAsync(Defaults); Assert.AreEqual(PlayerProfileStatus.Recovered,recovered.Status); CollectionAssert.AreEquivalent(data.ToArray(),recovered.Data.ToArray());
        await store.SaveAsync(Defaults with {Voice=new(.2f,true)}); var loaded=await store.ReadAsync(Defaults);
        CollectionAssert.AreEquivalent(data.ToArray(),loaded.Data.ToArray()); Assert.IsTrue(loaded.Settings.Voice.Muted);
        Assert.AreEqual(PlayerProfileStatus.ForeignProject,(await new PlayerProfileStore(Guid.NewGuid(),folder.File).ReadAsync(Defaults)).Status);
    }
    [TestMethod]
    [DataRow(null)] [DataRow("")] [DataRow("A")] [DataRow("1a")] [DataRow("a b")] [DataRow("а")] [DataRow("../a")] [DataRow("a/b")] [DataRow("a\\b")] [DataRow("a\n")]
    public void InvalidKeysCannotChangeProfile(string? key)
    {
        var session=new PlayerSettingsSession(new Store(),new(Defaults,PlayerProfileStatus.Missing,"")); Assert.IsFalse(AuthorKey.IsValid(key));
        Assert.Throws<ArgumentException>(()=>session.SetPersistent(new(key!,"x"))); Assert.Throws<ArgumentException>(()=>session.GetPersistent(key!));
        Assert.Throws<ArgumentException>(()=>new GameServiceCommand(Guid.NewGuid(),GameServiceAction.GetPersistent){Key=key}.Validate()); Assert.IsEmpty(session.Data); Assert.IsFalse(session.IsBusy);
    }
    [TestMethod]
    public async Task QuotasAreAtomicAndValuesCanBeReplacedRemovedOrEmpty()
    {
        foreach(var key in new[]{"a","a-_.09",new string('z',64)}) new PlayerProfileData(Defaults,Data(key)).Validate();
        Assert.IsFalse(AuthorKey.IsValid(new string('z',65)));
        foreach(var invalid in new[]{new PlayerProfileData(null!,Data()),new(Defaults,null!),new(Defaults,Data().Add("a",null!)),new(Defaults,Data().Add("A","x")),new(Defaults,Data().Add("a",new string('x',1025)))}) Assert.Throws<ArgumentException>(invalid.Validate);
        using var folder=new Folder(); var store=new PlayerProfileStore(Guid.NewGuid(),folder.File);
        var initial=Enumerable.Range(0,256).ToImmutableDictionary(i=>"a"+i,_=>""); var session=new PlayerSettingsSession(store,new(Defaults,PlayerProfileStatus.Loaded,""){Data=initial});
        Assert.Throws<ArgumentException>(()=>session.SetPersistent(new("extra","value"))); Assert.AreSame(initial,session.Data);
        Assert.Throws<ArgumentException>(()=>session.SetPersistent(new("a0",new string('x',1025)))); Assert.AreSame(initial,session.Data);
        session.SetPersistent(new("a0",null)); session.SetPersistent(new("new_key","")); Assert.AreEqual("",session.GetPersistent("new_key")); Assert.IsNull(session.GetPersistent("a0"));
        session.SetPersistent(new("a1","updated")); await session.FlushAsync(); Assert.AreEqual("updated",(await store.ReadAsync(Defaults)).Data["a1"]);
    }
    private sealed class Store : IPlayerProfileStore
    {
        internal readonly List<PlayerProfileData> Writes=[]; internal readonly Queue<TaskCompletionSource> Gates=new();
        public Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults,CancellationToken token=default)=>Task.FromResult(new PlayerProfileRead(defaults,PlayerProfileStatus.Missing,""));
        public Task SaveAsync(PlayerProfileData profile,CancellationToken token=default){lock(Writes){Writes.Add(profile);return Gates.Dequeue().Task;}}
        internal async Task WaitWrites(int count){for(var i=0;i<300&&Writes.Count<count;i++)await Task.Delay(5);Assert.HasCount(count,Writes);}
    }
    [TestMethod]
    public async Task CoalescingKeepsAllDataAndSettingsAndSameValueRetriesFailedWrite()
    {
        var store=new Store(); var first=new TaskCompletionSource();var second=new TaskCompletionSource();var retry=new TaskCompletionSource(); store.Gates.Enqueue(first);store.Gates.Enqueue(second);store.Gates.Enqueue(retry);
        var session=new PlayerSettingsSession(store,new(Defaults,PlayerProfileStatus.Missing,""));session.SetPersistent(new("first",null));Assert.IsFalse(session.IsBusy);
        var revision=session.SetPersistent(new("first","one"));await store.WaitWrites(1);Assert.AreEqual(revision,session.SetPersistent(new("first","one")));
        session.Set(Defaults with {Sound=new(.3f,true)});session.SetPersistent(new("second","two"));session.SetPersistent(new("first","changed"));
        first.SetResult();for(var i=0;i<300&&!session.Poll();i++)await Task.Delay(5);await store.WaitWrites(2);
        Assert.AreEqual("changed",store.Writes[1].Data["first"]);Assert.AreEqual("two",store.Writes[1].Data["second"]);Assert.AreEqual(.3f,store.Writes[1].Settings.Sound.Volume);
        second.SetException(new IOException("private/path"));await session.FlushAsync();Assert.IsFalse(session.LastWrite!.Success);Assert.AreEqual("two",session.GetPersistent("second"));
        session.SetPersistent(new("second","two"));await store.WaitWrites(3);retry.SetResult();await session.FlushAsync();Assert.IsTrue(session.LastWrite!.Success);
        session.SetPersistent(new("second","two"));Assert.IsFalse(session.IsBusy);Assert.HasCount(3,store.Writes);
    }
    [TestMethod]
    public async Task LockedFileKeepsDataUntilRetryAndOldSlotNewGameAndMenuNeverResetIt()
    {
        using var folder=new Folder();var story=NovelScene.Create("Story") with {Lines=[DialogueLine.Create("ru","one"),DialogueLine.Create("ru","two")]};var menu=NovelScene.Create("Menu");
        var project=NovelProject.Create("Data") with {Scenes=[story,menu],StartScreenSceneId=menu.Id};using var game=new GameSession(project,story.Id,"ru");game.NewGame(game.Generation);
        var save=GameSave.Serialize(game.Player.Execution);var store=new PlayerProfileStore(project.Id,folder.File);await store.SaveAsync(Defaults);var before=File.ReadAllBytes(folder.File);
        var profile=new PlayerSettingsSession(store,await store.ReadAsync(Defaults));using(var locked=new FileStream(folder.File,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {profile.SetPersistent(new("note","author value"));await profile.FlushAsync();Assert.IsFalse(profile.LastWrite!.Success);}
        CollectionAssert.AreEqual(before,File.ReadAllBytes(folder.File));Assert.IsEmpty(Directory.GetFiles(folder.Path,"*.tmp"));
        game.Player.Next();game.LoadSave(save);game.ReturnToMenu(game.Generation);game.NewGame(game.Generation);game.End(game.Generation);Assert.AreEqual("author value",profile.GetPersistent("note"));
        profile.SetPersistent(new("note","author value"));await profile.FlushAsync();Assert.AreEqual("author value",(await store.ReadAsync(Defaults)).Data["note"]);Assert.IsFalse(save.Contains("author value",StringComparison.Ordinal));
        profile.SetPersistent(new("note",null));await profile.FlushAsync();Assert.IsEmpty((await store.ReadAsync(Defaults)).Data);
    }
}
