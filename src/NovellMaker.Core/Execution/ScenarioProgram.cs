using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Execution;

public static class ScenarioProgram
{
    /// <summary>Legacy dialogue cues compile to ordinary actions; no second interpreter.</summary>
    public static ImmutableArray<ScenarioAction> Compile(NovelScene scene) => Compile(scene, CancellationToken.None);

    public static ImmutableArray<ScenarioAction> Compile(NovelScene scene, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return scene.HasLegacyActionScript ? scene.Actions : [.. EnumerateActions(scene, cancellationToken)];
    }

    internal static IEnumerable<ScenarioAction> EnumerateActions(NovelScene scene, CancellationToken cancellationToken)
        => EnumerateActions(scene, cancellationToken.ThrowIfCancellationRequested);

    internal static IEnumerable<ScenarioAction> EnumerateActions(NovelScene scene, Action checkpoint)
    {
        checkpoint();
        if (scene.HasLegacyActionScript)
        {
            foreach (var action in scene.Actions)
            { checkpoint(); yield return action; }
            yield break;
        }
        // Preserve order within each event without scanning all blocks for every phrase.
        var blocks = new Dictionary<(Guid?, ScriptPhase), List<ScenarioAction>>();
        foreach (var block in scene.ScriptBlocks)
        {
            checkpoint();
            var key = (block.LineId, block.Phase);
            if (!blocks.TryGetValue(key, out var eventActions)) blocks.Add(key, eventActions = []);
            eventActions.Add(block.Action);
        }
        IEnumerable<ScenarioAction> Blocks(Guid? line, ScriptPhase phase)
        {
            if (!blocks.TryGetValue((line, phase), out var eventActions)) yield break;
            foreach (var action in eventActions)
            { checkpoint(); yield return action; }
        }
        foreach (var action in Blocks(null, ScriptPhase.SceneStart)) yield return action;
        foreach (var line in scene.Lines)
        {
            checkpoint();
            var cue = 0;
            Guid Id() => new(MD5.HashData(Encoding.UTF8.GetBytes($"{line.Id}/cue/{cue++}")));
            yield return new LineEntryAction(LineEntryId(line.Id), line.Id);
            foreach (var placement in line.Placements)
            { checkpoint(); yield return new PlaceAction(Id(), placement.CharacterId, placement.Placement); }
            if (line.HideCharacterId is { } hidden) yield return new HideAction(Id(), hidden);
            if (line.CharacterStateId is { } state) yield return new StateAction(Id(), line.SpeakerId ?? Guid.Empty, state);
            if (line.SpeakerId is { } speaker) yield return new ShowAction(Id(), speaker);
            foreach (var action in Blocks(line.Id, ScriptPhase.BeforeLine)) yield return action;
            yield return new TextAction(Id(), line.Id);
            foreach (var action in Blocks(line.Id, ScriptPhase.DuringLine)) yield return action;
            yield return new WaitAction(Id(), "input");
            foreach (var action in Blocks(line.Id, ScriptPhase.AfterLine)) yield return action;
        }
        foreach (var action in Blocks(null, ScriptPhase.SceneEnd)) yield return action;
    }

    public static Guid LineEntryId(Guid lineId) => new(MD5.HashData(Encoding.UTF8.GetBytes($"{lineId}/entry")));

    public static void Validate(NovelProject project)
    {
        var ids = new HashSet<Guid> { project.Id };
        ids.UnionWith(project.Images.Select(i => i.Id));
        ids.UnionWith(project.Audio.Assets.Select(a => a.Id));
        if (project.Scripts.IsDefault) throw new ArgumentException("Коллекция скриптов отсутствует.");
        foreach (var script in project.Scripts)
        {
            if (script is null) throw new ArgumentException("Коллекция скриптов содержит null.");
            script.Validate();
            if (!ids.Add(script.Id)) throw new ArgumentException("Повторный ID скрипта.");
        }
        ids.UnionWith(project.Characters.Select(c => c.Id));
        ids.UnionWith(project.Characters.SelectMany(c => c.States).Select(s => s.Id));
        ids.UnionWith(project.Scenes.Select(s => s.Id));
        ids.UnionWith(project.Scenes.SelectMany(s => s.Lines).Select(l => l.Id));
        foreach (var scene in project.Scenes)
        {
            if (scene.Actions.IsDefault || scene.ScriptBlocks.IsDefault) throw new ArgumentException("Коллекция скрипта отсутствует.");
            if (scene.HasLegacyActionScript && !scene.ScriptBlocks.IsEmpty) throw new ArgumentException("Поток прежнего формата нельзя смешивать с привязками фраз.");
            foreach (var block in scene.ScriptBlocks)
                if (block is null || block.Action is null || !Enum.IsDefined(block.Phase) ||
                    (block.Phase is ScriptPhase.SceneStart or ScriptPhase.SceneEnd ? block.LineId is not null : !scene.Lines.Any(l => l.Id == block.LineId)))
                    throw new ArgumentException("Блок скрипта имеет неверную привязку к фразе.");
            foreach (var action in scene.Actions.Concat(scene.ScriptBlocks.Select(b => b.Action)))
            {
                if (action is null || action.Id == Guid.Empty || !ids.Add(action.Id))
                    throw new ArgumentException("Действие отсутствует или имеет пустой/повторный ID.");
                var valid = IsValidAction(project, scene, action);
                if (!valid) throw new ArgumentException($"Некорректное действие {action.Id} ({action.GetType().Name}) в сцене «{scene.Name}».");
            }
            var compiled = Compile(scene);
            if (compiled.Select(a => a.Id).Distinct().Count() != compiled.Length)
                throw new ArgumentException($"Блок сцены «{scene.Name}» использует ID автоматической команды фразы.");
        }
    }

