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
        if (InspectorTarget == InspectorTarget.Action && InspectedActionId == id) return;
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
        var script = LuaScriptAsset.Create("phrase.lua");
        var call = new RunScriptAction(Guid.NewGuid(), script.Id);
        var candidate = scene with { ScriptBlocks = scene.ScriptBlocks.Add(new(lineId, phase, call)) };
        EditProject(project with { Scripts = project.Scripts.Add(script), Scenes = project.Scenes.Replace(scene, candidate) });
        InspectAction(call.Id);
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var file = ScriptFiles.Ensure(ProjectDirectory!, script);
        StartScriptWatcher(); RefreshExternalScriptsCore(); codeEditor.Open(file);
    });
}
