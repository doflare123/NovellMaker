using System.Collections.Immutable;
using System.Numerics;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Characters;

[TestClass]
public sealed class CharacterAnimationTests
{
    private static CharacterVisual Rig()
    {
        var parent = new CharacterPart(Guid.NewGuid(), "Тело", Guid.NewGuid(), null, 10, 20, 5, new(100, 200, 90, 2, 1, .5));
        var child = new CharacterPart(Guid.NewGuid(), "Рука", Guid.NewGuid(), parent.Id, 2, 3, 1, new(30, 40, 0, 1, 1, .5));
        var clip = new CharacterClip(Guid.NewGuid(), "Поворот", 2, false,
            [new(child.Id, [new(0, child.Transform), new(2, child.Transform with { Rotation = 90, X = 50, Opacity = 1 })])], []);
        return new(400, 600, [parent, child], [clip], clip.Id);
    }

    [TestMethod]
    public void HierarchyUsesParentImageSpacePivotLayerAndInheritedOpacity()
    {
        var rig = Rig(); rig.Validate();
        var sampled = rig.Sample(0);
        Assert.AreEqual(rig.Parts[1].ImageId, sampled[0].ImageId, "Global layers are independent of hierarchy.");
        var pivot = Vector2.Transform(new(2, 3), sampled[0].World);
        Assert.AreEqual(80, pivot.X, .0001); Assert.AreEqual(240, pivot.Y, .0001);
        Assert.AreEqual(.25, sampled[0].Opacity);
        var half = rig.Sample(1)[0];
        Assert.AreEqual(.375, half.Opacity, .0001);
        Assert.AreNotEqual(sampled[0].World, half.World);
        Assert.AreEqual(rig.Sample(2)[0], rig.Sample(100)[0], "A once-only clip holds its last key.");
    }

    [TestMethod]
    public void StepKeysImageReplacementAndLoopBoundariesAreDeterministic()
    {
        var rig = Rig(); var part = rig.Parts[1]; var replacement = Guid.NewGuid();
        var clip = rig.Clips[0] with { Loop = true, Tracks = [new(part.Id,
            [new(0, part.Transform, Hold: true), new(1, part.Transform with { X = 80 }, replacement), new(2, part.Transform)])] };
        rig = rig with { Clips = [clip] };
        Assert.AreEqual(rig.Sample(0)[0].World, rig.Sample(.9)[0].World);
        Assert.AreEqual(replacement, rig.Sample(1)[0].ImageId);
        Assert.AreEqual(rig.Sample(0)[0], rig.Sample(2)[0]);
        Assert.AreEqual(rig.Sample(.5)[0], rig.Sample(200.5)[0]);
    }

    [TestMethod]
    public void FlipbookUsesIndividualDurationsAndHoldsLastFrame()
    {
        var one = Guid.NewGuid(); var two = Guid.NewGuid();
        var clip = new CharacterClip(Guid.NewGuid(), "Кадры", .6, false, [], [new(one, .1), new(two, .5)]);
        var visual = new CharacterVisual(100, 200, [], [clip], clip.Id); visual.Validate();
        Assert.AreEqual(one, visual.Sample(.099)[0].ImageId);
        Assert.AreEqual(two, visual.Sample(.1)[0].ImageId);
        Assert.AreEqual(two, visual.Sample(10)[0].ImageId);
        visual = visual with { Clips = [clip with { Loop = true }] };
        Assert.AreEqual(one, visual.Sample(.6)[0].ImageId);
        visual = visual with { Clips = [clip with { Loop = true, RepeatCount = 1 }] };
        Assert.AreEqual(one, visual.Sample(.6)[0].ImageId);
        Assert.AreEqual(two, visual.Sample(1.2)[0].ImageId, "Finite repetition holds the last frame after the last cycle.");
    }

    [TestMethod]
    public void ExplicitClipLaunchRestoresAndPoseSelectionResetsItWithoutLua()
    {
        var rig = Rig(); var second = rig.Clips[0] with { Id = Guid.NewGuid(), Name = "Другой клип", Loop = true };
        rig = rig with { Clips = rig.Clips.Add(second) };
        var images = rig.ImageIds().Select(id => new ImageAsset(id, "Часть", $"Assets/Images/{id}.png", 100, 100)).ToImmutableArray();
        var pose = new CharacterState(Guid.NewGuid(), "Поза", images[0].Id) { Visual = rig };
        var actor = NovelCharacter.Create("Герой") with { States = [pose], DefaultStateId = pose.Id };
        var first = DialogueLine.Create("ru", "Другой клип") with { SpeakerId = actor.Id };
        var next = DialogueLine.Create("ru", "Снова поза") with { SpeakerId = actor.Id, CharacterStateId = pose.Id };
        var scene = NovelScene.Create("Сцена") with { Lines = [first, next], ScriptBlocks =
            [new(first.Id, ScriptPhase.DuringLine, new PlayCharacterClipAction(Guid.NewGuid(), actor.Id, pose.Id, second.Id))] };
        var project = NovelProject.Create("Клипы") with { Images = images, Characters = [actor], Scenes = [scene] };
        ScenarioProgram.Validate(project);
        using var player = new ScenePlayer(project, scene.Id, "ru");
        Assert.AreEqual(second.Id, player.Frame.Characters[0].Visual!.DefaultClipId);
        player.Tick(TimeSpan.FromSeconds(.5)); var save = GameSave.Serialize(player.Execution);
        var before = player.Frame.Characters[0].Sprites;
        player.Tick(TimeSpan.FromSeconds(1)); player.LoadSave(save);
        CollectionAssert.AreEqual(before.ToArray(), player.Frame.Characters[0].Sprites.ToArray());
        Assert.AreEqual(second.Id, player.Execution.State.CharacterClips[actor.Id]);
        Assert.IsTrue(player.Next());
        Assert.AreEqual(rig.DefaultClipId, player.Frame.Characters[0].Visual!.DefaultClipId);
        Assert.AreEqual(0, player.Frame.Characters[0].AnimationTime);
    }

