using System.Collections.ObjectModel;
using NovellMaker.Core.Characters;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private CharacterLibraryDocumentViewModel? characterLibrary;
    private CharacterViewModel? selectedCharacter;
    private CharacterStateViewModel? selectedState;

    public ObservableCollection<CharacterViewModel> Characters { get; } = [];
    public AsyncCommand AddCharacterCommand { get; private set; } = null!;
    public AsyncCommand AddCharacterStateCommand { get; private set; } = null!;
    public AsyncCommand DeleteCharacterCommand { get; private set; } = null!;
    public AsyncCommand DeleteCharacterStateCommand { get; private set; } = null!;
    public AsyncCommand OpenCharacterLibraryCommand { get; private set; } = null!;

    public string CharacterLibraryTitle => "Персонажи" + (CharactersDifferFromSaved() ? " *" : "");

    public CharacterViewModel? SelectedCharacter
    {
        get => selectedCharacter;
        set
        {
            if (ReferenceEquals(selectedCharacter, value)) return;
            selectedCharacter = value;
            EndEditGroup();
            SelectedState = value is null
                ? null
                : value.States.FirstOrDefault(state => state.Id == value.DefaultStateId) ?? value.States.FirstOrDefault();
            NotifyCharacterSelection();
        }
    }

    public CharacterStateViewModel? SelectedState
    {
        get => selectedState;
        set
        {
            if (ReferenceEquals(selectedState, value)) return;
            if (value is not null && (selectedCharacter is null || !selectedCharacter.States.Contains(value))) return;
            selectedState = value;
            EndEditGroup();
            NotifyCharacterStateSelection();
        }
    }

    public string SelectedCharacterName
    {
        get => selectedCharacter?.Name ?? "";
        set
        {
            value ??= "";
            if (!CanEdit || project is null || selectedCharacter is null || selectedCharacter.Name == value) return;
            var index = FindCharacterIndex(selectedCharacter.Id);
            if (index >= 0)
            {
                EditProject(project with
                {
                    Characters = project.Characters.SetItem(index, selectedCharacter.Model with { Name = value })
                }, $"character-name:{selectedCharacter.Id}");
            }
        }
    }

    public string SelectedStateName
    {
        get => selectedState?.Name ?? "";
        set
        {
            value ??= "";
            if (!CanEditState || project is null || selectedCharacter is null || selectedState is null || selectedState.Name == value) return;
            UpdateSelectedCharacter(selectedCharacter.Model with
            {
                States = selectedCharacter.Model.States.SetItem(
                    FindStateIndex(selectedCharacter.Model, selectedState.Id),
                    selectedState.Model with { Name = value })
            }, $"character-state-name:{selectedState.Id}");
        }
    }

    public ImageAssetViewModel? SelectedStateImage
    {
        get => selectedState is null ? null : Images.FirstOrDefault(image => image.Id == selectedState.ImageId);
        set
        {
            // ComboBox can publish a transient null while its item catalog is being refreshed.
            if (value is null || !CanEditState || selectedCharacter is null || selectedState is null || value.Id == selectedState.ImageId) return;
            UpdateSelectedCharacter(selectedCharacter.Model with
            {
                States = selectedCharacter.Model.States.SetItem(
                    FindStateIndex(selectedCharacter.Model, selectedState.Id),
                    selectedState.Model with { ImageId = value.Id })
            });
        }
    }

    public Guid? SelectedStateImageId => selectedState?.ImageId;

    public CharacterStateViewModel? SelectedDefaultState
    {
        get => selectedCharacter is null ? null :
            selectedCharacter.States.FirstOrDefault(state => state.Id == selectedCharacter.DefaultStateId);
        set
        {
            // Default state is required once states exist; ignore selection churn caused by a refresh.
            if (value is null || !CanEditCharacter || selectedCharacter is null ||
                !selectedCharacter.States.Contains(value) || selectedCharacter.DefaultStateId == value.Id) return;
            UpdateSelectedCharacter(selectedCharacter.Model with { DefaultStateId = value.Id });
        }
    }

    public Guid? SelectedDefaultStateId => selectedCharacter?.DefaultStateId;
    public bool CanEditCharacter => CanEdit && selectedCharacter is not null;
    public bool CanEditState => CanEditCharacter && selectedState is not null;
    public bool CanAddCharacterState => CanEditCharacter && Images.Count > 0;
    public string CharacterValidation => selectedCharacter is not null && string.IsNullOrWhiteSpace(selectedCharacter.Name)
        ? "Введите имя персонажа."
        : "";
    public string CharacterStateValidation => selectedState is not null && string.IsNullOrWhiteSpace(selectedState.Name)
        ? "Введите название состояния."
        : "";
    public string CharacterStateStatus => selectedState?.Status ?? "";
    public string CharacterStateInfo => selectedState is null
        ? "Выберите состояние персонажа."
        : $"Состояние: {selectedState.Id}\nИзображение: {selectedState.ImageId}";
    public string AddCharacterStateHint => Images.Count == 0
        ? "Сначала импортируйте PNG или JPEG в библиотеку изображений."
        : "Состояние использует выбранное импортированное изображение.";

    private void InitializeCharacters()
    {
        AddCharacterCommand = Command(AddCharacter, () => CanEdit);
        AddCharacterStateCommand = Command(AddCharacterState, () => CanAddCharacterState);
        DeleteCharacterCommand = new(() => selectedCharacter is { } c ? DeleteCharacterAsync(c.Id) : Task.CompletedTask, () => CanEditCharacter);
        DeleteCharacterStateCommand = new(() => selectedCharacter is { } c && selectedState is { } s ? DeleteStateAsync(c.Id, s.Id) : Task.CompletedTask, () => CanEditState);
        OpenCharacterLibraryCommand = Command(OpenCharacterLibrary, () => CanEdit);
    }

    public void AddCharacter()
    {
        if (!CanEdit || project is null) return;
        var number = 1;
        while (project.Characters.Any(character => character.Name == $"Персонаж {number}")) number++;
        var character = NovelCharacter.Create($"Персонаж {number}");
        EditProject(project with { Characters = project.Characters.Add(character) });
        OpenCharacterLibrary();
        SelectedCharacter = Characters.First(item => item.Id == character.Id);
    }

    public void AddCharacterState()
    {
        if (!CanAddCharacterState || project is null || selectedCharacter is null) return;
        var number = 1;
        var name = "default";
        while (selectedCharacter.Model.States.Any(state => state.Name == name)) name = $"state-{++number}";
        var state = new CharacterState(Guid.NewGuid(), name, Images[0].Id);
        var updated = selectedCharacter.Model with
        {
            States = selectedCharacter.Model.States.Add(state),
            DefaultStateId = selectedCharacter.Model.DefaultStateId ?? state.Id
        };
        UpdateSelectedCharacter(updated);
        SelectedState = selectedCharacter?.States.First(item => item.Id == state.Id);
    }

    public void DeleteSelectedCharacterState()
    {
        if (!CanEditState || selectedCharacter is null || selectedState is null) return;
        var states = selectedCharacter.Model.States.RemoveAt(FindStateIndex(selectedCharacter.Model, selectedState.Id));
        var updated = selectedCharacter.Model with
        {
            States = states,
            DefaultStateId = selectedCharacter.DefaultStateId == selectedState.Id ? states.FirstOrDefault()?.Id : selectedCharacter.DefaultStateId
        };
        UpdateSelectedCharacter(updated);
    }

    public void OpenCharacterLibrary()
    {
        if (!HasProject) return;
        characterLibrary ??= new CharacterLibraryDocumentViewModel(this);
        if (!Documents.Contains(characterLibrary)) Documents.Add(characterLibrary);
        SelectedDocument = characterLibrary;
    }

    public void SelectCharacter(Guid characterId)
    {
        OpenCharacterLibrary();
        SelectedCharacter = Characters.FirstOrDefault(character => character.Id == characterId);
    }

    public void CloseCharacterLibrary()
    {
        if (IsBusy || characterLibrary is null) return;
        Documents.Remove(characterLibrary);
        if (ReferenceEquals(SelectedDocument, characterLibrary)) SelectedDocument = Documents.FirstOrDefault();
        characterLibrary = null;
    }

    private void ResetCharacters()
    {
        characterLibrary = null;
        selectedCharacter = null;
        selectedState = null;
        Characters.Clear();
    }

    private void NotifyCharactersChanged()
    {
        var models = project?.Characters ?? [];
        var selectedCharacterId = selectedCharacter?.Id;
        var selectedStateId = selectedState?.Id;
        var rows = Characters.ToDictionary(character => character.Id);
        var ids = models.Select(character => character.Id).ToHashSet();
        for (var index = Characters.Count - 1; index >= 0; index--)
        {
            if (!ids.Contains(Characters[index].Id)) Characters.RemoveAt(index);
        }

        var images = (IReadOnlyCollection<NovellMaker.Core.Assets.ImageAsset>)(project?.Images.ToArray() ?? []);
        for (var index = 0; index < models.Length; index++)
        {
            var model = models[index];
            if (!rows.TryGetValue(model.Id, out var row))
            {
                row = new CharacterViewModel(model);
                Characters.Insert(index, row);
            }
            else if (!ReferenceEquals(Characters[index], row))
            {
                Characters.Move(Characters.IndexOf(row), index);
            }
            row.Refresh(model, images, ProjectDirectory, AssetRevision);
        }

        selectedCharacter = selectedCharacterId is null ? selectedCharacter : Characters.FirstOrDefault(character => character.Id == selectedCharacterId);
        if (selectedCharacter is null)
        {
            selectedState = null;
        }
        else
        {
            selectedState = selectedStateId is null
                ? selectedState is null ? null : selectedCharacter.States.FirstOrDefault()
                : selectedCharacter.States.FirstOrDefault(state => state.Id == selectedStateId);
        }

        characterLibrary?.RefreshHeader();
        NotifyCharacterSelection();
        NotifyCharacterStateSelection();
        NotifyCharacterCommands();
    }

    private void UpdateSelectedCharacter(NovelCharacter updated, string? group = null)
    {
        if (project is null) return;
        var index = FindCharacterIndex(updated.Id);
        if (index >= 0) EditProject(project with { Characters = project.Characters.SetItem(index, updated) }, group);
    }

    private int FindCharacterIndex(Guid id)
    {
        if (project is null) return -1;
        for (var index = 0; index < project.Characters.Length; index++)
            if (project.Characters[index].Id == id) return index;
        return -1;
    }

    private static int FindStateIndex(NovelCharacter character, Guid id)
    {
        for (var index = 0; index < character.States.Length; index++)
            if (character.States[index].Id == id) return index;
        return -1;
    }

    private bool CharactersDifferFromSaved()
    {
        if (project is null) return false;
        if (savedProject is null) return project.Characters.Length > 0;
        if (project.Characters.Length != savedProject.Characters.Length) return true;
        for (var index = 0; index < project.Characters.Length; index++)
            if (!project.Characters[index].ContentEquals(savedProject.Characters[index])) return true;
        return false;
    }

    private void NotifyCharacterSelection()
    {
        foreach (var property in new[]
                 {
                     nameof(SelectedCharacter), nameof(SelectedCharacterName), nameof(SelectedDefaultState), nameof(SelectedDefaultStateId),
                     nameof(CanEditCharacter), nameof(CanAddCharacterState), nameof(CharacterValidation), nameof(AddCharacterStateHint)
                 }) Changed(property);
        NotifyCharacterCommands();
    }

    private void NotifyCharacterStateSelection()
    {
        foreach (var property in new[]
                 {
                     nameof(SelectedState), nameof(SelectedStateName), nameof(SelectedStateImage), nameof(SelectedStateImageId),
                     nameof(CanEditState), nameof(CharacterStateValidation), nameof(CharacterStateStatus), nameof(CharacterStateInfo)
                 }) Changed(property);
        NotifyCharacterCommands();
    }

    private void NotifyCharacterCommands()
    {
        AddCharacterCommand?.NotifyCanExecuteChanged();
        AddCharacterStateCommand?.NotifyCanExecuteChanged();
        DeleteCharacterCommand?.NotifyCanExecuteChanged();
        DeleteCharacterStateCommand?.NotifyCanExecuteChanged();
        OpenCharacterLibraryCommand?.NotifyCanExecuteChanged();
        characterLibrary?.CloseCommand.NotifyCanExecuteChanged();
        foreach (var property in new[] { nameof(CanEditCharacter), nameof(CanEditState), nameof(CanAddCharacterState), nameof(AddCharacterStateHint) }) Changed(property);
    }
}
