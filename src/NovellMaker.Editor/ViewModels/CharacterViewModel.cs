using System.Collections.ObjectModel;
using System.ComponentModel;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;

namespace NovellMaker.Editor.ViewModels;

public sealed class CharacterViewModel(NovelCharacter model) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelCharacter Model { get; private set; } = model;
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public Guid? DefaultStateId => Model.DefaultStateId;
    public ObservableCollection<CharacterStateViewModel> States { get; } = [];
    public string StateSummary => States.Count == 1 ? "1 состояние" : $"Состояний: {States.Count}";

    internal void Refresh(NovelCharacter value, IReadOnlyCollection<ImageAsset> images, string? projectDirectory, int revision)
    {
        var modelChanged = !ReferenceEquals(Model, value);
        var previousCount = States.Count;
        Model = value;
        var rows = States.ToDictionary(state => state.Id);
        var ids = value.States.Select(state => state.Id).ToHashSet();
        for (var index = States.Count - 1; index >= 0; index--)
        {
            if (!ids.Contains(States[index].Id)) States.RemoveAt(index);
        }

        for (var index = 0; index < value.States.Length; index++)
        {
            var state = value.States[index];
            if (!rows.TryGetValue(state.Id, out var row))
            {
                row = new CharacterStateViewModel(state);
                States.Insert(index, row);
            }
            else if (!ReferenceEquals(States[index], row))
            {
                States.Move(States.IndexOf(row), index);
            }

            row.Refresh(state, images, projectDirectory, revision);
        }

        if (modelChanged || previousCount != States.Count) PropertyChanged?.Invoke(this, new(null));
    }
}
