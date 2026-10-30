using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.ExternalServices;

/// <summary>Public, credential-free project declaration. Invalid editor drafts are validated before persistence/run.</summary>
[JsonConverter(typeof(ReleaseCheckSettingsConverter))]
public sealed record ReleaseCheckSettings(string Provider, string Owner, string Repository, string CurrentVersion)
{
    public ReleaseCheckConfiguration ToConfiguration()
    {
        if (Provider != ReleaseCheckConfiguration.Provider)
            throw new ArgumentException("Неизвестный провайдер проверки выпуска. Выберите GitHub Releases.");
        return new(Owner, Repository, CurrentVersion);
    }

    public override string ToString() => nameof(ReleaseCheckSettings);
}

/// <summary>Only the four declared public fields can cross persistence. Diagnostics never echo input.</summary>
public sealed class ReleaseCheckSettingsConverter : JsonConverter<ReleaseCheckSettings>
{
    public override ReleaseCheckSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        try
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("provider" or "owner" or "repository" or "currentVersion") ||
                    property.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(property.Name, property.Value.GetString()!))
                    throw new JsonException();
            }
            if (fields.Count != 4) throw new JsonException();
            var result = new ReleaseCheckSettings(fields["provider"], fields["owner"], fields["repository"], fields["currentVersion"]);
            _ = result.ToConfiguration();
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new JsonException("Некорректная декларация проверки выпуска: ожидаются только provider, owner, repository и currentVersion."); }
    }

    public override void Write(Utf8JsonWriter writer, ReleaseCheckSettings value, JsonSerializerOptions options)
    {
        _ = value.ToConfiguration();
        writer.WriteStartObject();
        writer.WriteString("provider", value.Provider); writer.WriteString("owner", value.Owner);
        writer.WriteString("repository", value.Repository); writer.WriteString("currentVersion", value.CurrentVersion);
        writer.WriteEndObject();
    }
}
