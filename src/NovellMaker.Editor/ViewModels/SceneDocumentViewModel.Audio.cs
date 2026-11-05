using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NovellMaker.Core.Audio;

namespace NovellMaker.Editor.ViewModels;

public sealed record AudioOption(Guid? Id, string Name) { public override string ToString() => Name; }

public sealed partial class SceneDocumentViewModel
{
    private ImmutableArray<AudioAsset> audioCatalog;
    private bool refreshingAudio;
    public ObservableCollection<AudioOption> AudioOptions { get; } = [new(null, "Нет")];
    private AudioProject Audio => Session.Project!.Audio;
    public AudioOption SceneSound
    {
        get => Option(Audio.SceneSounds.GetValueOrDefault(Id));
        set { if (!refreshingAudio && value is not null) Session.UpdateAudio(Audio with { SceneSounds = Set(Audio.SceneSounds, Id, value.Id) }); }
    }
    public AudioOption LineEffect
    {
        get => Option(SelectedLine is null ? null : Audio.LineEffects.GetValueOrDefault(SelectedLine.Id));
        set { if (!refreshingAudio && value is not null && SelectedLine is { } l) Session.UpdateAudio(Audio with { LineEffects = Set(Audio.LineEffects, l.Id, value.Id) }); }
    }
    public AudioOption LineVoice
    {
        get => Option(SelectedLine is null ? null : Audio.Voices.GetValueOrDefault(SelectedLine.Id)?.GetValueOrDefault(EditingLanguage));
        set
        {
            if (refreshingAudio || value is null || SelectedLine is not { } l || !IsEditingLanguageValid) return;
            var voices = Audio.Voices.GetValueOrDefault(l.Id) ?? ImmutableDictionary<string, Guid>.Empty;
            voices = value.Id is { } id ? voices.SetItem(EditingLanguage, id) : voices.Remove(EditingLanguage);
            Session.UpdateAudio(Audio with { Voices = voices.IsEmpty ? Audio.Voices.Remove(l.Id) : Audio.Voices.SetItem(l.Id, voices),
                VoiceOmissions = value.Id.HasValue ? ClearOmission(Audio, l.Id, EditingLanguage) : Audio.VoiceOmissions });
        }
    }
    private AudioOption Option(Guid? id) => AudioOptions.FirstOrDefault(a => a.Id == id) ?? AudioOptions[0];
    private static ImmutableDictionary<Guid, Guid> Set(ImmutableDictionary<Guid, Guid> values, Guid key, Guid? id) => id is { } v ? values.SetItem(key, v) : values.Remove(key);
    public decimal MasterVolume { get => Volume(AudioGroup.Master); set => Volume(AudioGroup.Master, value); }
    public decimal SoundVolume { get => Volume(AudioGroup.Sound); set => Volume(AudioGroup.Sound, value); }
    public decimal EffectVolume { get => Volume(AudioGroup.Effect); set => Volume(AudioGroup.Effect, value); }
    public decimal VoiceVolume { get => Volume(AudioGroup.Voice); set => Volume(AudioGroup.Voice, value); }
    public bool MasterMuted { get => Bus(AudioGroup.Master).Muted; set => Mute(AudioGroup.Master, value); }
    public bool SoundMuted { get => Bus(AudioGroup.Sound).Muted; set => Mute(AudioGroup.Sound, value); }
    public bool EffectMuted { get => Bus(AudioGroup.Effect).Muted; set => Mute(AudioGroup.Effect, value); }
    public bool VoiceMuted { get => Bus(AudioGroup.Voice).Muted; set => Mute(AudioGroup.Voice, value); }
    private AudioBus Bus(AudioGroup g) => Audio.Mixer.GetValueOrDefault(g) ?? new();
    private decimal Volume(AudioGroup g) => (decimal)Bus(g).Volume * 100;
    private void Volume(AudioGroup g, decimal v) => Session.UpdateAudio(Audio with { Mixer = Audio.Mixer.SetItem(g, Bus(g) with { Volume = (float)Math.Clamp(v / 100, 0, 1) }) }, "audio-volume:" + g);
    private void Mute(AudioGroup g, bool v) => Session.UpdateAudio(Audio with { Mixer = Audio.Mixer.SetItem(g, Bus(g) with { Muted = v }) });
    private void RefreshAudio()
    {
        refreshingAudio = true;
        try
        {
            if (audioCatalog != Audio.Assets)
            {
                audioCatalog = Audio.Assets;
                var wanted = new[] { AudioOptions[0] }.Concat(audioCatalog.Select(a => new AudioOption(a.Id, a.Name))).ToArray();
                for (var i = AudioOptions.Count - 1; i > 0; i--)
                    if (!wanted.Any(a => a.Id == AudioOptions[i].Id)) AudioOptions.RemoveAt(i);
                for (var i = 1; i < wanted.Length; i++)
                {
                    var existing = AudioOptions.FirstOrDefault(a => a.Id == wanted[i].Id);
                    if (existing is null) AudioOptions.Insert(i, wanted[i]);
                    else
                    {
                        var at = AudioOptions.IndexOf(existing);
                        if (at != i) AudioOptions.Move(at, i);
                        if (existing.Name != wanted[i].Name) AudioOptions[i] = wanted[i];
                    }
                }
            }
            foreach (var name in new[] { nameof(SceneSound), nameof(LineEffect), nameof(LineVoice), nameof(MasterVolume), nameof(SoundVolume), nameof(EffectVolume), nameof(VoiceVolume), nameof(MasterMuted), nameof(SoundMuted), nameof(EffectMuted), nameof(VoiceMuted) }) Changed(name);
            RefreshDialogueRows();
        }
        finally { refreshingAudio = false; }
    }
}
