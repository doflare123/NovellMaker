using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

/// <summary>Resource configuration belongs to the scene and the ordinary project history.</summary>
public sealed class MinigamePropertiesView : UserControl
{
    private EditorSession? session;
    private readonly CheckBox enabled = new() { Name = "MinigameEnabled", Content = "Использовать сцену как мини-игру" };
    private readonly CheckBox canSave = new() { Name = "MinigameCanSave", Content = "Разрешить сохранение между обработчиками" };
    private readonly TextBox parameters = new() { Name = "MinigameParameters", AcceptsReturn = true, MinHeight = 65 };
    private readonly TextBox state = new() { Name = "MinigameInitialState", AcceptsReturn = true, MinHeight = 65 };
    private readonly TextBox parameterFields = new() { Name = "MinigameParameterFields" };
    private readonly TextBox stateFields = new() { Name = "MinigameStateFields" };
    private readonly TextBox resultFields = new() { Name = "MinigameResultFields" };
    private readonly TextBox reason = new() { Name = "MinigameSaveReason" };
    private readonly NumericUpDown rate = new() { Name = "MinigameUpdateRate", Minimum = 1, Maximum = 20, Increment = 1 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SandyBrown };
    public MinigamePropertiesView()
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(enabled);
        void Field(string caption, Control input) { body.Children.Add(new TextBlock { Text = caption }); body.Children.Add(input); }
        Field("Параметры по умолчанию (JSON)", parameters); Field("Поля параметров", parameterFields);
        Field("Начальное состояние (JSON)", state); Field("Поля состояния", stateFields); Field("Поля результата", resultFields);
        ToolTip.SetTip(parameterFields, "Обязательные поля: seed:Number. Разделитель — запятая. Типы: Null, Boolean, Number, String, List, Object. Пусто — любой ограниченный объект.");
        ToolTip.SetTip(stateFields, ToolTip.GetTip(parameterFields)); ToolTip.SetTip(resultFields, ToolTip.GetTip(parameterFields));
        Field("Обновлений в секунду", rate); body.Children.Add(canSave); Field("Причина запрета сохранения", reason);
        var apply = new Button { Name = "ApplyMinigameResource", Content = "Применить мини-игру", HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += (_, _) => Apply(); body.Children.Add(apply); body.Children.Add(error); Content = body;
        DataContextChanged += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); };
        AttachedToVisualTree += (_, _) => { Subscribe(); Refresh(); }; DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private void Subscribe() { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } }
    private void Unsubscribe() { if (session is not null) session.PropertyChanged -= Changed; }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(EditorSession.SelectedScreen)) Refresh(); }
    private void Refresh()
    {
        var definition = session?.SelectedScreen?.Minigame ?? new();
        enabled.IsChecked = session?.SelectedScreen?.Minigame is not null;
        parameters.Text = definition.DefaultParameters.ToJson(); state.Text = definition.InitialState.ToJson();
        string Fields(MinigameSchema schema) => string.Join(", ", schema.Fields.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
        parameterFields.Text = Fields(definition.ParametersSchema); stateFields.Text = Fields(definition.StateSchema); resultFields.Text = Fields(definition.ResultSchema);
        canSave.IsChecked = definition.CanSave; reason.Text = definition.SaveDisabledReason; rate.Value = (decimal)definition.UpdatesPerSecond;
    }
    private static MinigameSchema Schema(string? text, MinigameSchema original)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, LuaValueKind>();
        foreach (var item in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !Enum.TryParse<LuaValueKind>(pair[1], true, out var kind) || !fields.TryAdd(pair[0], kind))
                throw new ArgumentException("Схема: укажите уникальные поля имя:Тип через запятую.");
        }
        return original with { Fields = fields.ToImmutable() };
    }
    private void Apply()
    {
        try
        {
            if (session?.SelectedScreen is not { } screen) return;
            var original = screen.Minigame ?? new MinigameDefinition();
            MinigameDefinition? definition = enabled.IsChecked != true ? null : original with
            {
                DefaultParameters = LuaValue.FromJson(parameters.Text ?? ""), InitialState = LuaValue.FromJson(state.Text ?? ""),
                ParametersSchema = Schema(parameterFields.Text, original.ParametersSchema), StateSchema = Schema(stateFields.Text, original.StateSchema),
                ResultSchema = Schema(resultFields.Text, original.ResultSchema),
                CanSave = canSave.IsChecked == true, SaveDisabledReason = reason.Text ?? "", UpdatesPerSecond = (double)(rate.Value ?? 5),
            };
            session.UpdateScreen(screen with { Minigame = definition }); error.Text = "";
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { error.Text = exception.Message; }
    }
}
