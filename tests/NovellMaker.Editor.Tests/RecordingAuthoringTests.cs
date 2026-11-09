using System.Collections.Immutable;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.Services.Preview;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Testing;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class RecordingAuthoringTests
{
    [TestMethod]
    public async Task ReturningThirtyWavsWithOneChangedTakeCreatesOnlyOneAssetAndExportsThirtyOne()
    {
        using var f = new Fixture(); var session = f.Session; await session.NewProjectAsync(); session.AddScene(); var doc = session.ActiveScene!;
        doc.InsertDialogue(Enumerable.Range(1, 30).Select(i => new DialoguePasteLine(null, "Реплика " + i)).ToArray());
        await session.SaveAsync(); await session.ExportRecordingSheetAsync(doc.Id, "ru");
        var rows = RecordingSheet.Read(await File.ReadAllTextAsync(f.Dialogs.Sheet));
        foreach (var row in rows) await File.WriteAllBytesAsync(Path.Combine(f.Dialogs.Folder, row.Wav), AudioFixture.Wave());
        await session.ImportRecordingSheetAsync(doc.Id); Assert.AreEqual(0, f.Dialogs.Errors.Count);
        await session.SaveAsync(); var baseline = session.Project!; var ids = baseline.Audio.Voices.ToDictionary(p => p.Key, p => p.Value["ru"]);
        doc.LineText = "Временная правка"; session.EndEditGroup(); session.Undo(); Assert.IsTrue(session.CanRedo);
        var beforeRepeat = session.Project;
        await session.ImportRecordingSheetAsync(doc.Id);
        Assert.AreEqual(30, f.Dialogs.Plan!.Matches); Assert.AreEqual(30, f.Dialogs.Plan.UnchangedCount); Assert.AreEqual(0, f.Dialogs.Plan.ImportCount); Assert.AreEqual(0, f.Dialogs.Plan.Replacements);
        Assert.AreSame(beforeRepeat, session.Project); Assert.IsFalse(session.IsDirty); Assert.IsTrue(session.CanRedo); Assert.AreEqual(30, Directory.GetFiles(Path.Combine(f.Root, "Assets", "Audio")).Length);
        var changed = rows[12]; var bytes = AudioFixture.Wave(); bytes[^1] ^= 1; // Same WAV size and duration, different PCM bytes.
        await File.WriteAllBytesAsync(Path.Combine(f.Dialogs.Folder, changed.Wav), bytes);
        await session.ImportRecordingSheetAsync(doc.Id); Assert.AreEqual(0, f.Dialogs.Errors.Count, string.Join('\n', f.Dialogs.Errors));
        Assert.AreEqual(30, f.Dialogs.Plan.Matches); Assert.AreEqual(29, f.Dialogs.Plan.UnchangedCount); Assert.AreEqual(1, f.Dialogs.Plan.ImportCount); Assert.AreEqual(1, f.Dialogs.Plan.Replacements);
        Assert.AreEqual(31, session.Project!.Audio.Assets.Length);
        foreach (var pair in ids)
            if (pair.Key == changed.LineId) Assert.AreNotEqual(pair.Value, session.Project.Audio.Voices[pair.Key]["ru"]);
            else Assert.AreEqual(pair.Value, session.Project.Audio.Voices[pair.Key]["ru"]);
        var replacement = session.Project.Audio.Voices[changed.LineId]["ru"];
        session.Undo(); Assert.IsTrue(baseline.ContentEquals(session.Project)); session.Redo(); Assert.AreEqual(replacement, session.Project!.Audio.Voices[changed.LineId]["ru"]);
        await session.SaveAsync(); var saved = session.Project; await session.ImportRecordingSheetAsync(doc.Id); Assert.AreSame(saved, session.Project); Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(31, Directory.GetFiles(Path.Combine(f.Root, "Assets", "Audio")).Length);
        var template = Path.Combine(f.Root, "template"); Directory.CreateDirectory(template); RuntimeTemplateFixture.Create(template);
        var export = Path.Combine(f.Root, "export");
        await new Services.Export.WindowsExportService(template, new Services.Assets.ImageAssetService()).ExportAsync(session.Project!, f.Root, doc.Id, export);
        Assert.AreEqual(31, Directory.GetFiles(Path.Combine(export, "Assets", "Audio")).Length);
    }

    [TestMethod]
    public async Task MixedBatchRejectsAnUnchangedWavModifiedAfterPreviewAndRetainsAllBindings()
    {
        using var f = new Fixture(); await f.Session.NewProjectAsync(); f.Session.AddScene(); var doc = f.Session.ActiveScene!;
        doc.InsertDialogue([new(null, "А"), new(null, "Б")]); await f.Session.SaveAsync(); await f.Session.ExportRecordingSheetAsync(doc.Id, "ru");
        var rows = RecordingSheet.Read(await File.ReadAllTextAsync(f.Dialogs.Sheet));
        foreach (var row in rows) await File.WriteAllBytesAsync(Path.Combine(f.Dialogs.Folder, row.Wav), AudioFixture.Wave());
        await f.Session.ImportRecordingSheetAsync(doc.Id);
        await File.WriteAllBytesAsync(Path.Combine(f.Dialogs.Folder, rows[0].Wav), AudioFixture.Wave(4800));
        var plan = await RecordingSheet.PreviewAsync(f.Session.Project!, doc.Id, rows, f.Dialogs.Folder, f.Root);
        Assert.AreEqual(1, plan.UnchangedCount); Assert.AreEqual(1, plan.ImportCount);
        await File.WriteAllBytesAsync(Path.Combine(f.Dialogs.Folder, rows[1].Wav), AudioFixture.Wave(7200));
        var before = f.Session.Project!; await f.Session.ApplyVoiceImportAsync(plan);
        Assert.AreEqual(1, f.Dialogs.Errors.Count); Assert.IsTrue(before.ContentEquals(f.Session.Project));
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(f.Root, "Assets", "Audio")).Length);
    }

    [TestMethod]
    [DataRow(".csv")] [DataRow(".tsv")]
    public async Task RecordingSheetScopeUsesTheFilterAndPreservesOriginalNumbersAndCancelDoesNotWrite(string extension)
    {
        using var f = new Fixture(); var session = f.Session; await session.NewProjectAsync();
        session.AddCharacter(); session.SelectedCharacterName = "Лена"; var lena = session.SelectedCharacter!.Id;
        session.AddCharacter(); session.SelectedCharacterName = "Антон"; var anton = session.SelectedCharacter!.Id;
        session.AddScene(); var doc = session.ActiveScene!;
        doc.InsertDialogue([new(lena, "Записано"), new(anton, "Нужно"), new(lena, "Нужно 1"), new(lena, "Без голоса"), new(lena, "Нужно 2"), new(null, "Рассказчик")]);
        var path = Path.Combine(f.Root, "voice.wav"); await File.WriteAllBytesAsync(path, AudioFixture.Wave());
        await session.AttachLineVoiceAsync(doc.Id, doc.Lines[0].Id, "ru", path); doc.Lines[3].VoiceOmitted = true;
        doc.SpeakerFilter = doc.SpeakerFilters.Single(c => c.Id == lena); doc.MissingVoicesOnly = true;
        Assert.AreEqual(2, doc.VisibleLines.Count); await session.SaveAsync();
        f.Dialogs.Sheet = Path.ChangeExtension(f.Dialogs.Sheet, extension); f.Dialogs.Scope = RecordingSheetScope.Filtered;
        var visible = doc.VisibleLines.Select(l => l.Id).ToArray();
        await session.ExportRecordingSheetAsync(doc.Id, "ru", visible);
        var filtered = RecordingSheet.Read(await File.ReadAllTextAsync(f.Dialogs.Sheet));
        CollectionAssert.AreEqual(new[] { 3, 5 }, filtered.Select(r => r.Number).ToArray()); CollectionAssert.AreEqual(visible, filtered.Select(r => r.LineId).ToArray());
        Assert.IsTrue(filtered.All(r => r.Speaker == "Лена" && r.Language == "ru" && r.Required)); Assert.AreEqual((6, 2), f.Dialogs.ScopeCounts);
        f.Dialogs.Scope = RecordingSheetScope.Scene; await session.ExportRecordingSheetAsync(doc.Id, "ru", visible);
        var all = RecordingSheet.Read(await File.ReadAllTextAsync(f.Dialogs.Sheet)); Assert.AreEqual(6, all.Count); Assert.IsFalse(all[3].Required);
        var previous = await File.ReadAllBytesAsync(f.Dialogs.Sheet); var calls = f.Dialogs.SheetPickerCalls;
        f.Dialogs.Scope = RecordingSheetScope.Filtered; await session.ExportRecordingSheetAsync(doc.Id, "ru", Array.Empty<Guid>()); Assert.AreEqual(calls, f.Dialogs.SheetPickerCalls);
        f.Dialogs.Scope = null; await session.ExportRecordingSheetAsync(doc.Id, "ru", visible); Assert.AreEqual(calls, f.Dialogs.SheetPickerCalls);
        f.Dialogs.Scope = RecordingSheetScope.Scene; f.Dialogs.CancelSheetSave = true; await session.ExportRecordingSheetAsync(doc.Id, "ru", visible);
        CollectionAssert.AreEqual(previous, await File.ReadAllBytesAsync(f.Dialogs.Sheet)); Assert.IsFalse(session.IsDirty); Assert.AreEqual(0, f.Dialogs.Errors.Count);
    }
    [TestMethod]
    [TestCategory("NativeRuntime")]
    [DataRow(false)] [DataRow(true)]
    public async Task ImportedThirtyLineScenePlaysThroughEditorBackendAndNativeF6Runtime(bool packaged)
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1" || packaged && Environment.GetEnvironmentVariable("NOVELLMAKER_EXPORT_SMOKE") != "1") Assert.Inconclusive("Enable native/export smoke.");
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(System.IO.Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var evidence = NativeDelivery.Evidence(repo!.FullName, "dialogue-recording", packaged ? "export" : "preview"); Directory.CreateDirectory(evidence);
        using var fixture = new Fixture(evidence); using var session = fixture.Session; await session.NewProjectAsync();
        fixture.Dialogs.ImagePath = System.IO.Path.Combine(evidence, "actor.png");
        using (var bitmap = new SkiaSharp.SKBitmap(32, 64))
        { bitmap.Erase(SkiaSharp.SKColors.Gray); using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100); using var file = File.Create(fixture.Dialogs.ImagePath); data.SaveTo(file); }
        await session.ImportImageAsync();
        session.AddCharacter(); session.SelectedCharacterName = "Лена"; session.AddCharacterState(); var lena = session.SelectedCharacter!.Id;
        session.AddCharacter(); session.SelectedCharacterName = "Антон"; session.AddCharacterState(); var anton = session.SelectedCharacter!.Id;
        session.AddScene(); var doc = session.ActiveScene!;
        doc.InsertDialogue(Enumerable.Range(1, 30).Select(i => new DialoguePasteLine(i % 2 == 0 ? anton : lena, $"Проверка реплики {i}")).ToArray());
        await session.SaveAsync(); await session.ExportRecordingSheetAsync(doc.Id, "ru");
        var rows = RecordingSheet.Read(await File.ReadAllTextAsync(fixture.Dialogs.Sheet));
        foreach (var row in rows) await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Dialogs.Folder, row.Wav), AudioFixture.Wave(48000));
        doc.SelectLine(rows[0].LineId); doc.MoveLine(1); doc.SelectLine(rows[29].LineId); doc.MoveLine(-1);
        await session.ImportRecordingSheetAsync(doc.Id); await session.SaveAsync(); Assert.AreEqual(0, fixture.Dialogs.Errors.Count);
        var backend = new NovellMaker.Runtime.Audio.MonoGameAudioBackend();
        using (var audition = new VoiceAudition(backend))
        {
            var asset = session.Project!.Audio.Assets.Single(a => a.Id == session.Project.Audio.Voices[rows[0].LineId]["ru"]);
            Microsoft.Xna.Framework.FrameworkDispatcher.Update(); audition.Play(AudioAssetPath.Resolve(evidence, asset.RelativePath), .25f);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (audition.IsPlaying && clock.Elapsed < TimeSpan.FromSeconds(5))
            { Microsoft.Xna.Framework.FrameworkDispatcher.Update(); audition.Update(); await Task.Delay(20); }
            Assert.IsFalse(audition.IsPlaying); Assert.AreEqual(1, backend.OpenedSources); Assert.AreEqual(96000L, backend.SubmittedBytes);
        }
        var plan = System.IO.Path.Combine(evidence, "plan.json"); await File.WriteAllTextAsync(plan, """
            [{"Kind":"wait_now","Seconds":0.35},{"Kind":"next"},{"Kind":"wait_now","Seconds":0.35}]
            """);
        var reportPath = System.IO.Path.Combine(evidence, "runtime.json");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var runtime = NativeDelivery.Preview(System.IO.Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", configuration, "net10.0", "NovellMaker.Runtime.exe"));
        if (packaged)
        {
            var outputDirectory = System.IO.Path.Combine(evidence, "game");
            await new NovellMaker.Editor.Services.Export.WindowsExportService(NativeDelivery.Template(repo.FullName), new NovellMaker.Editor.Services.Assets.ImageAssetService())
                .ExportAsync(session.Project!, evidence, doc.Id, outputDirectory);
            runtime = System.IO.Path.Combine(outputDirectory, "NovellMaker.Runtime.exe");
        }
        var start = new System.Diagnostics.ProcessStartInfo(runtime)
        { WorkingDirectory = evidence, UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!packaged) foreach (var arg in new[] { "--project", fixture.Dialogs.Path, "--assets-root", evidence, "--scene", doc.Id.ToString(), "--line", doc.Lines[0].Id.ToString(), "--language", "ru" }) start.ArgumentList.Add(arg);
        foreach (var arg in new[] { "--smoke-output", System.IO.Path.Combine(evidence, "runtime.png"), "--smoke-report", reportPath, "--smoke-plan", plan }) start.ArgumentList.Add(arg);
        NativeDelivery.Isolate(start, evidence, packaged);
        using var process = System.Diagnostics.Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); Assert.Fail("Runtime timed out."); }
        Assert.AreEqual(0, process.ExitCode, await errors + await output);
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)); var report = json.RootElement; var trace = report.GetProperty("ActionTrace");
        Assert.AreEqual(0, report.GetProperty("Diagnostics").GetArrayLength());
        Assert.AreEqual(doc.Lines[0].Id, trace[0].GetProperty("State").GetProperty("LineId").GetGuid());
        Assert.IsTrue(trace[0].GetProperty("Voice").GetProperty("Progress").GetDouble() > 0);
        Assert.AreEqual(doc.Lines[1].Id, trace[2].GetProperty("State").GetProperty("LineId").GetGuid());
        Assert.IsTrue(trace[2].GetProperty("Voice").GetProperty("Progress").GetDouble() > 0);
    }
    [TestMethod]
    public async Task ThirtyLinesSurviveActorRoundTripReorderingUndoReopenAndF6Snapshot()
    {
        using var fixture = new Fixture(); using var session = fixture.Session;
        await session.NewProjectAsync();
        session.AddCharacter(); session.SelectedCharacterName = "Лена"; var lena = session.SelectedCharacter!.Id;
        session.AddCharacter(); session.SelectedCharacterName = "Антон"; var anton = session.SelectedCharacter!.Id;
        session.AddScene(); var doc = session.ActiveScene!;
        var parsed = DialoguePaste.Parse(string.Join('\n', Enumerable.Range(1, 30).Select(i => $"{(i % 2 == 0 ? "Антон" : "Лена")}: Реплика {i}: «Да, слышал».")));
        doc.InsertDialogue(parsed.Select(l => new DialoguePasteLine(DialoguePaste.Match(l.Speaker, session.Project!.Characters), l.Text)).ToArray());
        Assert.AreEqual(30, doc.Lines.Count); Assert.AreEqual(15, doc.Lines.Count(l => l.Model.SpeakerId == lena)); Assert.AreEqual(15, doc.Lines.Count(l => l.Model.SpeakerId == anton));
        var ids = doc.Lines.Select(l => l.Id).ToArray();
        session.Undo(); Assert.AreEqual(0, doc.Lines.Count); session.Redo(); Assert.AreEqual(30, doc.Lines.Count);
        await session.SaveAsync(); Assert.AreEqual(0, fixture.Dialogs.Errors.Count);
        await session.ExportRecordingSheetAsync(doc.Id, "ru");
        var sheet = RecordingSheet.Read(await File.ReadAllTextAsync(fixture.Dialogs.Sheet));
        Assert.AreEqual(30, sheet.Count);
        foreach (var row in sheet) await File.WriteAllBytesAsync(Path.Combine(fixture.Dialogs.Folder, row.Wav), AudioFixture.Wave(2400 + row.Number * 240));
        doc.SelectLine(ids[0]); doc.MoveLine(1); doc.SelectLine(ids[29]); doc.MoveLine(-1);
        var reordered = doc.Lines.Select(l => l.Id).ToArray(); Assert.AreNotEqual(ids[0], reordered[0]);
        await session.ImportRecordingSheetAsync(doc.Id);
        Assert.AreEqual(0, fixture.Dialogs.Errors.Count, string.Join('\n', fixture.Dialogs.Errors));
        Assert.AreEqual(30, fixture.Dialogs.Plan!.Matches); Assert.AreEqual(0, fixture.Dialogs.Plan.Missing);
        Assert.AreEqual(30, session.Project!.Audio.Voices.Count);
        foreach (var line in doc.Lines)
        {
            var wav = session.Project.Audio.Assets.Single(a => a.Id == session.Project.Audio.Voices[line.Id]["ru"]);
            Assert.AreEqual(RecordingSheet.SuggestedWav(line.Id, "ru"), wav.Name);
        }
        session.Undo(); Assert.AreEqual(0, session.Project.Audio.Voices.Count); Assert.AreEqual(0, session.Project.Audio.Assets.Length);
        CollectionAssert.AreEqual(reordered, doc.Lines.Select(l => l.Id).ToArray());
        session.Redo(); Assert.AreEqual(30, session.Project.Audio.Voices.Count);
        doc.SelectLine(ids[15]); await session.PreviewSceneAsync();
        Assert.AreEqual(ids[15], fixture.Preview.Request!.LineId);
        Assert.AreEqual(30, fixture.Preview.Request.Project.Audio.Voices.Count);
        await session.SaveAsync(); await session.OpenProjectAsync(); session.OpenScene(doc.Id);
        Assert.AreEqual(0, fixture.Dialogs.Errors.Count); Assert.AreEqual(30, session.Project!.Audio.Voices.Count);
        CollectionAssert.AreEqual(reordered, session.ActiveScene!.Lines.Select(l => l.Id).ToArray());
        using var audio = new AudioService(session.Project, fixture.Root, "ru", new FakeBackend());
        audio.Handle(new(AudioEventKind.Line, doc.Id, ids[15]));
        Assert.IsTrue(audio.IsVoicePlaying);
    }

    [TestMethod]
    public async Task OmissionIsLocalizedUndoableClonedPrunedAndPersisted()
    {
        using var fixture = new Fixture(); using var session = fixture.Session; await session.NewProjectAsync(); session.AddScene(); var doc = session.ActiveScene!;
        doc.InsertDialogue([new(null, "Рассказчик"), new(null, "Нужен голос")]); var row = doc.Lines[0];
        row.VoiceOmitted = true; doc.MissingVoicesOnly = true;
        Assert.AreEqual(1, doc.VisibleLines.Count); Assert.AreNotEqual(row.Id, doc.VisibleLines[0].Id);
        doc.EditingLanguage = "en"; Assert.IsFalse(row.VoiceOmitted); Assert.IsTrue(row.NeedsVoice); Assert.AreEqual(2, doc.VisibleLines.Count);
        doc.EditingLanguage = "ru"; session.Undo(); Assert.IsFalse(row.VoiceOmitted); session.Redo(); Assert.IsTrue(row.VoiceOmitted);
        doc.MissingVoicesOnly = false; doc.SelectLine(row.Id); doc.DuplicateLine(); var clone = doc.SelectedLine!; Assert.IsTrue(clone.VoiceOmitted);
        await session.SaveAsync(); await session.OpenProjectAsync(); session.OpenScene(doc.Id); doc = session.ActiveScene!; doc.SelectLine(clone.Id);
        Assert.IsTrue(doc.SelectedLine!.VoiceOmitted); doc.DeleteLine(); Assert.IsFalse(session.Project!.Audio.VoiceOmissions.ContainsKey(clone.Id));
        session.Undo(); Assert.IsTrue(session.Project.Audio.VoiceOmissions.ContainsKey(clone.Id));
        var wave = Path.Combine(fixture.Root, "take.wav"); await File.WriteAllBytesAsync(wave, AudioFixture.Wave());
        await session.AttachLineVoiceAsync(doc.Id, row.Id, "ru", wave); Assert.AreEqual(0, fixture.Dialogs.Errors.Count);
        var restored = doc.Lines.Single(l => l.Id == row.Id); Assert.IsFalse(restored.VoiceOmitted); Assert.IsTrue(restored.HasVoice);
        doc.EditingLanguage = "en"; Assert.IsFalse(restored.HasVoice); Assert.IsTrue(restored.NeedsVoice);
        doc.EditingLanguage = "ru"; session.Undo(); Assert.IsTrue(restored.VoiceOmitted); Assert.IsFalse(restored.HasVoice);
    }

    [TestMethod]
    public async Task PreviewCountsMissingReplacementOmittedUnknownAndRejectsChangedFilesAtomically()
    {
        using var fixture = new Fixture(); using var session = fixture.Session; await session.NewProjectAsync(); session.AddScene(); var doc = session.ActiveScene!;
        doc.InsertDialogue(Enumerable.Range(0, 6).Select(i => new DialoguePasteLine(null, $"Фраза {i}")).ToArray());
        await session.SaveAsync();
        var wave = Path.Combine(fixture.Root, "first.wav"); await File.WriteAllBytesAsync(wave, AudioFixture.Wave());
        await session.AttachLineVoiceAsync(doc.Id, doc.Lines[0].Id, "ru", wave); doc.Lines[2].VoiceOmitted = true;
        var rows = RecordingSheet.Rows(session.Project!, session.Project!.Scenes.Single(), "ru").ToArray();
        foreach (var row in rows.Where((_, i) => i != 1)) await File.WriteAllBytesAsync(Path.Combine(fixture.Dialogs.Folder, row.Wav), AudioFixture.Wave());
        await File.WriteAllTextAsync(Path.Combine(fixture.Dialogs.Folder, rows[5].Wav), "not a WAV");
        rows[4] = rows[4] with { LineId = Guid.NewGuid() };
        var plan = await RecordingSheet.PreviewAsync(session.Project!, doc.Id, rows, fixture.Dialogs.Folder);
        Assert.AreEqual(2, plan.Matches); Assert.AreEqual(1, plan.Missing); Assert.AreEqual(1, plan.Replacements);
        StringAssert.Contains(plan.Items[5].Status, "Ошибка WAV");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Dialogs.Folder, rows[3].Wav), AudioFixture.Wave(4800));
        var before = session.Project!; var filesBefore = Directory.GetFiles(Path.Combine(fixture.Root, "Assets", "Audio")).Length;
        await session.ApplyVoiceImportAsync(plan);
        Assert.AreEqual(1, fixture.Dialogs.Errors.Count); Assert.IsTrue(before.ContentEquals(session.Project));
        Assert.AreEqual(filesBefore, Directory.GetFiles(Path.Combine(fixture.Root, "Assets", "Audio")).Length, "Copies from a failed batch must be removed.");
        await File.WriteAllTextAsync(AudioAssetPath.Resolve(fixture.Root, session.Project!.Audio.Assets.Single().RelativePath), "corrupt");
        StringAssert.Contains(doc.Lines[0].VoiceSummary, "файл недоступен");
    }

    [TestMethod]
    [DataRow(',')]
    [DataRow('\t')]
    public void CsvAndTsvRoundTripUnicodeQuotesTabsAndNewlines(char separator)
    {
        var row = new RecordingRow(1, "Лена", "Ты: \"слышал\"?\tДа,\nслышал.\r\n", "ru", Guid.NewGuid(), "take.wav", true);
        CollectionAssert.AreEqual(new[] { row }, RecordingSheet.Read("\uFEFF" + RecordingSheet.Write([row], separator)).ToArray());
        Assert.Throws<InvalidDataException>(() => RecordingSheet.Read(RecordingSheet.Write([row, row], separator)));
        Assert.Throws<InvalidDataException>(() => RecordingSheet.Read(RecordingSheet.Write([row with { Wav = "../take.wav" }], separator)));
        Assert.Throws<InvalidDataException>(() => RecordingSheet.Read(RecordingSheet.Write([row], separator).TrimEnd() + "\""));
    }

    [TestMethod]
    public async Task V23MigratesWithoutVoiceFlagsAndV24ValidatesConflicts()
    {
        using var fixture = new Fixture(); using var session = fixture.Session; await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.AddLine(); await session.SaveAsync();
        var json = await File.ReadAllTextAsync(fixture.Dialogs.Path); Assert.IsTrue(json.Contains("voiceOmissions"));
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!; legacy["formatVersion"] = 23; legacy["audio"]!.AsObject().Remove("voiceOmissions");
        await File.WriteAllTextAsync(fixture.Dialogs.Path, legacy.ToJsonString());
        var project = await new JsonProjectStore().LoadAsync(fixture.Dialogs.Path);
        Assert.AreEqual(NovelProject.CurrentFormatVersion, project.FormatVersion); Assert.AreEqual(0, project.Audio.VoiceOmissions.Count);
        var id = project.Scenes.Single().Lines.Single().Id;
        var invalid = project with { Audio = project.Audio with { VoiceOmissions = ImmutableDictionary<Guid, ImmutableHashSet<string>>.Empty.Add(Guid.NewGuid(), ["ru"]) } };
        Assert.Throws<ArgumentException>(() => AudioProject.Validate(invalid));
        var audioId = Guid.NewGuid();
        invalid = project with { Audio = project.Audio with { Assets = [new(audioId, "voice.wav", "Assets/Audio/voice.wav")],
            Voices = ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>>.Empty.Add(id, ImmutableDictionary<string, Guid>.Empty.Add("ru", audioId)),
            VoiceOmissions = ImmutableDictionary<Guid, ImmutableHashSet<string>>.Empty.Add(id, ["ru"]) } };
        Assert.Throws<ArgumentException>(() => AudioProject.Validate(invalid));
        await session.OpenProjectAsync(); session.OpenScene(project.Scenes.Single().Id);
        session.ActiveScene!.Lines[0].VoiceOmitted = true; await session.SaveAsync();
        var flagged = await new JsonProjectStore().LoadAsync(fixture.Dialogs.Path); Assert.IsTrue(flagged.Audio.VoiceOmissions[id].Contains("ru"));
    }

    [TestMethod]
    public void AuditionStopsPreviousTakeAndReleasesCompletedOrFailedSource()
    {
        var backend = new FakeBackend(); using var audition = new VoiceAudition(backend);
        audition.Play("a.wav", .5f); var first = backend.Last!; audition.Play("b.wav", 1);
        Assert.IsTrue(first.Disposed); var second = backend.Last!; second.Done = true; audition.Update(); Assert.IsFalse(audition.IsPlaying); Assert.IsTrue(second.Disposed);
        audition.Play("c.wav", 1); backend.Last!.Fail = true; Assert.Throws<IOException>(audition.Update); Assert.IsFalse(audition.IsPlaying); Assert.IsTrue(backend.Last.Disposed);
    }

    private sealed class FakeBackend : IAudioBackend
    {
        internal Source? Last;
        public IAudioSource Open(string path, bool loop) => Last = new Source();
        internal sealed class Source : IAudioSource
        {
            internal bool Done, Disposed, Fail;
            public float Volume { set { } }
            public bool Finished => Done;
            public TimeSpan Position => TimeSpan.Zero;
            public TimeSpan Duration => TimeSpan.FromSeconds(1);
            public void Update() { if (Fail) throw new IOException("device"); }
            public void Pause(bool paused) { }
            public void Dispose() => Disposed = true;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        private readonly bool retain;
        internal readonly Dialogs Dialogs;
        internal readonly Preview Preview = new();
        internal readonly EditorSession Session;
        internal Fixture(string? evidence = null)
        {
            Root = evidence ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NovellMaker.RecordingTests", Guid.NewGuid().ToString("N")); retain = evidence is not null;
            Directory.CreateDirectory(Root); Directory.CreateDirectory(System.IO.Path.Combine(Root, "takes"));
            Dialogs = new() { Path = System.IO.Path.Combine(Root, "voice.novell"), Sheet = System.IO.Path.Combine(Root, "sheet.tsv"), Folder = System.IO.Path.Combine(Root, "takes") };
            Session = new(new JsonProjectStore(), Dialogs, new Git(), previewService: Preview);
        }
        public void Dispose() { Session.Dispose(); if (!retain) Directory.Delete(Root, true); }
    }
    private sealed class Dialogs : IEditorDialogs
    {
        internal string Path = "", Sheet = "", Folder = "";
        internal string? ImagePath;
        public Task<string?> PickImageAsync() => Task.FromResult(ImagePath);
        internal List<string> Errors = [];
        internal VoiceImportPlan? Plan;
        internal RecordingSheetScope? Scope = RecordingSheetScope.Scene;
        internal (int, int) ScopeCounts;
        internal int SheetPickerCalls;
        internal bool CancelSheetSave;
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(Path);
        public Task<string?> PickSaveProjectAsync(string suggestedName) => Task.FromResult<string?>(Path);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName) => Task.FromResult(UnsavedChangesDecision.Discard);
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
        public Task<RecordingSheetScope?> PickRecordingSheetScopeAsync(int sceneCount, int filteredCount) { ScopeCounts = (sceneCount, filteredCount); return Task.FromResult(Scope); }
        public Task<string?> PickSaveRecordingSheetAsync(string name) { SheetPickerCalls++; return Task.FromResult<string?>(CancelSheetSave ? null : Sheet); }
        public Task<string?> PickRecordingSheetAsync() => Task.FromResult<string?>(Sheet);
        public Task<string?> PickVoiceFolderAsync() => Task.FromResult<string?>(Folder);
        public Task<bool> ConfirmVoiceImportAsync(VoiceImportPlan plan) { Plan = plan; return Task.FromResult(true); }
    }
    private sealed class Git : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string projectDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitStatusSnapshot(GitAvailability.NotRepository, null, [], "Git: нет репозитория"));
    }
    private sealed class Preview : IPreviewService
    {
        internal PreviewRequest? Request;
        public Task StartAsync(PreviewRequest request, Action<string> report) { Request = request; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
