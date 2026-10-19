namespace NovellMaker.Core.Audio;

public static class AudioContent
{
    public static bool Equals(AudioProject a, AudioProject b) =>
        a.Assets.SequenceEqual(b.Assets) && Equal(a.SceneSounds, b.SceneSounds) && Equal(a.LineEffects, b.LineEffects) &&
        Equal(a.Mixer, b.Mixer) && a.Voices.Count == b.Voices.Count && a.Voices.All(p => b.Voices.TryGetValue(p.Key, out var v) && Equal(p.Value, v));
    private static bool Equal<K,V>(IReadOnlyDictionary<K,V> a, IReadOnlyDictionary<K,V> b) where K : notnull =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && EqualityComparer<V>.Default.Equals(p.Value, v));
}
