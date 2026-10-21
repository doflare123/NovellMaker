using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class VisualScreenTests
{
    [TestMethod]
    public async Task Version11MigratesNamesAndSpritesWithoutWritingAndRejectsVersion12Fields()
    {
        var screen = ScreenFixture.Create("Старое меню"); var project = NovelProject.Create("Новелла") with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[screen]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray(), InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, screen.Id)] };
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(file, project);
            var doc = JsonNode.Parse(File.ReadAllText(file))!; doc["formatVersion"] = 11; doc.AsObject().Remove("inputBindings"); doc["pauseScreenId"] = screen.Id;
            var legacy = System.Text.Json.JsonSerializer.SerializeToNode(screen, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
            foreach (var binding in legacy["buttons"]!.AsArray()) { binding!.AsObject().Remove("group"); binding.AsObject().Remove("amount"); }
            legacy.AsObject().Remove("pauseOnOverlay"); legacy.AsObject().Remove("blockInputOnOverlay");
            doc["screens"] = new JsonArray(legacy); doc["scenes"] = new JsonArray();
            foreach (var node in doc["screens"]![0]!["nodes"]!.AsArray()) { node!.AsObject().Remove("imageId"); node.AsObject().Remove("name"); }
            File.WriteAllText(file, doc.ToJsonString()); var bytes = File.ReadAllBytes(file);
            var migrated = await store.LoadAsync(file); Assert.IsTrue(project.ContentEquals(migrated));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(file));
            doc["screens"]![0]!["nodes"]![1]!["imageId"] = null; File.WriteAllText(file, doc.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(file));
            doc["screens"]![0]!["nodes"]![1]!.AsObject().Remove("imageId"); doc["screens"]![0]!["nodes"]![1]!["kind"] = 4; File.WriteAllText(file, doc.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(file));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task SpriteAndNameRoundTripValidateAndChangeRevision()
    {
        var screen = ScreenFixture.Create("Меню"); var asset = new ImageAsset(Guid.NewGuid(), "Спрайт", "Assets/Images/sprite.png", 20, 20);
        var image = new ScreenNode(Guid.NewGuid(), screen.Nodes[0].Id, ScreenNodeKind.Image, new(0, 0, 100, 100)) { ImageId = asset.Id, Name = "Логотип" };
        screen = screen with { Nodes = screen.Nodes.Add(image) };
        var project = NovelProject.Create("Новелла") with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[screen]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray(), Images = [asset], InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, screen.Id)] };
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(file, project); Assert.IsTrue(project.ContentEquals(await store.LoadAsync(file)));
            Assert.Throws<ArgumentException>(() => ScreenAsset.ValidateProject(project with { Images = [] }));
            Assert.Throws<ArgumentException>(() => new ScreenDefinition(screen.Id, screen.Nodes.Replace(image, image with { ImageId = Guid.Empty })));
            Assert.Throws<ArgumentException>(() => new ScreenDefinition(screen.Id, screen.Nodes.Replace(image, image with { Name = new string('n', 129) })));
            Assert.IsFalse(screen.ContentEquals(screen with { Nodes = screen.Nodes.Replace(image, image with { ImageId = null }) }));
            Assert.IsFalse(screen.ContentEquals(screen with { Nodes = screen.Nodes.Replace(image, image with { Name = "Другое имя" }) }));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public void DesignerPickingUsesClippingSubtreeOrderAndCanSelectDisabledObjects()
    {
        var root = Guid.NewGuid(); var panel = Guid.NewGuid(); var image = Guid.NewGuid(); var button = Guid.NewGuid();
        var definition = new ScreenDefinition(Guid.NewGuid(), [
            new(root, null, ScreenNodeKind.Container, new(0, 0, 1280, 720)),
            new(panel, root, ScreenNodeKind.Panel, new(100, 100, 100, 100)),
            new(button, panel, ScreenNodeKind.Button, new(0, 0, 100, 100), Enabled: false),
            new(image, panel, ScreenNodeKind.Image, new(60, 60, 100, 100))]);
        Assert.AreEqual(new ScreenRect(160, 160, 100, 100), definition.AbsoluteBounds(image));
        Assert.AreEqual(button, definition.PickNode(120, 120)); Assert.IsNull(definition.HitTest(120, 120));
        Assert.AreEqual(image, definition.PickNode(180, 180)); Assert.AreEqual(root, definition.PickNode(230, 230));
        Assert.IsNull(definition.PickNode(1500, 1500));
    }
}
