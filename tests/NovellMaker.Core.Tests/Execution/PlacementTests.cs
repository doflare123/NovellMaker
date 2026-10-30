using System.Text.Json.Nodes;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class PlacementTests
{
    [TestMethod]
    public void Replay_RetainsHiddenPlacementResetsAndRestoresEarlierCommands()
    {
        var (project, a, b) = Fixture();
        var placement = new CharacterPlacement(0.25, 0.9, 0.8, 3);
        var lines = new[]
        {
            DialogueLine.Create("ru") with { SpeakerId = a, Placements = [new(b, placement)] },
            DialogueLine.Create("ru") with { SpeakerId = b },
            DialogueLine.Create("ru") with { HideCharacterId = b },
            DialogueLine.Create("ru") with { SpeakerId = b },
            DialogueLine.Create("ru") with { Placements = [new(b, null)] },
        };
        var scene = NovelScene.Create("Сцена") with { Lines = [.. lines] };
        project = project with { Scenes = [scene] };
        var player = new ScenePlayer(project, scene.Id, "ru", lines[3].Id);
        Assert.AreEqual(placement, player.Frame.Characters.Single(sprite => sprite.CharacterId == b).Placement);
        var snapshot = player.Frame;
        Assert.IsTrue(player.Next());
        Assert.IsNull(player.Frame.Characters.Single(sprite => sprite.CharacterId == b).Placement);
        Assert.AreEqual(placement, snapshot.Placements[b]);
        Assert.IsTrue(player.Previous());
        Assert.AreEqual(placement, player.Frame.Placements[b]);
        player.Seek(lines[2].Id);
        Assert.IsFalse(player.Frame.Characters.Any(sprite => sprite.CharacterId == b));
        Assert.AreEqual(placement, player.Frame.Placements[b]);
        player.Seek(lines[0].Id);
        Assert.AreEqual(placement, player.Frame.Placements[b]);
    }

    [TestMethod]
    public void Layers_AreStableAndCommandsCanMoveNonSpeaker()
    {
        var (project, a, b) = Fixture();
        var scene = NovelScene.Create("Сцена") with { Lines = [
            DialogueLine.Create("ru") with { SpeakerId = a },
            DialogueLine.Create("ru") with { SpeakerId = b, Placements = [new(a, new(0.5, 1, 1, 2))] },
            DialogueLine.Create("ru") with { Placements = [new(a, new(0.5, 1, 1, 0))] },
        ] };
        var player = new ScenePlayer(project with { Scenes = [scene] }, scene.Id, "ru");
        player.Next();
        CollectionAssert.AreEqual(new[] { b, a }, player.Frame.Characters.Select(sprite => sprite.CharacterId).ToArray());
        var automaticBob = player.Frame.Characters.Single(sprite => sprite.CharacterId == b);
        Assert.AreEqual(1, automaticBob.AutomaticSlot, "Layer sorting must not move automatic characters to other slots.");
        Assert.AreEqual(940, CharacterLayout.Calculate(600, 1000, automaticBob.AutomaticSlot, 2, automaticBob.Placement).X + 168, 0.001);
        player.Next();
        CollectionAssert.AreEqual(new[] { a, b }, player.Frame.Characters.Select(sprite => sprite.CharacterId).ToArray());
    }

    [TestMethod]
    public void Layout_UsesBottomCenterAndKeepsManualSizeIndependentOfCharacterCount()
    {
        var placement = new CharacterPlacement(0.25, 0.5, 0.5, 0);
        var bounds = CharacterLayout.Calculate(600, 1000, 0, 1, placement);
        Assert.AreEqual(320, bounds.X + bounds.Width / 2, 0.001);
        Assert.AreEqual(360, bounds.Y + bounds.Height, 0.001);
        Assert.AreEqual(280, bounds.Height, 0.001);
        Assert.AreEqual(bounds, CharacterLayout.Calculate(600, 1000, 4, 8, placement));
        var automatic = CharacterLayout.Calculate(600, 1000, 1, 2, null);
        Assert.AreEqual(940, automatic.X + automatic.Width / 2, 0.001);
        Assert.AreEqual(720, automatic.Y + automatic.Height, 0.001);
    }

    [TestMethod]
    public void MissingCharacter_IsDiagnosedWithoutShowingOrRemovingOtherCharacters()
    {
        var (project, a, _) = Fixture();
        var missing = Guid.NewGuid();
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru") with
        { SpeakerId = a, Placements = [new(missing, new(0.5, 1, 1, 0))] }] };
        var frame = new ScenePlayer(project with { Scenes = [scene] }, scene.Id, "ru").Frame;
        Assert.AreEqual(a, frame.Characters.Single().CharacterId);
        Assert.IsTrue(frame.Diagnostics.Any(message => message.Contains(missing.ToString())));
    }

    [TestMethod]
    [DataRow(double.NaN, 1d, 1d, 0)]
    [DataRow(0.5, double.PositiveInfinity, 1d, 0)]
    [DataRow(0.5, 1d, 0d, 0)]
    [DataRow(0.5, 1d, 6d, 0)]
    [DataRow(0.5, 1d, 1d, 1001)]
    public async Task Persistence_RejectsInvalidPlacement(double x, double y, double scale, int layer)
    {
        var (project, a, _) = Fixture();
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru") with
        { Placements = [new(a, new(x, y, scale, layer))] }] };
        var path = Path.Combine(Path.GetTempPath(), $"invalid-placement-{Guid.NewGuid():N}.novell");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new JsonProjectStore().SaveAsync(path, project with { Scenes = [scene] }));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task Version5_RoundTripsAndVersion4MigratesWithoutWriting()
    {
        var (project, a, _) = Fixture();
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru") with
        { SpeakerId = a, Placements = [new(a, new(0.2, 0.8, 1.2, -2))] }, DialogueLine.Create("ru") with { Placements = [new(a, null)] }] };
        project = project with { Scenes = [scene] };
        var path = Path.Combine(Path.GetTempPath(), $"placement-{Guid.NewGuid():N}.novell");
        try
        {
            var store = new JsonProjectStore();
            await store.SaveAsync(path, project);
            Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["formatVersion"] = 4; json.AsObject().Remove("releaseCheck"); json.AsObject().Remove("startScreenSceneId"); json.AsObject().Remove("inputBindings"); json.AsObject().Remove("screens"); json.AsObject().Remove("pauseScreenId"); json.AsObject().Remove("inputBindings"); if (json["scenes"] is JsonArray oldScenes) foreach (var oldScene in oldScenes) oldScene!.AsObject().Remove("canvas"); json.AsObject().Remove("audio"); json.AsObject().Remove("scripts");
            json.AsObject().Remove("explorerShowsFiles");
            foreach (var oldScene in json["scenes"]!.AsArray()) { oldScene!.AsObject().Remove("actions"); oldScene.AsObject().Remove("useActions"); oldScene.AsObject().Remove("scriptBlocks"); }
            foreach (var line in json["scenes"]![0]!["lines"]!.AsArray()) line!.AsObject().Remove("placements");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var before = await File.ReadAllBytesAsync(path);
            var migrated = await store.LoadAsync(path);
            Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion);
            Assert.IsTrue(migrated.Scenes[0].Lines.All(line => line.Placements.IsEmpty));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            // Legacy files must not accept fields from a newer schema.
            json["scenes"]![0]!["lines"]![0]!["placements"] = new JsonArray();
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("null")]
    [DataRow("duplicate")]
    [DataRow("incomplete")]
    [DataRow("unknown")]
    public async Task Version5_RejectsMalformedPlacementCommands(string mutation)
    {
        var (project, a, _) = Fixture();
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru") with { Placements = [new(a, new(0.5, 1, 1, 0))] }] };
        var path = Path.Combine(Path.GetTempPath(), $"placement-{Guid.NewGuid():N}.novell");
        try
        {
            var store = new JsonProjectStore();
            await store.SaveAsync(path, project with { Scenes = [scene] });
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var line = json["scenes"]![0]!["lines"]![0]!.AsObject();
            var commands = line["placements"]!.AsArray();
            switch (mutation)
            {
                case "missing": line.Remove("placements"); break;
                case "null": commands[0] = null; break;
                case "duplicate": commands.Add(commands[0]!.DeepClone()); break;
                case "incomplete": commands[0]!["placement"]!.AsObject().Remove("scale"); break;
                case "unknown": commands[0]!["placement"]!["rotation"] = 10; break;
            }
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static (NovelProject Project, Guid A, Guid B) Fixture()
    {
        var image = new ImageAsset(Guid.NewGuid(), "Спрайт", "Assets/Images/sprite.png", 600, 1000);
        NovelCharacter Character(string name)
        {
            var state = new CharacterState(Guid.NewGuid(), "normal", image.Id);
            return NovelCharacter.Create(name) with { States = [state], DefaultStateId = state.Id };
        }
        var a = Character("Алиса");
        var b = Character("Боб");
        return (NovelProject.Create("История") with { Images = [image], Characters = [a, b] }, a.Id, b.Id);
    }
}
