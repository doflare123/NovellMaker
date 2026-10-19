using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using SkiaSharp;

namespace NovellMaker.Editor.Services;

/// <summary>Example content lives in the authoring layer, never in the interpreter.</summary>
public static class BuiltInExample
{
    public static NovelProject Create(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Assets", "Images"));
        var images = new[] { Sprite(directory, "guide", new SKColor(103, 153, 180)), Sprite(directory, "traveller", new SKColor(183, 146, 100)) };
        NovelCharacter Character(string name, ImageAsset image)
        {
            var pose = new CharacterState(Guid.NewGuid(), "Обычное", image.Id);
            return NovelCharacter.Create(name) with { States = [pose], DefaultStateId = pose.Id };
        }
        var guide = Character("Проводник", images[0]);
        var traveller = Character("Путник", images[1]);
        var scene = NovelScene.Create("Проверка действий, Lua и сохранений");
        var lines = new[]
        {
            DialogueLine.Create("ru", "Я говорю, а молчащий путник движется. F7 — сохранить, F8 — загрузить. Попробуйте сохранить посреди движения. Затем нажмите Enter.") with { SpeakerId = guide.Id },
            DialogueLine.Create("ru", "Нажмите на предмет «Терминал» в центре сцены."),
            DialogueLine.Create("ru", "Мини-игра: сколько будет 2 + 2? Нажмите на ответ. Здесь тоже можно сохранить и загрузить игру."),
            DialogueLine.Create("ru", "Lua вернул win. Терминал открыл проход. Ветка успеха."),
            DialogueLine.Create("ru", "Lua вернул lose. Терминал оставил проход закрытым. Ветка неудачи."),
            DialogueLine.Create("ru", "Проверка завершена. Можно перезапустить сцену через F6 в редакторе."),
        };
        var terminal = Guid.NewGuid(); var correct = Guid.NewGuid(); var wrong = Guid.NewGuid();
        var terminalStart = new LuaScriptAsset(Guid.NewGuid(), "terminal-start.lua", "-- Очередь останавливается до щелчка; результат читает следующий Lua-блок.\ngame.wait_click()\n");
        Guid Id() => Guid.NewGuid();
        JumpAction Go(int number) => new(Id(), scene.Id, ScenarioProgram.LineEntryId(lines[number].Id));
        ChoiceAction EndingChoice() => new(Id(), [new(Id(), "Повторить", scene.Id, ScenarioProgram.LineEntryId(lines[0].Id)),
            new(Id(), "Завершить", scene.Id, ScenarioProgram.LineEntryId(lines[5].Id))]);
        lines[0] = lines[0] with { Placements = [new(guide.Id, new(0.2, 1, 1, 0)), new(traveller.Id, new(0.48, 1, 1, 1))] };
        ScriptBlock Cue(int number, ScriptPhase phase, ScenarioAction action) => new(lines[number].Id, phase, action);
        scene = scene with { Lines = [.. lines], ScriptBlocks = [
            Cue(0, ScriptPhase.BeforeLine, new ShowAction(Id(), traveller.Id)),
            Cue(0, ScriptPhase.DuringLine, new MoveAction(Id(), traveller.Id, new(0.83, 1, 0.8, 1), 8)),
            Cue(1, ScriptPhase.DuringLine, new HotspotAction(Id(), terminal, "Терминал", 0.43, 0.37, 0.16, 0.1)),
            Cue(1, ScriptPhase.DuringLine, new WaitAction(Id(), "interaction", Token: terminal)),
            Cue(1, ScriptPhase.DuringLine, Go(2)),
            Cue(2, ScriptPhase.DuringLine, new HotspotAction(Id(), correct, "4", 0.35, 0.35, 0.12, 0.12)),
            Cue(2, ScriptPhase.DuringLine, new HotspotAction(Id(), wrong, "5", 0.53, 0.35, 0.12, 0.12)),
            Cue(2, ScriptPhase.DuringLine, new RunScriptAction(Id(), terminalStart.Id)),
            Cue(2, ScriptPhase.DuringLine, new ScriptAction(Id(), """
                if game.get("input.object") == refs.correct then
                    game.finish("win")
                else
                    game.finish("lose")
                end
                """, "terminal-result.lua") { References = ImmutableDictionary<string, Guid>.Empty.Add("correct", correct) }),
            Cue(2, ScriptPhase.DuringLine, new BranchAction(Id(), "minigame.result", "win", scene.Id, ScenarioProgram.LineEntryId(lines[3].Id))),
            Cue(2, ScriptPhase.DuringLine, Go(4)),
            Cue(3, ScriptPhase.AfterLine, EndingChoice()),
            Cue(4, ScriptPhase.AfterLine, EndingChoice()),
        ] };
        return NovelProject.Create("Проверочный фрагмент") with { Scenes = [scene], Images = [.. images], Characters = [guide, traveller], Scripts = [terminalStart] };
    }

    private static ImageAsset Sprite(string directory, string name, SKColor color)
    {
        const int width = 300, height = 620;
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(36, 180, 264, 610), 36, 36, paint);
        paint.Color = new SKColor(220, 200, 178);
        canvas.DrawCircle(150, 100, 80, paint);
        paint.Color = new SKColor(45, 48, 54);
        canvas.DrawCircle(123, 100, 5, paint); canvas.DrawCircle(177, 100, 5, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var relative = $"Assets/Images/{name}.png";
        using var stream = File.Create(Path.Combine(directory, relative));
        data.SaveTo(stream);
        return new ImageAsset(Guid.NewGuid(), name, relative, width, height);
    }
}

