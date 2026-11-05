using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class SceneDocumentViewModel
{
    public ObservableCollection<DialogueLineViewModel> VisibleLines { get; } = [];
    private CharacterOption? speakerFilter;
    private bool missingVoicesOnly, showLineEffects;
    private bool refreshingDialogueRows;
    internal bool IsRefreshingDialogue => refreshingDialogueRows || refreshingAudio || refreshingCharacters;
    public IReadOnlyList<CharacterOption> SpeakerFilters => new[] { new CharacterOption(Guid.Empty, "Все персонажи") }.Concat(SpeakerOptions).ToArray();
    public CharacterOption? SpeakerFilter
    {
        get => speakerFilter;
        set { speakerFilter = value; Changed(); RefreshDialogueRows(); }
    }
    public bool MissingVoicesOnly
    {
        get => missingVoicesOnly;
        set { missingVoicesOnly = value; Changed(); RefreshDialogueRows(); }
    }
    public bool ShowLineEffects
    {
        get => showLineEffects;
        set { showLineEffects = value; Changed(); RefreshDialogueRows(); }
    }
    public string VoiceCoverage => $"Записано: {Lines.Count(l => l.HasVoice)} · нужно: {Lines.Count(l => l.NeedsVoice)} · без озвучки: {Lines.Count(l => l.VoiceOmitted)} · показано: {VisibleLines.Count}/{Lines.Count}";

    internal void EditRow(Guid id, Func<DialogueLine, DialogueLine> edit, string? group = null)
    {
        if (!Session.CanEdit || !IsEditingLanguageValid) return;
        var index = Array.FindIndex(scene.Lines.ToArray(), l => l.Id == id);
        if (index >= 0) Session.UpdateScene(scene with { Lines = scene.Lines.SetItem(index, edit(scene.Lines[index])) }, group);
    }
    public void InsertDialogue(IReadOnlyList<DialoguePasteLine> lines)
    {
        if (!Session.CanEdit || !IsEditingLanguageValid || lines.Count == 0) return;
        if (lines.Any(l => l.SpeakerId is { } id && !Session.Project!.Characters.Any(c => c.Id == id)))
            throw new InvalidOperationException("Персонаж вставки больше не существует.");
        var added = lines.Select(l => DialogueLine.Create(EditingLanguage, l.Text) with { SpeakerId = l.SpeakerId }).ToImmutableArray();
        var position = SelectedIndex < 0 ? scene.Lines.Length : SelectedIndex + 1;
        var canvas = scene.Canvas;
        if (scene.Lines.IsEmpty && !canvas.Nodes.Any(n => n.Dialogue != NovellMaker.Core.Screens.DialogueBinding.None))
        {
            var nodes = DialogueCanvas.Add(canvas.Nodes, scene.Id, style, legacy: true);
            _ = new NovellMaker.Core.Screens.ScreenDefinition(scene.Id, nodes);
            canvas = canvas with { Nodes = nodes };
        }
        Session.UpdateScene(scene with { Lines = scene.Lines.InsertRange(position, added), Canvas = canvas });
        SelectLine(added[0].Id);
    }
    internal static ImmutableDictionary<Guid, ImmutableHashSet<string>> ClearOmission(AudioProject audio, Guid id, string language)
    {
        var languages = audio.VoiceOmissions.GetValueOrDefault(id)?.Remove(language);
        return languages is null || languages.IsEmpty ? audio.VoiceOmissions.Remove(id) : audio.VoiceOmissions.SetItem(id, languages);
    }
    internal void SetVoice(Guid id, Guid? asset)
    {
        if (!Session.CanEdit || !IsEditingLanguageValid || !Lines.Any(l => l.Id == id)) return;
        if (asset.HasValue && !Audio.Assets.Any(a => a.Id == asset.Value)) return;
        var voices = Audio.Voices.GetValueOrDefault(id) ?? ImmutableDictionary<string, Guid>.Empty;
        voices = asset.HasValue ? voices.SetItem(EditingLanguage, asset.Value) : voices.Remove(EditingLanguage);
        Session.UpdateAudio(Audio with { Voices = voices.IsEmpty ? Audio.Voices.Remove(id) : Audio.Voices.SetItem(id, voices),
            VoiceOmissions = asset.HasValue ? ClearOmission(Audio, id, EditingLanguage) : Audio.VoiceOmissions });
    }
    internal void OmitVoice(Guid id, bool omit)
    {
        if (!Session.CanEdit || !IsEditingLanguageValid || !Lines.Any(l => l.Id == id)) return;
        var languages = Audio.VoiceOmissions.GetValueOrDefault(id) ?? ImmutableHashSet<string>.Empty;
        var voices = Audio.Voices.GetValueOrDefault(id)?.Remove(EditingLanguage);
        Session.UpdateAudio(Audio with {
            VoiceOmissions = omit ? Audio.VoiceOmissions.SetItem(id, languages.Add(EditingLanguage)) : ClearOmission(Audio, id, EditingLanguage),
            Voices = !omit ? Audio.Voices : voices is null || voices.IsEmpty ? Audio.Voices.Remove(id) : Audio.Voices.SetItem(id, voices) });
    }
    internal void RefreshDialogueRows()
    {
        if (refreshingDialogueRows) return;
        refreshingDialogueRows = true;
        try
        {
            foreach (var row in Lines) row.RefreshDetails();
            var desired = Lines.Where(l => (speakerFilter is null || speakerFilter.Id == Guid.Empty || speakerFilter.Id == l.Model.SpeakerId) &&
                (!missingVoicesOnly || l.NeedsVoice)).ToArray();
            for (var i = VisibleLines.Count - 1; i >= 0; i--) if (!desired.Contains(VisibleLines[i])) VisibleLines.RemoveAt(i);
            for (var i = 0; i < desired.Length; i++)
            {
                var at = VisibleLines.IndexOf(desired[i]);
                if (at < 0) VisibleLines.Insert(i, desired[i]);
                else if (at != i) VisibleLines.Move(at, i);
            }
            Changed(nameof(VoiceCoverage));
        }
        finally { refreshingDialogueRows = false; }
    }
}

