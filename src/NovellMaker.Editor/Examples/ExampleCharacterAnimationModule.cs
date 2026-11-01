using System.Collections.Immutable;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using SkiaSharp;

namespace NovellMaker.Editor.Examples;

/// <summary>Editable project data and generated sample art; no example logic in Core/Runtime.</summary>
public static class ExampleCharacterAnimationModule
{
    public static NovelProject AddTo(NovelProject project, string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Assets", "Images"));
        var body = Image(directory, "Тело", 160, 280, (canvas, paint) =>
        { paint.Color = new(77, 119, 151); canvas.DrawRoundRect(new(0, 0, 160, 280), 24, 24, paint); });
        var head = Image(directory, "Голова", 120, 140, (canvas, paint) =>
        {
            paint.Color = new(224, 197, 166); canvas.DrawOval(new(0, 0, 120, 140), paint);
            paint.Color = new(35, 40, 48); canvas.DrawCircle(40, 65, 5, paint); canvas.DrawCircle(80, 65, 5, paint);
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 4; canvas.DrawArc(new(40, 80, 80, 108), 10, 160, false, paint);
        });
        var arm = Image(directory, "Рука", 44, 170, (canvas, paint) =>
        { paint.Color = new(194, 148, 101); canvas.DrawRoundRect(new(0, 0, 44, 170), 18, 18, paint); });
        var torso = new CharacterPart(Guid.NewGuid(), "Тело", body.Id, null, 80, 140, 0, new(180, 330));
        var face = new CharacterPart(Guid.NewGuid(), "Голова", head.Id, torso.Id, 60, 120, 2, new(80, 5));
        var hand = new CharacterPart(Guid.NewGuid(), "Рука", arm.Id, torso.Id, 22, 22, 1, new(140, 35));
        var wave = new CharacterClip(Guid.NewGuid(), "Приветствие", 2, true,
            [new(torso.Id, [new(0, torso.Transform), new(1, torso.Transform with { Y = 320 }), new(2, torso.Transform)]),
             new(face.Id, [new(0, face.Transform), new(1, face.Transform with { Rotation = -8 }), new(2, face.Transform)]),
             new(hand.Id, [new(0, hand.Transform with { Rotation = -20 }), new(0.5, hand.Transform with { Rotation = -110 }),
                 new(1, hand.Transform with { Rotation = -70 }), new(1.5, hand.Transform with { Rotation = -110 }), new(2, hand.Transform with { Rotation = -20 })])], []);
        var rig = new CharacterVisual(360, 560, [torso, face, hand], [wave], wave.Id);
        var assets = new[] { body, head, arm };
        var frames = Enumerable.Range(0, 4).Select(index => Image(directory, $"Приветствие · кадр {index + 1}", 360, 560, (canvas, _) =>
        {
            foreach (var sprite in rig.Sample(index * 0.5))
            {
                var image = assets.Single(a => a.Id == sprite.ImageId);
                using var bitmap = SKBitmap.Decode(AssetPath.Resolve(directory, image.RelativePath));
                var m = sprite.World;
                canvas.Save();
                canvas.SetMatrix(new SKMatrix { ScaleX = m.M11, SkewX = m.M21, TransX = m.M31, SkewY = m.M12, ScaleY = m.M22, TransY = m.M32, Persp2 = 1 });
                canvas.DrawBitmap(bitmap, 0, 0); canvas.Restore();
            }
        })).ToArray();
        var flipbook = new CharacterClip(Guid.NewGuid(), "Покадровое приветствие", 2, true, [], [.. frames.Select(f => new CharacterFrame(f.Id, 0.5))]);
        var layeredState = new CharacterState(Guid.NewGuid(), "Из частей", body.Id) { Visual = rig };
        var frameState = new CharacterState(Guid.NewGuid(), "Покадровый", frames[0].Id)
        { Visual = new(360, 560, [], [flipbook], flipbook.Id) };
        var actor = NovelCharacter.Create("Механик") with { States = [layeredState, frameState], DefaultStateId = layeredState.Id };
        var scene = NovelScene.Create("Пример · персонаж из частей");
        var lines = new[]
        {
            DialogueLine.Create("ru", "Тело, голова и рука: общая иерархия, отдельные pivot и ключи. F7 — сохранить, F8 — загрузить; анимация продолжится с сохранённой фазы.") with { SpeakerId = actor.Id, CharacterStateId = layeredState.Id },
            DialogueLine.Create("ru", "Теперь та же поза воспроизводится покадровым клипом. Длительности кадров редактируются в библиотеке персонажей.") with { SpeakerId = actor.Id, CharacterStateId = frameState.Id },
            DialogueLine.Create("ru", "Вернулись к частям. Измените ключи в библиотеке персонажей и запустите F6. Готовая игра использует тот же исполнитель.") with { SpeakerId = actor.Id, CharacterStateId = layeredState.Id }
        };
        scene = scene with { Lines = [.. lines], Canvas = scene.Canvas with
        { Nodes = DialogueCanvas.Add(scene.Canvas.Nodes, scene.Id, NovellMaker.Core.Styling.DialogueCssParser.Parse(project.DialogueUi.Css), legacy: true) } };
        return project with { InputBindings = project.InputBindings.AddRange(new[] { new ProjectInputBinding("F7", ScreenButtonAction.Save, null), new ProjectInputBinding("F8", ScreenButtonAction.Load, null) }.Where(b => !project.InputBindings.Any(old => old.Key == b.Key))), Images = project.Images.AddRange(assets).AddRange(frames), Characters = project.Characters.Add(actor), Scenes = project.Scenes.Add(scene) };
    }

    private static ImageAsset Image(string directory, string name, int width, int height, Action<SKCanvas, SKPaint> draw)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true }; draw(canvas, paint);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var id = Guid.NewGuid(); var relative = $"Assets/Images/{id:N}.png";
        using var stream = new FileStream(AssetPath.Resolve(directory, relative), FileMode.CreateNew); data.SaveTo(stream);
        return new(id, name, relative, width, height);
    }
}
