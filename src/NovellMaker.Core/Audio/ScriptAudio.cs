namespace NovellMaker.Core.Audio;

/// <summary>Host identity, never supplied by author Lua.</summary>
public sealed record AudioOwner(Guid SessionId, long Generation, Guid OwnerId, Guid InstanceId);

public sealed record ScriptAudioCommand(string Kind, Guid Id, Guid? AssetId = null,
    AudioGroup? Group = null, bool? Loop = null, double? Volume = null)
{
    public void Validate()
    {
        if (Id == Guid.Empty || Kind switch
        {
            "play" => AssetId is null || AssetId == Guid.Empty || Group is not (AudioGroup.Sound or AudioGroup.Effect or AudioGroup.Voice) || Loop is null || !ValidVolume(Volume),
            "stop" => AssetId is not null || Group is not null || Loop is not null || Volume is not null,
            "volume" => AssetId is not null || Group is not null || Loop is not null || !ValidVolume(Volume),
            _ => true,
        }) throw new ArgumentException("Некорректная команда звука Lua.");
    }
    private static bool ValidVolume(double? value) => value is { } v && double.IsFinite(v) && v is >= 0 and <= 1;
}

public sealed record ScriptAudioResult(Guid Id, string Status, string Message = "")
{
    public void Validate()
    {
        if (Id == Guid.Empty || Status is not ("completed" or "stopped" or "failed") || Message is null || Message.Length > 1024)
            throw new ArgumentException("Некорректный результат звука Lua.");
    }
}
