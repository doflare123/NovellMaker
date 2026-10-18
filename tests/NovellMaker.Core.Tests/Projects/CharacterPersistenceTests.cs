using System.Collections.Immutable;
using System.Text;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Projects;

[TestClass]
public sealed class CharacterPersistenceTests
{
    [TestMethod]
    public async Task SaveAndLoad_Version4_PreservesCharactersStatesAndLineBindings()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "characters.novell");
        var calmImage = new ImageAsset(
            Guid.NewGuid(), "Алиса спокойна", "Assets/Images/alice-calm.png", 600, 1000);
        var sadImage = new ImageAsset(
            Guid.NewGuid(), "Алиса грустит", "Assets/Images/alice-sad.png", 600, 1000);
        var calmState = new CharacterState(Guid.NewGuid(), "Спокойна", calmImage.Id);
        var sadState = new CharacterState(Guid.NewGuid(), "Грустит", sadImage.Id);
        var alice = new NovelCharacter(Guid.NewGuid(), "Алиса")
        {
            States = [calmState, sadState],
            DefaultStateId = calmState.Id,
        };
        var bob = NovelCharacter.Create("Боб");
        var line = DialogueLine.Create("ru", "Мне жаль") with
        {
            SpeakerId = alice.Id,
            CharacterStateId = sadState.Id,
            HideCharacterId = bob.Id,
        };
        var expected = NovelProject.Create("История") with
        {
            Images = [calmImage, sadImage],
            Characters = [alice, bob],
            Scenes = [NovelScene.Create("Пролог") with { Lines = [line] }],
        };

        var store = new JsonProjectStore();
        await store.SaveAsync(path, expected);
        var actual = await store.LoadAsync(path);

        Assert.IsTrue(expected.ContentEquals(actual));
        Assert.AreEqual(alice.Id, actual.Scenes[0].Lines[0].SpeakerId);
        Assert.AreEqual(sadState.Id, actual.Scenes[0].Lines[0].CharacterStateId);
        Assert.AreEqual(bob.Id, actual.Scenes[0].Lines[0].HideCharacterId);
        StringAssert.Contains(await File.ReadAllTextAsync(path), $"\"formatVersion\": {NovelProject.CurrentFormatVersion}");
    }

    [TestMethod]
    public async Task Load_Version3_MigratesInMemoryWithoutChangingSourceFile()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "version3.novell");
        await File.WriteAllTextAsync(path, ValidVersion3Json, new UTF8Encoding(false));
        var originalBytes = await File.ReadAllBytesAsync(path);

        var migrated = await new JsonProjectStore().LoadAsync(path);

        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion);
        Assert.HasCount(0, migrated.Characters);
        Assert.HasCount(1, migrated.Images);
        var line = migrated.Scenes[0].Lines[0];
        Assert.IsNull(line.SpeakerId);
        Assert.IsNull(line.CharacterStateId);
        Assert.IsNull(line.HideCharacterId);
    }

    [TestMethod]
    public async Task SaveAndLoad_AllowsDanglingCharacterReferencesForAuthoringRepair()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "dangling.novell");
        var missingImageId = Guid.NewGuid();
        var missingDefaultStateId = Guid.NewGuid();
        var state = new CharacterState(Guid.NewGuid(), "Черновик", missingImageId);
        var character = new NovelCharacter(Guid.NewGuid(), "Алиса")
        {
            States = [state],
            DefaultStateId = missingDefaultStateId,
        };
        var missingSpeakerId = Guid.NewGuid();
        var missingStateId = Guid.NewGuid();
        var missingHiddenId = Guid.NewGuid();
        var line = DialogueLine.Create("ru", "Черновик") with
        {
            SpeakerId = missingSpeakerId,
            CharacterStateId = missingStateId,
            HideCharacterId = missingHiddenId,
        };
        var project = NovelProject.Create("Черновик") with
        {
            Characters = [character],
            Scenes = [NovelScene.Create("Сцена") with { Lines = [line] }],
        };

        var store = new JsonProjectStore();
        await store.SaveAsync(path, project);
        var loaded = await store.LoadAsync(path);

        Assert.AreEqual(missingImageId, loaded.Characters[0].States[0].ImageId);
        Assert.AreEqual(missingDefaultStateId, loaded.Characters[0].DefaultStateId);
        Assert.AreEqual(missingSpeakerId, loaded.Scenes[0].Lines[0].SpeakerId);
        Assert.AreEqual(missingStateId, loaded.Scenes[0].Lines[0].CharacterStateId);
        Assert.AreEqual(missingHiddenId, loaded.Scenes[0].Lines[0].HideCharacterId);
    }

    [TestMethod]
    [DataRow("unknownCharacterField")]
    [DataRow("unknownStateField")]
    [DataRow("unknownLineField")]
    [DataRow("missingCharacters")]
    [DataRow("missingStates")]
    [DataRow("missingDefaultStateId")]
    [DataRow("missingSpeakerId")]
    [DataRow("missingCharacterStateId")]
    [DataRow("missingHideCharacterId")]
    public async Task Load_Version4_RejectsUnknownOrMissingFields(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "strict-v4.novell");
        var json = invalidCase switch
        {
            "unknownCharacterField" => ValidVersion4Json.Replace(
                "\"name\": \"Алиса\", \"states\"",
                "\"name\": \"Алиса\", \"futureCharacterField\": true, \"states\"",
                StringComparison.Ordinal),
            "unknownStateField" => ValidVersion4Json.Replace(
                "\"name\": \"Спокойна\", \"imageId\"",
                "\"name\": \"Спокойна\", \"futureStateField\": true, \"imageId\"",
                StringComparison.Ordinal),
            "unknownLineField" => ValidVersion4Json.Replace(
                "\"speakerId\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"",
                "\"futureLineField\": true, \"speakerId\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"",
                StringComparison.Ordinal),
            "missingCharacters" => ValidVersion4Json.Replace(
                "\"characters\":",
                "\"missingCharacters\":",
                StringComparison.Ordinal),
            "missingStates" => ValidVersion4Json.Replace(
                ", \"states\": [{\"id\": \"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee\", \"name\": \"Спокойна\", \"imageId\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\"}]",
                string.Empty,
                StringComparison.Ordinal),
            "missingDefaultStateId" => ValidVersion4Json.Replace(
                ", \"defaultStateId\": \"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee\"",
                string.Empty,
                StringComparison.Ordinal),
            "missingSpeakerId" => ValidVersion4Json.Replace(
                ", \"speakerId\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"",
                string.Empty,
                StringComparison.Ordinal),
            "missingCharacterStateId" => ValidVersion4Json.Replace(
                ", \"characterStateId\": \"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee\"",
                string.Empty,
                StringComparison.Ordinal),
            "missingHideCharacterId" => ValidVersion4Json.Replace(
                ", \"hideCharacterId\": null",
                string.Empty,
                StringComparison.Ordinal),
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    [DataRow("defaultCharacters")]
    [DataRow("nullCharacter")]
    [DataRow("blankCharacterName")]
    [DataRow("defaultStates")]
    [DataRow("nullState")]
    [DataRow("blankStateName")]
    [DataRow("emptyDefaultStateId")]
    [DataRow("emptyStateImageId")]
    [DataRow("emptySpeakerId")]
    [DataRow("emptyCharacterStateId")]
    [DataRow("emptyHideCharacterId")]
    [DataRow("duplicateCharacterId")]
    [DataRow("duplicateStateId")]
    [DataRow("characterImageIdCollision")]
    [DataRow("stateSceneIdCollision")]
    public async Task Save_RejectsInvalidCharacterGraphBeforeWriting(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "invalid.novell");
        var image = new ImageAsset(Guid.NewGuid(), "Спрайт", "Assets/Images/alice.png", 600, 1000);
        var state = new CharacterState(Guid.NewGuid(), "Спокойна", image.Id);
        var character = new NovelCharacter(Guid.NewGuid(), "Алиса")
        {
            States = [state],
            DefaultStateId = state.Id,
        };
        var secondCharacter = NovelCharacter.Create("Боб");
        var line = DialogueLine.Create("ru", "Текст") with
        {
            SpeakerId = character.Id,
            CharacterStateId = state.Id,
            HideCharacterId = secondCharacter.Id,
        };
        var scene = NovelScene.Create("Сцена") with { Lines = [line] };
        var valid = NovelProject.Create("Проект") with
        {
            Images = [image],
            Characters = [character, secondCharacter],
            Scenes = [scene],
        };
        var invalid = invalidCase switch
        {
            "defaultCharacters" => valid with { Characters = default },
            "nullCharacter" => valid with { Characters = [null!] },
            "blankCharacterName" => valid with { Characters = [character with { Name = " " }] },
            "defaultStates" => valid with { Characters = [character with { States = default }] },
            "nullState" => valid with { Characters = [character with { States = [null!] }] },
            "blankStateName" => valid with
            {
                Characters = [character with { States = [state with { Name = string.Empty }] }],
            },
            "emptyDefaultStateId" => valid with
            {
                Characters = [character with { DefaultStateId = Guid.Empty }],
            },
            "emptyStateImageId" => valid with
            {
                Characters = [character with { States = [state with { ImageId = Guid.Empty }] }],
            },
            "emptySpeakerId" => valid with
            {
                Scenes = [scene with { Lines = [line with { SpeakerId = Guid.Empty }] }],
            },
            "emptyCharacterStateId" => valid with
            {
                Scenes = [scene with { Lines = [line with { CharacterStateId = Guid.Empty }] }],
            },
            "emptyHideCharacterId" => valid with
            {
                Scenes = [scene with { Lines = [line with { HideCharacterId = Guid.Empty }] }],
            },
            "duplicateCharacterId" => valid with
            {
                Characters = [character, secondCharacter with { Id = character.Id }],
            },
            "duplicateStateId" => valid with
            {
                Characters = [character, secondCharacter with
                {
                    States = [new CharacterState(state.Id, "Дубль", image.Id)],
                }],
            },
            "characterImageIdCollision" => valid with
            {
                Characters = [character with { Id = image.Id }],
            },
            "stateSceneIdCollision" => valid with
            {
                Characters = [character with { States = [state with { Id = scene.Id }] }],
            },
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new JsonProjectStore().SaveAsync(path, invalid));

        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private const string ValidVersion3Json = """
        {
          "formatVersion": 3,
          "id": "99999999-9999-9999-9999-999999999999",
          "name": "Проект",
          "scenes": [{"id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "name": "Сцена", "lines": [{"id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "textKey": "line.one", "texts": {"ru": "Текст"}}], "dialogueCssOverride": null, "backgroundImageId": "dddddddd-dddd-dddd-dddd-dddddddddddd"}],
          "images": [{"id": "dddddddd-dddd-dddd-dddd-dddddddddddd", "name": "Фон", "relativePath": "Assets/Images/background.png", "pixelWidth": 1920, "pixelHeight": 1080}],
          "defaultLanguage": "ru",
          "dialogueUi": {"css": ".dialogue {}", "showVoiceProgress": true, "showVoiceControls": true, "showNavigationControls": true}
        }
        """;

    private const string ValidVersion4Json = """
        {
          "formatVersion": 4,
          "id": "99999999-9999-9999-9999-999999999999",
          "name": "Проект",
          "scenes": [{"id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "name": "Сцена", "lines": [{"id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "textKey": "line.one", "texts": {"ru": "Текст"}, "speakerId": "cccccccc-cccc-cccc-cccc-cccccccccccc", "characterStateId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "hideCharacterId": null}], "dialogueCssOverride": null, "backgroundImageId": "dddddddd-dddd-dddd-dddd-dddddddddddd"}],
          "images": [{"id": "dddddddd-dddd-dddd-dddd-dddddddddddd", "name": "Фон", "relativePath": "Assets/Images/background.png", "pixelWidth": 1920, "pixelHeight": 1080}],
          "characters": [{"id": "cccccccc-cccc-cccc-cccc-cccccccccccc", "name": "Алиса", "states": [{"id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "name": "Спокойна", "imageId": "dddddddd-dddd-dddd-dddd-dddddddddddd"}], "defaultStateId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"}],
          "defaultLanguage": "ru",
          "dialogueUi": {"css": ".dialogue {}", "showVoiceProgress": true, "showVoiceControls": true, "showNavigationControls": true}
        }
        """;

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
                "NovellMaker.CharacterTests",
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
