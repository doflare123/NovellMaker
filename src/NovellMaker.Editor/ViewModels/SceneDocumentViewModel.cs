using System.Collections.ObjectModel;
using System.Collections.Immutable;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class SceneDocumentViewModel : DocumentViewModel
{
    private NovelScene scene;
    private DialogueLineViewModel? selectedLine;
    private string editingLanguage;
    private DialogueStyle style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss);
    private string cssError = "";
    private string? sampledCss;
    private int selectedSection = 6;

    // Transient authoring UI state; docking or switching documents must not reset it.
    public int SelectedSection
    {
        get => selectedSection;
        set
        {
            if (value is < 0 or > 6 || selectedSection == value) return;
            var wasCanvas = selectedSection == 6;
            selectedSection = value;
            Changed();
            if (Session.ActiveScene == this && (wasCanvas || value == 6))
            {
                if (value == 6) Session.SelectScreen(Id);
                else Session.InspectPhrase();
                Session.NotifySelectionChanged();
            }
        }
    }

    internal SceneDocumentViewModel(EditorSession session, NovelScene scene)
    {
        Session = session;
        this.scene = scene;
        editingLanguage = session.DefaultLanguage;
        AddLineCommand = EditorSession.Command(AddLine, () => Session.CanEdit && IsEditingLanguageValid);
        DeleteLineCommand = new(ConfirmDeleteLineAsync, () => CanEditLine);
        DuplicateLineCommand = EditorSession.Command(DuplicateLine, () => CanEditLine);
        MoveUpCommand = EditorSession.Command(() => MoveLine(-1), () => CanEditLine && CanPrevious);
        MoveDownCommand = EditorSession.Command(() => MoveLine(1), () => CanEditLine && CanNext);
        PreviousLineCommand = EditorSession.Command(() => Navigate(-1), () => Session.IsIdle && CanPrevious);
        NextLineCommand = EditorSession.Command(() => Navigate(1), () => Session.IsIdle && CanNext);
        CloseCommand = EditorSession.Command(() => Session.CloseDocument(this), () => Session.IsIdle);
        OpenPhraseScriptCommand = EditorSession.Command(Session.InspectPhrase, () => Session.IsIdle && SelectedLine is not null);
        Synchronize(scene);
    }

    public EditorSession Session { get; }
    public Guid Id => scene.Id;
    public override bool CanClose => true;
    public override AsyncCommand CloseCommand { get; }
    public override string Header => (string.IsNullOrWhiteSpace(Name) ? "Сцена без названия" : Name) + (IsModified ? " *" : "");
    public bool IsModified => !scene.ContentEquals(Session.SavedScene(Id)) || Session.IsAudioModified;
    public ObservableCollection<DialogueLineViewModel> Lines { get; } = [];
    public AsyncCommand AddLineCommand { get; }
    public AsyncCommand DeleteLineCommand { get; }
    public AsyncCommand DuplicateLineCommand { get; }
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand PreviousLineCommand { get; }
    public AsyncCommand NextLineCommand { get; }
    public AsyncCommand OpenPhraseScriptCommand { get; }
    public string SelectedPhraseScriptSummary => SelectedLine?.ScriptSummary ?? "Выберите фразу.";
    public bool CanEditLine => Session.CanEdit && SelectedLine is not null && IsEditingLanguageValid;
    public bool CanPrevious => SelectedIndex > 0;
    public bool CanNext => SelectedIndex >= 0 && SelectedIndex < Lines.Count - 1;
    private int SelectedIndex => SelectedLine is null ? -1 : Lines.IndexOf(SelectedLine);
    public string NavigationPosition => SelectedIndex >= 0 ? $"{SelectedIndex + 1} / {Lines.Count}" : $"0 / {Lines.Count}";
    public string SelectedTextKey => SelectedLine?.TextKey ?? "—";
    public bool IsEditingLanguageValid => !string.IsNullOrWhiteSpace(editingLanguage);
    public string LanguageValidation => IsEditingLanguageValid ? "" : "Укажите язык текста.";
    public bool IsNameValid => !string.IsNullOrWhiteSpace(Name);
    public string NameValidation => string.IsNullOrWhiteSpace(Name) ? "Введите название сцены." : "";

    public string Name
    {
        get => scene.Name;
        set
        {
            value ??= "";
            if (value != Name) Session.UpdateScene(scene with { Name = value }, $"scene-name:{Id}");
        }
    }

    public DialogueLineViewModel? SelectedLine
    {
        get => selectedLine;
        set
        {
            if (ReferenceEquals(selectedLine, value)) return;
            selectedLine = value;
            Session.EndEditGroup();
            RefreshLineSelection();
            if (Session.ActiveScene?.Id == Id) Session.InspectPhrase();
        }
    }

    public string EditingLanguage
    {
        get => editingLanguage;
        set
        {
            value ??= "";
            if (value == editingLanguage) return;
            editingLanguage = value;
            Session.EndEditGroup();
            foreach (var row in Lines) row.Refresh(row.Model, row.Number, value);
            Changed();
            Changed(nameof(IsEditingLanguageValid));
            Changed(nameof(LanguageValidation));
            RefreshLineSelection();
        }
    }

    public string LineText
    {
        get => SelectedLine?.Model.Texts.GetValueOrDefault(editingLanguage, "") ?? "";
        set
        {
            value ??= "";
            if (SelectedLine is not { } selected || !IsEditingLanguageValid || value == LineText) return;
            var index = SelectedIndex;
            var updated = selected.Model with { Texts = selected.Model.Texts.SetItem(editingLanguage, value) };
            Session.UpdateScene(scene with { Lines = scene.Lines.SetItem(index, updated) }, $"line-text:{updated.Id}:{editingLanguage}");
        }
    }

    public bool UseSceneStyle
    {
        get => scene.DialogueCssOverride is not null;
        set
        {
            if (value != UseSceneStyle)
                Session.UpdateScene(scene with { DialogueCssOverride = value ? Session.DialogueCss : null });
        }
    }

    public string Css
    {
        get => scene.DialogueCssOverride ?? Session.DialogueCss;
        set
        {
            value ??= "";
            if (UseSceneStyle && value != Css)
                Session.UpdateScene(scene with { DialogueCssOverride = value }, $"scene-css:{Id}");
        }
    }

    public DialogueStyle? Style => style;
    public string CssError => cssError;
    public bool HasCssError => cssError.Length > 0;
    public bool CanEditCss => Session.CanEdit && UseSceneStyle;
    public string SampleText => SelectedLine is null ? "Добавьте реплику, чтобы проверить оформление текста." :
        string.IsNullOrEmpty(LineText) ? "У выбранной реплики нет текста на этом языке." : LineText;

    public void SelectLine(Guid? id) => SelectedLine = Lines.FirstOrDefault(line => line.Id == id) ?? Lines.FirstOrDefault();

    public void AddLine()
    {
        if (!Session.CanEdit || !IsEditingLanguageValid) return;
        var line = DialogueLine.Create(editingLanguage);
        var position = SelectedIndex < 0 ? scene.Lines.Length : SelectedIndex + 1;
        var canvas = scene.Canvas;
        if (scene.Lines.IsEmpty && !canvas.Nodes.Any(n => n.Dialogue != NovellMaker.Core.Screens.DialogueBinding.None))
        {
            var nodes = DialogueCanvas.Add(canvas.Nodes, scene.Id, style, legacy: true);
            try { _ = new NovellMaker.Core.Screens.ScreenDefinition(scene.Id, nodes); }
            catch (ArgumentException error) { Session.ReportScreenError("Фраза и диалог не добавлены: " + error.Message); return; }
            canvas = canvas with { Nodes = nodes };
        }
        Session.UpdateScene(scene with { Lines = scene.Lines.Insert(position, line), Canvas = canvas });
        SelectLine(line.Id);
    }

    public Task ConfirmDeleteLineAsync()
    {
        var id = SelectedLine?.Id;
        return Session.ConfirmEditorDeletionAsync("Реплика", () => { if (SelectedLine?.Id == id) DeleteLineCore(); });
    }

    private void DeleteLineCore()
    {
        var index = SelectedIndex;
        if (index < 0) return;
        Session.UpdateScene(scene with { Lines = scene.Lines.RemoveAt(index) });
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
    }

    public void DeleteLine()
    {
        if (!CanEditLine) return;
        var index = SelectedIndex;
        Session.UpdateScene(scene with { Lines = scene.Lines.RemoveAt(index) });
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
    }

    public void DuplicateLine()
    {
        if (!CanEditLine || SelectedLine is null) return;
        var source = SelectedLine.Model;
        var copy = DialogueLine.Create(editingLanguage) with
        {
            Texts = source.Texts, SpeakerId = source.SpeakerId,
            CharacterStateId = source.CharacterStateId, HideCharacterId = source.HideCharacterId, Placements = source.Placements
        };
        var oldEntry = NovellMaker.Core.Execution.ScenarioProgram.LineEntryId(source.Id);
        var newEntry = NovellMaker.Core.Execution.ScenarioProgram.LineEntryId(copy.Id);
        NovellMaker.Core.Execution.ScenarioAction Clone(NovellMaker.Core.Execution.ScenarioAction action)
        {
            var result = action with { Id = Guid.NewGuid() };
            return result switch
            {
                NovellMaker.Core.Execution.JumpAction j when j.SceneId == scene.Id && j.ActionId == oldEntry => j with { ActionId = newEntry },
                NovellMaker.Core.Execution.BranchAction b when b.SceneId == scene.Id && b.ActionId == oldEntry => b with { ActionId = newEntry },
                NovellMaker.Core.Execution.ChoiceAction c => c with { Options = c.Options.Select(o => o with { Id = Guid.NewGuid(), TargetActionId = o.TargetSceneId == scene.Id && o.TargetActionId == oldEntry ? newEntry : o.TargetActionId }).ToImmutableArray() },
                NovellMaker.Core.Execution.ScriptAction s => s with { References = s.References.ToImmutableDictionary(p => p.Key, p => p.Value == source.Id ? copy.Id : p.Value) },
                _ => result,
            };
        }
        var blocks = scene.ScriptBlocks.Where(b => b.LineId == source.Id).Select(b => b with { LineId = copy.Id, Action = Clone(b.Action) });
        var audio = Audio;
        if (audio.Voices.TryGetValue(source.Id, out var voices)) audio = audio with { Voices = audio.Voices.SetItem(copy.Id, voices) };
        if (audio.LineEffects.TryGetValue(source.Id, out var effect)) audio = audio with { LineEffects = audio.LineEffects.SetItem(copy.Id, effect) };
        Session.UpdateScene(scene with { Lines = scene.Lines.Insert(SelectedIndex + 1, copy), ScriptBlocks = scene.ScriptBlocks.AddRange(blocks) }, audio: audio);
        SelectLine(copy.Id);
    }

    public void MoveLine(int delta)
    {
        if (!CanEditLine || SelectedLine is null || delta is not (-1 or 1)) return;
        var index = SelectedIndex;
        var target = index + delta;
        if (target < 0 || target >= scene.Lines.Length) return;
        var line = SelectedLine.Model;
        Session.UpdateScene(scene with { Lines = scene.Lines.RemoveAt(index).Insert(target, line) });
        SelectLine(line.Id);
    }

    public void Navigate(int direction)
    {
        if (!Session.IsIdle || direction is not (-1 or 1)) return;
        var target = SelectedIndex + direction;
        if (SelectedIndex >= 0 && target >= 0 && target < Lines.Count) SelectedLine = Lines[target];
    }

    internal void Synchronize(NovelScene updated)
    {
        var unchanged = ReferenceEquals(scene, updated) && sampledCss == Css;
        scene = updated;
        RefreshBackground();
        RefreshAudio();
        if (unchanged)
        {
            RefreshCharacterBindings();
            RefreshPlayback();
            RefreshHeader();
            return;
        }
        var lineIds = scene.Lines.Select(line => line.Id).ToHashSet();
        var rowsById = Lines.ToDictionary(line => line.Id);
        for (var index = Lines.Count - 1; index >= 0; index--)
            if (!lineIds.Contains(Lines[index].Id)) Lines.RemoveAt(index);
        for (var index = 0; index < scene.Lines.Length; index++)
        {
            var model = scene.Lines[index];
            if (!rowsById.TryGetValue(model.Id, out var row))
            {
                row = new DialogueLineViewModel(model);
                Lines.Insert(index, row);
            }
            else if (!ReferenceEquals(Lines[index], row)) Lines.Move(Lines.IndexOf(row), index);
            row.Refresh(model, index + 1, editingLanguage);
            row.RefreshScript(scene.ScriptBlocks.Where(b => b.LineId == model.Id).ToArray());
        }
        if (selectedLine is null || !Lines.Contains(selectedLine)) SelectedLine = Lines.FirstOrDefault();
        if (sampledCss != Css)
        {
            sampledCss = Css;
            try { style = DialogueCssParser.Parse(Css); cssError = ""; }
            catch (DialogueCssException error) { cssError = error.Message; }
        }
        foreach (var property in new[] { nameof(Name), nameof(NameValidation), nameof(IsNameValid), nameof(IsModified), nameof(UseSceneStyle), nameof(Css),
                     nameof(Style), nameof(CssError), nameof(HasCssError), nameof(CanEditCss) }) Changed(property);
        RefreshLineSelection();
        RefreshHeader();
    }

    private void RefreshLineSelection()
    {
        RefreshCharacterBindings();
        RefreshPlayback();
        RefreshAudio();
        foreach (var property in new[] { nameof(SelectedLine), nameof(LineText), nameof(SelectedTextKey), nameof(SampleText),
                     nameof(CanEditLine), nameof(CanPrevious), nameof(CanNext), nameof(NavigationPosition), nameof(SelectedPhraseScriptSummary) }) Changed(property);
        Session.NotifySelectionChanged();
        NotifyCommands();
    }

    internal void NotifyCommands()
    {
        foreach (var property in new[] { nameof(CanEditLine), nameof(CanEditCss), nameof(CanChooseState) }) Changed(property);
        foreach (var command in new[] { AddLineCommand, DeleteLineCommand, DuplicateLineCommand, MoveUpCommand, MoveDownCommand,
                     PreviousLineCommand, NextLineCommand, CloseCommand, OpenPhraseScriptCommand }) command?.NotifyCanExecuteChanged();
    }
}

