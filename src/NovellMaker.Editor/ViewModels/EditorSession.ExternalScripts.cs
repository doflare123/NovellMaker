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
    private CancellationTokenSource? syntaxCancellation;
    private int syntaxGeneration;
    private Task scriptValidation = Task.CompletedTask;
    internal Task SyntaxValidationTask => scriptValidation;
    private bool sessionDisposed;
    private int waitingForSyntax;
    private bool cancellingSyntaxForClose;
    private async Task AwaitScriptValidationAsync()
    {
        waitingForSyntax++;
        try { await scriptValidation; }
        finally { waitingForSyntax--; }
    }
    internal Func<string, string, CancellationToken, Task> ValidateLuaSyntax { get; set; } = LuaActionScript.ValidateSyntaxAsync;
    internal Func<Guid, string> ResolveLuaJournalPath { get; set; } = LuaAuthoringTools.JournalPath;
    public string CodeEditorLabel => codeEditor.Executable is { } executable ? Path.GetFileNameWithoutExtension(executable) : "Системное приложение для .lua";
    public bool OpenCodeOnCreation => codeEditor.OpenOnCreation;
    public Task SetOpenCodeOnCreationAsync(bool value) => RunAsync(() => { codeEditor.OpenOnCreation = value; Changed(nameof(OpenCodeOnCreation)); return Task.CompletedTask; });
    public string SelectedScriptPath => SelectedScript is { } script && ProjectDirectory is { } root ? Path.Combine(root, "Scripts", script.Id.ToString("N") + ".lua") : "Сохраните проект для работы с файлами Lua.";
    public string SelectedScriptDiagnostic => SelectedScriptId is { } id ? scriptDiagnostics.GetValueOrDefault(id, "Ещё не проверен") : "Выберите скрипт.";

    public Task PrepareLuaAnnotationsAsync() => RunAsync(async () =>
    {
        if (project is null || projectPath is null && !await SaveCoreAsync(false)) return;
        var file = LuaAuthoringTools.Install(ProjectDirectory!);
        codeEditor.Open(file);
        Log("Аннотации Lua API обновлены в Scripts/NovellMakerApi. Откройте папку проекта в редакторе с Lua Language Server.");
    });

    public Task OpenLuaJournalAsync() => RunAsync(() =>
    {
        if (project is null) return Task.CompletedTask;
        var path = ResolveLuaJournalPath(project.Id);
        if (!File.Exists(path)) { Log("Журнал Lua появится после запуска F6: " + path); return Task.CompletedTask; }
        codeEditor.Open(path);
        Log("Открыт журнал Lua F6: " + path);
        return Task.CompletedTask;
    });

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

    public Task RefreshExternalScriptsAsync() => RunAsync(async () => { RefreshExternalScriptsCore(); await AwaitScriptValidationAsync(); });

    private void RefreshExternalScriptsCore()
    {
        if (sessionDisposed || preserveRecoveredScripts || project is null || ProjectDirectory is not { } root) return;
        CancelSyntaxValidation();
        var cancellation = syntaxCancellation = new();
        var generation = syntaxGeneration;
        var projectId = project.Id;
        var checks = new List<(Guid Id, string Source, string File, string Name)>();
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
                checks.Add((script.Id, source, file, script.Name));
                diagnostic = "Проверка синтаксиса…";
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
        scriptValidation = ValidateScriptBatchAsync(checks, generation, projectId, cancellation.Token);
    }

    private async Task ValidateScriptBatchAsync(List<(Guid Id, string Source, string File, string Name)> checks,
        int generation, Guid projectId, CancellationToken token)
    {
        foreach (var check in checks)
        {
            string diagnostic;
            try { await ValidateLuaSyntax(check.Source, check.File, token); diagnostic = "Синтаксис корректен"; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (ArgumentException error) { diagnostic = error.Message; }
            if (sessionDisposed || token.IsCancellationRequested || generation != syntaxGeneration || project?.Id != projectId) return;
            if (project.Scripts.FirstOrDefault(s => s.Id == check.Id)?.Source != check.Source) continue;
            try
            {
                if (ScriptFiles.Read(ScriptFiles.Resolve(ProjectDirectory!, check.Id)) != check.Source)
                {
                    checkedScriptSources.Remove(check.Id);
                    scriptDiagnostics[check.Id] = "Исходник изменён во время проверки. Обновите ресурсы.";
                    ScheduleScriptRefresh();
                    Changed(nameof(SelectedScriptDiagnostic));
                    continue;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { diagnostic = error.Message; }
            scriptDiagnostics[check.Id] = diagnostic;
            if (diagnostic == "Синтаксис корректен") checkedScriptSources[check.Id] = check.Source;
            else checkedScriptSources.Remove(check.Id);
            Log(check.Name + ": " + diagnostic);
            Changed(nameof(SelectedScriptDiagnostic));
        }
    }

    private void CancelSyntaxValidation()
    {
        syntaxGeneration++;
        syntaxCancellation?.Cancel();
        syntaxCancellation?.Dispose();
        syntaxCancellation = null;
    }

    private async Task ValidateExternalScriptsForRunAsync()
    {
        RefreshExternalScriptsCore();
        var generation = syntaxGeneration;
        await AwaitScriptValidationAsync();
        if (sessionDisposed || generation != syntaxGeneration) throw new OperationCanceledException();
        if (project is null) return;
        foreach (var script in project.Scripts)
        {
            if (ProjectDirectory is not null && !preserveRecoveredScripts)
            {
                if (scriptDiagnostics.GetValueOrDefault(script.Id) != "Синтаксис корректен")
                    throw new InvalidOperationException(script.Name + ": " + scriptDiagnostics.GetValueOrDefault(script.Id, "Скрипт ещё не проверен."));
            }
            else
            {
                syntaxCancellation ??= new();
                waitingForSyntax++;
                try { await ValidateLuaSyntax(script.Source, script.Name, syntaxCancellation.Token); }
                finally { waitingForSyntax--; }
                if (sessionDisposed || generation != syntaxGeneration) throw new OperationCanceledException();
            }
        }
        var captured = project;
        syntaxCancellation ??= new();
        var moduleValidationToken = syntaxCancellation.Token;
        waitingForSyntax++;
        try
        {
            await Task.Run(() => NovellMaker.Core.Scripting.LuaModuleLibrary.ValidateProject(captured, moduleValidationToken), moduleValidationToken);
        }
        finally { waitingForSyntax--; }
        if (sessionDisposed || generation != syntaxGeneration || !ReferenceEquals(captured, project)) throw new OperationCanceledException();
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
        CancelSyntaxValidation();
        scriptWatchGeneration++;
        scriptWatcher?.Dispose(); scriptWatcher = null;
        scriptRefreshTimer?.Dispose(); scriptRefreshTimer = null;
        checkedScriptSources.Clear();
    }
}
