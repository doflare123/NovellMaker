using System.Collections.Immutable;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class SceneDocumentViewModel
{
    private ImmutableArray<NovelCharacter> speakerCatalog;
    private Guid? stateOwner;
    private bool refreshingCharacters;
    public IReadOnlyList<CharacterOption> SpeakerOptions { get; private set; } = [];
    public IReadOnlyList<CharacterOption> StateOptions { get; private set; } = [];
    public IReadOnlyList<CharacterOption> HideOptions { get; private set; } = [];
    public SceneFrame? SampleFrame { get; private set; }
    public string PlaybackDiagnostics => string.Join("\n", SampleFrame?.Diagnostics ?? []);

    public CharacterOption? SelectedSpeaker
    {
        get => SpeakerOptions.FirstOrDefault(option => option.Id == SelectedLine?.Model.SpeakerId);
        set
        {
            if (!refreshingCharacters && value is not null && CanEditLine && SelectedLine is { } line && value.Id != line.Model.SpeakerId)
                ChangeLine(line.Model with { SpeakerId = value.Id, CharacterStateId = null });
        }
    }
    public CharacterOption? SelectedCharacterState
    {
        get => StateOptions.FirstOrDefault(option => option.Id == SelectedLine?.Model.CharacterStateId);
        set
        {
            if (!refreshingCharacters && value is not null && CanEditLine && SelectedLine is { } line && value.Id != line.Model.CharacterStateId)
                ChangeLine(line.Model with { CharacterStateId = value.Id });
        }
    }
    public CharacterOption? SelectedHiddenCharacter
    {
        get => HideOptions.FirstOrDefault(option => option.Id == SelectedLine?.Model.HideCharacterId);
        set
        {
            if (!refreshingCharacters && value is not null && CanEditLine && SelectedLine is { } line && value.Id != line.Model.HideCharacterId)
                ChangeLine(line.Model with { HideCharacterId = value.Id });
        }
    }
    public bool CanChooseState => CanEditLine && SelectedLine?.Model.SpeakerId is not null;

    private void ChangeLine(DialogueLine line) => Session.UpdateScene(scene with { Lines = scene.Lines.SetItem(SelectedIndex, line) });

    private void RefreshCharacterBindings()
    {
        refreshingCharacters = true;
        try
        {
            var catalog = Session.Project?.Characters ?? [];
            var changed = speakerCatalog != catalog;
            speakerCatalog = catalog;
            var speaker = SelectedLine?.Model.SpeakerId;
            var hidden = SelectedLine?.Model.HideCharacterId;
            var pose = SelectedLine?.Model.CharacterStateId;
            if (changed || NeedsOptions(SpeakerOptions, catalog.Select(item => item.Id), speaker))
            {
                SpeakerOptions = Options("Рассказчик / без персонажа", catalog.Select(item => new CharacterOption(item.Id, item.Name)), speaker);
                Changed(nameof(SpeakerOptions));
            }
            if (changed || NeedsOptions(HideOptions, catalog.Select(item => item.Id), hidden))
            {
                HideOptions = Options("Никого", catalog.Select(item => new CharacterOption(item.Id, item.Name)), hidden);
                Changed(nameof(HideOptions));
            }
            var states = catalog.FirstOrDefault(item => item.Id == speaker)?.States ?? [];
            if (changed || stateOwner != speaker || NeedsOptions(StateOptions, states.Select(item => item.Id), pose))
            {
                stateOwner = speaker;
                StateOptions = Options("Сохранить текущее / исходное", states.Select(item => new CharacterOption(item.Id, item.Name)), pose);
                Changed(nameof(StateOptions));
            }
            foreach (var property in new[] { nameof(SelectedSpeaker), nameof(SelectedCharacterState), nameof(SelectedHiddenCharacter), nameof(CanChooseState) }) Changed(property);
        }
        finally { refreshingCharacters = false; }
    }

    private static bool NeedsOptions(IReadOnlyList<CharacterOption> options, IEnumerable<Guid> ids, Guid? selected)
    {
        var known = ids.ToHashSet();
        return options.Count != known.Count + 1 + (selected.HasValue && !known.Contains(selected.Value) ? 1 : 0) ||
               !options.Any(option => option.Id == selected);
    }

    private static IReadOnlyList<CharacterOption> Options(string empty, IEnumerable<CharacterOption> rows, Guid? selected)
    {
        var result = new List<CharacterOption> { new(null, empty) };
        result.AddRange(rows);
        if (selected.HasValue && !result.Any(option => option.Id == selected)) result.Add(new(selected, $"Отсутствует: {selected}"));
        return result;
    }

    private void RefreshPlayback()
    {
        SampleFrame = null;
        if (IsEditingLanguageValid && Session.Project is { } project)
        {
            // Static author composition does not use external services. Incomplete settings must remain editable.
            project = project with { ReleaseCheck = null };
            if (!scene.HasLegacyActionScript && !scene.ScriptBlocks.IsEmpty)
            {
                // Static composition of the selected phrase. Dynamic Lua/control flow runs through F6.
                var composition = scene with { ScriptBlocks = scene.ScriptBlocks.Where(b => b.Action is ShowAction or HideAction or StateAction or PlaceAction).ToImmutableArray() };
                var player = new ScenarioPlayer(project with { Scenes = project.Scenes.Replace(scene, composition) }, Id, editingLanguage);
                for (var i = 0; i < scene.Lines.Length && player.State.LineId != SelectedLine?.Id; i++)
                    if (!player.Advance()) break;
                SampleFrame = player.Frame;
            }
            else SampleFrame = new ScenePlayer(project, Id, editingLanguage, SelectedLine?.Id).Frame;
        }
        RefreshPlacement();
        Changed(nameof(SampleFrame));
        Changed(nameof(PlaybackDiagnostics));
    }
}

public sealed record CharacterOption(Guid? Id, string Name);
