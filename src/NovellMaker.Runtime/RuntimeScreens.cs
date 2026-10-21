using System.Collections.Immutable;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Runtime;

/// <summary>Project-owned scene instances and input. Contains no game menus or predefined shortcuts.</summary>
internal sealed class RuntimeScreens : IDisposable, IAsyncDisposable
{
    private readonly Guid ownerId;
    private readonly Action<ScreenButtonBinding>? hostAction;
    private NovelProject? project;
    private ScreenInstance? sceneInstance;
    private Guid? sceneId;
    private bool deferredHandler;
    private Func<ImmutableDictionary<string, string>> snapshot = () => ImmutableDictionary<string, string>.Empty;
    private readonly Queue<string> errors = [];

    public RuntimeScreens(Guid ownerId, bool enableLua = false, Action<ScreenButtonBinding>? hostAction = null)
    {
        this.ownerId = ownerId; this.hostAction = hostAction;
        if (enableLua) Scripts = new(Stack);
    }
    public ScreenStack Stack { get; } = new(Guid.NewGuid());
    public ScreenLuaSession? Scripts { get; }
    public Guid? NavigationRequested { get; private set; }
    public bool HasOverlays => Stack.Instances.Any(i => !ReferenceEquals(i, sceneInstance));
    public bool IsSceneInstance(ScreenInstance instance) => ReferenceEquals(instance, sceneInstance);
    public IEnumerable<string> Diagnostics => errors.Concat(Scripts?.Diagnostics ?? []);
    public bool HasPending => Scripts?.HasPending ?? false;
    public bool Poll() => Scripts?.Poll() ?? false;

    public void Configure(NovelProject value, Func<ImmutableDictionary<string, string>> capture)
    {
        ScreenAsset.ValidateProject(value); Reset(); project = value; snapshot = capture;
    }
    public void SyncScene(Guid id, bool afterLoad = false)
    {
        if (sceneId == id) return;
        Reset(); sceneId = id;
        if (project?.Scenes.FirstOrDefault(s => s.Id == id) is not { } scene) return;
        var asset = scene.AsScreen();
        if (asset.Nodes.Length == 1 && asset.Nodes[0].Kind == ScreenNodeKind.Container && asset.Nodes[0].ImageId is null && asset.HandlerScriptId is null) return;
        sceneInstance = Stack.Open(ownerId, asset.Definition(), new(false, false));
        deferredHandler = afterLoad;
        if (!afterLoad) AttachHandler(asset, sceneInstance);
    }
    private void Report(string error)
    {
        errors.Enqueue(error); while (errors.Count > 32) errors.Dequeue();
    }
    private void AttachHandler(ScreenAsset asset, ScreenInstance instance)
    {
        try
        {
            if (asset.HandlerScriptId is { } id && project?.Scripts.FirstOrDefault(s => s.Id == id) is { } script)
                Scripts?.Attach(instance, script.Source, asset.References, snapshot());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { Report(asset.Name + ": " + error.Message); }
    }
    private void Open(ScreenAsset asset, ScreenInstance? replace = null)
    {
        var policy = new ScreenPolicy(asset.BlockInputOnOverlay, asset.PauseOnOverlay);
        var instance = replace is null ? Stack.Open(ownerId, asset.Definition(), policy) : Stack.Replace(replace, asset.Definition(), policy);
        AttachHandler(asset, instance);
    }
    public bool RouteKey(string key)
    {
        var binding = project?.InputBindings.FirstOrDefault(b => b.Key == key);
        if (binding is null) return false;
        Dispatch(new(Guid.Empty, binding.Action, binding.TargetSceneId), null);
        return true;
    }
    public ScreenInputResult Route(ScreenInput input)
    {
        if (input.Kind == ScreenInputKind.Back && !HasOverlays) return new(false);
        if (input.Kind == ScreenInputKind.Activate && !HasOverlays && Stack.Focus is null) return new(false);
        var result = Stack.Route(input);
        if (result.Activation is not { } activation) return result;
        var instance = Stack.Instances.Single(i => i.Id == activation.InstanceId);
        var asset = project?.Scenes.FirstOrDefault(s => s.Id == instance.Definition.Id)?.AsScreen();
        if (asset is null) return result;
        if (ReferenceEquals(instance, sceneInstance) && deferredHandler)
        { deferredHandler = false; AttachHandler(asset, instance); }
        Dispatch(asset.Buttons.FirstOrDefault(b => b.NodeId == activation.NodeId) ?? new(activation.NodeId, ScreenButtonAction.Lua, null), instance);
        return result;
    }
    public ScreenInputResult Route(ScreenInput input, Guid currentSceneId)
    {
        SyncScene(currentSceneId);
        return Route(input);
    }
    private void Dispatch(ScreenButtonBinding binding, ScreenInstance? instance)
    {
        try
        {
            switch (binding.Action)
            {
                case ScreenButtonAction.None: break;
                case ScreenButtonAction.Close: if (instance is not null) Stack.Close(instance); break;
                case ScreenButtonAction.Back: if (HasOverlays) Stack.Route(new(ScreenInputKind.Back)); break;
                case ScreenButtonAction.Scene: NavigationRequested = binding.TargetScreenId; break;
                case ScreenButtonAction.Open:
                case ScreenButtonAction.Replace:
                case ScreenButtonAction.Toggle:
                    var target = project!.Scenes.Single(s => s.Id == binding.TargetScreenId).AsScreen();
                    if (binding.Action == ScreenButtonAction.Toggle && Stack.Instances.LastOrDefault() is { } top &&
                        !IsSceneInstance(top) && top.Definition.Id == target.Id) Stack.Close(top);
                    else if (binding.Action == ScreenButtonAction.Replace && ReferenceEquals(instance, sceneInstance)) NavigationRequested = target.Id;
                    else Open(target, binding.Action == ScreenButtonAction.Replace ? instance : null);
                    break;
                case ScreenButtonAction.Lua:
                    if (instance is not null && Scripts?.Enqueue(instance, new("click", binding.NodeId), snapshot())
                        is not (ScreenEventAdmission.Accepted or ScreenEventAdmission.Coalesced))
                        Report("Обработчик компонента недоступен или занят.");
                    break;
                default: hostAction?.Invoke(binding); break;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { Report("Команда проекта не выполнена: " + error.Message); }
    }
    public void Reset()
    { Stack.Clear(); Scripts?.Reset(); errors.Clear(); sceneId = null; sceneInstance = null; deferredHandler = false; NavigationRequested = null; }
    public void Dispose() { Stack.Clear(); Scripts?.Dispose(); }
    public async ValueTask DisposeAsync() { Dispose(); if (Scripts is { } scripts) await scripts.DisposeAsync(); }
}
