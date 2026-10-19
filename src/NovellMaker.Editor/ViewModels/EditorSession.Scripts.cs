using System.Collections.Immutable;
using System.Text;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand OpenScriptLibraryCommand { get; private set; } = null!;
    public Guid? SelectedScriptId { get; private set; }
    public LuaScriptAsset? SelectedScript => project?.Scripts.FirstOrDefault(s => s.Id == SelectedScriptId);
    public string ScriptLibraryTitle => "Lua-скрипты" + (project is not null &&
        (savedProject is null ? !project.Scripts.IsEmpty : project.Scripts.Length != savedProject.Scripts.Length ||
         project.Scripts.Where((s, i) => !s.ContentEquals(savedProject.Scripts[i])).Any()) ? " *" : "");

    private void InitializeScripts() => OpenScriptLibraryCommand = Command(() => OpenScriptLibrary(), () => CanEdit);

    public void OpenScriptLibrary(Guid? id = null)
    {
        if (!HasProject) return;
        InspectScript(id);
    }


    public void SelectScript(Guid? id)
    {
        if (SelectedScriptId != id) EndEditGroup();
        SelectedScriptId = id;
        Changed(nameof(SelectedScript));
        Changed(nameof(SelectedScriptPath)); Changed(nameof(SelectedScriptDiagnostic));
    }

    public void AddScript()
    {
        if (!CanEdit || project is null) return;
        var number = 1;
        while (project.Scripts.Any(s => s.Name == $"script_{number}.lua")) number++;
        var script = LuaScriptAsset.Create($"script_{number}.lua");
        SelectedScriptId = script.Id;
        EditProject(project with { Scripts = project.Scripts.Add(script) });
        OpenScriptLibrary(script.Id);
        if (ProjectDirectory is { } root) { ScriptFiles.Ensure(root, script); StartScriptWatcher(); RefreshExternalScriptsCore(); }
    }

    public void UpdateScript(LuaScriptAsset script, string? group = null)
    {
        if (!CanEdit || project is null) return;
        var old = project.Scripts.FirstOrDefault(s => s.Id == script.Id);
        var index = old is null ? -1 : project.Scripts.IndexOf(old);
        if (index < 0) return;
        if (script.Source.Length > LuaActionScript.MaximumSourceLength) throw new ArgumentException("Превышен размер Lua-скрипта.");
        if (ProjectDirectory is { } root && old!.Source != script.Source)
        {
            var file = ScriptFiles.Ensure(root, old);
            if (ScriptFiles.Read(file) != old.Source) throw new IOException("Файл Lua изменён внешним редактором. Обновите ресурсы (F5).");
            ScriptFiles.Write(file, script.Source, overwrite: true);
        }
        EditProject(project with { Scripts = project.Scripts.SetItem(index, script) }, group);
        if (ProjectDirectory is not null) RefreshExternalScriptsCore();
    }

    public Task DeleteScriptAsync(Guid id) => RunAsync(async () =>
    {
        var script = project?.Scripts.FirstOrDefault(s => s.Id == id);
        if (script is null) return;
        if (!await dialogs.ConfirmDeleteAsync(script.Name,
            "Ресурс и прямые вызовы будут удалены. Именованные ссылки на него будут очищены; использующий их Lua-код нужно исправить. Удаление можно отменить.")) return;
        EditProject(Services.ActionReferences.Prune(RemoveScriptResources(project!, [id])));
    });

    private static NovellMaker.Core.Projects.NovelProject RemoveScriptResources(NovellMaker.Core.Projects.NovelProject source, HashSet<Guid> ids) => source with
        {
            Scripts = source.Scripts.Where(s => !ids.Contains(s.Id)).Select(s => s with
            { References = s.References.Where(r => !ids.Contains(r.Value)).ToImmutableDictionary() }).ToImmutableArray(),
            Scenes = source.Scenes.Select(scene => scene with
            {
                Actions = scene.Actions.Select(a => ClearScriptReference(a, ids)).ToImmutableArray(),
                ScriptBlocks = scene.ScriptBlocks.Select(b => b with { Action = ClearScriptReference(b.Action, ids) }).ToImmutableArray()
            }).ToImmutableArray()
        };

    private static ScenarioAction ClearScriptReference(ScenarioAction action, HashSet<Guid> ids) => action is ScriptAction script
        ? script with { References = script.References.Where(r => !ids.Contains(r.Value)).ToImmutableDictionary() } : action;

    public void AttachScriptToSceneStart(Guid sceneId, Guid scriptId)
    {
        if (!CanEdit || project is null || !project.Scripts.Any(s => s.Id == scriptId)) return;
        var scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene is null) return;
        if (scene.HasLegacyActionScript) throw new ArgumentException("Для импортированного потока изменяйте существующие действия.");
        UpdateScene(scene with { ScriptBlocks = scene.ScriptBlocks.Add(new(null, ScriptPhase.SceneStart, new RunScriptAction(Guid.NewGuid(), scriptId))) });
    }

    public Task ImportScriptAsync(bool replaceSelected = false) => RunAsync(async () =>
    {
        if (project is null) return;
        var selected = replaceSelected ? SelectedScript : null;
        if (replaceSelected && selected is null) return;
        var path = await dialogs.PickLuaScriptAsync();
        if (path is null) return;
        if (!Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Выберите файл .lua.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > LuaActionScript.MaximumSourceLength * 4L + 3) throw new ArgumentException("Превышен размер Lua-скрипта.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var buffer = new char[LuaActionScript.MaximumSourceLength + 2];
        var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        var source = new string(buffer, 0, count).TrimStart('\uFEFF');
        if (source.Length > LuaActionScript.MaximumSourceLength) throw new ArgumentException("Превышен размер Lua-скрипта.");
        var script = selected is null ? new LuaScriptAsset(Guid.NewGuid(), Path.GetFileName(path), source) : selected with { Source = source };
        if (ProjectDirectory is { } root)
        {
            var file = ScriptFiles.Ensure(root, selected ?? script);
            if (selected is not null && ScriptFiles.Read(file) != selected.Source) throw new IOException("Файл Lua изменён вне редактора. Обновите ресурсы (F5).");
            if (selected is not null) ScriptFiles.Write(file, script.Source, overwrite: true);
        }
        SelectedScriptId = script.Id;
        EditProject(project with { Scripts = selected is null ? project.Scripts.Add(script) : project.Scripts.Replace(selected, script) });
        OpenScriptLibrary(script.Id);
        if (ProjectDirectory is not null) { StartScriptWatcher(); RefreshExternalScriptsCore(); }
        Log($"Lua «{script.Name}» импортирован.");
    });

    public Task ExportScriptAsync() => RunAsync(async () =>
    {
        if (SelectedScript is not { } script) return;
        var path = await dialogs.PickSaveLuaScriptAsync("script.lua");
        if (path is null) return;
        if (!Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Выберите новый файл .lua.");
        var temporaryPath = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) await writer.WriteAsync(script.Source);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        Log("Код Lua выгружен. Именованные ссылки остаются в проекте; импортируйте изменения в выбранный ресурс.");
    });
}

