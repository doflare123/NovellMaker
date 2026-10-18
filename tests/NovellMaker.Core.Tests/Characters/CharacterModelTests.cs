using NovellMaker.Core.Characters;

namespace NovellMaker.Core.Tests.Characters;

[TestClass]
public sealed class CharacterModelTests
{
    [TestMethod]
    public void Create_ProducesStableIndependentIdentifiersAndEmptyStates()
    {
        var first = NovelCharacter.Create("Алиса");
        var second = NovelCharacter.Create("Алиса");

        Assert.AreNotEqual(Guid.Empty, first.Id);
        Assert.AreNotEqual(Guid.Empty, second.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.HasCount(0, first.States);
        Assert.IsNull(first.DefaultStateId);
    }

    [TestMethod]
    public void Names_CanBeBlankInDraftModels()
    {
        var imageId = Guid.NewGuid();
        var character = new NovelCharacter(Guid.NewGuid(), string.Empty)
        {
            States = [new CharacterState(Guid.NewGuid(), "   ", imageId)],
        };

        Assert.AreEqual(string.Empty, character.Name);
        Assert.AreEqual("   ", character.States[0].Name);
    }

    [TestMethod]
    public void Constructors_RejectEmptyStableIdentifiers()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new NovelCharacter(Guid.Empty, "Персонаж"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CharacterState(Guid.Empty, "Спокоен", Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CharacterState(Guid.NewGuid(), "Спокоен", Guid.Empty));
    }

    [TestMethod]
    public void ContentEquals_UsesStateOrderAndAllStateContent()
    {
        var state = new CharacterState(Guid.NewGuid(), "Спокоен", Guid.NewGuid());
        var character = new NovelCharacter(Guid.NewGuid(), "Алиса")
        {
            States = [state],
            DefaultStateId = state.Id,
        };
        var separateCopy = new NovelCharacter(character.Id, character.Name)
        {
            States = [new CharacterState(state.Id, state.Name, state.ImageId)],
            DefaultStateId = state.Id,
        };

        Assert.IsTrue(character.ContentEquals(separateCopy));
        Assert.IsFalse(character.ContentEquals(separateCopy with { Name = "Боб" }));
        Assert.IsFalse(character.ContentEquals(separateCopy with { DefaultStateId = Guid.NewGuid() }));
        Assert.IsFalse(character.ContentEquals(separateCopy with
        {
            States = [separateCopy.States[0] with { Name = "Грустит" }],
        }));
        Assert.IsFalse(character.ContentEquals(separateCopy with
        {
            States = [separateCopy.States[0] with { ImageId = Guid.NewGuid() }],
        }));
        Assert.IsFalse(character.ContentEquals(separateCopy with
        {
            States = [separateCopy.States[0] with { Id = Guid.NewGuid() }],
        }));
    }
}