    internal static bool IsValidAction(NovelProject project, NovelScene scene, ScenarioAction action)
    {
        if (action is StartMinigameAction start)
        {
            var definition = project.Scenes.FirstOrDefault(s => s.Id == start.SceneId)?.Canvas.Minigame;
            if (definition is null) return false;
            try { definition.Validate(); definition.ParametersSchema.Validate(start.Parameters); return true; }
            catch (ArgumentException) { return false; }
        }
        return IsValidAction(action, id => project.Characters.Any(c => c.Id == id),
            (id, state) => project.Characters.Any(c => c.Id == id && c.States.Any(s => s.Id == state)),
            id => scene.Lines.Any(l => l.Id == id),
            (sceneId, actionId) => project.Scenes.Any(s => s.Id == sceneId && Compile(s).Any(a => a.Id == actionId)),
            id => project.Scripts.Any(s => s.Id == id));
    }

    internal static bool IsValidAction(ScenarioAction action, Func<Guid, bool> ObjectExists,
        Func<Guid, Guid, bool> StateExists, Func<Guid, bool> LineExists,
        Func<Guid, Guid, bool> TargetExists, Func<Guid, bool> ScriptExists)
    {
        return action switch
        {
            TextAction a => LineExists(a.LineId),
            LineEntryAction a => LineExists(a.LineId),
            ShowAction a => ObjectExists(a.ObjectId),
            HideAction a => ObjectExists(a.ObjectId),
            StateAction a => StateExists(a.ObjectId, a.StateId),
            PlaceAction a => ObjectExists(a.ObjectId) && (a.Placement is null || a.Placement.IsValid),
            MoveAction a => ObjectExists(a.ObjectId) && a.Target is { IsValid: true } && Duration(a.Seconds),
            WaitAction a => Duration(a.Seconds) && (a.Mode switch { "input" or "actions" => true, "timer" => true,
                "interaction" => a.Token != Guid.Empty, _ => false }),
            ChoiceAction a => !a.Options.IsDefaultOrEmpty && a.Options.Length <= 8 && a.Options.All(o => o is not null && o.Id != Guid.Empty &&
                !string.IsNullOrWhiteSpace(o.Text) && o.Text.Length <= LuaLimits.MaximumValueLength && TargetExists(o.TargetSceneId, o.TargetActionId)) && a.Options.Select(o => o.Id).Distinct().Count() == a.Options.Length,
            JumpAction a => TargetExists(a.SceneId, a.ActionId),
            BranchAction a => LuaLimits.ValidVariable(a.Variable, a.EqualsValue) && TargetExists(a.SceneId, a.ActionId),
            BranchMinigameAction a => a.Field is { Length: > 0 and <= 128 } && a.EqualsValue is not null && TargetExists(a.SceneId, a.ActionId),
            SetAction a => LuaLimits.ValidVariable(a.Variable, a.Value),
            RunScriptAction a => ScriptExists(a.ScriptId),
            ScriptAction a => !string.IsNullOrWhiteSpace(a.Name) && a.Source is not null && a.Source.Length <= LuaActionScript.MaximumSourceLength && a.References is not null &&
                LuaActionScript.ValidReferences(a.References),
            HotspotAction a => a.ObjectId != Guid.Empty && a.Label is not null && a.Label.Length <= LuaLimits.MaximumValueLength && double.IsFinite(a.X) && double.IsFinite(a.Y) &&
                a.X >= 0 && a.Y >= 0 && a.Width > 0 && a.Height > 0 && a.X + a.Width <= 1 && a.Y + a.Height <= 1,
            _ => false,
        };
    }

    internal static bool Duration(double value) => double.IsFinite(value) && value is >= 0 and <= 86400;

    // Canonical property ordering also covers dictionaries after a JSON round trip.
    public static string Fingerprint(NovelProject project)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(project));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
}


