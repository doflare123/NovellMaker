using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Assets;
using SkiaSharp;
using NovellMaker.Core.Styling;
using NovellMaker.Editor.Views;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

public sealed class UiAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, ShouldRenderOnUIThread = true });
}

[TestClass]
[DoNotParallelize]
public sealed class EditorWindowTests
{
    [ThreadStatic] private static List<MainWindow>? windows;
    [TestMethod]
    public Task WindowStartsDarkWithNoDocumentAndSaveDisabled() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _);
        var window = CreateWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.AreEqual(ThemeVariant.Dark, window.ActualThemeVariant);
        Assert.IsFalse(window.FindControl<TabControl>("Documents")!.IsVisible);
        Assert.IsFalse(window.FindControl<Button>("SaveProjectButton")!.IsEffectivelyEnabled);
        Assert.IsTrue(window.FindControl<Button>("NewProjectButton")!.IsEffectivelyEnabled);
        Capture(window, "editor-empty.png");
        Assert.IsTrue(await session.RequestCloseAsync());
        window.Close();
    });

    [TestMethod]
    public Task NewButtonTypingAndSaveShortcutUpdateTheProjectAndDirtyMarker() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out var store);
        var window = CreateWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var create = window.FindControl<Button>("NewProjectButton")!;
        var point = create.TranslatePoint(new Point(create.Bounds.Width / 2, create.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.HasProject, "The toolbar button must execute the create command.");
        Assert.IsTrue(window.FindControl<TabControl>("Documents")!.IsVisible);

        var name = Find<TextBox>(window, "ProjectNameBox");
        name.Focus();
        name.SelectAll();
        window.KeyTextInput("Лесные истории");
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual("Лесные истории", session.ProjectName);
        StringAssert.Contains(window.Title!, "*");

        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, store.Saves);
        Assert.AreEqual(1, dialogs.SavePickerCalls);
        Assert.AreEqual("Лесные истории", store.Saved!.Name);
        Assert.IsFalse(session.IsDirty);
        Assert.IsFalse(window.Title!.Contains('*'));
        Capture(window, "editor-project.png");
        window.Close();
        await Task.CompletedTask;
    });

    [TestMethod]
    public Task ClosingWindowHonorsCancelAndDiscardDecisions() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        dialogs.Decision = UnsavedChangesDecision.Cancel;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.IsTrue(window.IsVisible, "Cancel must keep the unsaved project window open.");
        Assert.IsTrue(session.IsDirty);

        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.IsFalse(window.IsVisible);
        Assert.AreEqual(2, dialogs.ConfirmationCalls);
    });

    [TestMethod]
    public Task EmptyNameDisablesSaveAndShowsValidation() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out var store);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();
        var name = Find<TextBox>(window, "ProjectNameBox");
        Assert.IsTrue(name.Focus());
        name.SelectAll();
        window.KeyTextInput("   ");
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(window.FindControl<Button>("SaveProjectButton")!.IsEffectivelyEnabled);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.NameValidation));
        Assert.AreEqual(0, store.Saves);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    public Task SceneEditorHandlesTypingOneLineNavigationAndUndoThroughWindowInput() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();

        Click(window, Find<Button>(window, "AddSceneButton"));
        Assert.IsNotNull(session.ActiveScene);
        var document = session.ActiveScene;
        Click(window, Find<Button>(window, "AddLineButton"));
        var firstId = document.SelectedLine!.Id;
        var text = Find<TextBox>(window, "LineTextBox");
        Assert.IsTrue(text.Focus());
        foreach (var character in "Первая реплика") window.KeyTextInput(character.ToString());
        Assert.AreEqual("Первая реплика", document.LineText);

        Click(window, Find<Button>(window, "AddLineButton"));
        var secondId = document.SelectedLine!.Id;
        text = Find<TextBox>(window, "LineTextBox");
        Assert.IsTrue(text.Focus());
        window.KeyTextInput("Вторая реплика");
        Assert.AreEqual("Вторая реплика", document.LineText);

        Click(window, Find<Button>(window, "PreviousLineButton"));
        Assert.AreEqual(firstId, document.SelectedLine!.Id);
        Assert.AreEqual("Первая реплика", Find<TextBox>(window, "LineTextBox").Text);
        Assert.IsFalse(Find<Button>(window, "PreviousLineButton").IsEffectivelyEnabled);
        Click(window, Find<Button>(window, "NextLineButton"));
        Assert.AreEqual(secondId, document.SelectedLine!.Id);
        Assert.IsFalse(Find<Button>(window, "NextLineButton").IsEffectivelyEnabled);

        text = Find<TextBox>(window, "LineTextBox");
        Assert.IsTrue(text.Focus());
        text.SelectAll();
        window.KeyTextInput("Исправленная реплика");
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.AreEqual("Вторая реплика", document.LineText);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.AreEqual("Исправленная реплика", document.LineText);
        Capture(window, "editor-scene.png");
        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    public Task DialogueSampleMapsCssAlphaAnimationAndVoiceVisibilityToRealControls() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        session.AddScene();
        session.ActiveScene!.AddLine();
        session.ActiveScene.LineText = "Образец текста";
        session.ActiveScene.UseSceneStyle = true;
        session.ActiveScene.Css = """
            .dialogue {
              background-color: #20304080;
              color: #E0D0C0;
              width: 960px;
              left: 100px;
              bottom: 24px;
              padding: 20px;
              font-size: 26px;
              animation-name: appear;
              animation-duration: 2s;
              animation-timing-function: linear;
            }
            @keyframes appear {
              from { opacity: 0; transform: translateY(40px); }
              to { opacity: 1; transform: translateY(0px); }
            }
            """;
        Dispatcher.UIThread.RunJobs();
        var sample = Find<DialogueStyleSample>(window, "DialogueSample");
        var plate = Find<Border>(sample, "DialoguePlate");
        var body = Find<TextBlock>(sample, "DialogueText");
        Assert.AreEqual(Color.FromArgb(128, 32, 48, 64), ((SolidColorBrush)plate.Background!).Color);
        Assert.AreEqual(26, body.FontSize);
        Assert.AreEqual("Образец текста", body.Text);
        sample.SampleAt(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0.5, plate.Opacity, 0.00001);
        var transform = (TransformGroup)plate.RenderTransform!;
        Assert.AreEqual(20, ((TranslateTransform)transform.Children[1]).Y, 0.00001);
        Assert.IsTrue(Find<Button>(sample, "PlayStyleAnimationButton").IsEffectivelyEnabled);

        session.ShowVoiceProgress = false;
        session.ShowVoiceControls = false;
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(Find<StackPanel>(sample, "VoiceProgressPanel").IsVisible);
        Assert.IsFalse(Find<Button>(sample, "PauseVoiceButton").IsVisible);
        session.ShowVoiceControls = true;
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(Find<Button>(sample, "PauseVoiceButton").IsVisible);
        Assert.IsFalse(Find<Button>(sample, "PauseVoiceButton").IsEffectivelyEnabled,
            "No real voice backend is present; an authoring sample must not fake playback.");
        sample.SampleAt(TimeSpan.FromSeconds(2));
        Capture(window, "editor-dialogue-style.png");
        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    public Task ImageLibraryImportsRendersAndRenamesThenSceneBindsBackgroundWithUndo() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.UiImages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Задник.png");
            using (var bitmap = new SKBitmap(640, 360))
            {
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(new SKColor(50, 69, 87));
                using var paint = new SKPaint { Color = new SKColor(86, 116, 123) };
                canvas.DrawRect(0, 220, 640, 140, paint);
                paint.Color = new SKColor(194, 185, 153);
                canvas.DrawCircle(480, 90, 36, paint);
                paint.Color = new SKColor(36, 55, 57);
                canvas.DrawRect(110, 100, 32, 210, paint);
                canvas.DrawCircle(126, 100, 65, paint);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(source);
                encoded.SaveTo(file);
            }
            var dialogs = new UiDialogs { SavePath = Path.Combine(directory, "story.novell"), ImagePath = source };
            var session = new EditorSession(new JsonProjectStore(), dialogs, new UiGit());
            var window = CreateWindow(session);
            window.Show();
            await session.NewProjectAsync();
            Click(window, Find<Button>(window, "AssetLibraryButton"));
            Click(window, Find<Button>(window, "ImportImageButton"));
            await UntilAsync(() => session.IsIdle && session.Images.Count == 1);
            var asset = session.Images.Single();
            Assert.AreEqual(asset.Id, session.SelectedImage!.Id);
            var preview = Find<ProjectImageView>(window, "SelectedImagePreview");
            await UntilAsync(() => preview.HasImage);
            Assert.IsTrue(window.GetVisualDescendants().OfType<ProjectImageView>().Count(view => view.HasImage) >= 2,
                "Both the real thumbnail and selected full preview must render.");
            Capture(window, "editor-image-library.png");

            var name = Find<TextBox>(window, "SelectedImageNameBox");
            name.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(name.Focus());
            name.SelectAll();
            foreach (var character in "Вечерний парк") window.KeyTextInput(character.ToString());
            Assert.AreEqual("Вечерний парк", session.SelectedImageName);
            Assert.AreEqual(asset.Id, session.SelectedImage.Id);
            var filter = Find<TextBox>(window, "AssetFilterBox");
            Assert.IsTrue(filter.Focus());
            window.KeyTextInput("ПАРК");
            Assert.AreEqual(1, session.FilteredImages.Count);
            filter.SelectAll();
            window.KeyTextInput("ничего не найдено");
            Assert.AreEqual(0, session.FilteredImages.Count);
            session.AssetFilter = "";

            Click(window, Find<Button>(window, "AddSceneButton"));
            var document = session.ActiveScene!;
            Find<TabControl>(window, "SceneSections").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var picker = Find<ComboBox>(window, "BackgroundPicker");
            picker.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(asset.Id, document.BackgroundImageId, "The picker must update the scene's stable ID binding.");
            preview = Find<ProjectImageView>(window, "BackgroundPreview");
            await UntilAsync(() => preview.HasImage);
            Assert.AreEqual(asset.Id, ((BackgroundImageOption)picker.SelectedItem!).Id,
                "After asynchronous rendering the picker must still display the assigned image.");
            Capture(window, "editor-background.png");
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.IsNull(document.BackgroundImageId);
            Assert.IsFalse(preview.HasImage);
            window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
            window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
            Assert.AreEqual(asset.Id, document.BackgroundImageId);
            await UntilAsync(() => preview.HasImage);

            File.Delete(AssetPath.Resolve(directory, asset.RelativePath));
            await session.RefreshGitAsync();
            await UntilAsync(() => !preview.HasImage && preview.PreviewStatus.Contains("Не удалось"));
            StringAssert.Contains(document.BackgroundStatus, "не найден");
            Assert.AreEqual(asset.Id, document.BackgroundImageId);
            dialogs.Decision = UnsavedChangesDecision.Discard;
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task PreviewDiscardsLateCompletionAfterSelectionChangesOrViewDetaches() => WithUi(async () =>
    {
        var service = new DelayedPreviewService();
        var first = new ImageAsset(Guid.NewGuid(), "first", "Assets/Images/first.png", 10, 20);
        var second = new ImageAsset(Guid.NewGuid(), "second", "Assets/Images/second.png", 30, 15);
        var view = new ProjectImageView(service) { ProjectDirectory = Path.GetTempPath(), Asset = first };
        var window = CreateWindow(CreateSession(out _, out _));
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var firstRequest = service.Requests.Single();
        var firstLoad = view.PreviewLoad;
        view.Asset = second;
        var secondRequest = service.Requests.Last();
        Assert.IsTrue(firstRequest.Cancellation.IsCancellationRequested);
        secondRequest.Completion.SetResult(PreviewPng(30, 15));
        await UntilAsync(() => view.HasImage);
        var image = view.GetVisualDescendants().OfType<Image>().Single();
        Assert.AreEqual(new PixelSize(30, 15), ((Bitmap)image.Source!).PixelSize);
        var accepted = image.Source;

        // A backend may finish native decoding despite cancellation. Its old result must be discarded.
        firstRequest.Completion.SetResult(PreviewPng(10, 20));
        await firstLoad;
        Assert.AreSame(accepted, image.Source);

        view.Asset = first;
        var detachedRequest = service.Requests.Last();
        var detachedLoad = view.PreviewLoad;
        window.Content = null;
        Assert.IsTrue(detachedRequest.Cancellation.IsCancellationRequested);
        Assert.IsFalse(view.HasImage);
        Assert.IsNull(image.Source);
        detachedRequest.Completion.SetResult(PreviewPng(10, 20));
        await detachedLoad;
        Assert.IsFalse(view.HasImage);
        Assert.IsNull(image.Source);
        window.Content = view;
        service.Requests.Last().Completion.SetResult(PreviewPng(10, 20));
        await UntilAsync(() => view.HasImage);
        Assert.AreEqual(new PixelSize(10, 20), ((Bitmap)image.Source!).PixelSize);
        window.Close();
    });

    private static byte[] PreviewPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SlateBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class DelayedPreviewService : IImageAssetService
    {
        public List<PreviewRequest> Requests { get; } = [];
        public Task<ImageAsset> ImportAsync(string directory, string source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string source, string target, IReadOnlyList<ImageAsset> images, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadPreviewAsync(string directory, ImageAsset asset, int maximumWidth = 1280, CancellationToken cancellationToken = default)
        {
            var request = new PreviewRequest(cancellationToken, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Requests.Add(request);
            return request.Completion.Task;
        }
    }

    private sealed record PreviewRequest(CancellationToken Cancellation, TaskCompletionSource<byte[]> Completion);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.IsTrue(condition(), "Asynchronous UI operation did not reach its expected state.");
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WithUi(Func<Task> action)
    {
        await using var headless = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await headless.Dispatch(async () =>
        {
            windows = [];
            try { await action(); return true; }
            finally
            {
                foreach (var window in windows) window.Hide();
                windows = null;
            }
        }, timeout.Token);
    }

    private static MainWindow CreateWindow(EditorSession session)
    {
        var window = new MainWindow(session);
        windows!.Add(window);
        return window;
    }

    private static void Capture(Window window, string fileName)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "The actual editor must render successfully.");
        Assert.IsGreaterThan(0, frame.PixelSize.Width);
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_CAPTURE_DIR");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, fileName), PngBitmapEncoderOptions.Default);
        }
    }

    private static EditorSession CreateSession(out UiDialogs dialogs, out UiStore store)
    {
        dialogs = new UiDialogs();
        store = new UiStore();
        return new(store, dialogs, new UiGit());
    }

    private sealed class UiDialogs : IEditorDialogs
    {
        public int SavePickerCalls { get; private set; }
        public int ConfirmationCalls { get; private set; }
        public string SavePath { get; set; } = Path.Combine(Path.GetTempPath(), "test-ui.novell");
        public string? ImagePath { get; set; }
        public UnsavedChangesDecision Decision { get; set; } = UnsavedChangesDecision.Cancel;
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickImageAsync() => Task.FromResult(ImagePath);
        public Task<string?> PickSaveProjectAsync(string name)
        {
            SavePickerCalls++;
            return Task.FromResult<string?>(SavePath);
        }
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name)
        {
            ConfirmationCalls++;
            return Task.FromResult(Decision);
        }
        public Task ShowErrorAsync(string title, string message) => throw new AssertFailedException($"{title}: {message}");
    }

    private sealed class UiStore : IProjectStore
    {
        public NovelProject? Saved { get; private set; }
        public int Saves { get; private set; }
        public Task<NovelProject> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Saved!);
        public Task SaveAsync(string path, NovelProject project, CancellationToken cancellationToken = default)
        {
            Saves++;
            Saved = project;
            return Task.CompletedTask;
        }
    }

    private sealed class UiGit : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.Available, path,
                [new GitFileChange("test-ui.novell", " ", "M")], "Git: 1 изменённый файл"));
    }
}
