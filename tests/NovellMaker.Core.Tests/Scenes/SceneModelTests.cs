using System.Collections.Immutable;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Scenes;

[TestClass]
public sealed class SceneModelTests
{
    [TestMethod]
    public void Create_ProducesIndependentSceneIdentifiers()
    {
        var first = NovelScene.Create("Пролог");
        var second = NovelScene.Create("Пролог");

        Assert.AreNotEqual(Guid.Empty, first.Id);
        Assert.AreNotEqual(Guid.Empty, second.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.HasCount(0, first.Lines);
    }

    [TestMethod]
    public void DialogueLineCreate_ProducesStableIndependentReferencesAndInitialText()
    {
        var first = DialogueLine.Create("ru", "Первая реплика");
        var second = DialogueLine.Create("ru");

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreNotEqual(first.TextKey, second.TextKey);
        StringAssert.Contains(first.TextKey, first.Id.ToString("N"));
        Assert.AreEqual("Первая реплика", first.Texts["ru"]);
        Assert.AreEqual(string.Empty, second.Texts["ru"]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void DialogueLineCreate_RejectsMissingLanguage(string? language)
    {
        Assert.ThrowsExactly<ArgumentException>(() => DialogueLine.Create(language!));
    }

    [TestMethod]
    public void ContentEquals_ComparesLineOrderAndTranslationsStructurally()
    {
        var firstLine = DialogueLine.Create("ru", "Привет") with
        {
            Texts = ImmutableDictionary<string, string>.Empty
                .Add("ru", "Привет")
                .Add("en", "Hello"),
        };
        var secondLine = DialogueLine.Create("ru", "Пока");
        var scene = NovelScene.Create("Сцена") with
        {
            DialogueCssOverride = ".dialogue { color: #fff; }",
            BackgroundImageId = Guid.NewGuid(),
            Lines = [firstLine, secondLine],
        };
        var separateCopy = new NovelScene(scene.Id, scene.Name)
        {
            DialogueCssOverride = scene.DialogueCssOverride,
            BackgroundImageId = scene.BackgroundImageId,
            Lines =
            [
                new DialogueLine(firstLine.Id, firstLine.TextKey)
                {
                    Texts = ImmutableDictionary<string, string>.Empty
                        .Add("en", "Hello")
                        .Add("ru", "Привет"),
                },
                new DialogueLine(secondLine.Id, secondLine.TextKey)
                {
                    Texts = secondLine.Texts.ToImmutableDictionary(StringComparer.Ordinal),
                },
            ],
        };

        Assert.IsTrue(scene.ContentEquals(separateCopy));
        Assert.IsFalse(scene.ContentEquals(separateCopy with
        {
            Lines = [separateCopy.Lines[1], separateCopy.Lines[0]],
        }));
        Assert.IsFalse(scene.ContentEquals(separateCopy with { BackgroundImageId = Guid.NewGuid() }));
    }
}
