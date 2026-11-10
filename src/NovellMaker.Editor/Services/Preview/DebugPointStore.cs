using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.Execution;

namespace NovellMaker.Editor.Services.Preview;

internal sealed record DebugPoint(string Name, PreviewStart Start)
{
    public override string ToString() => Name;
}

internal sealed record StoredDebugPoint(string FileName, DebugPoint? Point, string? Error)
{
    public override string ToString() => Point?.Name ?? $"Повреждена · {FileName}";
}

internal sealed class DebugPointStore(Guid projectId, Guid sceneId)
{
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovellMaker", "DebugPoints", projectId.ToString("N"), sceneId.ToString("N"));

    private string PointPath(string label) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label))) + ".json");

    internal List<StoredDebugPoint> Read()
    {
        if (!Directory.Exists(directory)) return [];
        var points = new List<StoredDebugPoint>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var point = JsonSerializer.Deserialize<DebugPoint>(File.ReadAllText(path));
                if (string.IsNullOrWhiteSpace(point?.Name) || point.Start?.Variables is null || !LuaLimits.ValidVariables(point.Start.Variables))
                    throw new InvalidDataException("Некорректные параметры точки.");
                points.Add(new(fileName, point, null));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                points.Add(new(fileName, null, error.Message));
            }
        }
        return points.OrderBy(p => p.Point is null).ThenBy(p => p.ToString(), StringComparer.Ordinal).ToList();
    }

    internal void Delete(StoredDebugPoint entry)
    {
        if (Path.GetFileName(entry.FileName) != entry.FileName || !entry.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Недопустимое имя файла точки.");
        File.Delete(Path.Combine(directory, entry.FileName));
    }

    internal string Save(DebugPoint point)
    {
        Directory.CreateDirectory(directory);
        var path = PointPath(point.Name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(point));
            File.Move(temporary, path, true);
            return Path.GetFileName(path);
        }
        finally { File.Delete(temporary); }
    }
}
