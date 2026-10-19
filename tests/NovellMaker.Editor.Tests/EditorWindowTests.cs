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
using NovellMaker.Editor.Services.Preview;
using GamePreviewRequest = NovellMaker.Editor.Services.Preview.PreviewRequest;
using Avalonia.Controls.Documents;
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
    [TestMethod]
    public Task WindowsExportDialogShowsStartSceneAndCancelsWithoutOpeningFolderPicker() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _);
        var window = CreateWindow(session); window.Show();
        await session.OpenExampleAsync(); Dispatcher.UIThread.RunJobs();
        var dialogs = new EditorDialogs(window);
        var result = dialogs.PickWindowsExportAsync(session.Project!);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var picker = Find<ComboBox>(dialog, "ExportStartScene");
        Assert.AreEqual(session.Project!.Scenes.Length, picker.ItemCount);
        StringAssert.Contains(picker.SelectedItem!.ToString()!, session.Project.Scenes[0].Name);
        Capture(dialog, "editor-windows-export.png");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsNull(await result);
        window.Close(); session.Dispose();
    });

    [TestMethod]
    public Task PhraseScriptBlocksApplyUndoSelectAndEditLuaWithNamedReferences() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session); window.Show(); Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "OpenExampleButton"));
        await UntilAsync(() => session.ActiveScene is not null && !session.IsBusy);
        Assert.AreEqual(4, session.ActiveScene!.SelectedSection);
        Assert.IsTrue(session.ExportWindowsCommand.CanExecute(null));
        Assert.IsNotNull(window.FindControl<MenuItem>("ExportWindowsMenu"));
        var inspector = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single();
        var blocks = Find<ListBox>(inspector, "ScriptBlocks");
        Assert.AreEqual(2, blocks.ItemCount);
        blocks.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var before = session.Project!.Scenes[0].ScriptBlocks[1];
        var seconds = inspector.GetVisualDescendants().OfType<NumericUpDown>().Last(); seconds.Value = 3;
        Click(window, Find<Button>(inspector, "ApplyScriptBlock"));
        Assert.AreEqual(3, ((NovellMaker.Core.Execution.MoveAction)session.Project.Scenes[0].ScriptBlocks[1].Action).Seconds);
        session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(before, session.Project.Scenes[0].ScriptBlocks[1]);
        session.Redo(); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(3, ((NovellMaker.Core.Execution.MoveAction)session.Project.Scenes[0].ScriptBlocks[1].Action).Seconds);
        session.ActiveScene.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
        StringAssert.Contains(session.ActiveScene.SelectedPhraseScriptSummary, "Во время: 1");
        Click(window, Find<Button>(window, "OpenPhraseScriptButton"));
        Assert.AreEqual(4, session.ActiveScene.SelectedSection);
        var phrases = Find<ComboBox>(inspector, "ScriptPhrasePicker"); phrases.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(session.Project.Scenes[0].Lines[2].Id, session.ActiveScene.SelectedLine!.Id);
        blocks.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
        var source = Find<TextBox>(inspector, "LuaSourceBox"); StringAssert.Contains(source.Text!, "refs.correct");
        StringAssert.Contains(Find<TextBlock>(inspector, "LuaQueueHelp").Text!, "следующий Lua-блок");
        Assert.IsFalse(source.Text!.Contains(session.Project.Characters[0].Id.ToString(), StringComparison.Ordinal));
        source.Text += "\n-- "; source.SelectionStart = source.SelectionEnd = source.Text.Length;
        Find<TextBox>(inspector, "LuaReferenceAlias").Text = "traveller";
        var scriptScroll = Find<ScrollViewer>(inspector, "ScriptInspectorScroll");
        scriptScroll.Offset = new Vector(0, scriptScroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(inspector, "InsertLuaReference"));
        Capture(window, "editor-phrase-script-insert.png");
        Assert.IsTrue(source.Text!.Contains("refs.traveller"), Find<TextBlock>(inspector, "ScriptError").Text);
        Click(window, Find<Button>(inspector, "ApplyScriptBlock"));
        Assert.IsTrue(string.IsNullOrEmpty(Find<TextBlock>(inspector, "ScriptError").Text), Find<TextBlock>(inspector, "ScriptError").Text);
        var lua = session.Project.Scenes[0].ScriptBlocks.Select(b => b.Action).OfType<NovellMaker.Core.Execution.ScriptAction>().Single(a => a.Name == "terminal-result.lua");
        Assert.AreEqual(session.Project.Characters[0].Id, lua.References["traveller"]);
        StringAssert.Contains(lua.Source, "refs.traveller");
        scriptScroll.Offset = default; Dispatcher.UIThread.RunJobs();
        Capture(window, "editor-phrase-script.png");
        var count = session.Project.Scenes[0].ScriptBlocks.Length;
        var remove = inspector.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Удалить"));
        Click(window, remove); Assert.AreEqual(count, session.Project.Scenes[0].ScriptBlocks.Length);
        dialogs.AcceptDeletion = true; Click(window, remove);
        await UntilAsync(() => session.Project.Scenes[0].ScriptBlocks.Length == count - 1);
        session.Undo(); Assert.AreEqual(count, session.Project.Scenes[0].ScriptBlocks.Length);
        session.Dispose(); window.Hide();
    });

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
            Find<TabControl>(window, "SceneSections").SelectedIndex = 3;
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

    [TestMethod]
    public Task CharacterLibraryAndLinePickersRenderSpeakerStateColorsAndAssignedBackground() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.UiCharacters", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var background = Path.Combine(directory, "park.png");
            var normal = Path.Combine(directory, "normal.png");
            var sad = Path.Combine(directory, "sad.png");
            File.WriteAllBytes(background, PreviewPng(640, 360));
            File.WriteAllBytes(normal, SpritePng(SKColors.Coral));
            File.WriteAllBytes(sad, SpritePng(SKColors.MediumPurple));
            var dialogs = new UiDialogs { SavePath = Path.Combine(directory, "story.novell") };
            var previewService = new RecordingPreview();
            var session = new EditorSession(new JsonProjectStore(), dialogs, new UiGit(), previewService: previewService);
            var window = CreateWindow(session);
            window.Width = 1600;
            window.Height = 1000;
            window.Show();
            await session.NewProjectAsync();
            foreach (var file in new[] { background, normal, sad }) { dialogs.ImagePath = file; await session.ImportImageAsync(); }
            Find<TreeView>(window, "ProjectExplorer").SelectedItem = session.Explorer[0].Children.Single(entry => entry.IsCharacterLibrary);
            Dispatcher.UIThread.RunJobs();
            Click(window, Find<Button>(window, "AddCharacterButton"));
            TypeInto(window, Find<TextBox>(window, "CharacterNameBox"), "Анна");
            Click(window, Find<Button>(window, "AddCharacterStateButton"));
            var normalId = session.SelectedState!.Id;
            TypeInto(window, Find<TextBox>(window, "CharacterStateNameBox"), "normal");
            Find<ComboBox>(window, "CharacterStateImageBox").SelectedItem = session.Images[1];
            Dispatcher.UIThread.RunJobs();
            Click(window, Find<Button>(window, "AddCharacterStateButton"));
            var sadId = session.SelectedState!.Id;
            TypeInto(window, Find<TextBox>(window, "CharacterStateNameBox"), "sad");
            Find<ComboBox>(window, "CharacterStateImageBox").SelectedItem = session.Images[2];
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(normalId, session.SelectedCharacter!.DefaultStateId);
            await UntilAsync(() => Find<ProjectImageView>(window, "CharacterStatePreview").HasImage);
            Capture(window, "editor-characters.png");

            Click(window, Find<Button>(window, "AddSceneButton"));
            Click(window, Find<Button>(window, "AddLineButton"));
            var document = session.ActiveScene!;
            var lineId = document.SelectedLine!.Id;
            document.BackgroundImageId = session.Images[0].Id;
            Find<ComboBox>(window, "SpeakerPicker").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(normalId, document.SampleFrame!.Characters.Single().StateId);
            Find<ComboBox>(window, "CharacterStatePicker").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            TypeInto(window, Find<TextBox>(window, "LineTextBox"), "Привет, §#E08080я грущу§r.");
            var sample = Find<DialogueStyleSample>(window, "DialogueSample");
            sample.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var sceneBackground = Find<ProjectImageView>(sample, "SceneBackground");
            var layer = Find<Canvas>(sample, "CharacterLayer");
            await UntilAsync(() => sceneBackground.HasImage && layer.Children.OfType<ProjectImageView>().Single().HasImage);
            Assert.AreEqual(sadId, document.SampleFrame!.Characters.Single().StateId);
            Assert.AreEqual("Анна", Find<TextBlock>(sample, "DialogueSpeaker").Text);
            var body = Find<TextBlock>(sample, "DialogueText");
            var colored = body.Inlines!.OfType<Run>().Single(run => run.Text == "я грущу");
            Assert.AreEqual(Color.FromRgb(224, 128, 128), ((SolidColorBrush)colored.Foreground!).Color);
            Assert.IsTrue(layer.Children.OfType<ProjectImageView>().Single().TransparentBackground);
            Capture(window, "editor-scene-character.png");

            Find<TabControl>(window, "SceneSections").SelectedItem = Find<TabItem>(window, "PlacementTab");
            Dispatcher.UIThread.RunJobs();
            sample = Find<DialogueStyleSample>(window, "PlacementSample");
            layer = Find<Canvas>(sample, "CharacterLayer");
            var xBox = Find<NumericUpDown>(window, "PlacementXBox");
            xBox.Value = 25;
            Find<NumericUpDown>(window, "PlacementYBox").Value = 85;
            Find<NumericUpDown>(window, "PlacementScaleBox").Value = 75;
            Find<NumericUpDown>(window, "PlacementLayerBox").Value = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(25m, document.PlacementX);
            Assert.AreEqual(85m, document.PlacementY);
            Assert.AreEqual(75m, document.PlacementScale);
            Assert.AreEqual(4m, document.PlacementLayer);
            sample.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var sprite = layer.Children.OfType<ProjectImageView>().Single();
            Assert.AreEqual(320, Canvas.GetLeft(sprite) + sprite.Width / 2, 0.01);
            Assert.AreEqual(612, Canvas.GetTop(sprite) + sprite.Height, 0.01);
            var beforeDrag = document.SelectedLine!.Model.Placements.Single().Placement!;
            var start = sprite.TranslatePoint(new Point(sprite.Width / 2, 60), window)!.Value;
            var end = layer.TranslatePoint(new Point(450, Canvas.GetTop(sprite) + 90), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var afterDrag = document.SelectedLine!.Model.Placements.Single().Placement!;
            Assert.AreNotEqual(beforeDrag.X, afterDrag.X, "Dragging the rendered sprite must write a placement command.");
            Assert.AreEqual(450, afterDrag.X * 1280, 2);
            Assert.AreEqual(beforeDrag.Y * 720 + 30, afterDrag.Y * 720, 2);
            Assert.AreEqual(beforeDrag.Scale, afterDrag.Scale);
            session.Undo();
            Assert.AreEqual(beforeDrag, document.SelectedLine.Model.Placements.Single().Placement, "A complete drag is one undo operation.");
            session.Redo();
            Assert.AreEqual(afterDrag, document.SelectedLine.Model.Placements.Single().Placement);
            // Escape cancels a live drag without writing another command.
            var cancelStart = sprite.TranslatePoint(new Point(sprite.Width / 2, 60), window)!.Value;
            window.MouseDown(cancelStart, MouseButton.Left);
            window.MouseMove(cancelStart + new Point(30, 0));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(cancelStart + new Point(30, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(afterDrag, document.SelectedLine.Model.Placements.Single().Placement);
            Assert.AreEqual(afterDrag.X * 1280, Canvas.GetLeft(sprite) + sprite.Width / 2, 0.01);
            Capture(window, "editor-placement.png");
            Find<TabControl>(window, "SceneSections").SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();


            Click(window, Find<Button>(window, "PreviewSceneButton"));
            await UntilAsync(() => previewService.Request is not null && session.IsIdle);
            Assert.AreEqual(lineId, previewService.Request!.LineId);
            Assert.AreEqual(document.Id, previewService.Request.SceneId);
            Assert.AreEqual(directory, previewService.Request.AssetsRoot);
            Assert.IsTrue(session.IsDirty, "Launching preview must use the current draft without implicitly saving it.");
            Assert.AreEqual(0, (await new JsonProjectStore().LoadAsync(dialogs.SavePath)).Scenes.Length);
            var captured = previewService.Request.Project;
            document.LineText = "Позже изменённый текст";
            Assert.AreEqual("Привет, §#E08080я грущу§r.", captured.Scenes.Single().Lines.Single().Texts["ru"]);
            Click(window, Find<Button>(window, "AddLineButton"));
            Find<ComboBox>(window, "HideCharacterPicker").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(0, document.SampleFrame!.Characters.Length);
            Assert.AreEqual("", Find<TextBlock>(Find<DialogueStyleSample>(window, "DialogueSample"), "DialogueSpeaker").Text);
            document.Navigate(-1);
            Assert.AreEqual(sadId, document.SampleFrame!.Characters.Single().StateId);
            dialogs.Decision = UnsavedChangesDecision.Discard;
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.IsTrue(previewService.Disposed);
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task WorkspaceSupportsMouseDockResizeHideRestoreAndKeepsDocumentState() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync(); session.AddScene();
        session.ActiveScene!.AddLine();
        session.ActiveScene.LineText = "Текст сохраняется при перестройке панелей";
        Dispatcher.UIThread.RunJobs();
        var scene = window.FindControl<ContentControl>("ScenePane")!;
        var docs = window.FindControl<Grid>("DocumentsPane")!;
        var sample = Find<DialogueStyleSample>(window, "DialogueSample");
        var layout = window.WorkspaceLayout;
        Find<TabControl>(window, "SceneSections").SelectedIndex = 2;
        var initialWidth = docs.Bounds.Width;
        layout.Hide("explorer"); Dispatcher.UIThread.RunJobs();
        Assert.IsGreaterThan(initialWidth, docs.Bounds.Width);
        layout.Hide("information"); Dispatcher.UIThread.RunJobs();
        var frame = scene.GetVisualAncestors().OfType<Border>().First();
        var header = frame.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Экран сцены");
        var start = header.TranslatePoint(new Point(12, 10), window)!.Value;
        var target = docs.TranslatePoint(new Point(docs.Bounds.Width - 15, docs.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left); window.MouseMove(target); window.MouseUp(target, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var sceneOrigin = scene.TranslatePoint(default, window)!.Value;
        var docsOrigin = docs.TranslatePoint(default, window)!.Value;
        Assert.IsGreaterThan(docsOrigin.X, sceneOrigin.X, "Scene must dock to the right of documents.");
        Assert.AreEqual(docsOrigin.Y, sceneOrigin.Y, 3);
        Assert.AreSame(session.ActiveScene, Find<DialogueStyleSample>(window, "DialogueSample").PlacementEditor);
        Assert.AreEqual("Текст сохраняется при перестройке панелей", session.ActiveScene.LineText);
        Assert.AreEqual(2, Find<TabControl>(window, "SceneSections").SelectedIndex, "Docking must preserve the active editor section.");
        Find<TabControl>(window, "SceneSections").SelectedIndex = 0;
        var split = scene.GetVisualAncestors().OfType<Grid>().First(g => g.Children.OfType<GridSplitter>().Any());
        var splitter = split.Children.OfType<GridSplitter>().Single();
        var divider = splitter.TranslatePoint(new Point(2, splitter.Bounds.Height / 2), window)!.Value;
        var beforeResize = docs.Bounds.Width;
        window.MouseDown(divider, MouseButton.Left); window.MouseMove(divider + new Point(60, 0));
        window.MouseUp(divider + new Point(60, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.IsGreaterThan(beforeResize, docs.Bounds.Width);
        var savedLayout = layout.ExportLayout();
        layout.Reset(); layout.ImportLayout(savedLayout); Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(layout.IsVisible("explorer"));
        Assert.AreEqual(beforeResize + 60, docs.Bounds.Width, 4);
        Capture(window, "workspace-docked.png");
        var stableLayout = layout.ExportLayout();
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => layout.ImportLayout("{\"Pane\":\"unknown\"}"));
        Assert.AreEqual(stableLayout, layout.ExportLayout());
        header = scene.GetVisualAncestors().OfType<Border>().First().GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "Экран сцены");
        start = header.TranslatePoint(new Point(12, 10), window)!.Value;
        target = docs.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(start, MouseButton.Left); window.MouseMove(target);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(target, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(stableLayout, layout.ExportLayout(), "Escape must cancel docking.");
        foreach (var id in new[] { "documents", "scene", "messages", "explorer", "information" }) layout.Hide(id);
        Dispatcher.UIThread.RunJobs();
        layout.Show("scene"); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(layout.IsVisible("scene"));
        Assert.AreSame(session.ActiveScene, Find<DialogueStyleSample>(window, "DialogueSample").PlacementEditor);
        layout.Reset(); Dispatcher.UIThread.RunJobs();
        Capture(window, "workspace-default.png");
        window.Close();
    });

    [TestMethod]
    public Task ExplorerModePickerChangesActualFilesAndParticipatesInUndo() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "novell-explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Assets", "Images"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Assets", "Images", "readme.txt"), "ресурсы");
        try
        {
            var session = CreateSession(out var dialogs, out _);
            dialogs.SavePath = Path.Combine(directory, "test-ui.novell");
            dialogs.Decision = UnsavedChangesDecision.Discard;
            var window = CreateWindow(session); window.Show();
            await session.NewProjectAsync(); await session.SaveAsync();
            Dispatcher.UIThread.RunJobs();
            var picker = Find<ComboBox>(window, "ExplorerModePicker");
            picker.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(session.Project!.ExplorerShowsFiles);
            Assert.IsTrue(session.IsDirty);
            var assets = session.Explorer[0].Items.Single(item => item.Label == "Assets/");
            var images = assets.Items.Single(item => item.Label == "Images/");
            Assert.AreEqual("readme.txt", images.Items.Single().Label);
            Assert.IsFalse(assets.ExpandInitially);
            var tree = Find<TreeView>(window, "FolderNavigation");
            Assert.IsFalse(window.FindControl<TreeView>("ProjectExplorer")!.IsVisible);
            var folder = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => ReferenceEquals(item.DataContext, assets));
            folder.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            var imageFolder = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => ReferenceEquals(item.DataContext, images));
            tree.SelectedItem = images; Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(Find<ListBox>(window, "FolderTiles").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "readme.txt"));
            await File.WriteAllTextAsync(Path.Combine(directory, "added.txt"), "обновление");
            await session.RefreshGitAsync(); Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(session.Explorer[0].Items.Any(item => item.Label == "added.txt"));
            session.Undo(); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(0, picker.SelectedIndex);
            Assert.IsFalse(session.IsDirty);
            session.Redo(); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, picker.SelectedIndex);
            await session.SaveAsync();
            Capture(window, "workspace-files.png");
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task ScenePanelRemainsVisibleWhenMovedLeftTopBottomAndBack() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.AddLine();
        session.ActiveScene.LineText = "Сцена остаётся видимой после переноса";
        var layout = window.WorkspaceLayout;
        foreach (var id in new[] { "information", "explorer", "messages" }) layout.Hide(id);
        Dispatcher.UIThread.RunJobs();
        var scene = window.FindControl<ContentControl>("ScenePane")!;
        var docs = window.FindControl<Grid>("DocumentsPane")!;
        foreach (var side in new[] { Dock.Left, Dock.Top, Dock.Bottom, Dock.Right, Dock.Left })
        {
            layout.Move("scene", "documents", side); Dispatcher.UIThread.RunJobs();
            var a = new Rect(scene.TranslatePoint(default, window)!.Value, scene.Bounds.Size);
            var b = new Rect(docs.TranslatePoint(default, window)!.Value, docs.Bounds.Size);
            Assert.IsFalse(a.Intersects(b), $"Panels must not overlap after docking {side}.");
            Assert.IsGreaterThan(100d, a.Width); Assert.IsGreaterThan(50d, a.Height);
            var sample = Find<DialogueStyleSample>(window, "DialogueSample");
            var point = sample.TranslatePoint(new Point(sample.Bounds.Width / 2, sample.Bounds.Height / 2), window)!.Value;
            var visible = window.GetVisualAt(point);
            Assert.IsTrue(visible is not null && visible.GetVisualAncestors().Contains(scene),
                $"Scene preview must be visible and not covered by documents after docking {side}.");
            Assert.AreEqual(session.ActiveScene.LineText, Find<TextBlock>(sample, "DialogueText").Text);
        }
        Capture(window, "workspace-scene-left.png");
        var saved = layout.ExportLayout(); layout.Reset(); layout.ImportLayout(saved);
        Dispatcher.UIThread.RunJobs();
        Assert.IsLessThan(docs.TranslatePoint(default, window)!.Value.X, scene.TranslatePoint(default, window)!.Value.X);
        layout.Hide("documents"); Dispatcher.UIThread.RunJobs();
        var host = window.FindControl<Grid>("Workspace")!;
        Assert.AreEqual(host.Bounds.Width - 2, scene.Bounds.Width, 2, "A panel promoted to root must fill the workspace.");
        window.Close();
    });

    [TestMethod]
    public Task FolderExplorerOpensScenesAndReflectsRenameAndUndo() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene();
        var first = session.ActiveScene!; first.Name = "Вступление";
        session.AddScene();
        session.ExplorerModeIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var tree = Find<TreeView>(window, "FolderNavigation");
        var browser = window.FindControl<ProjectFolderView>("ProjectFolders")!.Browser!;
        var scenes = session.Explorer[0].Items.Single(entry => entry.Label == "Сцены/");
        Assert.AreEqual(2, scenes.Items.Count);
        var folder = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => ReferenceEquals(item.DataContext, scenes));
        folder.IsExpanded = true; Dispatcher.UIThread.RunJobs();
        var firstFolder = scenes.Items.Single(item => item.SceneId == first.Id);
        Assert.IsTrue(firstFolder.IsFolder);
        tree.SelectedItem = firstFolder; Dispatcher.UIThread.RunJobs();
        Assert.AreNotEqual(first.Id, session.ActiveScene!.Id, "Browsing folders must not change the active document.");
        var tiles = Find<ListBox>(window, "FolderTiles");
        Assert.AreEqual(first.Id, browser.CurrentFolder!.SceneId);
        Assert.AreEqual(first.Id, browser.Tiles.Single().Entry.SceneId);
        tiles.SelectedItem = browser.Tiles.Single(); Dispatcher.UIThread.RunJobs(); Assert.IsTrue(tiles.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(first.Id, session.ActiveScene!.Id);
        Assert.AreEqual(first.Id, firstFolder.Items.Single().SceneId);
        session.ActiveScene.Name = "Новый пролог"; Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.Explorer[0].Items.Single(item => item.Label == "Сцены/").Items.Any(item => item.Label == "Новый пролог/"));
        session.Undo(); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.Explorer[0].Items.Single(item => item.Label == "Сцены/").Items.Any(item => item.Label == "Вступление/"));
        Assert.IsTrue(tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext is ExplorerEntry { Label: "Сцены/" }).IsExpanded,
            "Editing a scene must keep its project folder expanded.");
        Capture(window, "workspace-project-folders.png");
        window.Close();
    });

    [TestMethod]
    public Task FolderTilesNavigateSearchPreviewImagesAndKeepLocationAfterRefresh() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "novell-tiles-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(directory, "Novel");
        Directory.CreateDirectory(projectDirectory);
        foreach (var name in new[] { "Audio", "Backgrounds", "Scripts", "Sprites", "Fonts", "Video" }) Directory.CreateDirectory(Path.Combine(projectDirectory, name));
        try
        {
            var session = CreateSession(out var dialogs, out _);
            dialogs.Decision = UnsavedChangesDecision.Discard;
            dialogs.SavePath = Path.Combine(projectDirectory, "project.novell");
            var window = CreateWindow(session); window.Show();
            await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.Name = "Вступление";
            await session.SaveAsync();
            var source = Path.Combine(directory, "park.png"); await File.WriteAllBytesAsync(source, PreviewPng(160, 90));
            dialogs.ImagePath = source; await session.ImportImageAsync();
            session.ExplorerModeIndex = 1;
            var activeDocument = session.SelectedDocument;
            window.WorkspaceLayout.Hide("information"); window.WorkspaceLayout.Hide("scene"); window.WorkspaceLayout.Hide("messages");
            window.WorkspaceLayout.Move("explorer", "documents", Dock.Bottom);
            Dispatcher.UIThread.RunJobs();
            var view = window.FindControl<ProjectFolderView>("ProjectFolders")!;
            var browser = view.Browser!;
            var tiles = Find<ListBox>(view, "FolderTiles");
            Assert.IsTrue(tiles.ItemsPanelRoot is WrapPanel);
            Assert.IsGreaterThan(5, browser.Tiles.Count);
            Capture(window, "folder-browser-tiles.png");
            var assets = tiles.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is FolderTile { Name: "Assets" });
            var point = assets.TranslatePoint(new Point(30, 30), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Assets/", browser.CurrentFolder!.Label, "A double-click must enter the folder.");
            Assert.AreSame(activeDocument, session.SelectedDocument);
            browser.Open(browser.Tiles.Single(tile => tile.Name == "Images").Entry);
            Dispatcher.UIThread.RunJobs();
            var thumbnail = tiles.GetVisualDescendants().OfType<ProjectImageView>().Single(image => image.IsVisible);
            await UntilAsync(() => thumbnail.HasImage);
            Assert.IsTrue(browser.Tiles.Single().HasImage);
            var search = Find<TextBox>(view, "FolderSearchBox");
            TypeInto(window, search, "missing"); Assert.IsTrue(browser.IsEmpty);
            TypeInto(window, search, "park"); Assert.AreEqual(1, browser.Tiles.Count);
            TypeInto(window, search, "");
            await session.RefreshGitAsync(); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Images/", browser.CurrentFolder!.Label);
            Assert.IsTrue(browser.Tiles.Single().HasImage);
            Click(window, Find<Button>(view, "FolderBackButton"));
            Assert.AreEqual("Assets/", browser.CurrentFolder!.Label);
            browser.GoForward(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual("Images/", browser.CurrentFolder!.Label);
            Capture(window, "folder-browser-thumbnail.png");
            Click(window, Find<Button>(view, "FolderUpButton"));
            Assert.AreEqual("Assets/", browser.CurrentFolder!.Label);
            var rootCrumb = view.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is FolderBreadcrumb { Depth: 0 });
            Click(window, rootCrumb);
            Assert.AreEqual(session.ProjectName, browser.CurrentFolder!.Label);
            var address = browser.CurrentPath;
            window.WorkspaceLayout.Move("explorer", "documents", Dock.Left); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(address, browser.CurrentPath);
            session.ExplorerModeIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(window.FindControl<TreeView>("ProjectExplorer")!.IsVisible);
            Assert.IsFalse(view.IsVisible);
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task DeleteKeyInHierarchyAndTilesRequiresConfirmationAndPreservesTextEditing() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene();
        var id = session.ActiveScene!.Id;
        Dispatcher.UIThread.RunJobs();
        var tree = window.FindControl<TreeView>("ProjectExplorer")!;
        tree.SelectedItem = session.Explorer[0].Children.Single(e => e.Label.StartsWith("Сцены")).Children.Single();
        Dispatcher.UIThread.RunJobs();
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext is ExplorerEntry entry && entry.SceneId == id);
        Assert.IsTrue(row.Focus());
        PressDelete(window); Assert.AreEqual(1, dialogs.Deletions.Count); Assert.AreEqual(1, session.Project!.Scenes.Length);
        dialogs.AcceptDeletion = true; PressDelete(window); await UntilAsync(() => !session.IsBusy);
        Assert.AreEqual(0, session.Project.Scenes.Length);
        session.Undo(); session.ExplorerModeIndex = 1; Dispatcher.UIThread.RunJobs();
        var browserView = window.FindControl<ProjectFolderView>("ProjectFolders")!;
        var browser = browserView.Browser!;
        browser.Open(browser.Tiles.Single(tile => tile.Name == "Сцены").Entry); Dispatcher.UIThread.RunJobs();
        var tiles = Find<ListBox>(browserView, "FolderTiles");
        tiles.SelectedItem = browser.Tiles.Single(); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(tiles.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus());
        dialogs.AcceptDeletion = false; PressDelete(window);
        Assert.AreEqual(3, dialogs.Deletions.Count); Assert.AreEqual(1, session.Project.Scenes.Length);
        dialogs.AcceptDeletion = true; PressDelete(window); await UntilAsync(() => !session.IsBusy);
        Assert.AreEqual(0, session.Project.Scenes.Length); Assert.IsTrue(browser.IsEmpty);
        var search = Find<TextBox>(browserView, "FolderSearchBox");
        TypeInto(window, search, "abc"); search.SelectionStart = 0; search.SelectionEnd = 0; search.CaretIndex = 0;
        PressDelete(window);
        Assert.AreEqual("bc", search.Text); Assert.AreEqual(4, dialogs.Deletions.Count);
        window.Close();
    });

    [TestMethod]
    public Task SceneAndLineDeleteButtonsConfirmBeforeChangingTheProject() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene();
        var document = session.ActiveScene!; document.AddLine(); document.LineText = "Не удалять без подтверждения";
        Dispatcher.UIThread.RunJobs();
        var lineButton = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, document.DeleteLineCommand));
        Click(window, lineButton); Assert.AreEqual(1, document.Lines.Count); Assert.AreEqual(1, dialogs.Deletions.Count);
        dialogs.AcceptDeletion = true; Click(window, lineButton); Assert.AreEqual(0, document.Lines.Count);
        session.Undo(); Assert.AreEqual("Не удалять без подтверждения", session.ActiveScene!.LineText);
        dialogs.AcceptDeletion = false;
        var sceneButton = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "Удалить сцену");
        Click(window, sceneButton); Assert.AreEqual(1, session.Project!.Scenes.Length);
        dialogs.AcceptDeletion = true; Click(window, sceneButton); Assert.AreEqual(0, session.Project.Scenes.Length);
        Assert.AreEqual(4, dialogs.Deletions.Count); window.Close();
    });

    [TestMethod]
    public Task ActualDeleteDialogCancelsWithEscapeAndAcceptsOnlyExplicitDelete() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        var dialogs = new EditorDialogs(window);
        var result = dialogs.ConfirmDeleteAsync("Сцена 1", "Будут удалены все реплики сцены. Отмена: Ctrl+Z.");
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        Assert.IsTrue(Find<Button>(dialog, "CancelDeleteButton").IsFocused);
        Capture(dialog, "confirm-deletion.png");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsFalse(await result);
        result = dialogs.ConfirmDeleteAsync("Сцена 1", "Будут удалены все реплики сцены.");
        Dispatcher.UIThread.RunJobs(); dialog = window.OwnedWindows.Single();
        Click(dialog, Find<Button>(dialog, "ConfirmDeleteButton"));
        Assert.IsTrue(await result); window.Close();
    });

    [TestMethod]
    public Task CharacterStateAndImageButtonsUseTheSameConfirmationAndUndo() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "novell-delete-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard;
            dialogs.SavePath = Path.Combine(directory, "project.novell");
            var source = Path.Combine(directory, "source.png"); await File.WriteAllBytesAsync(source, PreviewPng(32, 32));
            dialogs.ImagePath = source;
            var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); await session.ImportImageAsync();
            var file = Path.Combine(directory, session.Project!.Images.Single().RelativePath);
            session.AddCharacter(); session.AddCharacterState(); Dispatcher.UIThread.RunJobs();
            var stateId = session.SelectedState!.Id;
            var stateButton = Find<Button>(window, "DeleteCharacterStateButton");
            Click(window, stateButton); Assert.AreEqual(stateId, session.SelectedState!.Id);
            dialogs.AcceptDeletion = true; Click(window, stateButton); Assert.IsTrue(session.Project.Characters[0].States.IsEmpty);
            session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(stateId, session.SelectedState!.Id);
            var characterButton = Find<Button>(window, "DeleteCharacterButton");
            dialogs.AcceptDeletion = false; Click(window, characterButton); Assert.AreEqual(1, session.Project.Characters.Length);
            dialogs.AcceptDeletion = true; Click(window, characterButton); Assert.IsTrue(session.Project.Characters.IsEmpty);
            session.Undo(); session.SelectImage(session.Project.Images.Single().Id); Dispatcher.UIThread.RunJobs();
            var imageButton = Find<Button>(window, "DeleteImageButton");
            dialogs.AcceptDeletion = false; Click(window, imageButton); Assert.IsTrue(File.Exists(file));
            dialogs.AcceptDeletion = true; Click(window, imageButton); await UntilAsync(() => !session.IsBusy);
            Assert.IsFalse(File.Exists(file)); Assert.IsTrue(session.Project.Images.IsEmpty);
            session.Undo(); Assert.IsTrue(File.Exists(file)); Assert.AreEqual(1, session.Project.Images.Length);
            Assert.AreEqual(6, dialogs.Deletions.Count);
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    });

    private static void PressDelete(Window window)
    {
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void TypeInto(Window window, TextBox box, string value)
    {
        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(box.Focus());
        box.SelectAll();
        window.KeyTextInput(value);
        Dispatcher.UIThread.RunJobs();
    }

    private static byte[] SpritePng(SKColor color)
    {
        using var bitmap = new SKBitmap(80, 180);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = color };
        canvas.DrawCircle(40, 35, 25, paint);
        canvas.DrawRect(15, 60, 50, 110, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class RecordingPreview : IPreviewService
    {
        public GamePreviewRequest? Request { get; private set; }
        public bool Disposed { get; private set; }
        public Task StartAsync(GamePreviewRequest request, Action<string> report) { Request = request; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }

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
        control.BringIntoView();
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
        public bool AcceptDeletion { get; set; }
        public List<string> Deletions { get; } = [];
        public Task<bool> ConfirmDeleteAsync(string name, string consequences)
        { Deletions.Add(name); return Task.FromResult(AcceptDeletion); }
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

