using System.Collections.Immutable;
using System.Text;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Tests.Projects;

[TestClass]
public sealed class JsonProjectStoreTests
{
    [TestMethod]
    public async Task Version6_RoundTripsExplorerModeAndStrictlyMigratesVersion5InMemory()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "editor-settings.novell");
        var project = NovelProject.Create("Настройки") with { ExplorerShowsFiles = true };
        var store = new JsonProjectStore();
        await store.SaveAsync(path, project);
        Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
        Assert.IsFalse(project.ContentEquals(project with { ExplorerShowsFiles = false }));
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["formatVersion"] = 5; document.AsObject().Remove("releaseCheck"); document.AsObject().Remove("startScreenSceneId"); document.AsObject().Remove("screens"); document.AsObject().Remove("pauseScreenId"); document.AsObject().Remove("inputBindings"); document.AsObject().Remove("audio"); document.Remove("scripts");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        document.Remove("explorerShowsFiles");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var bytes = await File.ReadAllBytesAsync(path);
        var migrated = await store.LoadAsync(path);
        Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion);
        Assert.IsFalse(migrated.ExplorerShowsFiles);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        document["formatVersion"] = 6; document.AsObject().Remove("releaseCheck"); document.AsObject().Remove("startScreenSceneId"); document.AsObject().Remove("screens"); document.AsObject().Remove("pauseScreenId"); document.AsObject().Remove("inputBindings"); document.AsObject().Remove("audio");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        document["explorerShowsFiles"] = "files";
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsProjectAtUnicodePath()
    {
        using var directory = TemporaryDirectory.Create("проект с пробелами");
        var path = Path.Combine(directory.Path, "история.novell");
        var expected = NovelProject.Create("Тайна старого маяка");
        var store = new JsonProjectStore();

        await store.SaveAsync(path, expected);
        var actual = await store.LoadAsync(path);

        Assert.IsTrue(expected.ContentEquals(actual));
        StringAssert.Contains(await File.ReadAllTextAsync(path), "Тайна старого маяка");
    }

    [TestMethod]
    public async Task SaveAndLoad_AfterRename_PreservesStableIdentifier()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "renamed.novell");
        var original = NovelProject.Create("Старое название");
        var renamed = original with { Name = "Новое название" };
        var store = new JsonProjectStore();

        await store.SaveAsync(path, renamed);
        var loaded = await store.LoadAsync(path);

        Assert.AreEqual(original.Id, loaded.Id);
        Assert.AreEqual("Новое название", loaded.Name);
    }

    [TestMethod]
    public async Task Save_WhenCancelled_PreservesExistingFile()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "existing.novell");
        var store = new JsonProjectStore();
        await store.SaveAsync(path, NovelProject.Create("Исходный проект"));
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            store.SaveAsync(path, NovelProject.Create("Новый проект"), cancellation.Token));

        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("id")]
    [DataRow("version")]
    public async Task Save_RejectsInvalidRecordCopiesAndPreservesExistingFile(string invalidProperty)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "existing.novell");
        var store = new JsonProjectStore();
        var original = NovelProject.Create("Исходный проект");
        await store.SaveAsync(path, original);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var invalid = invalidProperty switch
        {
            "name" => original with { Name = "   " },
            "id" => original with { Id = Guid.Empty },
            "version" => original with { FormatVersion = 999 },
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(path, invalid));

        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task Load_WhenCancelled_DoesNotReadFile()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "project.novell");
        await File.WriteAllTextAsync(path, "не json", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new JsonProjectStore().LoadAsync(path, cancellation.Token));
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"formatVersion\":1,\"id\":\"9a4b7915-17df-48ac-84fb-69d00f4bab41\",\"name\":\"Первое\",\"name\":\"Второе\"}")]
    [DataRow("{\"formatVersion\":1,\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"Проект\"}")]
    [DataRow("{\"formatVersion\":1,\"id\":\"9a4b7915-17df-48ac-84fb-69d00f4bab41\",\"name\":null}")]
    [DataRow("{\"formatVersion\":1,\"id\":\"9a4b7915-17df-48ac-84fb-69d00f4bab41\",\"name\":\"   \"}")]
    public async Task Load_RejectsMalformedNullOrIncompleteDocument(string json)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "invalid.novell");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        var exception = await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));

        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
    }

    [TestMethod]
    public async Task Load_RejectsFutureFormatVersionWithClearDiagnostic()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "future.novell");
        var json = """
            {
              "formatVersion": 999,
              "id": "9a4b7915-17df-48ac-84fb-69d00f4bab41",
              "name": "Проект из будущего",
              "futureFeature": { "nested": true }
            }
            """;
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        var exception = await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));

        StringAssert.Contains(exception.Message, "999");
        StringAssert.Contains(exception.Message, "не поддерживается");
    }

    [TestMethod]
    public async Task Load_RejectsUnknownFields()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "unknown-field.novell");
        var json = """
            {
              "formatVersion": 1,
              "id": "9a4b7915-17df-48ac-84fb-69d00f4bab41",
              "name": "Проект",
              "futureFeature": true
            }
            """;
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    public async Task Load_RejectsTrailingJsonData()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "trailing-data.novell");
        var json = """
            {"formatVersion":1,"id":"9a4b7915-17df-48ac-84fb-69d00f4bab41","name":"Проект"}
            {"another":"object"}
            """;
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    public async Task Save_WhenReplacementFails_PreservesTargetAndRemovesWrittenTemporaryFile()
    {
        using var directory = TemporaryDirectory.Create();
        var target = Path.Combine(directory.Path, "occupied.novell");
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(existing, "existing contents");

        var error = await Assert.ThrowsAsync<Exception>(() =>
            new JsonProjectStore().SaveAsync(target, NovelProject.Create("Новая новелла")));
        Assert.IsTrue(error is IOException or UnauthorizedAccessException,
            "Replacing a directory must fail with the platform's filesystem error.");

        Assert.AreEqual("existing contents", await File.ReadAllTextAsync(existing));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task Load_Version1_MigratesInMemoryWithoutWritingUntilSave()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "legacy.novell");
        var originalJson = """
            {
              "formatVersion": 1,
              "id": "9a4b7915-17df-48ac-84fb-69d00f4bab41",
              "name": "Старый проект"
            }
            """;
        await File.WriteAllTextAsync(path, originalJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var originalBytes = await File.ReadAllBytesAsync(path);
        var store = new JsonProjectStore();

        var migrated = await store.LoadAsync(path);

        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion);
        Assert.AreEqual(Guid.Parse("9a4b7915-17df-48ac-84fb-69d00f4bab41"), migrated.Id);
        Assert.AreEqual("Старый проект", migrated.Name);
        Assert.AreEqual("ru", migrated.DefaultLanguage);
        Assert.HasCount(0, migrated.Scenes);
        Assert.HasCount(0, migrated.Images);
        Assert.HasCount(0, migrated.Characters);
        Assert.AreEqual(DialogueCssParser.DefaultCss, migrated.DialogueUi.Css);

        await store.SaveAsync(path, migrated);

        var savedJson = await File.ReadAllTextAsync(path);
        StringAssert.Contains(savedJson, $"\"formatVersion\": {NovelProject.CurrentFormatVersion}");
        CollectionAssert.AreNotEqual(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task SaveAndLoad_Version2_RoundTripsOrderedMultilingualContentAndRawCss()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "multilingual.novell");
        var firstLine = DialogueLine.Create("ru", "Привет") with
        {
            Texts = ImmutableDictionary<string, string>.Empty
                .Add("ru", "Привет")
                .Add("de", "Hallo")
                .Add("en", "Hello"),
        };
        var secondLine = DialogueLine.Create("ru", "Пока");
        var firstScene = NovelScene.Create("Пролог") with
        {
            Lines = [firstLine, secondLine],
            DialogueCssOverride = ".dialogue { this is an unfinished author draft",
        };
        var secondScene = NovelScene.Create("Финал");
        var expected = NovelProject.Create("История") with
        {
            Scenes = [firstScene, secondScene],
            DefaultLanguage = "ru",
            DialogueUi = new DialogueUiSettings
            {
                Css = ".dialogue { color: #ddeeff; unfinished-draft: ; }",
                ShowVoiceProgress = false,
                ShowVoiceControls = true,
                ShowNavigationControls = false,
            },
        };
        var store = new JsonProjectStore();

        await store.SaveAsync(path, expected);
        var actual = await store.LoadAsync(path);

        Assert.IsTrue(expected.ContentEquals(actual));
        Assert.AreEqual(firstScene.Id, actual.Scenes[0].Id);
        Assert.AreEqual(secondScene.Id, actual.Scenes[1].Id);
        Assert.AreEqual(firstLine.Id, actual.Scenes[0].Lines[0].Id);
        Assert.AreEqual(firstLine.TextKey, actual.Scenes[0].Lines[0].TextKey);
        var json = await File.ReadAllTextAsync(path);
        Assert.IsTrue(json.IndexOf("\"de\"", StringComparison.Ordinal) <
                      json.IndexOf("\"en\"", StringComparison.Ordinal));
        Assert.IsTrue(json.IndexOf("\"en\"", StringComparison.Ordinal) <
                      json.IndexOf("\"ru\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Load_Version2_MigratesScenesWithoutChangingSourceFile()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "version2.novell");
        await File.WriteAllTextAsync(path, ValidVersion2Json);
        var originalBytes = await File.ReadAllBytesAsync(path);

        var migrated = await new JsonProjectStore().LoadAsync(path);

        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion);
        Assert.HasCount(0, migrated.Images);
        Assert.HasCount(0, migrated.Characters);
        Assert.HasCount(1, migrated.Scenes);
        Assert.AreEqual(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), migrated.Scenes[0].Id);
        Assert.IsNull(migrated.Scenes[0].BackgroundImageId);
        Assert.AreEqual(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), migrated.Scenes[0].Lines[0].Id);
        Assert.AreEqual("Текст", migrated.Scenes[0].Lines[0].Texts["ru"]);
    }

    [TestMethod]
    public async Task SaveAndLoad_Version3_PreservesImagesAndBackgroundReferences()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "images.novell");
        var firstImage = new ImageAsset(
            Guid.NewGuid(),
            "Лес",
            "Assets/Images/backgrounds/forest.PNG",
            1920,
            1080);
        var secondImage = new ImageAsset(
            Guid.NewGuid(),
            "Город",
            "Assets/Images/backgrounds/city.jpeg",
            2048,
            1152);
        var scene = NovelScene.Create("Пролог") with { BackgroundImageId = firstImage.Id };
        var expected = NovelProject.Create("Проект с фонами") with
        {
            Images = [firstImage with { Name = "Лес вечером" }, secondImage],
            Scenes = [scene],
        };
        var store = new JsonProjectStore();

        await store.SaveAsync(path, expected);
        var actual = await store.LoadAsync(path);

        Assert.IsTrue(expected.ContentEquals(actual));
        Assert.AreEqual(firstImage.Id, actual.Images[0].Id);
        Assert.AreEqual("Лес вечером", actual.Images[0].Name);
        Assert.AreEqual(firstImage.Id, actual.Scenes[0].BackgroundImageId);
    }

    [TestMethod]
    public async Task SaveAndLoad_AllowsDanglingBackgroundReferenceForAuthoringRepair()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "dangling-reference.novell");
        var missingImageId = Guid.NewGuid();
        var project = NovelProject.Create("Проект") with
        {
            Scenes = [NovelScene.Create("Сцена") with { BackgroundImageId = missingImageId }],
        };
        var store = new JsonProjectStore();

        await store.SaveAsync(path, project);
        var loaded = await store.LoadAsync(path);

        Assert.AreEqual(missingImageId, loaded.Scenes[0].BackgroundImageId);
    }

    [TestMethod]
    [DataRow("unknownImageField")]
    [DataRow("duplicateImageField")]
    [DataRow("missingImages")]
    [DataRow("missingBackgroundImageId")]
    public async Task Load_Version3_RejectsUnknownDuplicateOrMissingFields(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "strict-v3.novell");
        var json = invalidCase switch
        {
            "unknownImageField" => ValidVersion3Json.Replace(
                "\"pixelWidth\": 1920,",
                "\"pixelWidth\": 1920, \"futureImageField\": true,",
                StringComparison.Ordinal),
            "duplicateImageField" => ValidVersion3Json.Replace(
                "\"pixelWidth\": 1920,",
                "\"pixelWidth\": 1920, \"pixelWidth\": 100,",
                StringComparison.Ordinal),
            "missingImages" => ValidVersion3Json.Replace(
                ", \"images\": [{\"id\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\", \"name\": \"Фон\", \"relativePath\": \"Assets/Images/background.png\", \"pixelWidth\": 1920, \"pixelHeight\": 1080}]",
                string.Empty,
                StringComparison.Ordinal),
            "missingBackgroundImageId" => ValidVersion3Json.Replace(
                ", \"backgroundImageId\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\"",
                string.Empty,
                StringComparison.Ordinal),
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    [DataRow("nullImages")]
    [DataRow("nullImage")]
    [DataRow("duplicateImageId")]
    [DataRow("duplicateImagePath")]
    [DataRow("invalidDimensions")]
    [DataRow("emptyBackgroundId")]
    public async Task Load_Version3_RejectsInvalidImageGraph(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "invalid-images.novell");
        var json = invalidCase switch
        {
            "nullImages" => ValidVersion3Json.Replace(
                "[{\"id\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\", \"name\": \"Фон\", \"relativePath\": \"Assets/Images/background.png\", \"pixelWidth\": 1920, \"pixelHeight\": 1080}]",
                "null",
                StringComparison.Ordinal),
            "nullImage" => ValidVersion3Json.Replace(
                "[{\"id\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\"",
                "[null,{\"id\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\"",
                StringComparison.Ordinal),
            "duplicateImageId" => ValidVersion3Json.Replace(
                "\"pixelHeight\": 1080}]",
                "\"pixelHeight\": 1080}, {\"id\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\", \"name\": \"Дубль\", \"relativePath\": \"Assets/Images/other.png\", \"pixelWidth\": 100, \"pixelHeight\": 100}]",
                StringComparison.Ordinal),
            "duplicateImagePath" => ValidVersion3Json.Replace(
                "\"pixelHeight\": 1080}]",
                "\"pixelHeight\": 1080}, {\"id\": \"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee\", \"name\": \"Дубль\", \"relativePath\": \"Assets/Images/BACKGROUND.PNG\", \"pixelWidth\": 100, \"pixelHeight\": 100}]",
                StringComparison.Ordinal),
            "invalidDimensions" => ValidVersion3Json.Replace(
                "\"pixelWidth\": 1920",
                "\"pixelWidth\": 0",
                StringComparison.Ordinal),
            "emptyBackgroundId" => ValidVersion3Json.Replace(
                "\"backgroundImageId\": \"dddddddd-dddd-dddd-dddd-dddddddddddd\"",
                "\"backgroundImageId\": \"00000000-0000-0000-0000-000000000000\"",
                StringComparison.Ordinal),
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    [DataRow("unknownSceneField")]
    [DataRow("unknownLineField")]
    [DataRow("unknownDialogueUiField")]
    [DataRow("duplicateTranslation")]
    [DataRow("missingSceneCssOverride")]
    public async Task Load_Version2_RejectsUnknownDuplicateOrMissingNestedFields(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "strict-v2.novell");
        var json = invalidCase switch
        {
            "unknownSceneField" => ValidVersion2Json.Replace(
                "\"name\": \"Сцена\",",
                "\"name\": \"Сцена\", \"futureSceneField\": true,",
                StringComparison.Ordinal),
            "unknownLineField" => ValidVersion2Json.Replace(
                "\"textKey\": \"line.one\",",
                "\"textKey\": \"line.one\", \"futureLineField\": true,",
                StringComparison.Ordinal),
            "unknownDialogueUiField" => ValidVersion2Json.Replace(
                "\"showVoiceProgress\": true,",
                "\"showVoiceProgress\": true, \"futureUiField\": true,",
                StringComparison.Ordinal),
            "duplicateTranslation" => ValidVersion2Json.Replace(
                "\"ru\": \"Текст\"",
                "\"ru\": \"Текст\", \"ru\": \"Другой текст\"",
                StringComparison.Ordinal),
            "missingSceneCssOverride" => ValidVersion2Json.Replace(
                ", \"dialogueCssOverride\": null",
                string.Empty,
                StringComparison.Ordinal),
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    [DataRow("nullScene")]
    [DataRow("nullLines")]
    [DataRow("nullTexts")]
    [DataRow("nullText")]
    [DataRow("blankLanguage")]
    [DataRow("blankDefaultLanguage")]
    [DataRow("nullCss")]
    [DataRow("duplicateSceneId")]
    [DataRow("duplicateLineId")]
    [DataRow("duplicateTextKey")]
    public async Task Load_Version2_RejectsInvalidGraph(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "invalid-graph.novell");
        var json = invalidCase switch
        {
            "nullScene" => ValidVersion2Json.Replace(
                "[{\"id\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"",
                "[null,{\"id\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"",
                StringComparison.Ordinal),
            "nullLines" => ValidVersion2Json.Replace(
                "\"lines\": [{\"id\": \"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\", \"textKey\": \"line.one\", \"texts\": {\"ru\": \"Текст\"}}]",
                "\"lines\": null",
                StringComparison.Ordinal),
            "nullTexts" => ValidVersion2Json.Replace(
                "\"texts\": {\"ru\": \"Текст\"}",
                "\"texts\": null",
                StringComparison.Ordinal),
            "nullText" => ValidVersion2Json.Replace(
                "\"ru\": \"Текст\"",
                "\"ru\": null",
                StringComparison.Ordinal),
            "blankLanguage" => ValidVersion2Json.Replace(
                "\"ru\": \"Текст\"",
                "\"   \": \"Текст\"",
                StringComparison.Ordinal),
            "blankDefaultLanguage" => ValidVersion2Json.Replace(
                "\"defaultLanguage\": \"ru\"",
                "\"defaultLanguage\": \"   \"",
                StringComparison.Ordinal),
            "nullCss" => ValidVersion2Json.Replace(
                "\"css\": \".dialogue {}\"",
                "\"css\": null",
                StringComparison.Ordinal),
            "duplicateSceneId" => ValidVersion2Json.Replace(
                "] , \"defaultLanguage\"",
                ", {\"id\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\", \"name\": \"Дубль\", \"lines\": [], \"dialogueCssOverride\": null}] , \"defaultLanguage\"",
                StringComparison.Ordinal),
            "duplicateLineId" => ValidVersion2Json.Replace(
                "] , \"dialogueCssOverride\"",
                ", {\"id\": \"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\", \"textKey\": \"line.two\", \"texts\": {\"ru\": \"Два\"}}] , \"dialogueCssOverride\"",
                StringComparison.Ordinal),
            "duplicateTextKey" => ValidVersion2Json.Replace(
                "] , \"dialogueCssOverride\"",
                ", {\"id\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\", \"textKey\": \"line.one\", \"texts\": {\"ru\": \"Два\"}}] , \"dialogueCssOverride\"",
                StringComparison.Ordinal),
            _ => throw new AssertFailedException("Неизвестный вариант теста."),
        };
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsExactlyAsync<ProjectFormatException>(() =>
            new JsonProjectStore().LoadAsync(path));
    }

    [TestMethod]
    [DataRow("defaultScenes")]
    [DataRow("nullScene")]
    [DataRow("defaultLines")]
    [DataRow("nullLine")]
    [DataRow("duplicateSceneId")]
    [DataRow("duplicateLineId")]
    [DataRow("duplicateTextKey")]
    [DataRow("nullTexts")]
    [DataRow("blankLanguage")]
    [DataRow("nullText")]
    [DataRow("blankDefaultLanguage")]
    [DataRow("nullDialogueUi")]
    [DataRow("nullCss")]
    [DataRow("defaultImages")]
    [DataRow("nullImage")]
    [DataRow("duplicateImageId")]
    [DataRow("duplicateImagePath")]
    [DataRow("imageSceneIdCollision")]
    [DataRow("blankImageName")]
    [DataRow("unsafeImagePath")]
    [DataRow("invalidImageWidth")]
    [DataRow("invalidImagePixelCount")]
    [DataRow("emptyBackgroundId")]
    public async Task Save_RejectsInvalidContentGraphBeforeWriting(string invalidCase)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "invalid.novell");
        var firstLine = DialogueLine.Create("ru", "Один");
        var secondLine = DialogueLine.Create("ru", "Два");
        var firstImage = new ImageAsset(
            Guid.NewGuid(), "Фон", "Assets/Images/background.png", 1920, 1080);
        var secondImage = new ImageAsset(
            Guid.NewGuid(), "Другой фон", "Assets/Images/other.jpg", 1280, 720);
        var firstScene = NovelScene.Create("Первая") with
        {
            Lines = [firstLine],
            BackgroundImageId = firstImage.Id,
        };
        var secondScene = NovelScene.Create("Вторая") with { Lines = [secondLine] };
        var valid = NovelProject.Create("Проект") with
        {
            Scenes = [firstScene, secondScene],
            Images = [firstImage, secondImage],
        };
        var invalid = invalidCase switch
        {
            "defaultScenes" => valid with { Scenes = default },
            "nullScene" => valid with { Scenes = [null!] },
            "defaultLines" => valid with { Scenes = [firstScene with { Lines = default }] },
            "nullLine" => valid with { Scenes = [firstScene with { Lines = [null!] }] },
            "duplicateSceneId" => valid with { Scenes = [firstScene, secondScene with { Id = firstScene.Id }] },
            "duplicateLineId" => valid with
            {
                Scenes = [firstScene, secondScene with { Lines = [secondLine with { Id = firstLine.Id }] }],
            },
            "duplicateTextKey" => valid with
            {
                Scenes = [firstScene, secondScene with { Lines = [secondLine with { TextKey = firstLine.TextKey }] }],
            },
            "nullTexts" => valid with
            {
                Scenes = [firstScene with { Lines = [firstLine with { Texts = null! }] }],
            },
            "blankLanguage" => valid with
            {
                Scenes = [firstScene with
                {
                    Lines = [firstLine with { Texts = ImmutableDictionary<string, string>.Empty.Add(" ", "Текст") }],
                }],
            },
            "nullText" => valid with
            {
                Scenes = [firstScene with
                {
                    Lines = [firstLine with { Texts = ImmutableDictionary<string, string>.Empty.Add("ru", null!) }],
                }],
            },
            "blankDefaultLanguage" => valid with { DefaultLanguage = " " },
            "nullDialogueUi" => valid with { DialogueUi = null! },
            "nullCss" => valid with { DialogueUi = valid.DialogueUi with { Css = null! } },
            "defaultImages" => valid with { Images = default },
            "nullImage" => valid with { Images = [null!] },
            "duplicateImageId" => valid with
            {
                Images = [firstImage, secondImage with { Id = firstImage.Id }],
            },
            "duplicateImagePath" => valid with
            {
                Images = [firstImage, secondImage with { RelativePath = "Assets/Images/BACKGROUND.PNG" }],
            },
            "imageSceneIdCollision" => valid with
            {
                Images = [firstImage, secondImage with { Id = secondScene.Id }],
            },
            "blankImageName" => valid with { Images = [firstImage with { Name = " " }] },
            "unsafeImagePath" => valid with
            {
                Images = [firstImage with { RelativePath = "Assets/Images/../outside.png" }],
            },
            "invalidImageWidth" => valid with { Images = [firstImage with { PixelWidth = 0 }] },
            "invalidImagePixelCount" => valid with
            {
                Images = [firstImage with { PixelWidth = 8001, PixelHeight = 4000 }],
            },
            "emptyBackgroundId" => valid with
            {
                Scenes = [firstScene with { BackgroundImageId = Guid.Empty }],
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
          "scenes": [{"id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "name": "Сцена", "lines": [{"id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "textKey": "line.one", "texts": {"ru": "Текст"}}], "dialogueCssOverride": null, "backgroundImageId": "dddddddd-dddd-dddd-dddd-dddddddddddd"}], "images": [{"id": "dddddddd-dddd-dddd-dddd-dddddddddddd", "name": "Фон", "relativePath": "Assets/Images/background.png", "pixelWidth": 1920, "pixelHeight": 1080}], "defaultLanguage": "ru",
          "dialogueUi": {"css": ".dialogue {}", "showVoiceProgress": true, "showVoiceControls": true, "showNavigationControls": true}
        }
        """;

    private const string ValidVersion2Json = """
        {
          "formatVersion": 2,
          "id": "99999999-9999-9999-9999-999999999999",
          "name": "Проект",
          "scenes": [{"id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "name": "Сцена", "lines": [{"id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "textKey": "line.one", "texts": {"ru": "Текст"}}] , "dialogueCssOverride": null}] , "defaultLanguage": "ru",
          "dialogueUi": {"css": ".dialogue {}", "showVoiceProgress": true, "showVoiceControls": true, "showNavigationControls": true}
        }
        """;

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string cleanupRoot;

        private TemporaryDirectory(string path, string cleanupRoot)
        {
            Path = path;
            this.cleanupRoot = cleanupRoot;
        }

        public string Path { get; }

        public static TemporaryDirectory Create(string? childName = null)
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NovellMaker.Tests",
                Guid.NewGuid().ToString("N"));
            var path = childName is null ? root : System.IO.Path.Combine(root, childName);
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path, root);
        }

        public void Dispose()
        {
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
