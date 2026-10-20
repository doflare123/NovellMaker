using System.Diagnostics;
using System.Text.Json;

namespace NovellMaker.Editor.Services;

public interface IExternalCodeEditor
{
    string? Executable { get; }
    bool OpenOnCreation { get => false; set { } }
    void Configure(string? executable);
    void Open(string file);
}

/// <summary>Local user preference, never supplied by a novel project. No shell command templates.</summary>
public sealed class ExternalCodeEditor : IExternalCodeEditor
{
    private readonly string settingsPath;
    public string? Executable { get; private set; }
    private bool openOnCreation;
    public bool OpenOnCreation
    {
        get => openOnCreation;
        set { SaveSettings(Executable, value); openOnCreation = value; }
    }
    public ExternalCodeEditor(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "code-editor.json");
        try { if (File.Exists(this.settingsPath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(this.settingsPath)) is { } settings) { Executable = settings.Executable; openOnCreation = settings.OpenOnCreation; } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public void Configure(string? executable)
    {
        if (executable is not null)
        {
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable) || OperatingSystem.IsWindows() && !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Выберите исполняемый файл редактора" + (OperatingSystem.IsWindows() ? " (.exe)." : "."));
        }
        SaveSettings(executable, openOnCreation);
        Executable = executable;
    }
    private void SaveSettings(string? executable, bool openAfterCreation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(executable, openAfterCreation))); File.Move(temporary, settingsPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static ProcessStartInfo StartInfo(string file, string? executable)
    {
        file = Path.GetFullPath(file);
        if (executable is null) return new(file) { UseShellExecute = true, Verb = "open" };
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(file);
        return start;
    }
    public void Open(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Файл скрипта не найден.", file);
        using var process = Process.Start(StartInfo(file, Executable));
    }
    private sealed record Settings(string? Executable, bool OpenOnCreation = false);
}
