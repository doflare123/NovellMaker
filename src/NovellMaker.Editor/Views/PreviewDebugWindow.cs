using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using NovellMaker.Core.Minigames;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Execution;
using NovellMaker.Editor.Services.Preview;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

internal sealed class PreviewDebugWindow : Window
{
    private sealed record Target(Guid? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly JsonSerializerOptions json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ComboBox points = new() { Name = "SavedDebugPoints", PlaceholderText = "Сохранённые точки", MinWidth = 180 };
    private readonly TextBox name = new() { Name = "DebugPointName", PlaceholderText = "Название точки", Width = 180 };
    private readonly ComboBox target = new() { Name = "DebugStartTarget", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox inputs = new() { AcceptsReturn = true, Height = 100, Name = "DebugInputs", Text = "{}" };
    private readonly TextBox outcome = new() { Name = "DebugOutcome", AcceptsReturn = true, Height = 68, PlaceholderText = "null" };
    private readonly TextBox variables = new() { IsReadOnly = true, AcceptsReturn = true, Height = 100 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DebugPointStore store;
    private readonly EditorSession session;
    private readonly List<Target> targets = [new(null, "Начало сцены")];

    public PreviewDebugWindow(SceneDocumentViewModel document)
    {
        session = document.Session;
        var project = session.Project!;
        var scene = project.Scenes.First(s => s.Id == document.Id);
        store = new(project.Id, scene.Id);
        Title = "Отладка прохождения — " + scene.Name;
        Width = 850; Height = 670; MinWidth = 650; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (scene.HasLegacyActionScript)
            targets.AddRange(scene.Actions.Select((a, i) => new Target(a.Id, $"{i + 1}. {a.GetType().Name} · {a.Id}")));
        else
            targets.AddRange(scene.Lines.Select((line, i) => new Target(ScenarioProgram.LineEntryId(line.Id),
                $"{i + 1}. {line.Texts.GetValueOrDefault(document.EditingLanguage, line.Texts.GetValueOrDefault(project.DefaultLanguage, ""))}")));
        foreach (var block in scene.ScriptBlocks)
        {
            var line = block.LineId is { } id ? $" · реплика {scene.Lines.IndexOf(scene.Lines.First(l => l.Id == id)) + 1}" : "";
            targets.Add(new(block.Action.Id, $"{block.Phase}{line} · {block.Action.GetType().Name} · {block.Action.Id}"));
        }
        target.ItemsSource = targets;
        target.SelectedItem = targets.FirstOrDefault(t => document.SelectedLine is { } line && t.Id == ScenarioProgram.LineEntryId(line.Id)) ?? targets[0];
        var save = new Button { Name = "SaveDebugPoint", Content = "Сохранить точку" };
        var delete = new Button { Name = "DeleteDebugPoint", Content = "Удалить" };
        var run = new Button { Content = "Запустить с параметрами" };
        var copy = new Button { Content = "Взять состояние прохождения" };
        var top = new StackPanel { Spacing = 6, Children = {
            new WrapPanel { Children = { points, name, save, delete } },
            new TextBlock { Text = "Начать с" }, target,
            new TextBlock { Text = "Начальные переменные · JSON, например { \"route\": \"north\" }" }, inputs,
            new TextBlock { Text = "Результат мини-игры · JSON или null" }, outcome,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { run, copy } },
            status, new TextBlock { Text = "Текущие переменные" }, variables,
            new TextBlock { Text = "Журнал прохождения" } } };
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        layout.Children.Add(new ListBox { ItemsSource = session.PreviewJournal });
        Content = layout;
        RefreshVariables();
        session.PropertyChanged += SessionChanged;
        Closed += (_, _) => session.PropertyChanged -= SessionChanged;
        points.SelectionChanged += (_, _) =>
        {
            if (points.SelectedItem is not StoredDebugPoint entry) return;
            if (entry.Point is not { } point)
            {
                name.Text = ""; inputs.Text = "{}"; outcome.Text = "null"; target.SelectedItem = null;
                status.Text = $"Ошибка чтения {entry.FileName}: {entry.Error}";
                return;
            }
            outcome.Text = JsonSerializer.Serialize(point.Start.Outcome, json);
            name.Text = point.Name;
            inputs.Text = JsonSerializer.Serialize(point.Start.Variables, json);
            target.SelectedItem = targets.FirstOrDefault(t => t.Id == point.Start.ActionId);
            status.Text = target.SelectedItem is null ? "Цель удалена. Выберите новую точку входа." : "";
        };
        Try(() => points.ItemsSource = store.Read());
        copy.Click += (_, _) =>
        {
            inputs.Text = variables.Text;
            outcome.Text = JsonSerializer.Serialize(session.PreviewOutcome, json);
        };
        save.Click += (_, _) => Try(() =>
        {
            var label = name.Text?.Trim();
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("Введите название точки.");
            var point = new DebugPoint(label, ReadStart());
            var fileName = store.Save(point);
            var list = store.Read();
            points.ItemsSource = list;
            points.SelectedItem = list.Single(p => p.FileName == fileName);
            status.Text = "Точка сохранена.";
        });
        delete.Click += (_, _) => Try(() =>
        {
            if (points.SelectedItem is not StoredDebugPoint entry) return;
            store.Delete(entry);
            points.ItemsSource = store.Read();
            status.Text = "Точка удалена.";
        });
        run.Click += async (_, _) =>
        {
            PreviewStart? start = null;
            Try(() => start = ReadStart());
            if (start is null) return;
            run.IsEnabled = false;
            try { await session.PreviewSceneAsync(start); }
            finally { run.IsEnabled = true; }
        };
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorSession.PreviewVariables)) RefreshVariables();
    }
    private void RefreshVariables() => variables.Text = JsonSerializer.Serialize(session.PreviewVariables, json);

    private PreviewStart ReadStart()
    {
        if (target.SelectedItem is not Target selected) throw new ArgumentException("Выберите точку входа.");
        var values = JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(inputs.Text ?? "", json);
        if (values is null || !LuaLimits.ValidVariables(values)) throw new ArgumentException("Проверьте имена, строковые значения и размер переменных.");
        var result = string.IsNullOrWhiteSpace(outcome.Text) ? null : JsonSerializer.Deserialize<MinigameOutcome>(outcome.Text, json);
        result?.Validate(session.Project!);
        return new(selected.Id, values, result);
    }

    private void Try(Action operation)
    {
        try { status.Text = ""; operation(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException)
        { status.Text = error.Message; }
    }
}
