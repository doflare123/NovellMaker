using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

public sealed class StartPage : UserControl
{
    public StartPage()
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 650, Margin = new Thickness(36), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "NovellMaker", FontSize = 28 });
        panel.Children.Add(new TextBlock { Text = "Создайте новеллу или продолжите работу над проектом.", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel();
        void Button(string name, string label, string command)
        {
            var button = new Button { Name = name, Content = label, Margin = new Thickness(0, 0, 8, 8) };
            button.Bind(Avalonia.Controls.Button.CommandProperty, new Binding(command)); buttons.Children.Add(button);
        }
        Button("StartNewProjectButton", "Создать проект", "NewCommand");
        Button("StartOpenProjectButton", "Открыть проект…", "OpenCommand");
        Button("OpenExampleButton", "Открыть проверочный фрагмент", "OpenExampleCommand");
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Последние проекты", FontSize = 18 });
        var recent = new ItemsControl { Name = "RecentProjectsList" };
        recent.Bind(ItemsControl.ItemsSourceProperty, new Binding("RecentProjects"));
        recent.ItemTemplate = new FuncDataTemplate<RecentProject>((entry, _) =>
        {
            var content = new StackPanel { Spacing = 3, Children =
            { new TextBlock { Text = entry?.Name, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = entry?.Path, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis } } };
            var open = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            ToolTip.SetTip(open, entry?.Path);
            open.Click += async (_, _) => { if (entry is not null && DataContext is EditorSession session) await session.OpenRecentProjectAsync(entry.Path); };
            return open;
        });
        panel.Children.Add(recent);
        Content = new ScrollViewer { Content = panel };
    }
}
