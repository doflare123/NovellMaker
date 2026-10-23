using System.Collections.Immutable;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class LuaModuleTests
{
    private static ImmutableDictionary<string, Guid> Empty => ImmutableDictionary<string, Guid>.Empty;
    private static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Child = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Leaf = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Handler = "local m=ui.import(modules.child); return {on_load=function() ui.set_text(refs.label,m.value) end,on_event=function() end}";
    private static LuaModuleBundle Bundle(string source = "return {value='ok'}") => new(Root, Empty.Add("child", Child), [new(Child, source, Empty)]);
    private static LuaModuleLibrary Library(string rootSource, string childSource = "return {value='ok'}") => new(Root,
        [new(Root, "handler.lua", rootSource) { References = Empty.Add("child", Child) }, new(Child, "child.lua", childSource)]);

    [TestMethod]
    public void CatalogSelectsOnlyReachableScriptReferencesAndDoesNotRunCode()
    {
        var script = new LuaScriptAsset(Root, "renamed", Handler) { References = Empty.Add("child", Child).Add("scene", Guid.NewGuid()) };
        var library = new LuaModuleLibrary(Root, [script, new(Child, "also-renamed", "while true do end"), new(Leaf, "unused", "invalid @ source")]);
        var bundle = library.Prepare(Handler, () => { });
        Assert.AreEqual(1, bundle.Sources.Length); Assert.AreEqual(Child, bundle.Imports["child"]);
        Assert.IsFalse(bundle.Imports.ContainsKey("scene")); Assert.AreEqual("while true do end", bundle.Sources[0].Source);
    }

    [TestMethod]
    [DataRow("root-id")] [DataRow("default")] [DataRow("missing-root")] [DataRow("root-source")]
    [DataRow("null-entry")] [DataRow("duplicate")] [DataRow("empty-id")]
    public void InvalidCatalogsAreRejected(string mutation)
    {
        var scripts = ImmutableArray.Create(new LuaScriptAsset(Root, "handler", Handler)); var id = Root;
        switch (mutation)
        {
            case "root-id": id = Guid.Empty; break;
            case "default": scripts = default; break;
            case "missing-root": id = Child; break;
            case "root-source": scripts = [scripts[0] with { Source = "different" }]; break;
            case "null-entry": scripts = [null!]; break;
            case "duplicate": scripts = [scripts[0], scripts[0]]; break;
            case "empty-id": scripts = [scripts[0] with { Id = Guid.Empty }]; break;
        }
        Assert.Throws<ArgumentException>(() => new LuaModuleLibrary(id, scripts).Prepare(Handler, () => { }));
    }

    [TestMethod]
    [DataRow("root")] [DataRow("default")] [DataRow("null")] [DataRow("duplicate")] [DataRow("empty-id")]
    [DataRow("root-duplicate")] [DataRow("missing")] [DataRow("cycle")] [DataRow("self")]
    [DataRow("unreachable")] [DataRow("null-refs")] [DataRow("bad-name")] [DataRow("bad-ending")] [DataRow("null-source")]
    public void WorkerGraphValidationRejectsMalformedOrForgedBundles(string mutation)
    {
        var bundle = Bundle();
        bundle = mutation switch
        {
            "root" => bundle with { RootId = Guid.Empty }, "default" => bundle with { Sources = default },
            "null" => bundle with { Sources = [null!] }, "duplicate" => bundle with { Sources = [bundle.Sources[0], bundle.Sources[0]] },
            "empty-id" => bundle with { Sources = [bundle.Sources[0] with { Id = Guid.Empty }] },
            "root-duplicate" => bundle with { Sources = [bundle.Sources[0] with { Id = Root }] },
            "missing" => bundle with { Sources = [] }, "cycle" => bundle with { Sources = [bundle.Sources[0] with { Imports = Empty.Add("back", Root) }] },
            "self" => bundle with { Imports = Empty.Add("self", Root) },
            "unreachable" => bundle with { Sources = bundle.Sources.Add(new(Leaf, "return {}", Empty)) },
            "null-refs" => bundle with { Imports = null! }, "bad-name" => bundle with { Imports = Empty.Add("bad-name", Child) },
            "bad-ending" => bundle with { Imports = Empty.Add("name\n", Child) },
            _ => bundle with { Sources = [bundle.Sources[0] with { Source = null! }] },
        };
        Assert.Throws<ArgumentException>(() => bundle.Validate(Handler, () => { }));
    }

    [TestMethod]
    public void ResourceCodeDepthAndImportBudgetsIncludeExactBoundariesAndSharedDagPaths()
    {
        var resources = Enumerable.Range(0, 31).Select(_ => new LuaModuleSource(Guid.NewGuid(), "return {}", Empty)).ToImmutableArray();
        var imports = resources.Select((r, i) => KeyValuePair.Create("m" + i, r.Id)).ToImmutableDictionary();
        var graph = new LuaModuleBundle(Root, imports, resources);
        Assert.AreEqual(32, graph.Validate("return {}", () => { }).Count);
        Assert.Throws<ArgumentException>(() => (graph with { Sources = resources.Add(new(Guid.NewGuid(), "return {}", Empty)) }).Validate("return {}", () => { }));
        Assert.Throws<ArgumentException>(() => new LuaModuleLibrary(Root, [new(Root, "root", "return {}") { References = imports.Add("overflow", Leaf) },
            .. resources.Select(r => new LuaScriptAsset(r.Id, "child", r.Source)), new(Leaf, "overflow", "return {}")]).Prepare("return {}", () => { }));
        var repeated = Enumerable.Range(0, 32).ToImmutableDictionary(i => "m" + i, _ => Child);
        var aliases = Bundle() with { Imports = repeated };
        aliases.Validate(Handler, () => { });
        Assert.Throws<ArgumentException>(() => (aliases with { Imports = repeated.Add("extra", Child) }).Validate(Handler, () => { }));
        var comment = "--" + new string('x', 32766);
        (Bundle(comment)).Validate(comment, () => { });
        Assert.Throws<ArgumentException>(() => new LuaModuleBundle(Root, Empty.Add("a", Child).Add("b", Leaf),
            [new(Child, comment, Empty), new(Leaf, "x", Empty)]).Validate(comment, () => { }));
        var chain = Enumerable.Range(0, 16).Select(_ => Guid.NewGuid()).Prepend(Root).ToArray();
        LuaModuleBundle Chain(int length) => new(Root, Empty.Add("next", chain[1]).Add("shared", chain[length - 1]),
            [.. Enumerable.Range(1, length - 1).Select(i => new LuaModuleSource(chain[i], "return {}", i == length - 1 ? Empty : Empty.Add("next", chain[i + 1])))]);
        Chain(16).Validate("return {}", () => { });
        Assert.Throws<ArgumentException>(() => Chain(17).Validate("return {}", () => { }));
        var parents = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var allEdges = new LuaModuleBundle(Root, Enumerable.Range(0, 32).ToImmutableDictionary(i => "m" + i, i => parents[i % 7]),
            [.. parents.Select(id => new LuaModuleSource(id, "return {}", repeated)), new(Child, "return {}", Empty)]);
        allEdges.Validate("return {}", () => { });
        Assert.Throws<ArgumentException>(() => (allEdges with { Sources = allEdges.Sources.SetItem(7, new(Child, "return {}", Empty.Add("extra", Root))) }).Validate("return {}", () => { }));
    }

    [TestMethod]
    public void PreparationObservesCancellationAndFailedGraphsAreNotCached()
    {
        var library = Library(Handler); var checkpoints = 0;
        Assert.Throws<OperationCanceledException>(() => library.Prepare(Handler, () => { if (++checkpoints == 3) throw new OperationCanceledException(); }));
        Assert.AreEqual(1, library.Prepare(Handler, () => { }).Sources.Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("scene");
        var project = NovelProject.Create("project") with { Scenes = [scene with { Canvas = scene.Canvas with { HandlerScriptId = Root } }], Scripts = [new(Root, "root", Handler)] };
        Assert.Throws<OperationCanceledException>(() => LuaModuleLibrary.ValidateProject(project, cancelled.Token));
        Assert.Throws<ArgumentException>(() => LuaModuleLibrary.ValidateProject(project with { Scripts = [] }));
    }

    [TestMethod]
    public void VmUsesTheSameExportCacheAcrossImportsAndEvents()
    {
        const string source = """
            local first=ui.import(modules.child)
            local second=ui.import(modules.child)
            return {on_load=function() ui.set_text(refs.label,first==second and first.next() or 'bad') end,
              on_event=function() ui.set_text(refs.label,ui.import(modules.child).next()) end}
            """;
        var definition = ScreenLuaProtocolTests.Definition();
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), source, Empty.Add("label", definition.Nodes[2].Id),
            Bundle("local n=0; return {next=function() n=n+1; return 'value:'..n end}"));
        Assert.AreEqual("value:1", vm.Handle(null, ImmutableDictionary<string, string>.Empty).Single().Text);
        Assert.AreEqual("value:2", vm.Handle(new("update"), ImmutableDictionary<string, string>.Empty).Single().Text);
    }

    [TestMethod]
    [DataRow("ui.import(1)")] [DataRow("ui.import('unknown')")]
    [DataRow("ui.import('33333333333333333333333333333333')")]
    public void VmImportResolverRejectsWrongTypesUnknownIdsAndUndeclaredIds(string expression)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        Assert.Throws<MoonSharp.Interpreter.InterpreterException>(() => new ScreenLuaVm(definition, Guid.NewGuid(),
            expression + "; return {on_event=function() end}", Empty, Bundle()));
    }

    [TestMethod]
    [DataRow("return nil")] [DataRow("return 1")] [DataRow("return function() end")] [DataRow("return ui.null")]
    public void VmRejectsModulesWithoutAnExportTable(string code)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        Assert.Throws<MoonSharp.Interpreter.InterpreterException>(() => new ScreenLuaVm(definition, Guid.NewGuid(), Handler, Empty, Bundle(code)));
    }

    [TestMethod]
    public async Task RealWorkerTransitiveImportsAreCachedPerInstanceAndFileGlobalsAreSeparate()
    {
        const string source = """
            secret='root'
            local first=ui.import(modules.child)
            local second=ui.import(modules.child)
            return {on_load=function()
              ui.set_text(refs.label,first==second and secret=='root' and first.next() or 'bad')
            end,on_event=function() ui.set_text(refs.label,ui.import(modules.child).next()) end}
            """;
        var child = new LuaScriptAsset(Child, "child", """
            secret='child'
            local leaf=ui.import(modules.leaf)
            local n=0
            return {next=function() n=n+1; return secret=='child' and refs.label==nil and leaf.caption(n) or 'bad' end}
            """) { References = Empty.Add("leaf", Leaf) };
        var root = new LuaScriptAsset(Root, "root", source) { References = Empty.Add("child", Child) };
        var library = new LuaModuleLibrary(Root, [root, child, new(Leaf, "leaf", "return {caption=function(n) return 'value:'..n end}")]);
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        var refs = Empty.Add("label", instance.Definition.Nodes[2].Id);
        session.Attach(instance, source, refs, modules: library); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("value:1", instance.Definition.Nodes[2].Text);
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("value:2", instance.Definition.Nodes[2].Text);
        var other = stack.Open(instance.OwnerId, instance.Definition); session.Attach(other, source, refs, modules: library);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("value:1", other.Definition.Nodes[2].Text); Assert.AreEqual("value:2", instance.Definition.Nodes[2].Text);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task UnusedModulesDoNotExecuteAndALazyImportUsesTheCurrentEventSnapshot()
    {
        const string source = "return {on_event=function() local m=ui.import(modules.child); ui.set_text(refs.label,m.value) end}";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, Empty.Add("label", instance.Definition.Nodes[2].Id),
            ImmutableDictionary<string, string>.Empty.Add("value", "load"), modules: Library(source, "return {value=ui.get('value')}"));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty.Add("value", "event"));
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual("event", instance.Definition.Nodes[2].Text);
        var other = stack.Open(instance.OwnerId, instance.Definition);
        const string unused = "return {on_event=function() end}";
        session.Attach(other, unused, modules: Library(unused, "while true do end")); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task ANewSourceSnapshotGetsANewCacheWithoutChangingALiveInstance()
    {
        const string source = "local m=ui.import(modules.child); return {on_load=function() ui.set_text(refs.label,m.value) end,on_event=function() ui.set_text(refs.label,m.value) end}";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        var refs = Empty.Add("label", instance.Definition.Nodes[2].Id);
        session.Attach(instance, source, refs, modules: Library(source, "return {value='old'}")); await ScreenLuaSessionTests.Drain(session);
        var next = stack.Open(instance.OwnerId, instance.Definition);
        session.Attach(next, source, refs, modules: Library(source, "return {value='new'}")); await ScreenLuaSessionTests.Drain(session);
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("old", instance.Definition.Nodes[2].Text); Assert.AreEqual("new", next.Definition.Nodes[2].Text);
    }

    [TestMethod]
    [DataRow("return nil")] [DataRow("return 1")] [DataRow("return function() end")] [DataRow("return ui.null")]
    [DataRow("return os.execute('no')")] [DataRow("return require('file')")] [DataRow("return luanet.import_type('System.IO.File')")]
    [DataRow("return ui.import('not-registered')")]
    public async Task BadModulesDoNotCommitAnyStateOrVisuals(string module)
    {
        const string source = "return {on_load=function() ui.set_state(1) end,on_event=function() ui.set_state(2); ui.set_text(refs.label,'bad'); ui.import(modules.child) end}";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, Empty.Add("label", before.Nodes[2].Id), modules: Library(source, module));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        var state = session.GetState(instance);
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(state, session.GetState(instance)); Assert.AreSame(before, instance.Definition);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task NestedImportsConsumeOneInstructionBudgetAtLoadOrInsideAnEvent(bool deferred)
    {
        const string work = "local total=0; for i=1,1000 do total=total+i end; ";
        const string source = "return {on_load=function() ui.set_state(1) end,on_event=function() ui.set_state(2); ui.import(modules.child) end}";
        var rootSource = deferred ? source : "local m=ui.import(modules.child); return {on_event=function() end}";
        var root = new LuaScriptAsset(Root, "root", rootSource) { References = Empty.Add("child", Child) };
        var child = new LuaScriptAsset(Child, "child", work + "local leaf=ui.import(modules.leaf); return {}") { References = Empty.Add("leaf", Leaf) };
        var leaf = new LuaScriptAsset(Leaf, "leaf", work + "local final=ui.import(modules.final); return {}") { References = Empty.Add("final", Guid.Parse("44444444-4444-4444-4444-444444444444")) };
        var final = new LuaScriptAsset(Guid.Parse("44444444-4444-4444-4444-444444444444"), "final", work + "return {}");
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        var control = stack.Open(instance.OwnerId, instance.Definition);
        const string controlSource = "local m=ui.import(modules.child); return {on_event=function() end}";
        session.Attach(control, controlSource, modules: Library(controlSource, work + "return {}"));
        await ScreenLuaSessionTests.Drain(session); Assert.IsTrue(session.IsAvailable(control)); Assert.IsEmpty(session.Diagnostics);
        session.Attach(instance, rootSource, modules: new(Root, [root, child, leaf, final]));
        await ScreenLuaSessionTests.Drain(session);
        if (deferred)
        {
            Assert.IsEmpty(session.Diagnostics); session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty);
            await ScreenLuaSessionTests.Drain(session); Assert.AreEqual(1d, session.GetState(instance).Number);
        }
        Assert.IsTrue(session.Diagnostics.Any(d => d.Contains("20000", StringComparison.Ordinal)), string.Join('\n', session.Diagnostics));
    }

    [TestMethod]
    public async Task InvalidGraphFailsOnlyItsEntryBeforeSendingCodeToTheWorker()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var other = stack.Open(instance.OwnerId, instance.Definition);
        var connection = new ScreenLuaSessionTests.Connection(true); await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function() end}");
        session.Attach(other, Handler, modules: new(Root, [new(Root, "root", Handler) { References = Empty.Add("self", Root) }]));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsTrue(session.IsAvailable(instance)); Assert.IsFalse(session.IsAvailable(other));
        Assert.IsFalse(connection.Seen.Any(r => r.Stamp.InstanceId == other.Id));
        Assert.AreEqual(ScreenLuaIssueKind.Handler, session.Issues.Single().Kind);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task RegisteredButUndeclaredIdsCannotBeImportedEvenAfterChangingTheAliasTable(bool tamper)
    {
        var source = "local x=ui.import(" + (tamper ? "modules.forged" : "'" + Leaf.ToString("N") + "'") + "); return {on_event=function() end}";
        if (tamper) source = "modules.forged='" + Leaf.ToString("N") + "'; " + source;
        var root = new LuaScriptAsset(Root, "root", source) { References = Empty.Add("child", Child) };
        var child = new LuaScriptAsset(Child, "child", "return {}") { References = Empty.Add("leaf", Leaf) };
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, modules: new(Root, [root, child, new(Leaf, "leaf", "return {}") ]));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreEqual(0, session.AppliedBatches);
    }

    [TestMethod]
    [DataRow("handshake")] [DataRow("event")] [DataRow("close")]
    public void WorkerAcceptsModulePayloadsOnlyOnLoad(string kind)
    {
        var hello = ScreenLuaProtocolTests.Request("handshake") with
        {
            Stamp = new(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, Guid.Empty, 0), ResourceId = Guid.Empty, Event = null,
        };
        var worker = new ScreenLuaWorkerService();
        var modules = new LuaModuleBundle(Root, Empty, []);
        if (kind == "handshake")
        {
            Assert.AreEqual("error", worker.Process(hello with { Modules = modules }).Kind);
            Assert.AreEqual("ready", worker.Process(hello).Kind); return;
        }
        Assert.AreEqual("ready", worker.Process(hello).Kind);
        var definition = ScreenLuaProtocolTests.Definition();
        var load = ScreenLuaProtocolTests.Request("load") with { Stamp = hello.Stamp with { OwnerId = Guid.NewGuid(), InstanceId = Guid.NewGuid() },
            ResourceId = definition.Id, Nodes = definition.Nodes, Source = "return {on_event=function() end}", Event = null, Modules = modules };
        Assert.AreEqual("batch", worker.Process(load).Kind);
        var message = load with { Kind = kind, Stamp = load.Stamp with { RequestId = Guid.NewGuid(), Version = 1 },
            Nodes = [], Source = null, Event = kind == "event" ? new("update") : null };
        Assert.AreEqual("error", worker.Process(message).Kind);
        Assert.AreEqual(kind == "event" ? "batch" : "closed", worker.Process(message with { Modules = null }).Kind);
    }

    [TestMethod]
    public async Task WireRoundTripsRegisteredSourcesAndRejectsUnknownNestedFields()
    {
        using var stream = new MemoryStream(); var request = ScreenLuaProtocolTests.Request("load") with { Modules = Bundle(), Source = Handler };
        await ScreenLuaProtocol.WriteAsync(stream, request, CancellationToken.None); stream.Position = 0;
        var received = await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(stream, CancellationToken.None);
        Assert.AreEqual(Child, received.Modules!.Sources[0].Id); received.Modules.Validate(Handler, () => { });
        var json = JsonSerializer.Serialize(request, ScreenLuaProtocol.Json).Replace("\"RootId\":", "\"Unknown\":true,\"RootId\":", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScreenLuaRequest>(json, ScreenLuaProtocol.Json));
    }
}
