using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Xna.Framework;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using SkiaSharp;

namespace NovellMaker.Runtime.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeRuntimeSmokeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Runtime_RendersSceneAndUsesSharedNextPreviousStateTransitions(bool manualPlacement)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set NOVELLMAKER_NATIVE_SMOKE=1 to run the native MonoGame smoke test.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var artifactDirectory = Path.Combine(repositoryRoot, "artifacts", "screenshots");
        var fixtureDirectory = Path.Combine(artifactDirectory, "runtime-fixture");
        var imageDirectory = Path.Combine(fixtureDirectory, "Assets", "Images");
        Directory.CreateDirectory(imageDirectory);
        Directory.CreateDirectory(artifactDirectory);

        var fixture = await CreateFixtureAsync(fixtureDirectory, imageDirectory, manualPlacement);
        var screenshotPath = Path.Combine(artifactDirectory, manualPlacement ? "runtime-placement.png" : "runtime-scene.png");
        var reportPath = Path.Combine(artifactDirectory, manualPlacement ? "runtime-placement-report.json" : "runtime-scene-report.json");
        var runtimePath = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "NovellMaker.Runtime.exe" : "NovellMaker.Runtime");
        Assert.IsTrue(File.Exists(runtimePath), $"Runtime apphost not found: {runtimePath}");

        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                runtimePath,
                fixture.ProjectPath,
                fixtureDirectory,
                fixture.SceneId,
                fixture.StartLineId,
                screenshotPath,
                reportPath),
        };
        Assert.IsTrue(process.Start(), "The runtime process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Assert.Fail("The native runtime did not finish its smoke sequence within 20 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.AreEqual(0, process.ExitCode, $"Runtime stderr: {error}\nRuntime stdout: {output}");
        Assert.IsTrue(File.Exists(screenshotPath), "Runtime did not create a screenshot.");
        Assert.IsTrue(File.Exists(reportPath), "Runtime did not create a smoke report.");

        using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
        {
            var root = report.RootElement;
            Assert.IsTrue(root.GetProperty("Loaded").GetBoolean());
            Assert.IsGreaterThanOrEqualTo(4, root.GetProperty("DrawFrames").GetInt32());
            Assert.AreEqual(fixture.StartLineId, root.GetProperty("InitialLineId").GetGuid());
            Assert.AreEqual(fixture.NextLineId, root.GetProperty("AfterNextLineId").GetGuid());
            Assert.AreEqual(fixture.StartLineId, root.GetProperty("AfterPreviousLineId").GetGuid());
            Assert.IsTrue(root.GetProperty("NextResult").GetBoolean());
            Assert.IsTrue(root.GetProperty("PreviousResult").GetBoolean());
            Assert.IsTrue(root.GetProperty("PreviousRestoredInitialLine").GetBoolean());
            Assert.AreEqual(2, root.GetProperty("InitialVisibleCharacterCount").GetInt32());
            Assert.AreEqual(1, root.GetProperty("AfterNextVisibleCharacterCount").GetInt32());
            Assert.AreEqual(2, root.GetProperty("AfterPreviousVisibleCharacterCount").GetInt32());
            Assert.AreEqual(0, root.GetProperty("Diagnostics").GetArrayLength());
        }

        AssertScreenshotContainsSceneLayers(screenshotPath);
        if (manualPlacement)
        {
            using var bitmap = SKBitmap.Decode(screenshotPath);
            Assert.AreEqual(new SKColor(35, 190, 104), bitmap.GetPixel(650, 200), "Higher layer must cover the other sprite after Previous restores placement.");
            Assert.AreEqual(new SKColor(245, 158, 45), bitmap.GetPixel(780, 100), "Larger sprite must remain visible outside the overlap.");
            Assert.AreEqual(new SKColor(22, 58, 112), bitmap.GetPixel(340, 200), "Automatic left-hand position must be empty.");
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string runtimePath,
        string projectPath,
        string assetsRoot,
        Guid sceneId,
        Guid lineId,
        string screenshotPath,
        string reportPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimePath,
            WorkingDirectory = assetsRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }

        foreach (var argument in new[]
        {
            "--project", projectPath,
            "--assets-root", assetsRoot,
            "--scene", sceneId.ToString(),
            "--line", lineId.ToString(),
            "--language", "ru",
            "--smoke-output", screenshotPath,
            "--smoke-report", reportPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<RuntimeFixture> CreateFixtureAsync(
        string fixtureDirectory,
        string imageDirectory, bool manualPlacement)
    {
        var backgroundId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var normalImageId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var sadImageId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var secondImageId = Guid.Parse("10000000-0000-0000-0000-000000000004");
        WriteSolidPng(Path.Combine(imageDirectory, $"{backgroundId:N}.png"), 1280, 720, new SKColor(22, 58, 112));
        WriteSpritePng(Path.Combine(imageDirectory, $"{normalImageId:N}.png"), new SKColor(214, 58, 64));
        WriteSpritePng(Path.Combine(imageDirectory, $"{sadImageId:N}.png"), new SKColor(35, 190, 104));
        WriteSpritePng(Path.Combine(imageDirectory, $"{secondImageId:N}.png"), new SKColor(245, 158, 45));

        var normalState = new CharacterState(Guid.Parse("20000000-0000-0000-0000-000000000001"), "Обычное", normalImageId);
        var sadState = new CharacterState(Guid.Parse("20000000-0000-0000-0000-000000000002"), "Грусть", sadImageId);
        var alice = new NovelCharacter(Guid.Parse("30000000-0000-0000-0000-000000000001"), "Алиса")
        {
            DefaultStateId = normalState.Id,
            States = [normalState, sadState],
        };
        var bobState = new CharacterState(Guid.Parse("20000000-0000-0000-0000-000000000003"), "Обычное", secondImageId);
        var bob = new NovelCharacter(Guid.Parse("30000000-0000-0000-0000-000000000002"), "Борис")
        {
            DefaultStateId = bobState.Id,
            States = [bobState],
        };

        var first = CreateLine(Guid.Parse("40000000-0000-0000-0000-000000000001"), "Алиса входит в сцену.", alice.Id, normalState.Id);
        var second = CreateLine(Guid.Parse("40000000-0000-0000-0000-000000000002"), "Алиса меняет состояние на грустное.", alice.Id, sadState.Id);
        var third = CreateLine(Guid.Parse("40000000-0000-0000-0000-000000000003"), "Цвет: §#FF40C0проверка§r, кириллица.", bob.Id, bobState.Id);
        var fourth = CreateLine(Guid.Parse("40000000-0000-0000-0000-000000000004"), "Борис скрыт, Алиса остаётся.", alice.Id, sadState.Id) with
        {
            HideCharacterId = bob.Id,
        };
        if (manualPlacement)
        {
            first = first with { Placements = [new(alice.Id, new(0.5, 0.7, 0.8, 5))] };
            third = third with { Placements = [new(bob.Id, new(0.55, 0.7, 1, -1))] };
            fourth = fourth with { Placements = [new(alice.Id, new(0.2, 1, 1.2, 0))] };
        }
        var scene = new NovelScene(Guid.Parse("50000000-0000-0000-0000-000000000001"), "Smoke-сцена")
        {
            BackgroundImageId = backgroundId,
            Lines = [first, second, third, fourth],
        };
        var project = new NovelProject(
            NovelProject.CurrentFormatVersion,
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            "Runtime smoke")
        {
            Images =
            [
                new ImageAsset(backgroundId, "Фон", $"Assets/Images/{backgroundId:N}.png", 1280, 720),
                new ImageAsset(normalImageId, "Алиса обычная", $"Assets/Images/{normalImageId:N}.png", 120, 220),
                new ImageAsset(sadImageId, "Алиса грустная", $"Assets/Images/{sadImageId:N}.png", 120, 220),
                new ImageAsset(secondImageId, "Борис", $"Assets/Images/{secondImageId:N}.png", 120, 220),
            ],
            Characters = [alice, bob],
            Scenes = [scene],
            DialogueUi = new DialogueUiSettings
            {
                Css = ".dialogue { background-color: #202226FF; color: #FFFFFF; padding: 24px; " +
                    "border-color: #59606AFF; border-width: 2px; border-radius: 8px; font-size: 24px; " +
                    "font-family: Arial; text-align: left; width: 1100px; min-height: 150px; " +
                    "left: 32px; bottom: 32px; }",
            },
        };
        var projectPath = Path.Combine(fixtureDirectory, "runtime-smoke.novell");
        await new JsonProjectStore().SaveAsync(projectPath, project);
        return new RuntimeFixture(projectPath, scene.Id, third.Id, fourth.Id);
    }

    private static DialogueLine CreateLine(Guid id, string text, Guid speakerId, Guid stateId)
    {
        return new DialogueLine(id, $"line.{id:N}")
        {
            Texts = ImmutableDictionary<string, string>.Empty.Add("ru", text),
            SpeakerId = speakerId,
            CharacterStateId = stateId,
        };
    }

    private static void WriteSolidPng(string path, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        EncodePng(path, bitmap);
    }

    private static void WriteSpritePng(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(120, 220, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        canvas.DrawRect(new SKRect(18, 18, 102, 220), paint);
        EncodePng(path, bitmap);
    }

    private static void EncodePng(string path, SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ??
            throw new InvalidOperationException("Could not encode PNG fixture.");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    private static void AssertScreenshotContainsSceneLayers(string screenshotPath)
    {
        using var bitmap = SKBitmap.Decode(screenshotPath) ??
            throw new AssertFailedException("Runtime screenshot is not a decodable PNG.");
        Assert.AreEqual(1280, bitmap.Width);
        Assert.AreEqual(720, bitmap.Height);
        Assert.IsTrue(ContainsColor(bitmap, new SKColor(22, 58, 112), tolerance: 2), "Background is missing.");
        Assert.IsTrue(ContainsColor(bitmap, new SKColor(35, 190, 104), tolerance: 2), "Sad character sprite is missing.");
        Assert.IsTrue(ContainsColor(bitmap, new SKColor(245, 158, 45), tolerance: 2), "Second character sprite is missing.");
        Assert.IsTrue(ContainsColor(bitmap, new SKColor(32, 34, 38), tolerance: 3), "Dialogue plate is missing.");
        Assert.IsTrue(ContainsColor(bitmap, new SKColor(255, 64, 192), tolerance: 6), "Inline §#RRGGBB text is missing.");
        Assert.IsTrue(
            ContainsColorInBounds(bitmap, new SKColor(140, 149, 164), RuntimeInputMapper.NavigationBounds, tolerance: 4),
            "Visible Back/Next controls are missing.");
        Assert.IsTrue(CountBrightPixels(bitmap, new SKRectI(32, 538, 1132, 688)) > 80,
            "Speaker name or Cyrillic dialogue text was not rasterized.");
    }

    private static bool ContainsColor(SKBitmap bitmap, SKColor expected, int tolerance)
    {
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var actual = bitmap.GetPixel(x, y);
                if (Math.Abs(actual.Red - expected.Red) <= tolerance &&
                    Math.Abs(actual.Green - expected.Green) <= tolerance &&
                    Math.Abs(actual.Blue - expected.Blue) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int CountBrightPixels(SKBitmap bitmap, SKRectI bounds)
    {
        var count = 0;
        for (var y = bounds.Top; y < Math.Min(bounds.Bottom, bitmap.Height); y++)
        {
            for (var x = bounds.Left; x < Math.Min(bounds.Right, bitmap.Width); x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Red > 220 && color.Green > 220 && color.Blue > 220)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool ContainsColorInBounds(
        SKBitmap bitmap,
        SKColor expected,
        Rectangle bounds,
        int tolerance)
    {
        for (var y = bounds.Top; y < Math.Min(bounds.Bottom, bitmap.Height); y++)
        {
            for (var x = bounds.Left; x < Math.Min(bounds.Right, bitmap.Width); x++)
            {
                var actual = bitmap.GetPixel(x, y);
                if (Math.Abs(actual.Red - expected.Red) <= tolerance &&
                    Math.Abs(actual.Green - expected.Green) <= tolerance &&
                    Math.Abs(actual.Blue - expected.Blue) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NovellMaker.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate NovellMaker.slnx from the test output directory.");
    }

    private sealed record RuntimeFixture(string ProjectPath, Guid SceneId, Guid StartLineId, Guid NextLineId);
}
