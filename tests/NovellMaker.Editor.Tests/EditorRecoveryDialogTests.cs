using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class EditorRecoveryDialogTests
{
    [TestMethod]
    public async Task RecoveryDialogShowsSnapshotAndReturnsRestoreWithoutChangingFiles()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        await ui.Dispatch(async () =>
        {
            var owner = new Window(); owner.Show();
            try
            {
                var pending = new EditorDialogs(owner).ConfirmRecoveryAsync(new("Несохранённая новелла", "D:/Автор/project.novell", DateTimeOffset.UtcNow, true));
                Dispatcher.UIThread.RunJobs();
                var dialog = owner.OwnedWindows.Single();
                Assert.AreEqual("Восстановление работы редактора", dialog.Title);
                var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RecoveryRestore");
                Assert.IsNotNull(button);
                using var frame = dialog.CaptureRenderedFrame();
                Assert.IsNotNull(frame);
                SaveFrame(frame, "recovery-dialog.png");
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(RecoveryDecision.Restore, await pending);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task DeletedFileDialogDisablesReloadAndClosingKeepsLocalVersion()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        await ui.Dispatch(async () =>
        {
            var owner = new Window(); owner.Show();
            try
            {
                var pending = new EditorDialogs(owner).ConfirmExternalProjectChangeAsync("D:/Автор/project.novell", true, false);
                Dispatcher.UIThread.RunJobs();
                var dialog = owner.OwnedWindows.Single();
                var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.OfType<Button>().ToArray();
                Assert.IsFalse(buttons.Single(b => (string)b.Content! == "Загрузить с диска").IsEnabled);
                using var frame = dialog.CaptureRenderedFrame(); Assert.IsNotNull(frame);
                SaveFrame(frame, "external-project-dialog.png");
                dialog.Close();
                Assert.AreEqual(ExternalProjectDecision.Keep, await pending);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None);
    }

    private static void SaveFrame(Avalonia.Media.Imaging.WriteableBitmap frame, string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NovellMaker.slnx"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts", "editor-recovery");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
