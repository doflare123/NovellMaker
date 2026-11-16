using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Runtime.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeWindowTitleTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public void PreviewAndPackagedGamePreserveNativeUnicodeTitle()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_NATIVE_SMOKE") != "1")
            Assert.Inconclusive("Set NOVELLMAKER_NATIVE_SMOKE=1.");

        // MonoGame binds graphics to the first thread; run both variants synchronously on it.
        foreach (var packaged in new[] { false, true }) VerifyWindowTitle(packaged);
    }

    private static void VerifyWindowTitle(bool packaged)
    {
        var directory = Path.Combine(TestPaths.TempDirectory, "window-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Текст")] };
            var project = NovelProject.Create("Новелла — Ёжик, café, 日本語") with { Scenes = [scene] };
            using var session = new GameSession(project, scene.Id, "ru", null,
                packaged ? PlaybackMode.Game : PlaybackMode.AuthorPreview);
            var store = new PlayerProfileStore(project.Id, Path.Combine(directory, "profile.json"));
            var settings = new PlayerSettingsSession(store, store.ReadAsync(PlayerSettings.Defaults(project.Audio)).GetAwaiter().GetResult());
            var arguments = new RuntimeArguments("", directory, scene.Id, null, "ru", null, null)
            {
                Package = packaged ? new(GamePackage.CurrentVersion, project.Id, scene.Id, "ru", project.Name) : null
            };
            using var game = new RuntimeGame(arguments, session, false, settings);
            try
            {
                // RunOneFrame creates the final SDL/OpenGL window, replacing the constructor's window.
                game.RunOneFrame();
                var libraryName = OperatingSystem.IsWindows() ? "SDL2.dll"
                    : OperatingSystem.IsMacOS() ? "libSDL2-2.0.0.dylib" : "libSDL2-2.0.so.0";
                var library = NativeLibrary.Load(libraryName, typeof(Game).Assembly, null);
                try
                {
                    var getTitle = Marshal.GetDelegateForFunctionPointer<GetWindowTitle>(NativeLibrary.GetExport(library, "SDL_GetWindowTitle"));
                    var expected = packaged ? project.Name : "NovellMaker — предпросмотр сцены";
                    Assert.AreEqual(expected, game.Window.Title);
                    Assert.AreEqual(expected, Marshal.PtrToStringUTF8(getTitle(game.Window.Handle)),
                        "The actual native caption must match the managed title after window creation.");
                }
                finally { NativeLibrary.Free(library); }
            }
            finally
            {
                game.DisposeSlotsAsync().AsTask().GetAwaiter().GetResult();
                game.DisposeScreenLuaAsync().AsTask().GetAwaiter().GetResult();
                game.FlushLuaJournalAsync().GetAwaiter().GetResult();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetWindowTitle(nint window);
}
