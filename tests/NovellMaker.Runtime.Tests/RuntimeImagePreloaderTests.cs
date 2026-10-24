using NovellMaker.Core.Assets;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeImagePreloaderTests
{
    [TestMethod]
    public void ChoiceAndDeclarativeSceneTargetsYieldImagesWithoutExecutingLuaOrChangingState()
    {
        var a = NovelScene.Create("Current") with { Lines = [DialogueLine.Create("ru", "Choice")] };
        var image = new ImageAsset(Guid.NewGuid(), "Next", "Assets/Images/next.png", 1920, 1080);
        var b = NovelScene.Create("Future") with { BackgroundImageId = image.Id,
            ScriptBlocks = [new(null, ScriptPhase.SceneStart, new ScriptAction(Guid.NewGuid(), "error('must not run')", "future"))] };
        var project = NovelProject.Create("Prefetch") with { Scenes = [a, b], Images = [image] };
        using var player = new ScenePlayer(project, a.Id, "ru"); var original = player.Execution.State;
        var choice = new ChoiceAction(Guid.NewGuid(), [new(Guid.NewGuid(), "Go", b.Id, ScenarioProgram.LineEntryId(Guid.NewGuid()))]);
        var planner = new RuntimeImagePreloader(project);
        Assert.HasCount(1, planner.Hints(original with { Choice = choice }, player.Frame, []));
        Assert.AreEqual(image, planner.Hints(original with { Choice = choice }, player.Frame, [])[0]);
        Assert.AreSame(original, player.Execution.State); Assert.IsFalse(player.Execution.HasPendingLua);
        project = project with { Scenes = [a with { Canvas = a.Canvas with { Buttons = [new(Guid.NewGuid(), ScreenButtonAction.Scene, b.Id)] } }, b] };
        Assert.HasCount(1, new RuntimeImagePreloader(project).Hints(original, player.Frame, []));
    }

    [TestMethod]
    public void DynamicLuaTargetsAreNotEvaluated()
    {
        var image = new ImageAsset(Guid.NewGuid(), "Future", "Assets/Images/future.png", 1, 1);
        var a = NovelScene.Create("Current") with { Lines = [DialogueLine.Create("ru", "Current")], ScriptBlocks = [
            new(null, ScriptPhase.SceneEnd, new ScriptAction(Guid.NewGuid(), "game.jump('dynamic')", "dynamic"))] };
        var b = NovelScene.Create("Unreachable") with { BackgroundImageId = image.Id };
        var project = NovelProject.Create("Prefetch") with { Scenes = [a, b], Images = [image] };
        using var player = new ScenePlayer(project, a.Id, "ru");
        Assert.IsEmpty(new RuntimeImagePreloader(project).Hints(player.Execution.State, player.Frame, []));
    }
}
