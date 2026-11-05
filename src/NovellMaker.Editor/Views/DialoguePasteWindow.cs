using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

public sealed class DialoguePasteWindow : Window
{
    private readonly TextBox input = new() { Name = "DialoguePasteInput", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130, PlaceholderText = "Лена: Ты слышал?\nАнтон: Слышал.\nСтрока без имени — рассказчик." };
    private readonly StackPanel mappings = new() { Spacing = 6 };
    private readonly ListBox preview = new() { Name = "DialoguePastePreview", MinHeight = 130 };
    private readonly Button apply = new() { Name = "ApplyDialoguePaste", Content = "Вставить", IsEnabled = false };
    private readonly Dictionary<string, ComboBox> choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SceneDocumentViewModel document;
    private IReadOnlyList<ParsedDialogueLine> parsed = [];

    public DialoguePasteWindow(SceneDocumentViewModel document)
    {
        this.document = document;
        Title = "Вставить диалог"; Width = 800; Height = 690; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Одна строка — одна реплика. Имя до первого двоеточия сопоставляется один раз для всей вставки.", TextWrapping = TextWrapping.Wrap }, input } };
        var parse = new Button { Name = "ParseDialoguePaste", Content = "Предпросмотр разбора" }; parse.Click += (_, _) => Reparse(); top.Children.Add(parse);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var cancel = new Button { Content = "Отмена", IsCancel = true }; cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, apply } };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = new StackPanel { Spacing = 10, Children = { mappings, new TextBlock { Text = "Предпросмотр", Margin = new Thickness(0, 8, 0, 0) }, preview } } });
        Content = root;
        input.TextChanged += (_, _) => { apply.IsEnabled = false; preview.ItemsSource = null; mappings.Children.Clear(); parsed = []; };
        apply.Click += (_, _) =>
        {
            var lines = parsed.Select(line => new DialoguePasteLine(line.Speaker.Length == 0 ? null : ((CharacterOption)choices[line.Speaker].SelectedItem!).Id, line.Text)).ToArray();
            Close((IReadOnlyList<DialoguePasteLine>)lines);
        };
    }
    private void Reparse()
    {
        var previous = choices.Where(p => p.Value.SelectedItem is CharacterOption).ToDictionary(p => p.Key, p => ((CharacterOption)p.Value.SelectedItem!).Id, StringComparer.OrdinalIgnoreCase);
        choices.Clear(); mappings.Children.Clear(); parsed = DialoguePaste.Parse(input.Text ?? "");
        foreach (var name in parsed.Select(p => p.Speaker).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var options = document.SpeakerOptions;
            var picker = new ComboBox { ItemsSource = options, MinWidth = 240,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CharacterOption>((option, _) => new TextBlock { Text = option?.Name }) };
            if (previous.TryGetValue(name, out var old)) picker.SelectedItem = options.FirstOrDefault(o => o.Id == old);
            else if (DialoguePaste.Match(name, document.Session.Project!.Characters) is { } id) picker.SelectedItem = options.FirstOrDefault(o => o.Id == id);
            else if (name.Equals("Рассказчик", StringComparison.OrdinalIgnoreCase)) picker.SelectedItem = options.FirstOrDefault(o => o.Id is null);
            choices[name] = picker;
            picker.SelectionChanged += (_, _) => RefreshPreview();
            mappings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = name, Width = 200, VerticalAlignment = VerticalAlignment.Center }, picker } });
        }
        RefreshPreview();
    }
    private void RefreshPreview()
    {
        preview.ItemsSource = parsed.Select(line => $"{line.Number}. {(line.Speaker.Length == 0 ? "Рассказчик" : (choices[line.Speaker].SelectedItem as CharacterOption)?.Name ?? "[Сопоставьте: " + line.Speaker + "]")}: {line.Text}").ToArray();
        apply.IsEnabled = parsed.Count > 0 && choices.Values.All(c => c.SelectedItem is CharacterOption);
        apply.Content = $"Вставить {parsed.Count} реплик";
    }
}
