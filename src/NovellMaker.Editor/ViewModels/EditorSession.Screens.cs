using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public AsyncCommand OpenScreenLibraryCommand { get; private set; } = null!;
    public bool IsScreenDesigner => ActiveScene?.SelectedSection == 6;
    public Guid? SelectedScreenId { get; private set; }
    public Guid? SelectedScreenNodeId { get; private set; }
    public ScreenAsset? SelectedScreen => project?.Screens.FirstOrDefault(s => s.Id == SelectedScreenId);
    public ScreenNode? SelectedScreenNode => SelectedScreen?.Nodes.FirstOrDefault(n => n.Id == SelectedScreenNodeId);
    public string ScreenAuthoringError { get; private set; } = "";
    internal void ReportScreenError(string message) { ScreenAuthoringError = message; Changed(nameof(ScreenAuthoringError)); }

    private void CommitAddedNode(ScreenAsset screen, ScreenNode node)
    {
        try
        {
            UpdateScreen(screen with { Nodes = screen.Nodes.Add(node) });
            SelectScreenNode(node.Id); ScreenAuthoringError = "";
        }
        catch (ArgumentException error)
        {
            ScreenAuthoringError = "Компонент не добавлен: " + error.Message;
        }
        Changed(nameof(ScreenAuthoringError));
    }

    public void OpenScreenLibrary(Guid? id = null)
    {
        if (project is null) return;
        var selected = id ?? ActiveScene?.Id ?? SelectedScreenId ?? project.Scenes.FirstOrDefault()?.Id;
        if (selected is not { } scene) return;
        OpenScene(scene);
        ActiveScene!.SelectedSection = 6;
        SelectScreen(scene);
    }

    public void SelectScreen(Guid? id, Guid? nodeId = null)
    {
        if (SelectedScreenId != id) EndEditGroup();
        if (id is { } sceneId && ActiveScene?.Id != sceneId) OpenScene(sceneId);
        SelectedScreenId = project?.Scenes.Any(s => s.Id == id) == true ? id : null;
        SelectedScreenNodeId = SelectedScreen?.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Id ?? SelectedScreen?.Nodes.FirstOrDefault()?.Id;
        InspectorTarget = InspectorTarget.Screen; Changed(nameof(InspectorTarget));
        Changed(nameof(SelectedScreen)); Changed(nameof(SelectedScreenNode));
    }

    public void SelectScreenNode(Guid? id)
    {
        EndEditGroup(); SelectedScreenNodeId = id;
        InspectorTarget = InspectorTarget.Screen; Changed(nameof(InspectorTarget));
        Changed(nameof(SelectedScreenNode));
    }

    public void AddScreen(bool empty = false)
    {
        if (!CanEdit || project is null) return;
        AddScene();
    }

    public void UpdateScreen(ScreenAsset value, string? group = null)
    {
        if (!CanEdit || project is null || SelectedScreen is not { } old || old.Id != value.Id) return;
        var candidate = project.WithCanvas(value);
        ScreenAsset.ValidateProject(candidate);
        EditProject(candidate, group);
        ReportScreenError("");
    }

    public void SetInputBinding(ProjectInputBinding binding)
    {
        if (!CanEdit || project is null) return;
        var candidate = project with { InputBindings = project.InputBindings.Where(b => b.Key != binding.Key).Append(binding).ToImmutableArray() };
        ProjectInput.Validate(candidate); EditProject(candidate);
    }

    public void RemoveInputBinding(string key)
    {
        if (CanEdit && project is not null) EditProject(project with { InputBindings = project.InputBindings.Where(b => b.Key != key).ToImmutableArray() });
    }

    public void SetStartScreen(Guid? sceneId)
    {
        if (!CanEdit || project is null || project.StartScreenSceneId == sceneId) return;
        var candidate = project with { StartScreenSceneId = sceneId };
        ScreenAsset.ValidateProject(candidate);
        EditProject(candidate);
    }

    public void AddGameMenusExample()
    {
        if (!CanEdit || project is null || ActiveScene is not { } story) return;
        EditProject(Examples.ExampleGameMenusModule.AddTo(project, story.Id));
    }

    public void AddPlayerSettingsExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExamplePlayerSettingsModule.AddTo(project));
    }

    public void AddReleaseCheckExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleReleaseCheckModule.AddTo(project));
    }

    public void AddTriggersExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleTriggersModule.AddTo(project));
    }

    public void AddComponentStateExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleComponentStateModule.AddTo(project));
    }

    public void AddLuaModulesExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleLuaModulesModule.AddTo(project));
    }

    public void AddLuaLibraryExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleLuaLibraryModule.AddTo(project));
    }

    public void AddLuaRandomExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleLuaRandomModule.AddTo(project));
    }

    public void AddLuaDiagnosticsExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleLuaDiagnosticsModule.AddTo(project));
    }

    public void AddMinigameExample()
    {
        if (!CanEdit || project is null) return;
        var added = Examples.ExampleMinigameModule.AddTo(project);
        EditProject(added);
        OpenScene(added.Scenes[^3].Id);
    }

    public void AddAnimationsExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleAnimationsModule.AddTo(project));
    }

    public void AddAudioExample()
    {
        if (!CanEdit || project is null) return;
        if (project.Audio.Assets.IsEmpty) { ReportScreenError("Сначала импортируйте PCM WAV на вкладке «Звук»."); return; }
        EditProject(Examples.ExampleAudioModule.AddTo(project));
        ReportScreenError("");
    }

    public void AddInputExample()
    {
        if (!CanEdit || project is null) return;
        EditProject(Examples.ExampleInputModule.AddTo(project));
    }

    public void DuplicateScreen()
    {
        if (!CanEdit || project is null || SelectedScreen is not { } source) return;
        var (copy, ids) = SceneCopy.Create(project.Scenes.Single(s => s.Id == source.Id));
        var audio = project.Audio;
        foreach (var line in project.Scenes.Single(s => s.Id == source.Id).Lines)
        {
            if (audio.Voices.TryGetValue(line.Id, out var voices)) audio = audio with { Voices = audio.Voices.Add(ids[line.Id], voices) };
            if (audio.LineEffects.TryGetValue(line.Id, out var effect)) audio = audio with { LineEffects = audio.LineEffects.Add(ids[line.Id], effect) };
        }
        if (audio.SceneSounds.TryGetValue(source.Id, out var sound)) audio = audio with { SceneSounds = audio.SceneSounds.Add(copy.Id, sound) };
        EditProject(project with { Scenes = project.Scenes.Add(copy), Audio = audio }); OpenScreenLibrary(copy.Id);
    }

    public Task DeleteScreenAsync(Guid id) => RunAsync(async () =>
    {
        var before = project; var screen = project?.Screens.FirstOrDefault(s => s.Id == id);
        if (screen is null || !await dialogs.ConfirmDeleteAsync(screen.Name,
            "Сцена, её фразы, компоненты и переходы на неё будут удалены. Назначения клавиш на эту сцену будут удалены. Ctrl+Z отменяет удаление.")) return;
        if (!ReferenceEquals(before, project)) return;
        EditProject(Services.ActionReferences.Prune(project! with
        {
            Scenes = project!.Scenes.Where(s => s.Id != id).ToImmutableArray(),
            InputBindings = project.InputBindings.Where(b => b.TargetSceneId != id).ToImmutableArray()
        }));
    });

    public void AddScreenNode(ScreenNodeKind kind)
    {
        if (!CanEdit || SelectedScreen is not { } screen) return;
        var node = new ScreenNode(Guid.NewGuid(), screen.Nodes.Single(n => n.ParentId is null).Id, kind,
            new(40, 30, 200, 48), kind == ScreenNodeKind.Button ? "Кнопка" : "Текст");
        CommitAddedNode(screen, node);
    }

    public void AddScreenNodeAt(ScreenNodeKind kind, double x, double y, Guid? imageId = null)
    {
        try { AddScreenNodeAtCore(kind, x, y, imageId); }
        catch (ArgumentException error)
        {
            ScreenAuthoringError = "Компонент не добавлен: " + error.Message;
            Changed(nameof(ScreenAuthoringError));
        }
    }

    private void AddScreenNodeAtCore(ScreenNodeKind kind, double x, double y, Guid? imageId)
    {
        if (!CanEdit || SelectedScreen is not { } screen || !Enum.IsDefined(kind) || !double.IsFinite(x) || !double.IsFinite(y)) return;
        if (imageId is { } image && project?.Images.Any(i => i.Id == image) != true) return;
        var root = screen.Nodes.Single(n => n.ParentId is null);
        if (!screen.Definition().ContainsNode(root.Id, x, y))
        {
            var newRoot = new ScreenNode(Guid.NewGuid(), null, ScreenNodeKind.Container, new(0, 0, 1280, 720));
            screen = screen with { Nodes = screen.Nodes.Replace(root, root with { ParentId = newRoot.Id }).Add(newRoot) };
        }
        var definition = screen.Definition();
        var parent = definition.VisibleRegions(new(0, 0, 1280, 720)).Reverse()
            .FirstOrDefault(r => r.Node.Kind is ScreenNodeKind.Container or ScreenNodeKind.Panel && definition.ContainsNode(r.Node.Id, x, y)).Node ?? screen.Nodes.Single(n => n.ParentId is null);
        var origin = definition.Visual(parent.Id).ToLocal(x, y);
        var node = new ScreenNode(Guid.NewGuid(), parent.Id, kind,
            new(origin.X, origin.Y, kind is ScreenNodeKind.Image or ScreenNodeKind.Panel ? 240 : 200, kind is ScreenNodeKind.Image or ScreenNodeKind.Panel ? 160 : 48),
            kind == ScreenNodeKind.Button ? "Кнопка" : kind == ScreenNodeKind.Text ? "Текст" : "") { ImageId = imageId };
        CommitAddedNode(screen, node);
    }

    public Task ImportScreenSpriteAsync() => RunAsync(async () =>
    {
        if (project is null || SelectedScreen is not { } screen || SelectedScreenNode is not { } node) return;
        var source = await dialogs.PickImageAsync();
        if (source is null || projectPath is null && !await SaveCoreAsync(false)) return;
        var image = await imageAssets.ImportAsync(ProjectDirectory!, source);
        EditProject(project!.WithCanvas(screen with { Nodes = screen.Nodes.Replace(node, node with { ImageId = image.Id }) }) with { Images = project.Images.Add(image) });
        await UpdateGitAsync();
    });

    public void UpdateScreenNode(ScreenNode node, string? group = null)
    {
        if (SelectedScreen is not { } screen || !screen.Nodes.Any(n => n.Id == node.Id)) return;
        var original = screen.Nodes.Single(n => n.Id == node.Id);
        if (node.Bounds != original.Bounds && node.Dialogue != DialogueBinding.None && ScreenPresentation is { } presentation)
        {
            screen = screen with { Nodes = DialogueCanvas.Materialize(screen.Definition(), presentation) };
            if (node.Dialogue == DialogueBinding.CssPanel) node = node with { Dialogue = DialogueBinding.Panel };
        }
        var buttons = node.Kind is ScreenNodeKind.Button or ScreenNodeKind.Area ? screen.Buttons : screen.Buttons.Where(b => b.NodeId != node.Id).ToImmutableArray();
        UpdateScreen(screen with { Nodes = screen.Nodes.SetItem(screen.Nodes.IndexOf(screen.Nodes.Single(n => n.Id == node.Id)), node), Buttons = buttons }, group);
    }

    public Task DeleteScreenNodeAsync(Guid id) => RunAsync(async () =>
    {
        var before = project; var screen = SelectedScreen; var node = screen?.Nodes.FirstOrDefault(n => n.Id == id);
        if (screen is null || node is null || node.ParentId is null) return;
        if (!await dialogs.ConfirmDeleteAsync("Элемент меню", "Элемент, его дочерние элементы, привязки и refs будут удалены. Ctrl+Z отменяет удаление.")) return;
        if (!ReferenceEquals(before, project)) return;
        var removed = new HashSet<Guid> { id };
        bool added;
        do { added = false; foreach (var n in screen.Nodes) if (n.ParentId is { } p && removed.Contains(p)) added |= removed.Add(n.Id); } while (added);
        // RunAsync owns the busy state; use the same immutable edit boundary directly.
        EditProject(project!.WithCanvas(screen with
        {
            Nodes = screen.Nodes.Where(n => !removed.Contains(n.Id)).ToImmutableArray(),
            Buttons = screen.Buttons.Where(b => !removed.Contains(b.NodeId)).ToImmutableArray(),
            References = screen.References.Where(r => !removed.Contains(r.Value)).ToImmutableDictionary()
        }));
    });

    public void MoveScreenNode(int direction)
    {
        if (SelectedScreen is not { } screen || SelectedScreenNode is not { ParentId: { } parent } node) return;
        var siblings = screen.Nodes.Where(n => n.ParentId == parent).OrderBy(n => n.Order).ToArray();
        var index = Array.IndexOf(siblings, node); var next = index + direction;
        if (next < 0 || next >= siblings.Length) return;
        var a = screen.Nodes.IndexOf(node); var b = screen.Nodes.IndexOf(siblings[next]);
        UpdateScreen(screen with { Nodes = screen.Nodes.SetItem(a, siblings[next] with { Order = node.Order }).SetItem(b, node with { Order = siblings[next].Order }) });
    }

    public void ReparentScreenNode(Guid parent)
    {
        if (SelectedScreen is not { } screen || SelectedScreenNode is not { } node) return;
        if (node.Dialogue != DialogueBinding.None && ScreenPresentation is { } presentation)
            screen = screen with { Nodes = DialogueCanvas.Materialize(screen.Definition(), presentation) };
        UpdateScreen(screen with { Nodes = ScreenTree.Reparent(screen.Definition(), node.Id, parent).Nodes });
    }

    public void DuplicateScreenNode()
    {
        if (SelectedScreen is not { } screen || SelectedScreenNode is not { ParentId: { } parent } node) return;
        var (definition, ids) = ScreenTree.Copy(screen.Definition(), node.Id, parent);
        var references = screen.References;
        foreach (var pair in screen.References.Where(r => ids.ContainsKey(r.Value)))
        {
            var suffix = 1; var stem = pair.Key.Length > 110 ? pair.Key[..110] : pair.Key;
            var name = stem + "_copy";
            while (references.ContainsKey(name)) name = stem + "_copy" + ++suffix;
            references = references.Add(name, ids[pair.Value]);
        }
        UpdateScreen(screen with { Nodes = definition.Nodes, References = references,
            Buttons = screen.Buttons.AddRange(screen.Buttons.Where(b => ids.ContainsKey(b.NodeId)).Select(b => b with { NodeId = ids[b.NodeId] })) });
        SelectScreenNode(ids[node.Id]);
    }

    public Task CreateScreenHandlerAsync() => RunAsync(async () =>
    {
        if (project is null || SelectedScreen is not { } screen) return;
        var name = await dialogs.EditLuaNameAsync(NextScriptName("menu"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var script = new LuaScriptAsset(Guid.NewGuid(), name.Trim(), """
            return {
              on_event = function(event)
              end
            }
            """);
        var updated = screen with { HandlerScriptId = script.Id };
        EditProject(project.WithCanvas(updated) with { Scripts = project.Scripts.Add(script) });
        if (ProjectDirectory is { } root) { ScriptFiles.Ensure(root, script); StartScriptWatcher(); RefreshExternalScriptsCore(); }
        if (OpenCodeOnCreation) await OpenScriptExternallyCoreAsync(script.Id);
    });

    private void NotifyScreensChanged()
    {
        if (project?.Screens.Any(s => s.Id == SelectedScreenId) != true) SelectedScreenId = project?.Screens.FirstOrDefault()?.Id;
        if (SelectedScreen?.Nodes.Any(n => n.Id == SelectedScreenNodeId) != true) SelectedScreenNodeId = SelectedScreen?.Nodes.FirstOrDefault()?.Id;
        Changed(nameof(SelectedScreen)); Changed(nameof(SelectedScreenNode));
        OpenScreenLibraryCommand?.NotifyCanExecuteChanged();
    }
}
