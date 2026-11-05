using System.Diagnostics;

namespace NovellMaker.Editor.Tests;

internal static class NativeDelivery
{
    private static string? Editor => Environment.GetEnvironmentVariable("NOVELLMAKER_DELIVERY_EDITOR_DIR");

    internal static string Preview(string fallback) => Editor is { } editor
        ? Path.Combine(editor, "Runtime", "NovellMaker.Runtime.exe") : fallback;

    internal static string Template(string repository) => Editor is { } editor
        ? Path.Combine(editor, "ExportTemplates", "win-x64")
        : Path.Combine(repository, "artifacts", "export-templates", "win-x64");

    internal static string Evidence(string repository, string feature, string variant)
    {
        var root = Environment.GetEnvironmentVariable("NOVELLMAKER_DELIVERY_EVIDENCE_DIR")
            ?? Path.Combine(repository, "artifacts", "screenshots");
        return Path.Combine(root, feature, variant, Guid.NewGuid().ToString("N"));
    }

    internal static void Isolate(ProcessStartInfo start, string directory, bool packaged)
    {
        if (!packaged && Editor is null) return;
        start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(directory, "absent");
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
    }
}
