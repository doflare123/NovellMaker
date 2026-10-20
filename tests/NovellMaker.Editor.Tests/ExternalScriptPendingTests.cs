namespace NovellMaker.Editor.Tests;

public sealed partial class ExternalScriptTests
{
    [TestMethod]
    public async Task BackgroundSyntaxValidationLeavesEditorUsableAndRejectsOldDiagnostics()
    {
        using var root = new TestRoot();
        using var session = await Session(new Dialogs(root.Path));
        await session.SaveAsync();
        var checks = new List<(TaskCompletionSource Completion, CancellationToken Token)>();
        session.ValidateLuaSyntax = (_, _, token) =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            checks.Add((completion, token)); return completion.Task;
        };
        session.UpdateScript(session.SelectedScript! with { Source = "-- first" });
        var first = session.SyntaxValidationTask;
        Assert.IsTrue(session.CanEdit); Assert.IsFalse(session.IsBusy);
        StringAssert.Contains(session.SelectedScriptDiagnostic, "Проверка синтаксиса");
        session.UpdateScript(session.SelectedScript! with { Source = "-- second" });
        var second = session.SyntaxValidationTask;
        Assert.IsTrue(checks[0].Token.IsCancellationRequested);
        checks[1].Completion.SetResult(); await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
        checks[0].Completion.SetException(new ArgumentException("old-error")); await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
        Assert.AreEqual("-- second", session.SelectedScript!.Source);
    }

    [TestMethod]
    public async Task FileChangedDuringValidationCannotBeReportedAsAValidatedOldSnapshot()
    {
        using var root = new TestRoot();
        using var session = await Session(new Dialogs(root.Path));
        await session.SaveAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ValidateLuaSyntax = (_, _, _) => completion.Task;
        session.UpdateScript(session.SelectedScript! with { Source = "-- checked" });
        var pending = session.SyntaxValidationTask;
        await File.WriteAllTextAsync(session.SelectedScriptPath, "-- changed outside");
        completion.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(session.SelectedScriptDiagnostic, "изменён во время проверки");
        session.ValidateLuaSyntax = (_, _, _) => Task.CompletedTask;
        await session.RefreshExternalScriptsAsync();
        Assert.AreEqual("-- changed outside", session.SelectedScript!.Source);
        Assert.AreEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
    }

    [TestMethod]
    public async Task DisposingEditorCancelsValidationWithoutPublishingLateDiagnostics()
    {
        using var root = new TestRoot();
        using var session = await Session(new Dialogs(root.Path));
        await session.SaveAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        session.ValidateLuaSyntax = (_, _, token) => { captured = token; return completion.Task; };
        session.UpdateScript(session.SelectedScript! with { Source = "-- pending" });
        var pending = session.SyntaxValidationTask;
        var count = session.Diagnostics.Count;
        session.Dispose();
        Assert.IsTrue(captured.IsCancellationRequested); Assert.IsFalse(session.CanEdit);
        completion.SetException(new ArgumentException("late-error")); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(count, session.Diagnostics.Count);
    }

    [TestMethod]
    public async Task DisposingEditorDuringPreviewValidationNeverLaunchesPreview()
    {
        using var root = new TestRoot();
        var dialogs = new Dialogs(root.Path); var preview = new Preview();
        using var session = await Session(dialogs, preview);
        await session.SaveAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ValidateLuaSyntax = (_, _, _) => completion.Task;
        session.UpdateScript(session.SelectedScript! with { Source = "-- pending" });
        session.OpenScene(session.Project!.Scenes[0].Id);
        var operation = session.PreviewSceneAsync();
        Assert.IsFalse(operation.IsCompleted);
        session.Dispose(); completion.SetCanceled();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, preview.Starts); Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task CloseRequestCancelsPendingPreviewBeforeAskingToLeaveProject()
    {
        using var root = new TestRoot();
        var dialogs = new Dialogs(root.Path); var preview = new Preview();
        using var session = await Session(dialogs, preview);
        await session.SaveAsync();
        session.ValidateLuaSyntax = (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        session.UpdateScript(session.SelectedScript! with { Source = "-- pending" });
        session.OpenScene(session.Project!.Scenes[0].Id);
        var operation = session.PreviewSceneAsync();
        Assert.IsTrue(session.IsBusy);
        Assert.IsTrue(await session.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await operation;
        Assert.AreEqual(0, preview.Starts); Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileFailureDuringPendingValidationCanRecoverWithIdenticalSource(bool invalidUtf8)
    {
        using var root = new TestRoot();
        using var session = await Session(new Dialogs(root.Path));
        await session.SaveAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ValidateLuaSyntax = (_, _, _) => completion.Task;
        session.UpdateScript(session.SelectedScript! with { Source = "-- recoverable" });
        var pending = session.SyntaxValidationTask;
        var file = session.SelectedScriptPath;
        if (invalidUtf8) await File.WriteAllBytesAsync(file, [0xff, 0xfe]); else File.Delete(file);
        completion.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreNotEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
        await File.WriteAllTextAsync(file, "-- recoverable");
        session.ValidateLuaSyntax = (_, _, _) => Task.CompletedTask;
        await session.RefreshExternalScriptsAsync();
        Assert.AreEqual("Синтаксис корректен", session.SelectedScriptDiagnostic);
    }
}
