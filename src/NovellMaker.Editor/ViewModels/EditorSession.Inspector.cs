using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public enum InspectorTarget { Project, Phrase, Action, Script }

public sealed partial class EditorSession
{
    public InspectorTarget InspectorTarget { get; private set; }
    public Guid? InspectedActionId { get; private set; }
    public void InspectPhrase()
    {
        InspectorTarget = InspectorTarget.Phrase; InspectedActionId = null;
        Changed(nameof(InspectorTarget));
    }
    public void InspectAction(Guid id)
    {
        InspectorTarget = InspectorTarget.Action; InspectedActionId = id;
        Changed(nameof(InspectorTarget));
    }
    public void InspectScript(Guid? id = null)
    {
        SelectScript(id ?? SelectedScriptId ?? project?.Scripts.FirstOrDefault()?.Id);
        InspectorTarget = InspectorTarget.Script; InspectedActionId = null;
        Changed(nameof(InspectorTarget));
    }

    public Task CreatePhraseScriptAsync(Guid sceneId, Guid? lineId, ScriptPhase phase) => RunAsync(async () =>
    {
        if (project is null) return;
        var scene = project.Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene is null || scene.HasLegacyActionScript || lineId is { } line && !scene.Lines.Any(l => l.Id == line)) return;
        var name = await dialogs.EditLuaNameAsync(NextScriptName("phrase"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var script = LuaScriptAsset.Create(name.Trim());
        var call = new RunScriptAction(Guid.NewGuid(), script.Id);
        var candidate = scene with { ScriptBlocks = scene.ScriptBlocks.Add(new(lineId, phase, call)) };
        EditProject(project with { Scripts = project.Scripts.Add(script), Scenes = project.Scenes.Replace(scene, candidate) });
        InspectAction(call.Id);
        if (ProjectDirectory is { } root)
        { ScriptFiles.Ensure(root, script); StartScriptWatcher(); RefreshExternalScriptsCore(); }
        if (!OpenCodeOnCreation) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var file = ScriptFiles.Ensure(ProjectDirectory!, script);
        StartScriptWatcher(); RefreshExternalScriptsCore(); codeEditor.Open(file);
    });

    public Task AttachExistingScriptAsync(Guid sceneId, Guid? lineId, ScriptPhase phase) => RunAsync(async () =>
    {
        if (project is null) return;
        var selected = await dialogs.PickScriptAsync(project.Scripts);
        if (selected is { } id) AttachScriptCore(sceneId, lineId, phase, id);
    });

    public void AttachScript(Guid sceneId, Guid? lineId, ScriptPhase phase, Guid scriptId)
    { if (CanEdit) AttachScriptCore(sceneId, lineId, phase, scriptId); }

    private void AttachScriptCore(Guid sceneId, Guid? lineId, ScriptPhase phase, Guid scriptId)
    {
        var scene = project?.Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene is null || scene.HasLegacyActionScript || project is null || !project.Scripts.Any(s => s.Id == scriptId)) return;
        if (lineId is { } line && !scene.Lines.Any(l => l.Id == line)) return;
        if (lineId is null && phase is not (ScriptPhase.SceneStart or ScriptPhase.SceneEnd) || lineId is not null && phase is ScriptPhase.SceneStart or ScriptPhase.SceneEnd) return;
        var call = new RunScriptAction(Guid.NewGuid(), scriptId);
        EditProject(project with { Scenes = project.Scenes.Replace(scene, scene with { ScriptBlocks = scene.ScriptBlocks.Add(new(lineId, phase, call)) }) });
        OpenScene(sceneId); InspectAction(call.Id);
    }
}