    [TestMethod]
    [DataRow("cycle")] [DataRow("missing-parent")] [DataRow("duplicate-part")]
    [DataRow("nan")] [DataRow("negative-key")] [DataRow("duplicate-key")] [DataRow("missing-track")]
    [DataRow("invalid-frame")]
    public void InvalidRigIsRejectedBeforePersistence(string kind)
    {
        var rig = Rig(); var p = rig.Parts[0]; var clip = rig.Clips[0];
        rig = kind switch
        {
            "cycle" => rig with { Parts = rig.Parts.SetItem(0, p with { ParentId = rig.Parts[1].Id }) },
            "missing-parent" => rig with { Parts = rig.Parts.SetItem(0, p with { ParentId = Guid.NewGuid() }) },
            "duplicate-part" => rig with { Parts = [p, p] },
            "nan" => rig with { Parts = rig.Parts.SetItem(0, p with { Transform = p.Transform with { Rotation = double.NaN } }) },
            "negative-key" => rig with { Clips = [clip with { Tracks = [new(p.Id, [new(-.1, p.Transform)])] }] },
            "duplicate-key" => rig with { Clips = [clip with { Tracks = [new(p.Id, [new(0, p.Transform), new(0, p.Transform)])] }] },
            "missing-track" => rig with { Clips = [clip with { Tracks = [new(Guid.NewGuid(), [new(0, p.Transform)])] }] },
            _ => rig with { Clips = [clip with { Tracks = [], Frames = [new(p.ImageId, 0)] }] }
        };
        Assert.ThrowsExactly<ArgumentException>(rig.Validate);
    }

    [TestMethod]
    public async Task ProjectRoundTripAndLoadRestoreExactAnimationPhaseWithoutReplayingPose()
    {
        var rig = Rig();
        var images = rig.ImageIds().Select(id => new ImageAsset(id, "Часть", $"Assets/Images/{id}.png", 100, 100)).ToImmutableArray();
        var pose = new CharacterState(Guid.NewGuid(), "Приветствие", images[0].Id) { Visual = rig };
        var actor = NovelCharacter.Create("Герой") with { States = [pose], DefaultStateId = pose.Id };
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Текст") with { SpeakerId = actor.Id }] };
        var project = NovelProject.Create("Риг") with { Images = images, Characters = [actor], Scenes = [scene] };
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project); var loaded = await store.LoadAsync(path);
            Assert.IsTrue(project.ContentEquals(loaded));
            var missing = rig.Parts[1].ImageId;
            using var broken = new ScenePlayer(loaded with { Images = [.. loaded.Images.Where(i => i.Id != missing)] }, scene.Id, "ru");
            Assert.IsEmpty(broken.Frame.Characters);
            Assert.IsTrue(broken.Frame.Diagnostics.Any(d => d.Contains(missing.ToString(), StringComparison.Ordinal)));

            using var player = new ScenePlayer(loaded, scene.Id, "ru");
            player.Tick(TimeSpan.FromSeconds(.75));
            var sample = player.Frame.Characters[0].Sprites; var save = GameSave.Serialize(player.Execution);
            player.Tick(TimeSpan.FromSeconds(1)); player.LoadSave(save);
            CollectionAssert.AreEqual(sample.ToArray(), player.Frame.Characters[0].Sprites.ToArray());
            Assert.AreEqual(.75, player.Frame.Characters[0].AnimationTime);
            player.Execution.IsPaused = true; player.Tick(TimeSpan.FromSeconds(10));
            Assert.AreEqual(.75, player.Frame.Characters[0].AnimationTime);
            player.Execution.IsPaused = false; player.Tick(TimeSpan.FromSeconds(.25));
            Assert.AreEqual(1, player.Frame.Characters[0].AnimationTime);
            var invalid = player.Execution.State with { CharacterStartedAt = ImmutableDictionary<Guid, double>.Empty };
            Assert.ThrowsExactly<ArgumentException>(() => player.Execution.Restore(invalid));
        }
        finally { File.Delete(path); }
    }
}
