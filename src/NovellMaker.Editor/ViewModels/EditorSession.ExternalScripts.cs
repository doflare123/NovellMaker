using System.Collections.Immutable;
using Avalonia.Threading;
using NovellMaker.Core.Execution;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private readonly IExternalCodeEditor codeEditor;
    private FileSystemWatcher? scriptWatcher;
    private System.Threading.Timer? scriptRefreshTimer;
    private int scriptWatchGeneration;
    private readonly Dictionary<Guid, string> scriptDiagnostics = [];
    private readonly Dictionary<Guid, string> checkedScriptSources = [];
    public string CodeEditorLabel => codeEditor.Executable is { } executable ? Path.GetFileNameWithoutExtension(executable) : "Системное приложение для .lua";
    public bool OpenCodeOnCreation => codeEditor.OpenOnCreation;
    public Task SetOpenCodeOnCreationAsync(bool value) => RunAsync(() => { codeEditor.OpenOnCreation = value; Changed(nameof(OpenCodeOnCreation)); return Task.CompletedTask; });
    public string SelectedScriptPath => SelectedScript is { } script && ProjectDirectory is { } root ? Path.Combine(root, "Scripts", script.Id.ToString("N") + ".lua") : "Сохраните проект для работы с файлами Lua.";
    public string SelectedScriptDiagnostic => SelectedScriptId is { } id ? scriptDiagnostics.GetValueOrDefault(id, "Ещё не проверен") : "Выберите скрипт.";

    public Task ChooseCodeEditorAsync() => RunAsync(async () =>
    {
        var executable = await dialogs.PickCodeEditorAsync();
        if (executable is null) return;
        codeEditor.Configure(executable);
        Changed(nameof(CodeEditorLabel));
    });
    public Task UseSystemCodeEditorAsync() => RunAsync(() =>
    {
        codeEditor.Configure(null); Changed(nameof(CodeEditorLabel)); return Task.CompletedTask;
    });

    public Task OpenScriptExternallyAsync(Guid? id = null) => RunAsync(() => OpenScriptExternallyCoreAsync(id));

    private async Task OpenScriptExternallyCoreAsync(Guid? id = null)
    {
        if (project is null) return;
        if (id is not null) OpenScriptLibrary(id);
        if (SelectedScript is not { } script) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        ScriptFiles.Ensure(ProjectDirectory!, script);
        StartScriptWatcher();
        RefreshExternalScriptsCore();
        codeEditor.Open(ScriptFiles.Resolve(ProjectDirectory!, script.Id));
        Log("Скрипт открыт в «" + CodeEditorLabel + "». Сохранения файла проверяются автоматически.");
    }

    public Task CreateAndOpenScriptAsync() => RunAsync(async () =>
    {
        if (project is null) return;
        var name = await dialogs.EditLuaNameAsync(NextScriptName());
        if (string.IsNullOrWhiteSpace(name)) return;
        AddScriptCore(name.Trim());
        if (OpenCodeOnCreation) await OpenScriptExternallyCoreAsync();
    });

    public Task OpenInlineScriptExternallyAsync(Guid sceneId, Guid actionId) => RunAsync(async () =>
    {
        if (project is null) return;
        var scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId);
        var inline = scene?.Actions.Concat(scene.ScriptBlocks.Select(b => b.Action)).OfType<ScriptAction>().FirstOrDefault(a => a.Id == actionId);
        if (scene is null || inline is null) return;
        var script = new LuaScriptAsset(Guid.NewGuid(), inline.Name, inline.Source) { References = inline.References };
        var call = new RunScriptAction(inline.Id, script.Id);
        var updated = scene with
        {
            Actions = scene.Actions.Select(a => a.Id == actionId ? call : a).ToImmutableArray(),
            ScriptBlocks = scene.ScriptBlocks.Select(b => b.Action.Id == actionId ? b with { Action = call } : b).ToImmutableArray()
        };
        EditProject(project with { Scripts = project.Scripts.Add(script), Scenes = project.Scenes.Replace(scene, updated) });
        OpenScriptLibrary(script.Id);
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var file = ScriptFiles.Ensure(ProjectDirectory!, script);
        StartScriptWatcher(); RefreshExternalScriptsCore(); codeEditor.Open(file);
    });

    private void InitializeScriptFiles()
    {
        if (project is null || ProjectDirectory is not { } root) return;
        foreach (var script in project.Scripts) ScriptFiles.Ensure(root, script);
        StartScriptWatcher(); RefreshExternalScriptsCore();
    }

    public Task RefreshExternalScriptsAsync() => RunAsync(() => { RefreshExternalScriptsCore(); return Task.CompletedTask; });

    private void RefreshExternalScriptsCore()
    {
        if (project is null || ProjectDirectory is not { } root) return;
        var scripts = project.Scripts;
        var changed = false;
        foreach (var script in scripts)
        {
            string diagnostic;
            try
            {
                var file = ScriptFiles.Resolve(root, script.Id);
                var source = ScriptFiles.Read(file);
                if (source != script.Source) { project = project with { Scripts = project.Scripts.Replace(script, script with { Source = source }) }; changed = true; }
                if (checkedScriptSources.TryGetValue(script.Id, out var checkedSource) && checkedSource == source && scriptDiagnostics.ContainsKey(script.Id)) continue;
                checkedScriptSources[script.Id] = source;
                LuaActionScript.ValidateSyntax(source, file);
                diagnostic = "Синтаксис корректен";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { diagnostic = error.Message; checkedScriptSources.Remove(script.Id); }
            if (scriptDiagnostics.GetValueOrDefault(script.Id) != diagnostic)
            {
                scriptDiagnostics[script.Id] = diagnostic;
                Log(script.Name + ": " + diagnostic);
            }
        }
        if (changed) { EndEditGroup(); NotifyProjectChanged(); }
        Changed(nameof(SelectedScriptDiagnostic)); Changed(nameof(SelectedScriptPath));
    }

    private void ValidateExternalScriptsForRun()
    {
        RefreshExternalScriptsCore();
        if (project is null) return;
        foreach (var script in project.Scripts)
        {
            if (ProjectDirectory is not null && scriptDiagnostics.GetValueOrDefault(script.Id) is { } diagnostic && diagnostic != "Синтаксис корректен")
                throw new InvalidOperationException(script.Name + ": " + diagnostic);
            LuaActionScript.ValidateSyntax(script.Source, script.Name);
        }
    }

    private void StartScriptWatcher()
    {
        if (ProjectDirectory is not { } root || project is null || project.Scripts.IsEmpty) return;
        StopScriptWatcher();
        var generation = scriptWatchGeneration;
        var folder = Path.GetDirectoryName(ScriptFiles.Resolve(root, project.Scripts[0].Id))!;
        Directory.CreateDirectory(folder);
        scriptRefreshTimer = new(_ => Dispatcher.UIThread.Post(() =>
        {
            if (generation != scriptWatchGeneration) return;
            if (isBusy) { ScheduleScriptRefresh(); return; }
            RefreshExternalScriptsCore();
        }), null, Timeout.Infinite, Timeout.Infinite);
        scriptWatcher = new(folder, "*.lua") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        scriptWatcher.Changed += ScriptFileChanged; scriptWatcher.Created += ScriptFileChanged; scriptWatcher.Deleted += ScriptFileChanged;
        scriptWatcher.Renamed += ScriptFileChanged; scriptWatcher.Error += (_, _) => ScheduleScriptRefresh();
        scriptWatcher.EnableRaisingEvents = true;
    }
    private void ScriptFileChanged(object sender, FileSystemEventArgs e) => ScheduleScriptRefresh();
    private void ScheduleScriptRefresh()
    {
        try { scriptRefreshTimer?.Change(400, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }
    private void StopScriptWatcher()
    {
        scriptWatchGeneration++;
        scriptWatcher?.Dispose(); scriptWatcher = null;
        scriptRefreshTimer?.Dispose(); scriptRefreshTimer = null;
        checkedScriptSources.Clear();
    }
}
