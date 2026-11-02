using System.Reflection.PortableExecutable;

namespace NovellMaker.Editor.Tests;

internal static class ExecutableResources
{
    internal static byte[] Read(string path, int type, int id)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        var rva = pe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
        var data = pe.GetSectionData(rva).GetContent().ToArray();
        int Find(int directory, int? key)
        {
            var count = BitConverter.ToUInt16(data, directory + 12) + BitConverter.ToUInt16(data, directory + 14);
            for (var i = 0; i < count; i++)
            {
                var entry = directory + 16 + i * 8;
                if (key is null || BitConverter.ToUInt32(data, entry) == key)
                    return (int)(BitConverter.ToUInt32(data, entry + 4) & 0x7fffffff);
            }
            throw new AssertFailedException($"Missing resource {type}/{id} in {path}.");
        }
        var leaf = Find(Find(Find(0, type), id), null);
        return pe.GetSectionData(BitConverter.ToInt32(data, leaf)).GetContent(0, BitConverter.ToInt32(data, leaf + 4)).ToArray();
    }

    internal static void AssertIcon(string executable, byte[] ico)
    {
        var group = Read(executable, 14, 1);
        var count = BitConverter.ToUInt16(ico, 4);
        Assert.AreEqual(count, BitConverter.ToUInt16(group, 4));
        for (var i = 0; i < count; i++)
        {
            CollectionAssert.AreEqual(ico.AsSpan(6 + i * 16, 12).ToArray(), group.AsSpan(6 + i * 14, 12).ToArray());
            var length = BitConverter.ToInt32(ico, 6 + i * 16 + 8);
            var offset = BitConverter.ToInt32(ico, 6 + i * 16 + 12);
            CollectionAssert.AreEqual(ico.AsSpan(offset, length).ToArray(), Read(executable, 3, BitConverter.ToUInt16(group, 6 + i * 14 + 12)));
        }
    }
}
