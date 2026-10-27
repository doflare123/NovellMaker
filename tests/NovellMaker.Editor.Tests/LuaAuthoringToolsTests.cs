using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.Tests;

public sealed partial class ExternalScriptTests
{
    [TestMethod]
    public async Task AnimationExampleHistorySaveAsAndReferencesRoundTrip()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); using var session = await Session(dialogs, editor: new CodeEditor());
        var before = session.Project!; session.AddAnimationsExample(); var added = session.Project!;
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); session.Redo(); Assert.IsTrue(added.ContentEquals(session.Project));
        await session.SaveAsync(); dialogs.SavePath = Path.Combine(root.Path, "copy", "animations.novell"); await session.SaveAsync(true);
        var copy = await new JsonProjectStore().LoadAsync(dialogs.SavePath); Assert.IsTrue(added.ContentEquals(copy));
        var script = copy.Scripts.Last(); Assert.AreEqual(script.Source, await File.ReadAllTextAsync(ScriptFiles.Resolve(Path.GetDirectoryName(dialogs.SavePath)!, script.Id)));
        Assert.AreEqual(copy.Scenes.Last().Id, copy.InputBindings.Single(b => b.Key == "F11").TargetSceneId);
        Assert.IsTrue(copy.Scenes.Last().Canvas.References.Values.All(id => copy.Scenes.Last().Canvas.Nodes.Any(n => n.Id == id)));
        Assert.IsEmpty(dialogs.Errors);
    }
    [TestMethod]
    public async Task AnnotationToolsPreserveSourceProjectAndUserConfigurationAndOpenJournal()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); var editor = new CodeEditor();
        using var session = await Session(dialogs, editor: editor); await session.SaveAsync();
        session.ResolveLuaJournalPath = id => Path.Combine(root.Path, "profile", id.ToString("N"), "lua-diagnostics.log");
        var before = session.Project; var source = await File.ReadAllTextAsync(session.SelectedScriptPath);
        var config = Path.Combine(root.Path, ".luarc.json"); await File.WriteAllTextAsync(config, "{\"user\":true}");
        await session.PrepareLuaAnnotationsAsync();
        Assert.AreSame(before, session.Project); Assert.AreEqual(source, await File.ReadAllTextAsync(session.SelectedScriptPath));
        Assert.AreEqual("{\"user\":true}", await File.ReadAllTextAsync(config));
        var directory = Path.Combine(root.Path, "Scripts", "NovellMakerApi");
        Assert.AreEqual(Path.Combine(directory, "README.md"), editor.Opened.Last());
        var ui = await File.ReadAllTextAsync(Path.Combine(directory, "ui.lua"));
        StringAssert.Contains(ui, "function ui.log(level, message)"); StringAssert.Contains(ui, "---@class NMEvent");
        foreach (var file in Directory.EnumerateFiles(directory, "*.lua")) LuaActionScript.ValidateSyntax(await File.ReadAllTextAsync(file), "annotations");
        await File.WriteAllTextAsync(Path.Combine(directory, "ui.lua"), "old generated file");
        await session.PrepareLuaAnnotationsAsync(); Assert.AreEqual(ui, await File.ReadAllTextAsync(Path.Combine(directory, "ui.lua")));
        var journal = session.ResolveLuaJournalPath(session.Project!.Id);
        await session.OpenLuaJournalAsync(); StringAssert.Contains(session.Status, "после запуска F6");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        try { await File.WriteAllTextAsync(journal, "fixture"); await session.OpenLuaJournalAsync(); Assert.AreEqual(journal, editor.Opened.Last()); }
        finally { File.Delete(journal); Directory.Delete(Path.GetDirectoryName(journal)!); }
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task DiagnosticsExampleRoundTripsAndSaveAsKeepsOnlyRegisteredSourceResources()
    {
        using var root = new TestRoot(); var dialogs = new Dialogs(root.Path); var editor = new CodeEditor();
        using var session = await Session(dialogs, editor: editor);
        var before = session.Project!; session.AddLuaDiagnosticsExample(); var added = session.Project!;
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); session.Redo();
        Assert.IsTrue(added.ContentEquals(session.Project)); await session.SaveAsync();
        await session.PrepareLuaAnnotationsAsync();
        var next = Path.Combine(root.Path, "copy", "project.novell"); dialogs.SavePath = next; await session.SaveAsync(true);
        var copy = await new JsonProjectStore().LoadAsync(next);
        Assert.IsTrue(added.ContentEquals(copy));
        foreach (var script in copy.Scripts) Assert.AreEqual(script.Source, await File.ReadAllTextAsync(ScriptFiles.Resolve(Path.GetDirectoryName(next)!, script.Id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(next)!, "Scripts", "NovellMakerApi")));
    }
}
