using System.Text;

namespace Source2.Compiler.Io;

/// <summary>
/// resourcecompiler's resource name rules (0923; checked against the loaded
/// DLL by the tests): FixupResourceName (181c1e120, reached through 181c1ea80)
/// and the lowercase MurmurHash2 its callers take of the result (1800c7c10).
/// </summary>
public static class ResourceNames
{
    /// <summary>
    /// FixupResourceName for a resource type's extension: an absolute path, one
    /// starting with '/', or one whose extension is another type's fails (null,
    /// with a warning in the compile). A name with no extension gets ".ext".
    /// The result goes through FixupPathName('\'), ToLowerFast and
    /// FixSlashes('/'). An empty name stays empty.
    /// </summary>
    public static byte[]? Fixup(string name, string extension)
    {
        var s = Terminated(name);
        if (s[0] == 0)
            return s;
        if (Tier0Paths.IsAbsolutePath(s) || s[0] == (byte)'/')
            return null;
        var ext = Tier0Paths.GetFileExtension(s);
        if (ext >= 0)
        {
            if (!EqualsIgnoreCase(s, ext, extension))
                return null;
        }
        else
        {
            s = Tier0Paths.SetExtension(s, extension);
        }
        Tier0Paths.FixupPathName(s, (byte)'\\');
        Tier0Paths.ToLowerFast(s);
        Tier0Paths.FixSlashes(s, (byte)'/');
        return s;
    }

    /// <summary>
    /// A shape's tool material hash (rc 180c25900): 0 for no material, a name
    /// FixupResourceName rejects or one it leaves empty; otherwise
    /// <see cref="Hash"/> of the fixed-up "vmat" name.
    /// </summary>
    public static uint ToolMaterialHash(string material)
    {
        if (material.Length == 0)
            return 0;
        var fixedUp = Fixup(material, "vmat");
        if (fixedUp == null || fixedUp[0] == 0)
            return 0;
        return Hash(fixedUp, Length(fixedUp), 0x31415926);
    }

    /// <summary>1800c7c10: MurmurHash2 of the first <paramref name="length"/> bytes after ToLowerFast.</summary>
    public static uint Hash(byte[] bytes, int length, uint seed)
    {
        var data = new byte[length + 1];
        Array.Copy(bytes, data, length);
        Tier0Paths.ToLowerFast(data);
        const uint M = 0x5bd1e995;
        var h = (uint)length ^ seed;
        var i = 0;
        for (; i + 4 <= length; i += 4)
        {
            var k = BitConverter.ToUInt32(data, i) * M;
            h = (h * M) ^ (((k >> 24) ^ k) * M);
        }
        switch (length - i)
        {
            case 3:
                h ^= (uint)data[i + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)data[i + 1] << 8;
                goto case 1;
            case 1:
                h = (data[i] ^ h) * M;
                break;
        }
        h = ((h >> 13) ^ h) * M;
        return (h >> 15) ^ h;
    }

    /// <summary>The name's bytes as the compile holds it (UTF-8), 0-terminated.</summary>
    public static byte[] Terminated(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var s = new byte[bytes.Length + 1];
        bytes.CopyTo(s, 0);
        return s;
    }

    public static string Text(byte[] s) => Encoding.UTF8.GetString(s, 0, Length(s));

    private static int Length(byte[] s)
    {
        var n = 0;
        while (s[n] != 0)
            n++;
        return n;
    }

    // V_stricmp_fast of the name's extension with the type's (A-Z folded).
    private static bool EqualsIgnoreCase(byte[] s, int at, string ext)
    {
        var i = 0;
        for (; s[at + i] != 0 && i < ext.Length; i++)
        {
            var a = s[at + i];
            var b = (byte)ext[i];
            if ((byte)(a - 'A') < 26)
                a += 0x20;
            if ((byte)(b - 'A') < 26)
                b += 0x20;
            if (a != b)
                return false;
        }
        return s[at + i] == 0 && i == ext.Length;
    }
}
