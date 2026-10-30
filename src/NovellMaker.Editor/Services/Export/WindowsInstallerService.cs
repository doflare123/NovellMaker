using System.Diagnostics;
using System.Text;
using NovellMaker.Core.Projects;

namespace NovellMaker.Editor.Services.Export;

public sealed record WindowsInstallerOptions(string Version = "1.0.0", string? IconPath = null)
{
    public string ValidateVersion()
    {
        if (!System.Version.TryParse(Version, out var value) || value.Build < 0 ||
            new[] { value.Major, value.Minor, value.Build, Math.Max(0, value.Revision) }.Any(n => n > 65535))
            throw new ArgumentException("Версия установщика: три или четыре числа от 0 до 65535, например 1.0.0.");
        return $"{value.Major}.{value.Minor}.{value.Build}.{Math.Max(0, value.Revision)}";
    }

    public void ValidateProjectVersion(NovelProject project)
    {
        var version = ValidateVersion();
        if (project.ReleaseCheck?.ToConfiguration() is { } release && version != release.CurrentVersion.ToString())
            throw new ArgumentException("Версия установщика должна совпадать с версией игры в параметрах проверки выпуска.");
    }
}

/// <summary>Compiles a Windows installer from the same validated game snapshot as folder export.</summary>
public sealed class WindowsInstallerService(WindowsExportService exporter, string compilerDirectory)
{
    public static string DefaultCompilerDirectory => Path.Combine(AppContext.BaseDirectory, "BuildTools", "NSIS");

    public async Task ExportAsync(NovelProject project, string assetsRoot, Guid startSceneId, string destination,
        WindowsInstallerOptions options, CancellationToken cancellationToken = default)
    {
        DialogueDisplayValidation.Validate(project);
        options.ValidateProjectVersion(project);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Сборка установщика EXE пока поддержана только на Windows.");
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Выберите файл установщика с расширением .exe.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Файл сборки уже существует. Выберите новое имя установщика.");
        var compiler = Path.Combine(Path.GetFullPath(compilerDirectory), "makensis.exe");
        if (!File.Exists(compiler)) throw new FileNotFoundException("В поставке редактора отсутствует NSIS. Подготовьте её скриптом scripts/prepare-windows-installer.ps1.", compiler);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".novell-installer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = Path.Combine(staging, "payload");
            await exporter.ExportAsync(project, assetsRoot, startSceneId, payload);
            if (options.IconPath is { } icon)
            {
                if (new FileInfo(icon).Length > 1048576) throw new ArgumentException("ICO-файл должен быть не больше 1 МиБ.");
                var bytes = await File.ReadAllBytesAsync(icon, cancellationToken);
                if (bytes.Length is < 22 or > 1048576 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0 || BitConverter.ToUInt16(bytes, 4) == 0)
                    throw new ArgumentException("Выберите настоящий ICO-файл размером до 1 МиБ.");
                await File.WriteAllBytesAsync(Path.Combine(payload, "game.ico"), bytes, cancellationToken);
            }
            var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(payload, p)).Order(StringComparer.Ordinal).ToArray();
            if (options.IconPath is not null) File.Copy(Path.Combine(payload, "game.ico"), Path.Combine(staging, "installer.ico"));
            // Compiler source names never contain project data or preprocessor syntax.
            Directory.CreateDirectory(Path.Combine(staging, "files"));
            for (var index = 0; index < files.Length; index++)
                File.Move(Path.Combine(payload, files[index]), Path.Combine(staging, "files", $"{index:D5}.dat"));
            await File.WriteAllTextAsync(Path.Combine(staging, "installer.nsi"),
                WindowsInstallerScript.Create(project.Id, project.Name, options, files), new UTF8Encoding(true), cancellationToken);
            var start = new ProcessStartInfo(compiler)
            {
                WorkingDirectory = staging, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.Environment["NSISDIR"] = Path.GetDirectoryName(compiler);
            start.Environment.Remove("NSISCONFDIR");
            foreach (var argument in new[] { "/V2", "/WX", "/NOCONFIG", "/INPUTCHARSET", "UTF8", "/OUTPUTCHARSET", "UTF8", "installer.nsi" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить сборку установщика.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new IOException("Сборка установщика отменена или превысила 3 минуты.");
            }
            var output = await stdout + await stderr;
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(staging, "novell-package.exe")))
                throw new IOException($"Не удалось собрать установщик (NSIS {process.ExitCode}):\n{output[..Math.Min(output.Length, 8192)]}");
            File.Move(Path.Combine(staging, "novell-package.exe"), destination, overwrite: false);
        }
        finally
        {
            if (Directory.Exists(staging) && Path.GetDirectoryName(Path.GetFullPath(staging)) == parent && Path.GetFileName(staging).StartsWith(".novell-installer-", StringComparison.Ordinal))
                Directory.Delete(staging, recursive: true);
        }
    }
}
