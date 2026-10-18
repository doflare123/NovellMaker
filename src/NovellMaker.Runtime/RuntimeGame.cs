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

    public RuntimeGame(RuntimeArguments arguments, ScenePlayer player, bool showNavigationControls)
    {
        this.arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.showNavigationControls = showNavigationControls;
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

        if (arguments.SmokeOutputPath is not null)
        {
            AdvanceSmokeSequence();
        }
        else
        {
            var keyboard = Keyboard.GetState();
            var mouse = Mouse.GetState();
            if (Pressed(keyboard, Keys.Escape))
            {
                Exit();
            }
            else if (Pressed(keyboard, Keys.Right) || Pressed(keyboard, Keys.Enter) ||
                     Pressed(keyboard, Keys.Space))
            {
                ApplyAction(RuntimeAction.Next);
            }
            else if (Pressed(keyboard, Keys.Left))
            {
                ApplyAction(RuntimeAction.Previous);
            }
            else if (showNavigationControls && mouse.LeftButton == ButtonState.Pressed &&
                     previousMouse.LeftButton == ButtonState.Released)
            {
                var letterbox = RuntimeInputMapper.CalculateLetterbox(
                    GraphicsDevice.PresentationParameters.BackBufferWidth,
                    GraphicsDevice.PresentationParameters.BackBufferHeight);
                if (RuntimeInputMapper.TryMapWindowToLogical(new Point(mouse.X, mouse.Y), letterbox, out var logical) &&
                    RuntimeInputMapper.HitTestNavigation(
                        logical,
                        player.Frame.CanPrevious,
                        player.Frame.CanNext) is { } action)
                {
                    ApplyAction(action);
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
        if (arguments.SmokeOutputPath is not null && drawFrames >= 4 && !smokeWritten)
        {
            WriteSmokeArtifacts(sceneTarget);
            smokeWritten = true;
            Exit();
        }

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
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
        var frameKey = new FrameRenderKey(frame.LineId, frame.SpeakerName, frame.Text, frame.Style);
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

        var sample = frame.Style.Sample(lineElapsed);
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
            new Vector2(16, 16),
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
            Diagnostics: CurrentDiagnostics());
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
        NovellMaker.Core.Styling.DialogueStyle Style);
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
    IReadOnlyList<string> Diagnostics);
