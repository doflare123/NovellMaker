using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class ScenePlayerTests
{
    [TestMethod]
    public void Playback_AppliesDefaultExplicitRetainedHideAndNarratorBehavior()
    {
        var fixture = CreateFixture();
        var player = new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru");

        AssertFrameLine(player.Frame, fixture.Lines[0], 0, "Алиса", true, true);
        AssertVisible(player.Frame, (fixture.Alice.Id, fixture.AliceCalm.Id));
        Assert.AreEqual(fixture.Background.Id, player.Frame.Background?.Id);

        Assert.IsTrue(player.Next());
        AssertFrameLine(player.Frame, fixture.Lines[1], 1, "Алиса", false, true);
        AssertVisible(player.Frame, (fixture.Alice.Id, fixture.AliceSad.Id));

        Assert.IsTrue(player.Next());
        AssertFrameLine(player.Frame, fixture.Lines[2], 2, "Боб", false, true);
        AssertVisible(
            player.Frame,
            (fixture.Alice.Id, fixture.AliceSad.Id),
            (fixture.Bob.Id, fixture.BobDefault.Id));

        Assert.IsTrue(player.Next());
        AssertFrameLine(player.Frame, fixture.Lines[3], 3, "Алиса", false, true);
        AssertVisible(
            player.Frame,
            (fixture.Alice.Id, fixture.AliceSad.Id),
            (fixture.Bob.Id, fixture.BobDefault.Id));

        Assert.IsTrue(player.Next());
        AssertFrameLine(player.Frame, fixture.Lines[4], 4, string.Empty, false, true);
        AssertVisible(
            player.Frame,
            (fixture.Alice.Id, fixture.AliceSad.Id),
            (fixture.Bob.Id, fixture.BobDefault.Id));

        Assert.IsTrue(player.Next());
        AssertFrameLine(player.Frame, fixture.Lines[5], 5, string.Empty, false, true);
        AssertVisible(player.Frame, (fixture.Bob.Id, fixture.BobDefault.Id));

        Assert.IsTrue(player.Next());
        AssertFrameLine(player.Frame, fixture.Lines[6], 6, "Алиса", false, false);
        AssertVisible(
            player.Frame,
            (fixture.Bob.Id, fixture.BobDefault.Id),
            (fixture.Alice.Id, fixture.AliceSad.Id));
        Assert.HasCount(0, player.Frame.Diagnostics);
    }

    [TestMethod]
    public void Constructor_WithSelectedLine_ReplaysEarlierCues()
    {
        var fixture = CreateFixture();

        var player = new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru", fixture.Lines[6].Id);

        AssertFrameLine(player.Frame, fixture.Lines[6], 6, "Алиса", false, false);
        AssertVisible(
            player.Frame,
            (fixture.Bob.Id, fixture.BobDefault.Id),
            (fixture.Alice.Id, fixture.AliceSad.Id));
    }

    [TestMethod]
    public void Previous_ReconstructsExactEarlierFrameAndRememberedState()
    {
        var fixture = CreateFixture();
        var player = new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru");
        var frames = new List<SceneFrame> { player.Frame };
        while (player.Next())
        {
            frames.Add(player.Frame);
        }

        for (var expectedIndex = frames.Count - 2; expectedIndex >= 0; expectedIndex--)
        {
            Assert.IsTrue(player.Previous());
            AssertFrameEquals(frames[expectedIndex], player.Frame);
        }
    }

    [TestMethod]
    public void NavigationBoundariesAndInvalidSeek_DoNotMutateCurrentFrame()
    {
        var fixture = CreateFixture();
        var player = new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru");
        var first = player.Frame;

        Assert.IsFalse(player.Previous());
        Assert.AreSame(first, player.Frame);
        Assert.IsFalse(player.Seek(Guid.NewGuid()));
        Assert.AreSame(first, player.Frame);

        while (player.Next())
        {
        }

        var last = player.Frame;
        Assert.IsFalse(player.Next());
        Assert.AreSame(last, player.Frame);
    }

    [TestMethod]
    public void FramesAreSnapshotsAndPlaybackDoesNotModifyProjectModel()
    {
        var fixture = CreateFixture();
        var originalLines = fixture.Scene.Lines;
        var originalAliceStates = fixture.Alice.States;
        var player = new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru");
        var firstFrame = player.Frame;

        Assert.IsTrue(player.Next());
        Assert.AreEqual(fixture.AliceCalm.Id, firstFrame.Characters[0].StateId);
        Assert.AreEqual(fixture.AliceSad.Id, player.Frame.Characters[0].StateId);
        Assert.AreSequenceEqual(originalLines, fixture.Project.Scenes[0].Lines);
        Assert.AreSequenceEqual(originalAliceStates, fixture.Project.Characters[0].States);
        Assert.AreEqual(fixture.AliceCalm.Id, fixture.Project.Characters[0].DefaultStateId);
        Assert.IsNull(fixture.Project.Scenes[0].Lines[0].CharacterStateId);
    }

    [TestMethod]
    public void MissingRequestedTranslation_FallsBackToProjectLanguageWithDiagnostic()
    {
        var fixture = CreateFixture();

        var player = new ScenePlayer(fixture.Project, fixture.Scene.Id, "en");

        Assert.AreEqual(fixture.Lines[0].Texts["ru"], player.Frame.Text);
        Assert.IsTrue(player.Frame.Diagnostics.Any(message =>
            message.Contains("нет перевода «en»", StringComparison.Ordinal) &&
            message.Contains("«ru»", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EmptyScene_HasStableEmptyFrame()
    {
        var scene = NovelScene.Create("Пустая");
        var project = NovelProject.Create("Проект") with { Scenes = [scene] };

        var player = new ScenePlayer(project, scene.Id, "ru");

        Assert.IsNull(player.Frame.LineId);
        Assert.AreEqual(-1, player.Frame.LineIndex);
        Assert.AreEqual(string.Empty, player.Frame.Text);
        Assert.AreEqual(string.Empty, player.Frame.SpeakerName);
        Assert.HasCount(0, player.Frame.Characters);
        Assert.IsFalse(player.Frame.CanPrevious);
        Assert.IsFalse(player.Frame.CanNext);
        Assert.IsFalse(player.Next());
        Assert.IsFalse(player.Previous());
    }

    [TestMethod]
    public void Constructor_RejectsMissingSceneStartLineAndLanguage()
    {
        var fixture = CreateFixture();

        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScenePlayer(fixture.Project, Guid.NewGuid(), "ru"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru", Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ScenePlayer(fixture.Project, fixture.Scene.Id, " "));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ScenePlayer(null!, fixture.Scene.Id, "ru"));
    }

    [TestMethod]
    public void DanglingReferences_ReportDiagnosticsWithoutRemovingUnrelatedSprites()
    {
        var validImage = Image("Боб", "bob.png");
        var bobState = new CharacterState(Guid.NewGuid(), "Обычно", validImage.Id);
        var bob = new NovelCharacter(Guid.NewGuid(), "Боб")
        {
            States = [bobState],
            DefaultStateId = bobState.Id,
        };
        var aliceImage = Image("Алиса", "alice.png");
        var aliceState = new CharacterState(Guid.NewGuid(), "Обычно", aliceImage.Id);
        var alice = new NovelCharacter(Guid.NewGuid(), "Алиса")
        {
            States = [aliceState],
            DefaultStateId = aliceState.Id,
        };
        var missingImageState = new CharacterState(Guid.NewGuid(), "Сломано", Guid.NewGuid());
        var charlie = new NovelCharacter(Guid.NewGuid(), "Чарли")
        {
            States = [missingImageState],
            DefaultStateId = missingImageState.Id,
        };
        var missingCharacterId = Guid.NewGuid();
        var missingStateId = Guid.NewGuid();
        var missingHideId = Guid.NewGuid();
        var missingBackgroundId = Guid.NewGuid();
        var lines = new[]
        {
            Line("Боб", bob.Id),
            Line("Скрыть отсутствующего", null, null, missingHideId),
            Line("Нет говорящего", missingCharacterId),
            Line("Нет состояния", alice.Id, missingStateId),
            Line("Состояние без говорящего", null, missingStateId),
            Line("Нет изображения", charlie.Id),
        };
        var scene = NovelScene.Create("Диагностика") with
        {
            BackgroundImageId = missingBackgroundId,
            Lines = [.. lines],
        };
        var project = NovelProject.Create("Проект") with
        {
            Images = [validImage, aliceImage],
            Characters = [alice, bob, charlie],
            Scenes = [scene],
        };
        var player = new ScenePlayer(project, scene.Id, "ru");

        AssertVisible(player.Frame, (bob.Id, bobState.Id));
        Assert.IsNull(player.Frame.Background);
        while (player.Next())
        {
            Assert.IsTrue(player.Frame.Characters.Any(sprite => sprite.CharacterId == bob.Id));
        }

        Assert.IsTrue(player.Frame.Diagnostics.Any(message => message.Contains(missingBackgroundId.ToString(), StringComparison.Ordinal)));
        Assert.IsTrue(player.Frame.Diagnostics.Any(message => message.Contains(missingHideId.ToString(), StringComparison.Ordinal)));
        Assert.IsTrue(player.Frame.Diagnostics.Any(message => message.Contains(missingCharacterId.ToString(), StringComparison.Ordinal)));
        Assert.IsTrue(player.Frame.Diagnostics.Any(message => message.Contains(missingStateId.ToString(), StringComparison.Ordinal)));
        Assert.IsTrue(player.Frame.Diagnostics.Any(message => message.Contains("состояние задано без говорящего", StringComparison.Ordinal)));
        Assert.IsTrue(player.Frame.Diagnostics.Any(message => message.Contains(missingImageState.ImageId.ToString(), StringComparison.Ordinal)));
        AssertVisible(player.Frame, (bob.Id, bobState.Id));
    }

    [TestMethod]
    public void InvalidSceneCss_UsesDocumentedFallbackAndReportsDiagnostic()
    {
        var fixture = CreateFixture();
        var invalidScene = fixture.Scene with { DialogueCssOverride = ".dialogue { color: red; }" };
        var project = fixture.Project with { Scenes = [invalidScene] };
        var expectedFallback = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);

        var player = new ScenePlayer(project, invalidScene.Id, "ru");

        Assert.AreEqual(expectedFallback.BackgroundColor, player.Frame.Style.BackgroundColor);
        Assert.AreEqual(expectedFallback.TextColor, player.Frame.Style.TextColor);
        Assert.AreEqual(expectedFallback.FontFamily, player.Frame.Style.FontFamily);
        Assert.AreEqual(expectedFallback.FontSize, player.Frame.Style.FontSize);
        Assert.IsTrue(player.Frame.Diagnostics.Any(message =>
            message.StartsWith("CSS сцены:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SavedAndReloadedProject_ProducesSameFrames()
    {
        var fixture = CreateFixture();
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "playback.novell");
        var store = new JsonProjectStore();
        await store.SaveAsync(path, fixture.Project);
        var loaded = await store.LoadAsync(path);
        var originalPlayer = new ScenePlayer(fixture.Project, fixture.Scene.Id, "ru");
        var loadedPlayer = new ScenePlayer(loaded, fixture.Scene.Id, "ru");

        var originalFrames = PlayAll(originalPlayer);
        var loadedFrames = PlayAll(loadedPlayer);

        Assert.AreEqual(originalFrames.Count, loadedFrames.Count);
        for (var index = 0; index < originalFrames.Count; index++)
        {
            AssertFrameEquals(originalFrames[index], loadedFrames[index]);
        }
    }

    private static Fixture CreateFixture()
    {
        var background = Image("Лес", "forest.png");
        var aliceCalmImage = Image("Алиса спокойна", "alice-calm.png");
        var aliceSadImage = Image("Алиса грустит", "alice-sad.png");
        var bobImage = Image("Боб", "bob.png");
        var aliceCalm = new CharacterState(Guid.NewGuid(), "Спокойна", aliceCalmImage.Id);
        var aliceSad = new CharacterState(Guid.NewGuid(), "Грустит", aliceSadImage.Id);
        var bobDefault = new CharacterState(Guid.NewGuid(), "Обычно", bobImage.Id);
        var alice = new NovelCharacter(Guid.NewGuid(), "Алиса")
        {
            States = [aliceCalm, aliceSad],
            DefaultStateId = aliceCalm.Id,
        };
        var bob = new NovelCharacter(Guid.NewGuid(), "Боб")
        {
            States = [bobDefault],
            DefaultStateId = bobDefault.Id,
        };
        var lines = new[]
        {
            Line("Привет", alice.Id),
            Line("Мне грустно", alice.Id, aliceSad.Id),
            Line("Я рядом", bob.Id),
            Line("Спасибо", alice.Id),
            Line("Наступила тишина"),
            Line("Алиса ушла", null, null, alice.Id),
            Line("Алиса вернулась", alice.Id),
        };
        var scene = NovelScene.Create("Пролог") with
        {
            BackgroundImageId = background.Id,
            Lines = [.. lines],
        };
        var project = NovelProject.Create("История") with
        {
            Images = [background, aliceCalmImage, aliceSadImage, bobImage],
            Characters = [alice, bob],
            Scenes = [scene],
        };
        return new(project, scene, background, alice, aliceCalm, aliceSad, bob, bobDefault, lines);
    }

    private static ImageAsset Image(string name, string fileName) =>
        new(Guid.NewGuid(), name, $"Assets/Images/{fileName}", 600, 1000);

    private static DialogueLine Line(
        string text,
        Guid? speakerId = null,
        Guid? stateId = null,
        Guid? hideCharacterId = null) =>
        DialogueLine.Create("ru", text) with
        {
            SpeakerId = speakerId,
            CharacterStateId = stateId,
            HideCharacterId = hideCharacterId,
        };

    private static List<SceneFrame> PlayAll(ScenePlayer player)
    {
        var frames = new List<SceneFrame> { player.Frame };
        while (player.Next())
        {
            frames.Add(player.Frame);
        }

        return frames;
    }

    private static void AssertFrameLine(
        SceneFrame frame,
        DialogueLine line,
        int index,
        string speakerName,
        bool first,
        bool hasNext)
    {
        Assert.AreEqual(line.Id, frame.LineId);
        Assert.AreEqual(index, frame.LineIndex);
        Assert.AreEqual(line.Texts["ru"], frame.Text);
        Assert.AreEqual(speakerName, frame.SpeakerName);
        Assert.AreEqual(!first, frame.CanPrevious);
        Assert.AreEqual(hasNext, frame.CanNext);
    }

    private static void AssertVisible(
        SceneFrame frame,
        params (Guid CharacterId, Guid StateId)[] expected)
    {
        Assert.AreEqual(expected.Length, frame.Characters.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index].CharacterId, frame.Characters[index].CharacterId);
            Assert.AreEqual(expected[index].StateId, frame.Characters[index].StateId);
        }
    }

    private static void AssertFrameEquals(SceneFrame expected, SceneFrame actual)
    {
        Assert.AreEqual(expected.LineId, actual.LineId);
        Assert.AreEqual(expected.LineIndex, actual.LineIndex);
        Assert.AreEqual(expected.Text, actual.Text);
        Assert.AreEqual(expected.SpeakerName, actual.SpeakerName);
        Assert.AreEqual(expected.Background?.Id, actual.Background?.Id);
        Assert.AreEqual(expected.CanPrevious, actual.CanPrevious);
        Assert.AreEqual(expected.CanNext, actual.CanNext);
        Assert.AreEqual(expected.Style.BackgroundColor, actual.Style.BackgroundColor);
        Assert.AreEqual(expected.Style.TextColor, actual.Style.TextColor);
        Assert.AreEqual(expected.Style.FontFamily, actual.Style.FontFamily);
        Assert.AreEqual(expected.Style.FontSize, actual.Style.FontSize);
        AssertVisible(actual, expected.Characters.Select(sprite => (sprite.CharacterId, sprite.StateId)).ToArray());
        CollectionAssert.AreEqual(expected.Diagnostics.ToArray(), actual.Diagnostics.ToArray());
    }

    private sealed record Fixture(
        NovelProject Project,
        NovelScene Scene,
        ImageAsset Background,
        NovelCharacter Alice,
        CharacterState AliceCalm,
        CharacterState AliceSad,
        NovelCharacter Bob,
        CharacterState BobDefault,
        DialogueLine[] Lines);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NovellMaker.ScenePlayerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
