using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class GameSaveCaptureTests
{
    [TestMethod]
    public async Task CaptureIsImmutableAndCanBePreparedAfterPlayerAdvancesAndIsDisposed()
    {
        var scene = NovelScene.Create("Capture") with { Lines = [DialogueLine.Create("ru", "First"), DialogueLine.Create("ru", "Second")] };
        var project = NovelProject.Create("Capture") with { Scenes = [scene] };
        var session = new GameSession(project, scene.Id, "ru");
        var expected = GameSave.Serialize(session.Player.Execution);
        var state = session.Player.Execution.State;
        var captured = GameSlotStore.Capture(session);
        Assert.AreSame(state, captured.Save.State); Assert.AreSame(project, captured.Save.Project);
        session.Player.Next(); session.Dispose();
        var prepared = await Task.Run(() => GameSlotStore.Prepare(captured));
        Assert.AreEqual(expected, prepared.SaveJson); Assert.AreEqual("First", prepared.Metadata.Phrase);
        Assert.AreEqual(scene.Lines[0].Id, prepared.Metadata.LineId);
    }

    [TestMethod]
    public void CaptureDefersProjectValidationToPreparation()
    {
        var scene = NovelScene.Create("Capture") with { Lines = [DialogueLine.Create("ru", "First")] };
        var project = NovelProject.Create("Capture") with { Scenes = [scene] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru");
        var capture = GameSave.Capture(player);
        // Invalid immutable input is still rejected by the serializer, never published unchecked.
        Assert.Throws<ArgumentException>(() => GameSave.Serialize(capture with { Project = project with { Name = "Changed" } }));
    }
}