public sealed partial class DialogueLineViewModel
{
    private bool refreshingDetails;
    private bool CanWrite => !refreshingDetails && Owner?.IsRefreshingDialogue == false;
    internal SceneDocumentViewModel? Owner { get; init; }
    public bool CanEdit => Owner?.Session.CanEdit == true && Owner.IsEditingLanguageValid;
    public string Text
    {
        get => Model.Texts.GetValueOrDefault(Owner?.EditingLanguage ?? "", "");
        set { if (CanWrite && Owner is { } owner && value != Text) owner.EditRow(Id, l => l with { Texts = l.Texts.SetItem(owner.EditingLanguage, value ?? "") }, $"line-text:{Id}:{owner.EditingLanguage}"); }
    }
    public IReadOnlyList<CharacterOption> Speakers => Owner?.SpeakerOptions ?? [];
    public CharacterOption? Speaker
    {
        get => Speakers.FirstOrDefault(c => c.Id == Model.SpeakerId);
        set { if (CanWrite && value is not null && value.Id != Model.SpeakerId) Owner?.EditRow(Id, l => l with { SpeakerId = value.Id, CharacterStateId = null }); }
    }
    public IReadOnlyList<AudioOption> AudioOptions => Owner?.AudioOptions ?? (IReadOnlyList<AudioOption>)Array.Empty<AudioOption>();
    private AudioProject? Audio => Owner?.Session.Project?.Audio;
    private string Language => Owner?.EditingLanguage ?? "";
    public Guid? VoiceId => Audio?.Voices.GetValueOrDefault(Id)?.TryGetValue(Language, out var id) == true ? id : null;
    public bool HasVoice => VoiceId.HasValue;
    public bool NeedsVoice => !HasVoice && !VoiceOmitted;
    public AudioOption? Voice
    {
        get => AudioOptions.FirstOrDefault(a => a.Id == VoiceId);
        set { if (CanWrite && value is not null && value.Id != VoiceId) Owner?.SetVoice(Id, value.Id); }
    }
    public AudioOption? Effect
    {
        get => AudioOptions.FirstOrDefault(a => a.Id == (Audio?.LineEffects.TryGetValue(Id, out var id) == true ? id : (Guid?)null));
        set
        {
            if (CanWrite && CanEdit && Audio is { } audio && value is not null && value.Id != Effect?.Id)
                Owner!.Session.UpdateAudio(audio with { LineEffects = value.Id.HasValue ? audio.LineEffects.SetItem(Id, value.Id.Value) : audio.LineEffects.Remove(Id) });
        }
    }
    public bool ShowEffect => Owner?.ShowLineEffects == true;
    public bool VoiceOmitted
    {
        get => Audio?.VoiceOmissions.GetValueOrDefault(Id)?.Contains(Language) == true;
        set { if (CanWrite && value != VoiceOmitted) Owner?.OmitVoice(Id, value); }
    }
    public string VoiceSummary => VoiceOmitted ? "Без озвучки" : VoiceId is { } id && Audio?.Assets.FirstOrDefault(a => a.Id == id) is { } asset
        ? Owner!.Session.DescribeVoice(asset) : "Запись нужна";
    internal void RefreshDetails()
    {
        refreshingDetails = true;
        try
        {
            foreach (var name in new[] { nameof(CanEdit), nameof(Speakers), nameof(Speaker), nameof(AudioOptions), nameof(Voice), nameof(VoiceId), nameof(HasVoice),
                nameof(NeedsVoice), nameof(VoiceOmitted), nameof(VoiceSummary), nameof(ShowEffect), nameof(Effect) })
                PropertyChanged?.Invoke(this, new(name));
        }
        finally { refreshingDetails = false; }
    }
}
