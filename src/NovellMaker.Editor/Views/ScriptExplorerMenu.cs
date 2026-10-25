using Avalonia.Controls;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

internal static class ScriptExplorerMenu
{
    public static ContextMenu Create(EditorSession session, Func<ExplorerEntry?> selected)
    {
        var menu = new ContextMenu();
        var create = new MenuItem { Name = "CreateLuaResourceMenu", Header = "Создать Lua-скрипт…" };
        create.Click += async (_, _) => await session.CreateAndOpenScriptAsync();
        var import = new MenuItem { Header = "Импортировать Lua…" }; import.Click += async (_, _) => await session.ImportScriptAsync();
        var open = new MenuItem { Name = "OpenLuaResourceMenu", Header = "Открыть код" };
        open.Click += async (_, _) => { if (selected()?.ScriptId is { } id) await session.OpenScriptExternallyAsync(id); };
        var properties = new MenuItem { Name = "InspectLuaResourceMenu", Header = "Свойства" };
        var rename = new MenuItem { Name = "RenameLuaResourceMenu", Header = "Переименовать…", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.F2) };
        rename.Click += async (_, _) => { if (selected()?.ScriptId is { } id) await session.RenameScriptAsync(id); };
        properties.Click += (_, _) => { if (selected()?.ScriptId is { } id) session.InspectScript(id); else session.OpenScriptLibrary(); };
        var delete = new MenuItem { Header = "Удалить" }; delete.Click += async (_, _) => { if (selected() is { } entry) await session.DeleteExplorerEntryAsync(entry); };
        var editor = new MenuItem { Header = "Редактор Lua по умолчанию…" }; editor.Click += async (_, _) => await session.ChooseCodeEditorAsync();
        var annotations = new MenuItem { Name = "PrepareLuaAnnotationsMenu", Header = "Подсказки Lua API" };
        annotations.Click += async (_, _) => await session.PrepareLuaAnnotationsAsync();
        var journal = new MenuItem { Name = "OpenLuaJournalMenu", Header = "Журнал Lua F6" };
        journal.Click += async (_, _) => await session.OpenLuaJournalAsync();
        var autoOpen = new MenuItem { Name = "OpenCodeOnCreationMenu", Header = "Открывать код после создания", ToggleType = MenuItemToggleType.CheckBox };
        autoOpen.Click += async (_, _) => await session.SetOpenCodeOnCreationAsync(autoOpen.IsChecked);
        menu.ItemsSource = new object[] { create, import, new Separator(), open, properties, rename, delete, new Separator(), annotations, journal, editor, autoOpen };
        menu.Opening += (_, _) =>
        {
            var entry = selected(); var isScript = entry?.ScriptId is not null;
            autoOpen.IsChecked = session.OpenCodeOnCreation;
            create.IsEnabled = import.IsEnabled = editor.IsEnabled = session.CanEdit;
            annotations.IsEnabled = journal.IsEnabled = session.CanEdit;
            open.IsEnabled = delete.IsEnabled = rename.IsEnabled = session.CanEdit && isScript;
            properties.IsEnabled = session.CanEdit && (isScript || entry?.IsScriptLibrary == true);
        };
        return menu;
    }
}
