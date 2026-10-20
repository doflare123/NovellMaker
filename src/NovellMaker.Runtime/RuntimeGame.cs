using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame : Game
{
    private const int LogicalWidth = RuntimeInputMapper.LogicalWidth;
    private const int LogicalHeight = RuntimeInputMapper.LogicalHeight;

    private readonly GraphicsDeviceManager graphics;
    private readonly RuntimeArguments arguments;
    private readonly ScenePlayer player;
    private readonly bool showNavigationControls;
    private readonly Dictionary<Guid, Texture2D> assetTextures = [];
    private readonly Dictionary<Guid, string> assetDiagnostics = [];
    private SpriteBatch? spriteBatch;
    private RenderTarget2D? sceneTarget;
    private Texture2D? pixel;
    private RenderedTexture? dialogueTexture;
    private RenderedTexture? diagnosticsTexture;
    private RenderedTexture? navigationTexture;
    private FrameRenderKey? renderedFrameKey;
    private string diagnosticsKey = string.Empty;
    private KeyboardState previousKeyboard;
    private MouseState previousMouse;
    private bool inputActive;
    private TimeSpan lineElapsed;
    private Guid? initialLineId;
    private Guid? afterNextLineId;
    private Guid? afterPreviousLineId;
    private bool smokeNextResult;
    private bool smokePreviousResult;
    private bool smokeAdvanced;
    private bool smokeReturned;
    private bool smokeWritten;
    private int drawFrames;
    private int pendingLuaDrawFrames;
    private int pendingScreenLuaDrawFrames;
    private int initialVisibleCharacterCount;
    private bool initialFrameCaptured;
    private int afterNextVisibleCharacterCount;
    private int afterPreviousVisibleCharacterCount;
    private readonly List<(Guid Id, Rectangle Bounds, RenderedTexture Texture)> interactionButtons = [];
    private string interactionKey = "";
    private readonly RuntimeSmokeStep[]? smokePlan;
    private readonly List<RuntimeActionTrace> actionTrace = [];
    private RuntimeSmokeStep? pendingSmokeStep;
    private bool pendingSmokeSuccess;
    private RenderedTexture? actionHelp;
    private string saveMessage = "F7/F8 — слот · Enter — дальше · F9 — звук · P — пауза";
    private string SavePath => RuntimeStorage.SavePath(player.Execution.State.ProjectId, arguments.Package is not null);
    private bool UsesGameState => player.UsesActions || player.IsGamePlayback;

    public RuntimeGame(RuntimeArguments arguments, ScenePlayer player, bool showNavigationControls)
    {
        this.arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.showNavigationControls = showNavigationControls;
        if (arguments.SmokePlanPath is { } plan) smokePlan = JsonSerializer.Deserialize<RuntimeSmokeStep[]>(File.ReadAllText(plan)) ?? throw new ArgumentException("Пустой smoke-план.");
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = LogicalWidth,
            PreferredBackBufferHeight = LogicalHeight,
            SynchronizeWithVerticalRetrace = arguments.SmokeOutputPath is null,
        };
        IsFixedTimeStep = arguments.SmokeOutputPath is null;
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = arguments.Package?.Title ?? "NovellMaker — предпросмотр сцены";
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent()
    {
        InitializeAudio();
        spriteBatch = new SpriteBatch(GraphicsDevice);
        sceneTarget = new RenderTarget2D(
            GraphicsDevice,
            LogicalWidth,
            LogicalHeight,
            false,
            SurfaceFormat.Color,
            DepthFormat.None,
            preferredMultiSampleCount: 0,
            RenderTargetUsage.PreserveContents);
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData([Color.White]);
        initialLineId = player.Frame.LineId;
        initialVisibleCharacterCount = player.Frame.Characters.Length;
        initialFrameCaptured = !player.Execution.HasPendingLua;
        RefreshFrameResources();
        RefreshTextTextures();
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        screens.Poll();
        var hostActive = arguments.SmokeOutputPath is not null || IsActive;
        screens.Stack.SetActive(hostActive);
        var paused = screens.Stack.PausesScenario || !hostActive;
        player.Execution.IsPaused = paused;
        SynchronizeAudio();
        var scenarioTime = player.Execution.State.Time;
        if (!paused && !UsesGameState) lineElapsed += gameTime.ElapsedGameTime;
        if (UsesGameState)
        {
            var line = player.Frame.LineId;
            if (smokePlan is null && !paused) player.Tick(gameTime.ElapsedGameTime);
            else if (!paused) player.PollLua();
            lineElapsed += TimeSpan.FromSeconds(Math.Max(0, player.Execution.State.Time - scenarioTime));
            if (line != player.Frame.LineId) lineElapsed = TimeSpan.Zero;
            RefreshFrameResources();
            RefreshTextTextures();
            RefreshInteractions();
        }

        if (arguments.SmokeOutputPath is not null)
        {
            if (!initialFrameCaptured && !player.Execution.HasPendingLua)
            {
                initialLineId = player.Frame.LineId;
                initialVisibleCharacterCount = player.Frame.Characters.Length;
                initialFrameCaptured = true;
            }
            if (smokePlan is null) AdvanceSmokeSequence(); else AdvanceActionSmoke();
        }
        else if (IsActive && inputActive)
        {
            var keyboard = Keyboard.GetState();
            var mouse = Mouse.GetState();
            if (Pressed(keyboard, Keys.Escape))
            {
                if (!RouteScreen(new(ScreenInputKind.Back))) Exit();
            }
            else if (Pressed(keyboard, Keys.F9)) ToggleMixer();
            else if (Pressed(keyboard, Keys.P)) { screens.TogglePause(); RefreshMixer(); }
            else if (Pressed(keyboard, Keys.Tab)) RouteScreen(new(keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift)
                ? ScreenInputKind.FocusPrevious : ScreenInputKind.FocusNext));
            else if (UsesGameState && screens.Stack.Instances.IsEmpty && Pressed(keyboard, Keys.F7)) SaveGame();
            else if (UsesGameState && screens.Stack.Instances.IsEmpty && Pressed(keyboard, Keys.F8)) LoadGame();
            else if (Pressed(keyboard, Keys.Enter) || Pressed(keyboard, Keys.Space))
            {
                if (!RouteScreen(new(ScreenInputKind.Activate))) ApplyAction(RuntimeAction.Next);
            }
            else if (screens.Stack.Instances.IsEmpty && Pressed(keyboard, Keys.Right)) ApplyAction(RuntimeAction.Next);
            else if (screens.Stack.Instances.IsEmpty && Pressed(keyboard, Keys.Left))
            {
                ApplyAction(RuntimeAction.Previous);
            }
            else if (mouse.LeftButton != previousMouse.LeftButton)
            {
                var letterbox = RuntimeInputMapper.CalculateLetterbox(
                    GraphicsDevice.PresentationParameters.BackBufferWidth,
                    GraphicsDevice.PresentationParameters.BackBufferHeight);
                var mapped = RuntimeInputMapper.TryMapWindowToLogical(new Point(mouse.X, mouse.Y), letterbox, out var logical);
                var down = mouse.LeftButton == ButtonState.Pressed;
                var consumed = RouteScreen(new(down ? ScreenInputKind.PointerDown : ScreenInputKind.PointerUp,
                    mapped ? logical.X : -1, mapped ? logical.Y : -1));
                if (!consumed && down && mapped)
                {
                    if (!ClickLogical(logical) && showNavigationControls && RuntimeInputMapper.HitTestNavigation(
                        logical,
                        player.Frame.CanPrevious,
                        player.Frame.CanNext) is { } action)
                    { ApplyAction(action); }
                }
            }

            previousKeyboard = keyboard;
            previousMouse = mouse;
        }
        else { previousKeyboard = Keyboard.GetState(); previousMouse = Mouse.GetState(); }

        inputActive = IsActive;
        screens.Poll();
        if (screens.Scripts?.Diagnostics.IsEmpty == false) RefreshTextTextures();
        player.Execution.IsPaused = screens.Stack.PausesScenario || !hostActive;
        SynchronizeAudio();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (spriteBatch is null || sceneTarget is null || pixel is null)
        {
            return;
        }

        GraphicsDevice.SetRenderTarget(sceneTarget);
        GraphicsDevice.Clear(new Color(18, 20, 24));
        spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);
        DrawBackground(spriteBatch, player.Frame.Background);
        DrawCharacters(spriteBatch, player.Frame.Characters);
        DrawDialogue(spriteBatch, player.Frame);
        DrawDiagnostics(spriteBatch);
        DrawNavigationControls(spriteBatch);
        foreach (var button in interactionButtons) spriteBatch.Draw(button.Texture.Texture, button.Bounds, Color.White);
        if (actionHelp is not null) spriteBatch.Draw(actionHelp.Texture, new Vector2(16, 16), Color.White);
        DrawPause(spriteBatch);
        DrawMixer(spriteBatch);
        spriteBatch.End();

        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);
        var destination = RuntimeInputMapper.CalculateLetterbox(GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
        spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.Opaque,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);
        spriteBatch.Draw(sceneTarget, destination, Color.White);
        spriteBatch.End();

        drawFrames++;
        if (player.Execution.HasPendingLua) pendingLuaDrawFrames++;
        if (screens.HasPending) pendingScreenLuaDrawFrames++;
        if (arguments.SmokeOutputPath is not null && drawFrames >= 4 &&
            (smokePlan is null || actionTrace.Count == smokePlan.Length) && !player.Execution.HasPendingLua && !screens.HasPending && !smokeWritten)
        {
            WriteSmokeArtifacts(sceneTarget);
            smokeWritten = true;
            Exit();
        }

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        player.Dispose();
        screens.Dispose();
        foreach (var texture in pauseTextures) texture.Texture.Dispose();
        audio?.Dispose();
        foreach (var button in mixerButtons) button.Texture.Dispose();
        actionHelp?.Dispose();
        foreach (var button in interactionButtons) button.Texture.Dispose();
        diagnosticsTexture?.Dispose();
        dialogueTexture?.Dispose();
        navigationTexture?.Dispose();
        foreach (var texture in assetTextures.Values)
        {
            texture.Dispose();
        }

        assetTextures.Clear();
        pixel?.Dispose();
        sceneTarget?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }

    private void AdvanceSmokeSequence()
    {
        if (player.Execution.HasPendingLua || screens.HasPending) return;
        if (drawFrames >= 1 && !smokeAdvanced)
        {
            smokeNextResult = ApplyAction(RuntimeAction.Next);
            afterNextLineId = player.Frame.LineId;
            afterNextVisibleCharacterCount = player.Frame.Characters.Length;
            smokeAdvanced = true;
        }
        else if (drawFrames >= 2 && smokeAdvanced && !smokeReturned)
        {
            smokePreviousResult = ApplyAction(RuntimeAction.Previous);
            afterPreviousLineId = player.Frame.LineId;
            afterPreviousVisibleCharacterCount = player.Frame.Characters.Length;
            smokeReturned = true;
        }
    }

    private bool ClickLogical(Point logical)
    {
        var hit = interactionButtons.FirstOrDefault(b => b.Bounds.Contains(logical));
        if (hit.Id == Guid.Empty) return false;
        var changed = player.Execution.State.Choice is not null ? player.Choose(hit.Id) : player.Click(hit.Id);
        if (changed) { lineElapsed = TimeSpan.Zero; RefreshFrameResources(); RefreshTextTextures(); RefreshInteractions(); }
        return changed;
    }

    private void AdvanceActionSmoke()
    {
        if (player.Execution.HasPendingLua || screens.HasPending) return;
        if (pendingSmokeStep is { } finished)
        {
            actionTrace.Add(CaptureTrace(finished.Kind, pendingSmokeSuccess));
            pendingSmokeStep = null;
            return;
        }
        var index = actionTrace.Count;
        if (index < 0 || index >= smokePlan!.Length) return;
        var step = smokePlan[index];
        var success = true;
        var path = Path.ChangeExtension(arguments.SmokeReportPath!, ".save.json");
        switch (step.Kind)
        {
            case "tick": player.Tick(TimeSpan.FromSeconds(step.Seconds)); break;
            case "next": success = ApplyAction(RuntimeAction.Next); break;
            case "click":
                var consumed = RouteScreen(new(ScreenInputKind.PointerDown, step.X, step.Y));
                success = RouteScreen(new(ScreenInputKind.PointerUp, step.X, step.Y)) || consumed;
                if (!success) success = ClickLogical(new Point(step.X, step.Y));
                break;
            case "mixer": ToggleMixer(); break;
            case "pause": screens.TogglePause(); break;
            case "back": success = RouteScreen(new(ScreenInputKind.Back)); break;
            case "activate": success = RouteScreen(new(ScreenInputKind.Activate)); break;
            case "tab": success = RouteScreen(new(ScreenInputKind.FocusNext)); break;
            case "save": GameSave.SaveAsync(path, player.Execution).GetAwaiter().GetResult(); break;
            case "load": player.LoadSave(File.ReadAllText(path)); screens.Reset(); RefreshMixer(); break;
            case "quicksave": success = SaveGame(); break;
            case "quickload": success = LoadGame(); break;
            default: throw new ArgumentException("Неизвестный шаг smoke-плана.");
        }
        RefreshFrameResources(); RefreshTextTextures(); RefreshInteractions();
        if (player.Execution.HasPendingLua || screens.HasPending) { pendingSmokeStep = step; pendingSmokeSuccess = success; }
        else actionTrace.Add(CaptureTrace(step.Kind, success));
    }

    private RuntimeActionTrace CaptureTrace(string kind, bool success)
    {
        player.Execution.IsPaused = screens.Stack.PausesScenario;
        SynchronizeAudio();
        return new(kind, success, player.Execution.State, player.Frame.Text)
        {
            ScreenCount = screens.Stack.Instances.Length,
            PausedAudioSources = audioBackend.PausedSources,
            AudioSources = audio?.ActiveSources ?? 0,
            AudioOpenedSources = audioBackend.OpenedSources,
            AudioSubmittedBytes = audioBackend.SubmittedBytes,
        };
    }

    private bool ApplyAction(RuntimeAction action)
    {
        var changed = action switch
        {
            RuntimeAction.Next => player.Next(),
            RuntimeAction.Previous => player.Previous(),
            _ => false,
        };
        if (!changed)
        {
            return false;
        }

        lineElapsed = TimeSpan.Zero;
        RefreshFrameResources();
        RefreshTextTextures();
        return true;
    }

    private bool Pressed(KeyboardState keyboard, Keys key) =>
        keyboard.IsKeyDown(key) && previousKeyboard.IsKeyUp(key);

    private bool SaveGame()
    {
        var success = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            GameSave.SaveAsync(SavePath, player.Execution).GetAwaiter().GetResult();
            saveMessage = "Игра сохранена · F8 — загрузить · Enter — продолжить";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { saveMessage = "Не удалось сохранить: " + error.Message; success = false; }
        RefreshInteractions();
        return success;
    }

    private bool LoadGame()
    {
        var success = true;
        try
        {
            player.LoadSave(File.ReadAllText(SavePath));
            screens.Reset(); RefreshMixer();
            saveMessage = "Игра загружена · F7 — сохранить · Enter — продолжить";
            lineElapsed = TimeSpan.Zero;
            RefreshFrameResources(); RefreshTextTextures();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { saveMessage = "Не удалось загрузить: " + error.Message; success = false; }
        RefreshInteractions();
        return success;
    }

    private void RefreshInteractions()
    {
        var state = player.Execution.State;
        var buttons = new List<(Guid Id, string Label, Rectangle Bounds)>();
        if (state.Choice is { } choice)
            buttons.AddRange(choice.Options.Select((o, i) => (o.Id, o.Text, new Rectangle(380, 140 + i * 48, 520, 44))));
        else if (state.Wait?.Mode == "interaction")
            buttons.AddRange(state.Hotspots.Where(h => h.Enabled && (state.Wait.Token is null || state.Wait.Token == h.ObjectId)).Select(h =>
                (h.ObjectId, h.Label, new Rectangle((int)(h.X * LogicalWidth), (int)(h.Y * LogicalHeight), Math.Max(1, (int)(h.Width * LogicalWidth)), Math.Max(1, (int)(h.Height * LogicalHeight))))));
        var key = System.Text.Json.JsonSerializer.Serialize(buttons.Select(b => new { b.Id, b.Label, b.Bounds.X, b.Bounds.Y, b.Bounds.Width, b.Bounds.Height })) + saveMessage + state.Completed;
        if (key == interactionKey) return;
        interactionKey = key;
        foreach (var button in interactionButtons) button.Texture.Dispose();
        interactionButtons.Clear();
        foreach (var b in buttons) interactionButtons.Add((b.Id, b.Bounds, DialogueTextureRenderer.RenderButton(GraphicsDevice, b.Label, b.Bounds.Width, b.Bounds.Height)));
        actionHelp?.Dispose();
        actionHelp = DialogueTextureRenderer.RenderButton(GraphicsDevice, state.Completed ? "Сценарий завершён · F8 — загрузить · Escape — закрыть" : saveMessage, 1040, 40);
    }

    private void RefreshFrameResources()
    {
        var wanted = new Dictionary<Guid, ImageAsset>();
        if (player.Frame.Background is { } background)
        {
            wanted[background.Id] = background;
        }

        foreach (var character in player.Frame.Characters)
        {
            wanted[character.Image.Id] = character.Image;
        }

        foreach (var staleId in assetTextures.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
        {
            assetTextures[staleId].Dispose();
            assetTextures.Remove(staleId);
        }

        foreach (var staleId in assetDiagnostics.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
        {
            assetDiagnostics.Remove(staleId);
        }

        foreach (var (id, asset) in wanted)
        {
            if (assetTextures.ContainsKey(id) || assetDiagnostics.ContainsKey(id))
            {
                continue;
            }

            try
            {
                assetTextures[id] = RuntimeImageLoader.Load(GraphicsDevice, arguments.AssetsRoot, asset);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                assetDiagnostics[id] = $"Ресурс «{asset.Name}»: {exception.Message}";
            }
        }
    }

    private void RefreshTextTextures()
    {
        var frame = player.Frame;
        var frameKey = new FrameRenderKey(frame.LineId, frame.SpeakerName, frame.Text, frame.Style, frame.CanNext, frame.CanPrevious);
        if (frameKey != renderedFrameKey)
        {
            dialogueTexture?.Dispose();
            dialogueTexture = DialogueTextureRenderer.RenderDialogue(
                GraphicsDevice,
                frame.Style,
                frame.SpeakerName,
                frame.Text);
            renderedFrameKey = frameKey;

            if (showNavigationControls)
            {
                navigationTexture?.Dispose();
                navigationTexture = DialogueTextureRenderer.RenderNavigationControls(
                    GraphicsDevice,
                    frame.CanPrevious,
                    frame.CanNext);
            }
        }

        var diagnostics = CurrentDiagnostics();
        var newDiagnosticsKey = string.Join('\n', diagnostics);
        if (!string.Equals(newDiagnosticsKey, diagnosticsKey, StringComparison.Ordinal))
        {
            diagnosticsTexture?.Dispose();
            diagnosticsTexture = DialogueTextureRenderer.RenderDiagnostics(GraphicsDevice, diagnostics);
            diagnosticsKey = newDiagnosticsKey;
        }
    }

    private IReadOnlyList<string> CurrentDiagnostics() =>
        player.Frame.Diagnostics.Concat(assetDiagnostics.Values.Order(StringComparer.Ordinal)).Concat(audio?.Diagnostics ?? [])
            .Concat(screens.Scripts?.Diagnostics ?? []).ToArray();

    private void DrawBackground(SpriteBatch batch, ImageAsset? background)
    {
        if (background is null || !assetTextures.TryGetValue(background.Id, out var texture))
        {
            return;
        }

        var scale = Math.Max(LogicalWidth / (float)texture.Width, LogicalHeight / (float)texture.Height);
        var width = (int)Math.Ceiling(texture.Width * scale);
        var height = (int)Math.Ceiling(texture.Height * scale);
        var destination = new Rectangle((LogicalWidth - width) / 2, (LogicalHeight - height) / 2, width, height);
        batch.Draw(texture, destination, Color.White);
    }

    private void DrawCharacters(SpriteBatch batch, IReadOnlyList<VisibleCharacter> characters)
    {
        if (characters.Count == 0)
        {
            return;
        }

        for (var index = 0; index < characters.Count; index++)
        {
            var character = characters[index];
            if (!assetTextures.TryGetValue(character.Image.Id, out var texture))
            {
                continue;
            }

            var bounds = CharacterLayout.Calculate(texture.Width, texture.Height, character.AutomaticSlot, characters.Count, character.Placement);
            var scale = (float)(bounds.Width / texture.Width);
            var position = new Vector2((float)bounds.X, (float)bounds.Y);
            batch.Draw(
                texture,
                position,
                sourceRectangle: null,
                Color.White,
                rotation: 0,
                origin: Vector2.Zero,
                new Vector2(scale),
                SpriteEffects.None,
                layerDepth: 0);
        }
    }

    private void DrawDialogue(SpriteBatch batch, SceneFrame frame)
    {
        if (dialogueTexture is null)
        {
            return;
        }

        var sample = frame.Style.Sample(UsesGameState ? TimeSpan.FromSeconds(player.Execution.State.Time - player.Execution.State.TextStartedAt) : lineElapsed);
        var center = new Vector2(
            (float)frame.Style.Left + (dialogueTexture.Width / 2f) + (float)sample.TranslateX,
            LogicalHeight - (float)frame.Style.Bottom - (dialogueTexture.Height / 2f) + (float)sample.TranslateY);
        batch.Draw(
            dialogueTexture.Texture,
            center,
            sourceRectangle: null,
            Color.White * (float)Math.Clamp(sample.Opacity, 0, 1),
            rotation: 0,
            new Vector2(dialogueTexture.Width / 2f, dialogueTexture.Height / 2f),
            new Vector2((float)Math.Max(0, sample.Scale)),
            SpriteEffects.None,
            layerDepth: 0);
    }

    private void DrawDiagnostics(SpriteBatch batch)
    {
        if (diagnosticsTexture is null)
        {
            return;
        }

        var scale = Math.Min(1f, (LogicalWidth - 32f) / diagnosticsTexture.Width);
        batch.Draw(
            diagnosticsTexture.Texture,
            new Vector2(16, UsesGameState ? 64 : 16),
            sourceRectangle: null,
            Color.White,
            rotation: 0,
            origin: Vector2.Zero,
            new Vector2(scale),
            SpriteEffects.None,
            layerDepth: 0);
    }

    private void DrawNavigationControls(SpriteBatch batch)
    {
        if (!showNavigationControls || navigationTexture is null)
        {
            return;
        }

        batch.Draw(
            navigationTexture.Texture,
            RuntimeInputMapper.NavigationBounds,
            Color.White);
    }

    private void WriteSmokeArtifacts(RenderTarget2D target)
    {
        var outputDirectory = Path.GetDirectoryName(arguments.SmokeOutputPath!);
        var reportDirectory = Path.GetDirectoryName(arguments.SmokeReportPath!);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (!string.IsNullOrEmpty(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }

        using (var stream = new FileStream(
            arguments.SmokeOutputPath!,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            target.SaveAsPng(stream, LogicalWidth, LogicalHeight);
        }

        var report = new RuntimeSmokeReport(
            Loaded: true,
            DrawFrames: drawFrames,
            InitialLineId: initialLineId,
            AfterNextLineId: afterNextLineId,
            AfterPreviousLineId: afterPreviousLineId,
            NextResult: smokeNextResult,
            PreviousResult: smokePreviousResult,
            PreviousRestoredInitialLine: smokePreviousResult && afterPreviousLineId == initialLineId,
            InitialVisibleCharacterCount: initialVisibleCharacterCount,
            AfterNextVisibleCharacterCount: afterNextVisibleCharacterCount,
            AfterPreviousVisibleCharacterCount: afterPreviousVisibleCharacterCount,
            Diagnostics: CurrentDiagnostics()) { ActionTrace = actionTrace, SavePath = SavePath,
                PendingLuaDrawFrames = pendingLuaDrawFrames,
                PendingScreenLuaDrawFrames = pendingScreenLuaDrawFrames,
                ScreenLuaAppliedBatches = screens.Scripts?.AppliedBatches ?? 0,
                ScreenLuaDiagnostics = screens.Scripts?.Diagnostics ?? [],
                Packaged = arguments.Package is not null, WindowTitle = Window.Title,
                RuntimeLibraryPath = typeof(object).Assembly.Location, AudioSources = audio?.ActiveSources ?? 0, AudioSubmittedBytes = audioBackend.SubmittedBytes, AudioOpenedSources = audioBackend.OpenedSources, AudioMixer = Enum.GetValues<NovellMaker.Core.Audio.AudioGroup>().ToDictionary(g => g, g => audio!.Bus(g)) };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
        };
        File.WriteAllText(arguments.SmokeReportPath!, JsonSerializer.Serialize(report, options));
    }

    private sealed record FrameRenderKey(
        Guid? LineId,
        string SpeakerName,
        string Text,
        NovellMaker.Core.Styling.DialogueStyle Style, bool CanNext, bool CanPrevious);
}

internal sealed record RuntimeSmokeReport(
    bool Loaded,
    int DrawFrames,
    Guid? InitialLineId,
    Guid? AfterNextLineId,
    Guid? AfterPreviousLineId,
    bool NextResult,
    bool PreviousResult,
    bool PreviousRestoredInitialLine,
    int InitialVisibleCharacterCount,
    int AfterNextVisibleCharacterCount,
    int AfterPreviousVisibleCharacterCount,
    IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<RuntimeActionTrace> ActionTrace { get; init; } = [];
    public int PendingLuaDrawFrames { get; init; }
    public int PendingScreenLuaDrawFrames { get; init; }
    public int ScreenLuaAppliedBatches { get; init; }
    public IReadOnlyList<string> ScreenLuaDiagnostics { get; init; } = [];
    public string? SavePath { get; init; }
    public bool Packaged { get; init; }
    public string? WindowTitle { get; init; }
    public string? RuntimeLibraryPath { get; init; }
    public int AudioSources { get; init; }
    public long AudioSubmittedBytes { get; init; }
    public int AudioOpenedSources { get; init; }
    public Dictionary<NovellMaker.Core.Audio.AudioGroup, NovellMaker.Core.Audio.AudioBus> AudioMixer { get; init; } = [];
}
internal sealed record RuntimeSmokeStep(string Kind, double Seconds = 0, int X = 0, int Y = 0);
internal sealed record RuntimeActionTrace(string Kind, bool Success, GameState State, string Text)
{
    public int ScreenCount { get; init; }
    public int PausedAudioSources { get; init; }
    public int AudioSources { get; init; }
    public int AudioOpenedSources { get; init; }
    public long AudioSubmittedBytes { get; init; }
}
