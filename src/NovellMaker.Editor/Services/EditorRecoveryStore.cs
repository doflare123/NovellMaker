using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.ExternalServices;

namespace NovellMaker.Editor.Services;

public enum RecoveryDecision { Later, Restore, Discard }
public enum ExternalProjectDecision { Keep, Reload, SaveAs }
public sealed record RecoveryOffer(string Name, string? ProjectPath, DateTimeOffset CreatedAt, bool FileChanged);

// This is an editor draft format, independent of both .novell and player saves.
internal sealed record EditorRecoverySnapshot(int Version, NovelProject Project, NovelProject? SavedProject,
    string DraftName, string? ProjectPath, string? FileRevision, DateTimeOffset CreatedAt,
    Guid? SceneId, Guid? LineId);

public sealed class EditorRecoveryStore(string? directory)
{
    public static string ProfileDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Recovery");
    internal bool Enabled => directory is not null;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private sealed record Envelope(int Version, string Payload, string Sha256);

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Constructors reject unfinished names; editor drafts deliberately permit them.
            // All persisted properties are then populated, including stable IDs.
            if (info.Type == typeof(NovelScene)) info.CreateObject = () => NovelScene.Create("Черновик");
            if (info.Type == typeof(ImageAsset)) info.CreateObject = () => new ImageAsset(Guid.NewGuid(), "Черновик", "Assets/Images/draft.png", 1, 1);
            if (info.Kind == JsonTypeInfoKind.Object)
                foreach (var property in info.Properties.Where(p => p.Set is not null))
                {
                    property.IsRequired = true;
                    property.ShouldSerialize = (_, _) => true;
                }
        });
        return new() { TypeInfoResolver = resolver, IgnoreReadOnlyProperties = true, AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new ReleaseDraftConverter() } };
    }

    // The project converter validates before writing. Recovery must also preserve unfinished form input.
    private sealed class ReleaseDraftConverter : JsonConverter<ReleaseCheckSettings>
    {
        private sealed record Draft(string Provider, string Owner, string Repository, string CurrentVersion);
        public override ReleaseCheckSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var draft = JsonSerializer.Deserialize<Draft>(ref reader, options) ?? throw new JsonException("Пустой черновик параметров выпуска.");
            return new(draft.Provider, draft.Owner, draft.Repository, draft.CurrentVersion);
        }
        public override void Write(Utf8JsonWriter writer, ReleaseCheckSettings value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new Draft(value.Provider, value.Owner, value.Repository, value.CurrentVersion), options);
    }

    internal IEnumerable<string> Candidates() => directory is not null && Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.recovery.json").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];

    internal Lease Create()
    {
        Directory.CreateDirectory(directory!);
        return Claim(Path.Combine(directory!, Guid.NewGuid().ToString("N") + ".recovery.json"))
            ?? throw new IOException("Не удалось создать файл блокировки восстановительного снимка.");
    }

    internal Lease? Claim(string path)
    {
        try { return new(path, new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException) { return null; } // Another live editor owns this draft.
    }

    internal sealed class Lease(string path, FileStream ownership) : IDisposable
    {
        internal async Task<EditorRecoverySnapshot> ReadAsync()
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(path), Options)
                ?? throw new InvalidDataException("Пустой снимок восстановления.");
            if (envelope.Version != 1 || envelope.Sha256 != Hash(envelope.Payload))
                throw new InvalidDataException("Снимок восстановления повреждён или имеет неподдерживаемую версию.");
            var snapshot = JsonSerializer.Deserialize<EditorRecoverySnapshot>(envelope.Payload, Options)
                ?? throw new InvalidDataException("Пустой снимок восстановления.");
            if (snapshot.Version != 1 || snapshot.Project is null || snapshot.Project.FormatVersion != NovelProject.CurrentFormatVersion || snapshot.DraftName is null)
                throw new InvalidDataException("Неподдерживаемый снимок восстановления.");
            return snapshot;
        }

        internal Task WriteAsync(EditorRecoverySnapshot snapshot) => Task.Run(async () =>
        {
            var payload = JsonSerializer.Serialize(snapshot, Options);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, new Envelope(1, payload, Hash(payload)), Options);
                    await stream.FlushAsync();
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });

        internal void Delete() => File.Delete(path);
        public void Dispose()
        {
            // Keep the lock inode: unlinking it races with another process acquiring it on Unix.
            ownership.Dispose();
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
