using System.Collections.ObjectModel;
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

    internal SceneDocumentViewModel(EditorSession session, NovelScene scene)
    {
        Session = session;
        this.scene = scene;
        editingLanguage = session.DefaultLanguage;
        AddLineCommand = EditorSession.Command(AddLine, () => Session.CanEdit && IsEditingLanguageValid);
        DeleteLineCommand = EditorSession.Command(DeleteLine, () => CanEditLine);
        DuplicateLineCommand = EditorSession.Command(DuplicateLine, () => CanEditLine);
        MoveUpCommand = EditorSession.Command(() => MoveLine(-1), () => CanEditLine && CanPrevious);
        MoveDownCommand = EditorSession.Command(() => MoveLine(1), () => CanEditLine && CanNext);
        PreviousLineCommand = EditorSession.Command(() => Navigate(-1), () => Session.IsIdle && CanPrevious);
        NextLineCommand = EditorSession.Command(() => Navigate(1), () => Session.IsIdle && CanNext);
        CloseCommand = EditorSession.Command(() => Session.CloseDocument(this), () => Session.IsIdle);
        Synchronize(scene);
    }

    public EditorSession Session { get; }
    public Guid Id => scene.Id;
    public override bool CanClose => true;
    public override AsyncCommand CloseCommand { get; }
    public override string Header => (string.IsNullOrWhiteSpace(Name) ? "Сцена без названия" : Name) + (IsModified ? " *" : "");
    public bool IsModified => !scene.ContentEquals(Session.SavedScene(Id));
    public ObservableCollection<DialogueLineViewModel> Lines { get; } = [];
    public AsyncCommand AddLineCommand { get; }
    public AsyncCommand DeleteLineCommand { get; }
    public AsyncCommand DuplicateLineCommand { get; }
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand PreviousLineCommand { get; }
    public AsyncCommand NextLineCommand { get; }
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
        Session.UpdateScene(scene with { Lines = scene.Lines.Insert(position, line) });
        SelectLine(line.Id);
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
        var copy = DialogueLine.Create(editingLanguage) with { Texts = SelectedLine.Model.Texts };
        Session.UpdateScene(scene with { Lines = scene.Lines.Insert(SelectedIndex + 1, copy) });
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
        if (unchanged)
        {
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
        foreach (var property in new[] { nameof(SelectedLine), nameof(LineText), nameof(SelectedTextKey), nameof(SampleText),
                     nameof(CanEditLine), nameof(CanPrevious), nameof(CanNext), nameof(NavigationPosition) }) Changed(property);
        Session.NotifySelectionChanged();
        NotifyCommands();
    }

    internal void NotifyCommands()
    {
        foreach (var property in new[] { nameof(CanEditLine), nameof(CanEditCss) }) Changed(property);
        foreach (var command in new[] { AddLineCommand, DeleteLineCommand, DuplicateLineCommand, MoveUpCommand, MoveDownCommand,
                     PreviousLineCommand, NextLineCommand, CloseCommand }) command?.NotifyCanExecuteChanged();
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
