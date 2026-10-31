using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Views;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class EditorRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    [DoNotParallelize]
    public async Task ForcedProcessTerminationRecoversAutomaticSnapshotFromActualEditorWindow()
    {
        using var fixture = new Fixture();
        var ready = Path.Combine(fixture.Root, "ready");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(EditorRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName~RecoveryProcessWriterProbe");
        start.ArgumentList.Add("/Logger:console;verbosity=detailed");
        var settingsPath = Path.Combine(fixture.Root, "probe.runsettings");
        new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("RunSettings", new System.Xml.Linq.XElement("TestRunParameters",
            new System.Xml.Linq.XElement("Parameter", new System.Xml.Linq.XAttribute("name", "RecoveryProbeRoot"),
                new System.Xml.Linq.XAttribute("value", fixture.Root))))).Save(settingsPath);
        start.ArgumentList.Add("/Settings:" + settingsPath);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!File.Exists(ready) && !process.HasExited) await Task.Delay(100, deadline.Token);
            Assert.IsTrue(File.Exists(ready), process.HasExited ? await output + await errors : "Автоматический снимок не появился.");
            var main = await File.ReadAllBytesAsync(fixture.Dialogs.SavePath!);
            process.Kill(entireProcessTree: true); // No Closing event, save prompt or Dispose.
            await process.WaitForExitAsync(deadline.Token);
            using var recovered = fixture.Session();
            await recovered.RecoverOnStartupAsync();
            Assert.AreEqual("Правки перед принудительным завершением", recovered.ProjectName);
            Assert.AreEqual("Реплика из аварийного процесса", recovered.ActiveScene!.LineText);
            Assert.IsTrue(recovered.IsDirty);
            CollectionAssert.AreEqual(main, await File.ReadAllBytesAsync(fixture.Dialogs.SavePath!));
            await recovered.SaveAsync();
            Assert.IsFalse(recovered.IsDirty);
            Assert.IsEmpty(fixture.Recovery.Candidates());
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    // Invoked in an isolated testhost so the parent can kill an actual live Avalonia editor.
    [TestMethod]
    [DoNotParallelize]
    public async Task RecoveryProcessWriterProbe()
    {
        if (!TestContext.Properties.TryGetValue("RecoveryProbeRoot", out var parameter) || parameter is not string root) return;
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        await ui.Dispatch(async () =>
        {
            var dialogs = new Dialogs { SavePath = Path.Combine(root, "project.novell") };
            using var session = new EditorSession(new JsonProjectStore(), dialogs, new Git(), recovery: new(Path.Combine(root, "Recovery")));
            var window = new MainWindow(session); window.Show(); Dispatcher.UIThread.RunJobs();
            while (session.IsBusy) await Task.Delay(10);
            await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.AddLine();
            await session.SaveAsync();
            // Edit real GUI controls. The timer is started by MainWindow.Opened, not by this test.
            var settings = new ProjectSettingsView { DataContext = session };
            settings.FindControl<TextBox>("ProjectNameBox")!.Text = "Правки перед принудительным завершением";
            session.ActiveScene.SelectedSection = 0;
            var scene = new SceneEditorView { DataContext = session.ActiveScene };
            scene.FindControl<TextBox>("LineTextBox")!.Text = "Реплика из аварийного процесса";
            Dispatcher.UIThread.RunJobs();
            var expected = session.Project;
            while (!session.RecoveryStatus.StartsWith("Снимок восстановления", StringComparison.Ordinal)) await Task.Delay(50);
            Assert.AreEqual("Правки перед принудительным завершением", expected!.Name);
            Assert.AreEqual("Реплика из аварийного процесса", expected.Scenes[0].Lines[0].Texts["ru"]);
            await File.WriteAllTextAsync(Path.Combine(root, "ready"), session.RecoveryStatus);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return true;
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task CrashRecoveryRestoresDialogueIdsAndDraftWithoutChangingMainFile()
    {
        using var fixture = new Fixture();
        var dialogs = fixture.Dialogs;
        Guid sceneId, lineId;
        byte[] original;
        using (var crashed = fixture.Session())
        {
            await crashed.NewProjectAsync();
            crashed.AddScene(); crashed.ActiveScene!.AddLine();
            crashed.ActiveScene.LineText = "Сохранённая реплика";
            await crashed.SaveAsync();
            original = await File.ReadAllBytesAsync(dialogs.SavePath!);
            sceneId = crashed.ActiveScene.Id; lineId = crashed.ActiveScene.SelectedLine!.Id;
            crashed.ActiveScene.LineText = "Несохранённая реплика после сбоя";
            crashed.ProjectName = "Черновик автора";
            await crashed.AutosaveAsync();
            Assert.IsTrue(crashed.IsDirty);
            Assert.HasCount(1, fixture.Recovery.Candidates().ToArray());
        } // Dispose does not run the normal close confirmation/cleanup.
        using var recovered = fixture.Session();
        await recovered.RecoverOnStartupAsync();
        Assert.AreEqual("Черновик автора", recovered.ProjectName);
        Assert.AreEqual("Несохранённая реплика после сбоя", recovered.ActiveScene!.LineText);
        Assert.AreEqual(sceneId, recovered.ActiveScene.Id);
        Assert.AreEqual(lineId, recovered.ActiveScene.SelectedLine!.Id);
        Assert.IsTrue(recovered.IsDirty);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(dialogs.SavePath!));
        await recovered.SaveAsync();
        Assert.IsFalse(recovered.IsDirty);
        Assert.IsEmpty(fixture.Recovery.Candidates());
        var saved = await new JsonProjectStore().LoadAsync(dialogs.SavePath!);
        Assert.IsTrue(saved.ContentEquals(recovered.Project));
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task NeverSavedProjectRecoversUnfinishedNamesLanguageCssAndReleaseSettings()
    {
        using var fixture = new Fixture();
        using (var crashed = fixture.Session())
        {
            await crashed.NewProjectAsync(); crashed.AddScene(); crashed.ActiveScene!.AddLine();
            crashed.ActiveScene.LineText = "Незавершённая работа";
            crashed.ActiveScene.Name = "";
            crashed.ProjectName = "";
            crashed.DefaultLanguage = "";
            crashed.DialogueCss = "unfinished {";
            crashed.ReleaseCheckEnabled = true;
            crashed.ReleaseCheckVersion = "unfinished";
            await crashed.AutosaveAsync();
            StringAssert.Contains(crashed.RecoveryStatus, "Снимок восстановления");
        }
        using var recovered = fixture.Session();
        await recovered.RecoverOnStartupAsync();
        Assert.IsTrue(recovered.HasProject);
        Assert.IsNull(recovered.ProjectPath);
        Assert.AreEqual("", recovered.ProjectName);
        Assert.AreEqual("", recovered.ActiveScene!.Name);
        Assert.AreEqual("Незавершённая работа", recovered.Project!.Scenes[0].Lines[0].Texts["ru"]);
        Assert.AreEqual("", recovered.DefaultLanguage);
        Assert.AreEqual("unfinished {", recovered.DialogueCss);
        Assert.AreEqual("unfinished", recovered.ReleaseCheckVersion);
        Assert.IsFalse(recovered.AreDocumentsValid);
        Assert.IsTrue(recovered.IsDirty);
        Assert.IsEmpty(fixture.Dialogs.Errors);
    }

    [TestMethod]
    public async Task RecoveryRoundTripsCompleteExampleIncludingPolymorphicActionsAndLua()
    {
        using var fixture = new Fixture();
        var project = BuiltInExample.Create(fixture.Root);
        using var lease = fixture.Recovery.Create();
        await lease.WriteAsync(new(1, project, project, project.Name, fixture.Dialogs.SavePath, "revision", DateTimeOffset.UtcNow, null, null));
        var loaded = await lease.ReadAsync();
        Assert.IsTrue(project.ContentEquals(loaded.Project));
        Assert.IsTrue(project.ContentEquals(loaded.SavedProject));
    }

    [TestMethod]
    public async Task ActiveEditorSnapshotIsNotOfferedByAnotherInstance()
    {
        using var fixture = new Fixture();
        using var active = fixture.Session();
        await active.NewProjectAsync(); await active.AutosaveAsync();
        using var second = fixture.Session();
        await second.RecoverOnStartupAsync();
        Assert.IsFalse(second.HasProject);
        Assert.IsEmpty(fixture.Dialogs.Offers);
        active.Dispose();
        await second.RecoverOnStartupAsync();
        Assert.IsTrue(second.HasProject);
        Assert.HasCount(1, fixture.Dialogs.Offers);
    }

    [TestMethod]
    [DataRow(RecoveryDecision.Later, 1)]
    [DataRow(RecoveryDecision.Discard, 0)]
    public async Task DecliningRecoveryPreservesOrDeletesOnlyTheSnapshot(RecoveryDecision decision, int expected)
    {
        using var fixture = new Fixture();
        using (var crashed = fixture.Session()) { await crashed.NewProjectAsync(); await crashed.AutosaveAsync(); }
        fixture.Dialogs.Recovery = decision;
        using var session = fixture.Session();
        await session.RecoverOnStartupAsync();
        Assert.IsFalse(session.HasProject);
        Assert.HasCount(expected, fixture.Recovery.Candidates().ToArray());
        Assert.IsFalse(File.Exists(fixture.Dialogs.SavePath));
    }

    [TestMethod]
    [DataRow(UnsavedChangesDecision.Cancel, false, 1)]
    [DataRow(UnsavedChangesDecision.Discard, true, 0)]
    [DataRow(UnsavedChangesDecision.Save, true, 0)]
    public async Task NormalCloseCleansSnapshotOnlyAfterSaveOrExplicitDiscard(UnsavedChangesDecision decision, bool closed, int remaining)
    {
        using var fixture = new Fixture(); using var session = fixture.Session();
        await session.NewProjectAsync(); await session.AutosaveAsync();
        fixture.Dialogs.Unsaved = decision;
        Assert.AreEqual(closed, await session.RequestCloseAsync());
        Assert.HasCount(remaining, fixture.Recovery.Candidates().ToArray());
    }

    [TestMethod]
    public async Task UndoToSavedStateRemovesOutdatedRecovery()
    {
        using var fixture = new Fixture(); using var session = fixture.Session();
        await session.NewProjectAsync(); await session.SaveAsync();
        session.ProjectName = "Временная правка"; await session.AutosaveAsync();
        session.Undo(); Assert.IsFalse(session.IsDirty);
        await session.AutosaveAsync();
        Assert.IsEmpty(fixture.Recovery.Candidates());
    }

    [TestMethod]
    public async Task DamagedSnapshotIsRetainedAndDoesNotBlockOtherRecovery()
    {
        using var fixture = new Fixture();
        using (var crashed = fixture.Session()) { await crashed.NewProjectAsync(); await crashed.AutosaveAsync(); }
        var damaged = Path.Combine(fixture.Root, "Recovery", "damaged.recovery.json");
        await File.WriteAllTextAsync(damaged, "{ broken");
        using var session = fixture.Session(); await session.RecoverOnStartupAsync();
        Assert.IsTrue(session.HasProject);
        Assert.IsTrue(File.Exists(damaged));
        Assert.IsTrue(session.Diagnostics.Any(d => d.Contains("Не удалось прочитать снимок")));
    }

    [TestMethod]
    public async Task FailedAutosavePreservesLastValidSnapshotAndReportsFailure()
    {
        using var fixture = new Fixture();
        using (var crashed = fixture.Session())
        {
            await crashed.NewProjectAsync(); crashed.ProjectName = "Последний целый снимок";
            await crashed.AutosaveAsync();
            var path = fixture.Recovery.Candidates().Single();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                crashed.ProjectName = "Новый черновик";
                await crashed.AutosaveAsync();
                StringAssert.Contains(crashed.RecoveryStatus, "Ошибка автосохранения");
            }
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        using var session = fixture.Session(); await session.RecoverOnStartupAsync();
        Assert.AreEqual("Последний целый снимок", session.ProjectName);
    }

    [TestMethod]
    [DataRow("changed")]
    [DataRow("deleted")]
    [DataRow("broken")]
    public async Task ExternalChangesAfterCrashArePreservedUntilExplicitSaveAs(string change)
    {
        using var fixture = new Fixture();
        var path = fixture.Dialogs.SavePath!;
        using (var crashed = fixture.Session())
        {
            await crashed.NewProjectAsync(); await crashed.SaveAsync();
            crashed.ProjectName = "Несохранённый черновик"; await crashed.AutosaveAsync();
        }
        if (change == "deleted") File.Delete(path);
        else if (change == "broken") await File.WriteAllTextAsync(path, "{ broken");
        else await new JsonProjectStore().SaveAsync(path, NovelProject.Create("Чужие правки"));
        var external = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        using var session = fixture.Session(); await session.RecoverOnStartupAsync();
        Assert.IsTrue(fixture.Dialogs.Offers.Single().FileChanged);
        Assert.AreEqual("Несохранённый черновик", session.ProjectName);
        await session.SaveAsync(); Assert.IsTrue(session.IsDirty);
        Assert.HasCount(1, fixture.Dialogs.Errors);
        if (external is not null) CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(path));
        else Assert.IsFalse(File.Exists(path));
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "recovered.novell");
        await session.SaveAsync(saveAs: true);
        Assert.IsFalse(session.IsDirty);
        Assert.IsEmpty(fixture.Recovery.Candidates());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalNotificationKeepsDraftAndCanReloadExplicitly(bool dirty)
    {
        using var fixture = new Fixture(); using var session = fixture.Session();
        await session.NewProjectAsync(); await session.SaveAsync();
        if (dirty) { session.ProjectName = "Моя версия"; await session.AutosaveAsync(); }
        await new JsonProjectStore().SaveAsync(fixture.Dialogs.SavePath!, NovelProject.Create("Внешняя версия"));
        fixture.Dialogs.External = ExternalProjectDecision.Keep;
        await session.CheckExternalProjectAsync(); await session.CheckExternalProjectAsync();
        Assert.AreEqual(1, fixture.Dialogs.ExternalPrompts);
        Assert.AreEqual(dirty, session.IsDirty);
        Assert.AreNotEqual("Внешняя версия", session.ProjectName);
        // A subsequent external edit is a new notification; Reload explicitly discards the local draft.
        await new JsonProjectStore().SaveAsync(fixture.Dialogs.SavePath!, NovelProject.Create("Следующая внешняя версия"));
        fixture.Dialogs.External = ExternalProjectDecision.Reload;
        await session.CheckExternalProjectAsync();
        Assert.AreEqual("Следующая внешняя версия", session.ProjectName);
        Assert.IsFalse(session.IsDirty);
        Assert.IsEmpty(fixture.Recovery.Candidates());
        Assert.IsEmpty(fixture.Dialogs.Errors);
    }

    [TestMethod]
    public async Task RecoveredLuaIsNotSilentlyReplacedByWorkingFiles()
    {
        using var fixture = new Fixture();
        string sourceFile;
        using (var crashed = fixture.Session())
        {
            await crashed.NewProjectAsync(); crashed.AddScene(); crashed.ActiveScene!.AddLine(); crashed.AddScript(); await crashed.SaveAsync();
            sourceFile = Path.Combine(fixture.Root, "Scripts", crashed.Project!.Scripts[0].Id.ToString("N") + ".lua");
            await File.WriteAllTextAsync(sourceFile, "-- текст на момент снимка");
            await crashed.RefreshExternalScriptsAsync(); await crashed.AutosaveAsync();
        }
        await File.WriteAllTextAsync(sourceFile, "-- изменён после сбоя");
        var preview = new Preview();
        using var recovered = fixture.Session(preview); await recovered.RecoverOnStartupAsync();
        await recovered.RefreshGitAsync(); await recovered.SaveAsync();
        Assert.AreEqual("-- текст на момент снимка", recovered.Project!.Scripts[0].Source);
        Assert.AreEqual("-- изменён после сбоя", await File.ReadAllTextAsync(sourceFile));
        Assert.IsTrue(recovered.IsDirty);
        await recovered.PreviewSceneAsync();
        Assert.IsNotNull(preview.Request);
        Assert.AreEqual("-- текст на момент снимка", preview.Request.Project.Scripts[0].Source);
        var target = Path.Combine(fixture.Root, "Recovered"); Directory.CreateDirectory(target);
        fixture.Dialogs.SavePath = Path.Combine(target, "project.novell");
        await recovered.SaveAsync(saveAs: true);
        Assert.IsFalse(recovered.IsDirty);
        Assert.AreEqual("-- текст на момент снимка", await File.ReadAllTextAsync(Path.Combine(target, "Scripts", Path.GetFileName(sourceFile))));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovellMaker.Recovery.Tests", Guid.NewGuid().ToString("N"));
        internal Dialogs Dialogs { get; } = new();
        internal EditorRecoveryStore Recovery { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Dialogs.SavePath = Path.Combine(Root, "project.novell");
            Recovery = new(Path.Combine(Root, "Recovery"));
        }
        internal EditorSession Session(Services.Preview.IPreviewService? preview = null) => new(new JsonProjectStore(), Dialogs, new Git(), previewService: preview, recovery: Recovery);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Dialogs : IEditorDialogs
    {
        internal string? SavePath { get; set; }
        internal RecoveryDecision Recovery { get; set; } = RecoveryDecision.Restore;
        internal UnsavedChangesDecision Unsaved { get; set; } = UnsavedChangesDecision.Cancel;
        internal ExternalProjectDecision External { get; set; }
        internal int ExternalPrompts { get; private set; }
        internal List<RecoveryOffer> Offers { get; } = [];
        internal List<string> Errors { get; } = [];
        public Task<string?> PickOpenProjectAsync() => Task.FromResult(SavePath);
        public Task<string?> PickSaveProjectAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName) => Task.FromResult(Unsaved);
        public Task<RecoveryDecision> ConfirmRecoveryAsync(RecoveryOffer offer) { Offers.Add(offer); return Task.FromResult(Recovery); }
        public Task<ExternalProjectDecision> ConfirmExternalProjectChangeAsync(string path, bool hasUnsavedChanges, bool canReload)
        { ExternalPrompts++; return Task.FromResult(External); }
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string root, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }
    private sealed class Preview : Services.Preview.IPreviewService
    {
        internal Services.Preview.PreviewRequest? Request { get; private set; }
        public Task StartAsync(Services.Preview.PreviewRequest request, Action<string> report) { Request = request; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
