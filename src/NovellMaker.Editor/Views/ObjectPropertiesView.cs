using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

/// <summary>A single docked properties surface; inspecting an object never changes the active document.</summary>
public sealed class ObjectPropertiesView : UserControl
{
    private EditorSession? session;
    private string? key;
    public ObjectPropertiesView()
    {
        DataContextChanged += (_, _) =>
        {
            if (session is not null) session.PropertyChanged -= Changed;
            session = DataContext as EditorSession;
            if (session is not null) session.PropertyChanged += Changed;
            key = null; Refresh();
        };
        AttachedToVisualTree += (_, _) => { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } Refresh(); };
        DetachedFromVisualTree += (_, _) => { if (session is not null) session.PropertyChanged -= Changed; };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSession.InspectorTarget) or nameof(EditorSession.ActiveScene) or nameof(EditorSession.HasProject) or nameof(EditorSession.IsScreenDesigner)) Refresh();
    }
    private void Refresh()
    {
        if (session is null || !session.HasProject) { Content = new TextBlock { Text = "Выберите объект", Margin = new Thickness(10) }; return; }
        if (session.IsScreenDesigner && session.InspectorTarget != InspectorTarget.Script)
        {
            if (Content is not ScreenPropertiesView) Content = new ScreenPropertiesView { DataContext = session };
            key = null; return;
        }
        var scene = session.ActiveScene;
        var nextKey = $"{session.InspectorTarget}/{scene?.Id}/{scene?.SelectedLine?.Id}/{session.InspectedActionId}/{session.SelectedScriptId}";
        if (key == nextKey) return;
        key = nextKey;
        if (session.InspectorTarget == InspectorTarget.Script)
        { Content = new ScriptLibraryView(true) { DataContext = session }; return; }
        if (scene is not null)
        {
            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
            if (session.InspectorTarget != InspectorTarget.Action)
            {
                panel.Children.Add(new TextBlock { Text = "Фраза", FontWeight = FontWeight.SemiBold });
                void Choice(string label, string name, string items, string selected, string enabled)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("95,6,*") };
                    row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                    var picker = new ComboBox { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch };
                    picker.Bind(ItemsControl.ItemsSourceProperty, new Binding(items));
                    picker.Bind(ComboBox.SelectedItemProperty, new Binding(selected) { Mode = BindingMode.TwoWay });
                    picker.Bind(IsEnabledProperty, new Binding(enabled));
                    picker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<NovellMaker.Editor.ViewModels.CharacterOption>((item, _) => new TextBlock { Text = item?.Name });
                    Grid.SetColumn(picker, 2); row.Children.Add(picker); panel.Children.Add(row);
                }
                panel.DataContext = scene;
                Choice("Говорящий", "SpeakerPicker", "SpeakerOptions", "SelectedSpeaker", "CanEditLine");
                Choice("Состояние", "CharacterStatePicker", "StateOptions", "SelectedCharacterState", "CanChooseState");
                Choice("Скрыть", "HideCharacterPicker", "HideOptions", "SelectedHiddenCharacter", "CanEditLine");
                panel.Children.Add(new TextBlock { Text = "События фразы", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            }
            panel.Children.Add(new ScenarioActionsView(true, session.InspectorTarget == InspectorTarget.Action) { DataContext = scene });
            Content = new ScrollViewer { Content = panel };
            return;
        }
        var project = new StackPanel { Margin = new Thickness(10), Spacing = 6, DataContext = session };
        project.Children.Add(new TextBlock { Text = "Проект", FontWeight = FontWeight.SemiBold });
        var nameBox = new TextBox(); nameBox.Bind(TextBox.TextProperty, new Binding("ProjectName") { Mode = BindingMode.TwoWay }); project.Children.Add(nameBox);
        Content = project;
    }
}
