using NovellMaker.Core.Audio;

namespace NovellMaker.Core.Profiles;

public sealed record PlayerAudioSettings(float Volume, bool Muted)
{
    public AudioBus ToBus() => new(Volume, Muted);
    internal bool IsValid => float.IsFinite(Volume) && Volume is >= 0 and <= 1;
}

/// <summary>Player-owned preferences. Never part of a project revision or a gameplay save.</summary>
public sealed record PlayerSettings(PlayerAudioSettings Master, PlayerAudioSettings Sound,
    PlayerAudioSettings Effect, PlayerAudioSettings Voice, bool Fullscreen)
{
    public static PlayerSettings Defaults(AudioProject audio) => new(
        DefaultBus(audio, AudioGroup.Master), DefaultBus(audio, AudioGroup.Sound), DefaultBus(audio, AudioGroup.Effect), DefaultBus(audio, AudioGroup.Voice), false);
    // An explicit factory keeps missing author mixer entries equivalent to AudioService defaults.
    private static PlayerAudioSettings DefaultBus(AudioProject audio, AudioGroup group)
    { var bus = audio.Mixer.GetValueOrDefault(group) ?? new(); return new(bus.Volume, bus.Muted); }

    public void Validate()
    {
        if (Master is not { IsValid: true } || Sound is not { IsValid: true } ||
            Effect is not { IsValid: true } || Voice is not { IsValid: true })
            throw new ArgumentException("Громкость профиля: конечное число от 0 до 1.");
    }

    public PlayerAudioSettings Bus(AudioGroup group) => group switch
    {
        AudioGroup.Master => Master, AudioGroup.Sound => Sound, AudioGroup.Effect => Effect,
        AudioGroup.Voice => Voice, _ => throw new ArgumentException("Неизвестная группа звука."),
    };

    public PlayerSettings SetBus(AudioGroup group, AudioBus bus)
    {
        var value = new PlayerAudioSettings(bus.Volume, bus.Muted);
        var updated = group switch
        {
            AudioGroup.Master => this with { Master = value }, AudioGroup.Sound => this with { Sound = value },
            AudioGroup.Effect => this with { Effect = value }, AudioGroup.Voice => this with { Voice = value },
            _ => throw new ArgumentException("Неизвестная группа звука."),
        };
        updated.Validate(); return updated;
    }

    public PlayerSettings Apply(PlayerSettingChange change)
    {
        change.Validate();
        if (change.Key == "fullscreen") return this with { Fullscreen = change.Flag!.Value };
        var group = change.Key.Split('_')[0] switch
        { "master" => AudioGroup.Master, "sound" => AudioGroup.Sound, "effect" => AudioGroup.Effect, _ => AudioGroup.Voice };
        var bus = Bus(group).ToBus();
        return SetBus(group, change.Number is { } number ? bus with { Volume = (float)number } : bus with { Muted = change.Flag!.Value });
    }
}

public sealed record PlayerSettingChange(string Key, double? Number, bool? Flag)
{
    public void Validate()
    {
        var volume = Key is "master_volume" or "sound_volume" or "effect_volume" or "voice_volume";
        var flag = Key is "master_muted" or "sound_muted" or "effect_muted" or "voice_muted" or "fullscreen";
        if (!(volume && Number is { } number && double.IsFinite(number) && number is >= 0 and <= 1 && Flag is null ||
              flag && Flag is not null && Number is null))
            throw new ArgumentException("Неизвестная настройка, неверный тип или значение.");
    }
}
