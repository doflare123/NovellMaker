using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Execution;

namespace NovellMaker.Runtime;

internal sealed class RuntimeGame : Game
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
    private int initialVisibleCharacterCount;
    private int afterNextVisibleCharacterCount;
    private int afterPreviousVisibleCharacterCount;
    private readonly List<(Guid Id, Rectangle Bounds, RenderedTexture Texture)> interactionButtons = [];
    private string interactionKey = "";
    private readonly RuntimeSmokeStep[]? smokePlan;
    private readonly List<RuntimeActionTrace> actionTrace = [];
    private RenderedTexture? actionHelp;
    private string saveMessage = "F7 — сохранить · F8 — загрузить · Enter — продолжить";
    private string SavePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovellMaker", "Saves", player.Execution.State.ProjectId + ".json");

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
        Window.Title = "NovellMaker — предпросмотр сцены";
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent()
    {
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
        RefreshFrameResources();
        RefreshTextTextures();
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        lineElapsed += gameTime.ElapsedGameTime;
        if (player.UsesActions)
        {
            var line = player.Frame.LineId;
            if (smokePlan is null) player.Tick(gameTime.ElapsedGameTime);
            if (line != player.Frame.LineId) lineElapsed = TimeSpan.Zero;
            RefreshFrameResources();
            RefreshTextTextures();
            RefreshInteractions();
        }

        if (arguments.SmokeOutputPath is not null)
        {
            if (smokePlan is null) AdvanceSmokeSequence(); else AdvanceActionSmoke();
        }
        else
        {
            var keyboard = Keyboard.GetState();
            var mouse = Mouse.GetState();
            if (Pressed(keyboard, Keys.Escape))
            {
                Exit();
            }
            else if (player.UsesActions && Pressed(keyboard, Keys.F7)) SaveGame();
            else if (player.UsesActions && Pressed(keyboard, Keys.F8)) LoadGame();
            else if (Pressed(keyboard, Keys.Right) || Pressed(keyboard, Keys.Enter) ||
                     Pressed(keyboard, Keys.Space))
            {
                ApplyAction(RuntimeAction.Next);
            }
            else if (Pressed(keyboard, Keys.Left))
            {
                ApplyAction(RuntimeAction.Previous);
            }
            else if (mouse.LeftButton == ButtonState.Pressed &&
                     previousMouse.LeftButton == ButtonState.Released)
            {
                var letterbox = RuntimeInputMapper.CalculateLetterbox(
                    GraphicsDevice.PresentationParameters.BackBufferWidth,
                    GraphicsDevice.PresentationParameters.BackBufferHeight);
                if (RuntimeInputMapper.TryMapWindowToLogical(new Point(mouse.X, mouse.Y), letterbox, out var logical))
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
        if (arguments.SmokeOutputPath is not null && drawFrames >= Math.Max(4, (smokePlan?.Length ?? 0) + 1) && !smokeWritten)
        {
            WriteSmokeArtifacts(sceneTarget);
            smokeWritten = true;
            Exit();
        }

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
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
        var index = drawFrames - 1;
        if (index < 0 || index >= smokePlan!.Length) return;
        var step = smokePlan[index];
        var success = true;
        var path = Path.ChangeExtension(arguments.SmokeReportPath!, ".save.json");
        switch (step.Kind)
        {
            case "tick": player.Tick(TimeSpan.FromSeconds(step.Seconds)); break;
            case "next": success = ApplyAction(RuntimeAction.Next); break;
            case "click": success = ClickLogical(new Point(step.X, step.Y)); break;
            case "save": GameSave.SaveAsync(path, player.Execution).GetAwaiter().GetResult(); break;
            case "load": player.LoadSave(File.ReadAllText(path)); break;
            default: throw new ArgumentException("Неизвестный шаг smoke-плана.");
        }
        RefreshFrameResources(); RefreshTextTextures(); RefreshInteractions();
        actionTrace.Add(new(step.Kind, success, player.Execution.State, player.Frame.Text));
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

    private void SaveGame()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            GameSave.SaveAsync(SavePath, player.Execution).GetAwaiter().GetResult();
            saveMessage = "Игра сохранена · F8 — загрузить · Enter — продолжить";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { saveMessage = "Не удалось сохранить: " + error.Message; }
        RefreshInteractions();
    }

    private void LoadGame()
    {
        try
        {
            player.LoadSave(File.ReadAllText(SavePath));
            saveMessage = "Игра загружена · F7 — сохранить · Enter — продолжить";
            lineElapsed = TimeSpan.Zero;
            RefreshFrameResources(); RefreshTextTextures();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { saveMessage = "Не удалось загрузить: " + error.Message; }
        RefreshInteractions();
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
        player.Frame.Diagnostics.Concat(assetDiagnostics.Values.Order(StringComparer.Ordinal)).ToArray();

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

        var sample = frame.Style.Sample(player.UsesActions ? TimeSpan.FromSeconds(player.Execution.State.Time - player.Execution.State.TextStartedAt) : lineElapsed);
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
            new Vector2(16, player.UsesActions ? 64 : 16),
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
            Diagnostics: CurrentDiagnostics()) { ActionTrace = actionTrace };
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
}
internal sealed record RuntimeSmokeStep(string Kind, double Seconds = 0, int X = 0, int Y = 0);
internal sealed record RuntimeActionTrace(string Kind, bool Success, GameState State, string Text);
