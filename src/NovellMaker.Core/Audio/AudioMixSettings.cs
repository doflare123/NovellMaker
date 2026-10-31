namespace NovellMaker.Core.Audio;

/// <summary>Project-authored envelopes. Zero durations preserve immediate playback.</summary>
public sealed record AudioMixSettings
{
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    public double CrossfadeSeconds { get; init; }
    public bool DuckingEnabled { get; init; }
    public float DuckingVolume { get; init; } = .35f;
    public double DuckingAttackSeconds { get; init; } = .15;
    public double DuckingReleaseSeconds { get; init; } = .3;

    public void Validate()
    {
        if (new[] { FadeInSeconds, FadeOutSeconds, CrossfadeSeconds, DuckingAttackSeconds, DuckingReleaseSeconds }
            .Any(v => !double.IsFinite(v) || v is < 0 or > 30) ||
            !float.IsFinite(DuckingVolume) || DuckingVolume is < 0 or > 1)
            throw new ArgumentException("Огибающие звука: 0–30 с; громкость приглушения: 0–1.");
    }
}

public sealed record VoicePlayback(TimeSpan Position, TimeSpan Duration, bool Available, bool Paused)
{
    public static VoicePlayback None { get; } = new(TimeSpan.Zero, TimeSpan.Zero, false, false);
    public double Progress => Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public string Label => Available ? $"{(Paused ? "Пауза" : "Озвучка")} {Position.ToString(@"mm\:ss")} / {Duration.ToString(@"mm\:ss")}" : "Нет озвучки";
}