public sealed class DialogueLineViewModel(DialogueLine model) : System.ComponentModel.INotifyPropertyChanged
{
    private string? lastLanguage;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public DialogueLine Model { get; private set; } = model;
    public Guid Id => Model.Id;
    public string TextKey => Model.TextKey;
    public int Number { get; private set; }
    public string Label { get; private set; } = "";
    public string ScriptSummary { get; private set; } = "Без дополнительных блоков";
    public bool HasScript { get; private set; }

    internal void RefreshScript(ScriptBlock[] blocks)
    {
        HasScript = blocks.Length > 0;
        ScriptSummary = HasScript ? string.Join(" · ", blocks.GroupBy(b => b.Phase).OrderBy(g => g.Key).Select(g =>
            (g.Key switch { ScriptPhase.BeforeLine => "До", ScriptPhase.DuringLine => "Во время", _ => "После" }) + ": " + g.Count())) : "Без дополнительных блоков";
        PropertyChanged?.Invoke(this, new(nameof(ScriptSummary)));
        PropertyChanged?.Invoke(this, new(nameof(HasScript)));
    }

    internal void Refresh(DialogueLine line, int number, string language)
    {
        if (ReferenceEquals(Model, line) && Number == number && lastLanguage == language) return;
        Model = line;
        lastLanguage = language;
        Number = number;
        var text = line.Texts.GetValueOrDefault(language, "").Replace('\r', ' ').Replace('\n', ' ');
        Label = $"{number}. {(text.Length == 0 ? "Пустая реплика" : text.Length > 64 ? text[..64] + "…" : text)}";
        PropertyChanged?.Invoke(this, new(null));
    }
}
