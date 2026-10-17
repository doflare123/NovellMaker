using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Tests.Projects;

[TestClass]
public sealed class NovelProjectTests
{
    [TestMethod]
    public void Create_ProducesIndependentStableIdentifiers()
    {
        var first = NovelProject.Create("Первый проект");
        var second = NovelProject.Create("Второй проект");

        Assert.AreNotEqual(Guid.Empty, first.Id);
        Assert.AreNotEqual(Guid.Empty, second.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(NovelProject.CurrentFormatVersion, first.FormatVersion);
        Assert.AreEqual("ru", first.DefaultLanguage);
        Assert.HasCount(0, first.Scenes);
        Assert.HasCount(0, first.Images);
        Assert.AreEqual(DialogueCssParser.DefaultCss, first.DialogueUi.Css);
    }

    [TestMethod]
    public void Rename_WithExpression_PreservesIdentifier()
    {
        var original = NovelProject.Create("До переименования");

        var renamed = original with { Name = "После переименования" };

        Assert.AreEqual(original.Id, renamed.Id);
        Assert.AreEqual("После переименования", renamed.Name);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Create_RejectsMissingName(string? name)
    {
        Assert.ThrowsExactly<ArgumentException>(() => NovelProject.Create(name!));
    }

    [TestMethod]
    public void Constructor_RejectsEmptyIdentifier()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new NovelProject(NovelProject.CurrentFormatVersion, Guid.Empty, "Проект"));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    public void Constructor_RejectsUnsupportedFormatVersion(int version)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new NovelProject(version, Guid.NewGuid(), "Проект"));
    }

    [TestMethod]
    public void ContentEquals_UsesNestedContentInsteadOfCollectionIdentity()
    {
        var line = DialogueLine.Create("ru", "Текст") with
        {
            Texts = ImmutableDictionary<string, string>.Empty
                .Add("ru", "Текст")
                .Add("en", "Text"),
        };
        var image = new ImageAsset(
            Guid.NewGuid(),
            "Фон",
            "Assets/Images/background.png",
            1920,
            1080);
        var scene = NovelScene.Create("Сцена") with { Lines = [line], BackgroundImageId = image.Id };
        var project = NovelProject.Create("Проект") with { Scenes = [scene], Images = [image] };
        var separateCopy = new NovelProject(project.FormatVersion, project.Id, project.Name)
        {
            DefaultLanguage = project.DefaultLanguage,
            DialogueUi = project.DialogueUi with { },
            Images =
            [
                new ImageAsset(image.Id, image.Name, image.RelativePath, image.PixelWidth, image.PixelHeight),
            ],
            Scenes =
            [
                new NovelScene(scene.Id, scene.Name)
                {
                    BackgroundImageId = image.Id,
                    Lines =
                    [
                        new DialogueLine(line.Id, line.TextKey)
                        {
                            Texts = ImmutableDictionary<string, string>.Empty
                                .Add("en", "Text")
                                .Add("ru", "Текст"),
                        },
                    ],
                },
            ],
        };

        Assert.IsTrue(project.ContentEquals(separateCopy));
        Assert.IsTrue(NovelProject.ContentEquals(project, separateCopy));
        Assert.IsFalse(project.ContentEquals(separateCopy with
        {
            Images = [separateCopy.Images[0] with { PixelWidth = 1280 }],
        }));
        Assert.IsFalse(project.ContentEquals(separateCopy with
        {
            Images = [separateCopy.Images[0] with { PixelHeight = 720 }],
        }));
        Assert.IsFalse(project.ContentEquals(separateCopy with
        {
            Images = [separateCopy.Images[0] with { RelativePath = "Assets/Images/other.png" }],
        }));
        Assert.IsFalse(project.ContentEquals(separateCopy with
        {
            Images = [separateCopy.Images[0] with { Name = "Другой фон" }],
        }));
        Assert.IsFalse(project.ContentEquals(separateCopy with
        {
            Scenes = [separateCopy.Scenes[0] with { Name = "Другая сцена" }],
        }));
        Assert.IsFalse(project.ContentEquals(separateCopy with
        {
            Scenes =
            [
                separateCopy.Scenes[0] with
                {
                    Lines =
                    [
                        separateCopy.Scenes[0].Lines[0] with
                        {
                            Texts = separateCopy.Scenes[0].Lines[0].Texts.SetItem("ru", "Изменено"),
                        },
                    ],
                },
            ],
        }));
        Assert.IsTrue(NovelProject.ContentEquals(null, null));
        Assert.IsFalse(NovelProject.ContentEquals(project, null));
    }
}
