using NovellMaker.Core.Scripting;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class LuaJournalFileTests
{
    [TestMethod]
    public async Task EmptyJournalPublishesAtomicallyAndUnavailableDirectoryDoesNotThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.Journal", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "profile", "lua-diagnostics.log"); var journal = new LuaDiagnosticJournal(); var writer = new LuaJournalFile(file);
            writer.Publish(journal); writer.Publish(journal); await writer.FlushAsync();
            StringAssert.Contains(await File.ReadAllTextAsync(file), "last 256; evicted=0"); Assert.IsNull(writer.Error);
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any());
            var bad = new LuaJournalFile(Path.Combine(file, "blocked.log")); bad.Publish(journal); await bad.FlushAsync();
            StringAssert.Contains(bad.Error!, "Не удалось записать журнал Lua");
            Assert.AreEqual(0L, journal.Sequence);
            Assert.AreEqual(Path.Combine(Path.GetDirectoryName(RuntimeStorage.ProfilePath(Guid.Empty, false))!, "lua-diagnostics.log"), RuntimeStorage.LuaJournalPath(Guid.Empty, false));
            StringAssert.Contains(RuntimeStorage.LuaJournalPath(Guid.Empty, true), "Games");
        }
        finally { Directory.Delete(directory, true); }
    }
}
