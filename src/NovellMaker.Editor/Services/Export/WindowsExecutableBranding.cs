using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NovellMaker.Editor.Services.Export;

/// <summary>Edits only the staged apphost's resources, after template integrity checks. No compiler or SDK.</summary>
internal static class WindowsExecutableBranding
{
    internal static byte[]? ReadIcon(string? path)
    {
        if (path is null) return null;
        if (new FileInfo(path).Length > 1048576) throw InvalidIcon();
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 22 or > 1048576 || BitConverter.ToUInt16(bytes, 0) != 0 || BitConverter.ToUInt16(bytes, 2) != 1)
            throw InvalidIcon();
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count is 0 or > 256 || 6 + count * 16 > bytes.Length) throw InvalidIcon();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var length = BitConverter.ToUInt32(bytes, entry + 8);
            var offset = BitConverter.ToUInt32(bytes, entry + 12);
            if (bytes[entry + 3] != 0 || length < 8 || offset < 6 + count * 16 || (ulong)offset + length > (ulong)bytes.Length)
                throw InvalidIcon();
            var data = bytes.AsSpan((int)offset, (int)length);
            // ICO frames are PNG or DIB. Reject truncated/unknown frame headers before touching an EXE.
            if (!data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                (length < 40 || BitConverter.ToUInt32(data) is not (40 or 108 or 124))) throw InvalidIcon();
        }
        return bytes;
    }

    private static ArgumentException InvalidIcon() => new("Выберите корректный ICO-файл размером до 1 МиБ.");

    internal static void Apply(string executable, string title, WindowsGameOptions options, byte[]? icon)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Оформление игрового EXE требует Windows.");
        if (title.Length > 240 || title.Any(char.IsControl)) throw new ArgumentException("Некорректное название игры для EXE.");
        var version = BuildVersion(title, options.ValidateVersion());
        var old = Resources(executable, icon is null ? [16] : [16, 14, 3]);
        var update = BeginUpdateResource(executable, false);
        if (update == 0) throw Failure();
        try
        {
            foreach (var resource in old)
                WithName(resource.Name, name => Write(update, resource.Type, name, resource.Language, null));
            Write(update, 16, 1, 0, version);
            if (icon is not null)
            {
                var count = BitConverter.ToUInt16(icon, 4);
                using var group = new MemoryStream(); using var writer = new BinaryWriter(group);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write(count);
                for (var i = 0; i < count; i++)
                {
                    var entry = 6 + i * 16;
                    var length = (int)BitConverter.ToUInt32(icon, entry + 8);
                    var offset = (int)BitConverter.ToUInt32(icon, entry + 12);
                    writer.Write(icon, entry, 12); writer.Write((ushort)(i + 1));
                    Write(update, 3, i + 1, 0, icon.AsSpan(offset, length).ToArray());
                }
                Write(update, 14, 1, 0, group.ToArray());
            }
            var completed = update; update = 0;
            if (!EndUpdateResource(completed, false)) throw Failure();
        }
        finally { if (update != 0) EndUpdateResource(update, true); }
    }

    // VERSIONINFO blocks have WORD lengths and DWORD-aligned values/children. Text lengths are UTF-16 units.
    internal static byte[] BuildVersion(string title, string version)
    {
        var numbers = System.Version.Parse(version);
        using var fixedInfo = new MemoryStream(); using var writer = new BinaryWriter(fixedInfo);
        writer.Write(0xFEEF04BDu); writer.Write(0x00010000u);
        var high = ((uint)numbers.Major << 16) | (uint)numbers.Minor;
        var low = ((uint)numbers.Build << 16) | (uint)numbers.Revision;
        writer.Write(high); writer.Write(low); writer.Write(high); writer.Write(low);
        writer.Write(0x3fu); writer.Write(0u); writer.Write(0x40004u); writer.Write(1u);
        writer.Write(0u); writer.Write(0u); writer.Write(0u);
        byte[] Text(string key, string value) => Block(key, Encoding.Unicode.GetBytes(value + '\0'), true);
        return Block("VS_VERSION_INFO", fixedInfo.ToArray(), false,
            Block("StringFileInfo", [], true, Block("000004b0", [], true,
                Text("ProductName", title), Text("FileDescription", title),
                Text("FileVersion", version), Text("ProductVersion", version),
                Text("OriginalFilename", "NovellMaker.Runtime.exe"))),
            Block("VarFileInfo", [], true, Block("Translation", [0, 0, 0xb0, 4], false)));
    }

    private static byte[] Block(string key, byte[] value, bool text, params byte[][] children)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        writer.Write((ushort)0); writer.Write(checked((ushort)(text ? value.Length / 2 : value.Length))); writer.Write((ushort)(text ? 1 : 0));
        writer.Write(Encoding.Unicode.GetBytes(key + '\0'));
        void Align() { while (buffer.Position % 4 != 0) writer.Write((byte)0); }
        Align(); writer.Write(value);
        foreach (var child in children) { Align(); writer.Write(child); }
        var length = checked((ushort)buffer.Length); buffer.Position = 0; writer.Write(length);
        return buffer.ToArray();
    }

    private sealed record Resource(int Type, object Name, ushort Language);

    private static List<Resource> Resources(string executable, int[] types)
    {
        var module = LoadLibraryEx(executable, 0, 0x22); // Data file + image resource; never execute the host.
        if (module == 0) throw Failure();
        try
        {
            var resources = new List<Resource>();
            foreach (var type in types)
            {
                var names = new List<object>();
                EnumName callback = (_, _, name, _) => { names.Add((nuint)name <= ushort.MaxValue ? (object)(int)name : Marshal.PtrToStringUni(name)!); return true; };
                if (!EnumResourceNames(module, type, callback, 0) && Marshal.GetLastWin32Error() is not (1812 or 1813)) throw Failure();
                foreach (var name in names)
                    WithName(name, pointer =>
                    {
                        EnumLanguage language = (_, _, _, lang, _) => { resources.Add(new(type, name, lang)); return true; };
                        if (!EnumResourceLanguages(module, type, pointer, language, 0)) throw Failure();
                    });
            }
            return resources;
        }
        finally { FreeLibrary(module); }
    }

    private static void WithName(object name, Action<nint> action)
    {
        if (name is int id) { action(id); return; }
        var pointer = Marshal.StringToHGlobalUni((string)name);
        try { action(pointer); } finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void Write(nint update, int type, nint name, ushort language, byte[]? data)
    {
        if (!UpdateResource(update, type, name, language, data, (uint)(data?.Length ?? 0))) throw Failure();
    }

    private static IOException Failure() => new("Не удалось записать оформление игрового EXE.", new Win32Exception(Marshal.GetLastWin32Error()));
    private delegate bool EnumName(nint module, nint type, nint name, nint parameter);
    private delegate bool EnumLanguage(nint module, nint type, nint name, ushort language, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string file, nint reserved, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
    private static extern bool EnumResourceNames(nint module, nint type, EnumName callback, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceLanguagesW", SetLastError = true)]
    private static extern bool EnumResourceLanguages(nint module, nint type, nint name, EnumLanguage callback, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint BeginUpdateResource(string file, bool deleteExisting);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    private static extern bool UpdateResource(nint update, nint type, nint name, ushort language, byte[]? data, uint length);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    private static extern bool EndUpdateResource(nint update, bool discard);
}
