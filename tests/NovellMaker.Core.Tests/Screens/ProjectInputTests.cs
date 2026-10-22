using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ProjectInputTests
{
    [TestMethod]
    public void SupportedKeysAndDataOnlyActionsValidateWithoutImpliedBindings()
    {
        var scene = ScreenAsset.Create("Сцена").ToScene();
        var keys = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).Concat(Enumerable.Range(1, 12).Select(i => "F" + i))
            .Concat(new[] { "Escape", "Enter", "Space", "Tab", "Left", "Right", "Up", "Down" });
        var project = NovelProject.Create("Все клавиши") with { Scenes = [scene], InputBindings = keys.Select(k => new ProjectInputBinding(k, ScreenButtonAction.Open, scene.Id)).ToImmutableArray() };
        ProjectInput.Validate(project);
        foreach (var kind in new[] { ScreenButtonAction.Back, ScreenButtonAction.Continue, ScreenButtonAction.Save, ScreenButtonAction.Load })
            ProjectInput.Validate(project with { InputBindings = [new("A", kind, null)] });
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)]
    public void AudioCommandsRejectInvalidGroupsNonfiniteOrUnboundedAmounts(int kind)
    {
        var screen = ScreenFixture.Create("Сцена");
        var binding = new ScreenButtonBinding(screen.Nodes[2].Id, ScreenButtonAction.Volume, null) { Amount = -.1 };
        binding = kind switch
        {
            0 => binding with { Group = (NovellMaker.Core.Audio.AudioGroup)999 }, 1 => binding with { Amount = double.NaN },
            2 => binding with { Amount = double.PositiveInfinity }, 3 => binding with { Amount = 1.01 }, _ => binding with { Action = ScreenButtonAction.Mute }
        };
        var project = NovelProject.Create("Проект") with { Scenes = [(screen with { Buttons = [binding] }).ToScene()] };
        Assert.Throws<ArgumentException>(() => ScreenAsset.ValidateProject(project));
    }
    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)] [DataRow(7)] [DataRow(8)] [DataRow(9)] [DataRow(10)]
    public void InvalidProjectBindingsAreRejected(int kind)
    {
        var scene = ScreenAsset.Create("Сцена").ToScene();
        var binding = new ProjectInputBinding("F9", ScreenButtonAction.Open, scene.Id);
        var project = NovelProject.Create("Проект") with { Scenes = [scene], InputBindings = kind switch
        {
            0 => default, 1 => [null!], 2 => [binding with { Key = "" }], 3 => [binding with { Key = "F13" }],
            4 => [binding with { Key = "F01" }], 5 => [binding with { Action = ScreenButtonAction.Lua }],
            6 => [binding with { TargetSceneId = Guid.NewGuid() }], 7 => [binding, binding],
            8 => [binding with { Action = ScreenButtonAction.Save }], 9 => [binding with { Key = null! }],
            _ => Enumerable.Repeat(binding, 65).ToImmutableArray()
        } };
        Assert.Throws<ArgumentException>(() => ProjectInput.Validate(project));
    }

    [TestMethod]
    public async Task Version13MigratesOnlyExplicitPauseBindingAndRetiresBuiltinMixerWithoutWriting()
    {
        var scene = ScreenFixture.Create("Авторская сцена").ToScene();
        var project = NovelProject.Create("Старый") with { Scenes = [scene] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var doc = JsonNode.Parse(File.ReadAllText(path))!; doc["formatVersion"] = 13; doc.AsObject().Remove("startScreenSceneId"); doc.AsObject().Remove("inputBindings"); doc["pauseScreenId"] = scene.Id;
            ScreenFixture.RemoveGeometry(doc);
            foreach (var binding in doc["scenes"]![0]!["canvas"]!["buttons"]!.AsArray()) { binding!.AsObject().Remove("group"); binding.AsObject().Remove("amount"); }
            doc["scenes"]![0]!["canvas"]!["buttons"]![1]!["action"] = 5;
            File.WriteAllText(path, doc.ToJsonString()); var original = File.ReadAllBytes(path);
            var loaded = await store.LoadAsync(path); CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.HasCount(1, loaded.InputBindings); Assert.AreEqual(new ProjectInputBinding("P", ScreenButtonAction.Toggle, scene.Id), loaded.InputBindings[0]);
            Assert.AreEqual(ScreenButtonAction.None, loaded.Scenes[0].Canvas.Buttons[1].Action);
            Assert.IsFalse(loaded.InputBindings.Any(b => b.Key == "F9"));
            doc["scenes"]![0]!["canvas"]!["buttons"]![1]!["action"] = (int)ScreenButtonAction.Volume;
            File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            doc["scenes"]![0]!["canvas"]!["buttons"]![1]!["action"] = 5; doc["pauseScreenId"] = null;
            File.WriteAllText(path, doc.ToJsonString()); Assert.IsEmpty((await store.LoadAsync(path)).InputBindings);
            doc["scenes"]![0]!["canvas"]!["nodes"]![1]!["kind"] = 5;
            File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task CurrentFormatRoundTripsGenericAudioAndKeysAndRequiresTheirFields()
    {
        var scene = ScreenFixture.Create("Сцена").ToScene();
        scene = scene with { Canvas = scene.Canvas with { Buttons = [new(scene.Canvas.Nodes[2].Id, ScreenButtonAction.Volume, null) { Group = NovellMaker.Core.Audio.AudioGroup.Voice, Amount = -.2 }] } };
        var project = NovelProject.Create("Проект") with { Scenes = [scene], InputBindings = [new("F2", ScreenButtonAction.Save, null)] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project); var json = File.ReadAllText(path);
            Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
            foreach (var field in new[] { "group", "amount" })
            {
                var doc = JsonNode.Parse(json)!; doc["scenes"]![0]!["canvas"]!["buttons"]![0]!.AsObject().Remove(field);
                File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            }
            var extra = JsonNode.Parse(json)!; extra["pauseScreenId"] = null; File.WriteAllText(path, extra.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }
}
