namespace Source2.Compiler.Io;

/// <summary>
/// tier0's path string helpers, ported from the 0923 build (tier0.dll exports
/// of the same names) and checked against those exports by the tests. Strings
/// are byte arrays with a terminating 0, edited in place as the originals do.
/// </summary>
public static class Tier0Paths
{
    private static bool Sep(byte c) => c == (byte)'\\' || c == (byte)'/';

    private static int Length(byte[] s)
    {
        var n = 0;
        while (s[n] != 0)
            n++;
        return n;
    }

    // memmove(s + dst, s + src, count).
    private static void Move(byte[] s, int dst, int src, int count) => Array.Copy(s, src, s, dst, count);

    /// <summary>V_IsAbsolutePath: a drive letter and colon, two leading separators, or "vpk:" prefixes not followed by "ugc:".</summary>
    public static bool IsAbsolutePath(byte[] s)
    {
        var c = s[0];
        if (c == 0)
            return false;
        if (((byte)(c - 'a') < 26 || (byte)(c - 'A') < 26) && s[1] == ':')
            return true;
        if (Sep(c) && Sep(s[1]))
            return true;
        var p = 0;
        while (HasPrefix(s, p, "vpk:"))
            p += 4;
        if (HasPrefix(s, p, "ugc:"))
            return false;
        return p != 0;
    }

    // The case-insensitive prefix test V_IsAbsolutePath inlines (A-Z folded).
    private static bool HasPrefix(byte[] s, int at, string prefix)
    {
        for (var i = 0; i < prefix.Length; i++)
        {
            var a = s[at + i];
            var b = (byte)prefix[i];
            if ((byte)(a - 'A') < 26)
                a += 0x20;
            if ((byte)(b - 'A') < 26)
                b += 0x20;
            if (a != b)
                return false;
            if (a == 0)
                return false;
        }
        return true;
    }

    /// <summary>V_GetFileExtension: the index after the last '.' of the last path element, or -1.</summary>
    public static int GetFileExtension(byte[] s)
    {
        var len = Length(s);
        if (len <= 1)
            return -1;
        var p = len - 1;
        while (true)
        {
            if (p == 0)
                return -1;
            if (s[p - 1] == (byte)'.')
                return Sep(s[p]) ? -1 : p;
            if (Sep(s[p]))
                return -1;
            p--;
        }
    }

    /// <summary>
    /// CBufferString::SetExtension(ext, false): the last element's text from its
    /// last '.' dropped, then '.' (unless the extension starts with one) and the
    /// extension appended.
    /// </summary>
    public static byte[] SetExtension(byte[] s, string ext)
    {
        var len = Length(s);
        for (var i = len - 1; i >= 0; i--)
        {
            if (Sep(s[i]))
                break;
            if (s[i] == (byte)'.')
            {
                len = i;
                break;
            }
        }
        var tail = (ext.Length > 0 && ext[0] == '.' ? "" : ".") + ext;
        var result = new byte[len + tail.Length + 1];
        Array.Copy(s, result, len);
        for (var i = 0; i < tail.Length; i++)
            result[len + i] = (byte)tail[i];
        return result;
    }

    /// <summary>CBufferString::ToLowerFast / V_strlower_fast: A-Z only.</summary>
    public static void ToLowerFast(byte[] s)
    {
        for (var i = 0; s[i] != 0; i++)
            if ((byte)(s[i] - 'A') < 26)
                s[i] += 0x20;
    }

    /// <summary>CBufferString::FixSlashes / V_FixSlashes: every '/' and '\' becomes <paramref name="sep"/>.</summary>
    public static void FixSlashes(byte[] s, byte sep)
    {
        for (var i = 0; s[i] != 0; i++)
            if (Sep(s[i]))
                s[i] = sep;
    }

    /// <summary>
    /// CBufferString::FixupPathName: separators to '\', a doubled separator
    /// after the first character taken out once per position (the scan moves
    /// on after a removal), then <see cref="RemoveDotSlashes"/>; the separators
    /// are set to <paramref name="sep"/> again only when that fails and sep is not '\'.
    /// </summary>
    public static void FixupPathName(byte[] s, byte sep)
    {
        if (s[0] == 0)
            return;
        FixSlashes(s, (byte)'\\');
        var end = Length(s);
        var last = end - 1;
        for (var i = 1; i < last; i++)
        {
            if (Sep(s[i]) && Sep(s[i + 1]))
            {
                Move(s, i, i + 1, end - i);
                end--;
                last--;
            }
        }
        if (!RemoveDotSlashes(s, sep) && sep != (byte)'\\')
            FixSlashes(s, sep);
    }

    /// <summary>
    /// V_RemoveDotSlashes: runs of separators to one (two leading ones stay),
    /// "./" taken out at the start or after ':', '\' or '/', a trailing "\."
    /// dropped, and each "..\" folded into the element before it. A "$(...)",
    /// "${...}", "$name" or "%name%" element is a floor ".." does not climb
    /// past; ".." right after a drive colon fails (false). On success every
    /// separator becomes <paramref name="sep"/>.
    /// </summary>
    public static bool RemoveDotSlashes(byte[] s, byte sep)
    {
        // Runs of separators.
        var c = s[0];
        var w = 0;
        if (c != 0 && Sep(c))
        {
            w = 1;
            c = s[w];
        }
        var prevSep = false;
        if (c != 0)
        {
            var r = w;
            c = s[r];
            do
            {
                if (Sep(c))
                {
                    if (!prevSep)
                        s[w++] = c;
                    prevSep = true;
                }
                else
                {
                    s[w++] = c;
                    prevSep = false;
                }
                c = s[++r];
            }
            while (c != 0);
        }
        s[w] = 0;

        // "./" at the start or after ':', '\' or '/'.
        var rd = 0;
        w = 0;
        c = s[0];
        while (c != 0)
        {
            if (c == (byte)'.' && Sep(s[rd + 1]) && (rd == 0 || s[rd - 1] == (byte)':' || Sep(s[rd - 1])))
                rd += 2;
            else
                s[w++] = s[rd++];
            c = s[rd];
        }
        s[w] = 0;

        // A trailing "\.".
        var len = Length(s);
        if (len > 2 && s[len - 1] == (byte)'.' && Sep(s[len - 2]))
            s[len - 2] = 0;

        // "..": each folds into the element before it, down to the floor.
        var end = Length(s) + 1;
        var cur = 0;
        var floor = 0;
        c = s[0];
        while (c != 0)
        {
            int next;
            var newFloor = floor;
            if (c == (byte)'.' && s[cur + 1] == (byte)'.' && (s[cur + 2] == 0 || Sep(s[cur + 2])))
            {
                if (cur - 1 <= floor || !Anchor(s[cur - 1]))
                {
                    next = cur + 2;
                    newFloor = next;
                }
                else
                {
                    var src = cur + 2;
                    var dst = cur - 2;
                    if (dst >= 0 && (s[dst] == (byte)':' || s[cur - 1] == (byte)':'))
                        return false;
                    while (floor < dst && !Anchor(s[dst]))
                        dst--;
                    var at = s[dst];
                    if (at == (byte)':')
                    {
                        dst++;
                        if (s[src] != 0)
                            src = cur + 3;
                    }
                    else if (dst == 0 && Sep(s[src]) && at != (byte)'\\')
                    {
                        if (at != (byte)'/')
                            src = cur + 3;
                    }
                    Move(s, dst, src, end - src);
                    end += dst - src;
                    next = dst;
                }
            }
            else
            {
                next = cur + 1;
                if ((c == (byte)'$' || c == (byte)'%') && (cur == floor || Sep(s[cur - 1])))
                {
                    var p = cur + 1;
                    var skip = true;
                    if (c == (byte)'$' && (s[p] == (byte)'{' || s[p] == (byte)'('))
                    {
                        var open = s[p];
                        var close = open == (byte)'{' ? (byte)'}' : (byte)')';
                        var depth = 1;
                        p = cur + 2;
                        do
                        {
                            var ch = s[p];
                            if (ch == 0)
                            {
                                FixSlashes(s, sep);
                                return true;
                            }
                            if (ch == close)
                                depth--;
                            else if (ch == open)
                                depth++;
                            p++;
                        }
                        while (depth != 0);
                    }
                    while (!Sep(s[p]) && s[p] != 0)
                        p++;
                    if (c == (byte)'$')
                    {
                        if (s[cur + 1] == (byte)'{')
                            skip = s[p - 1] == (byte)'}';
                        else if (s[cur + 1] == (byte)'(')
                            skip = s[p - 1] == (byte)')';
                    }
                    else
                    {
                        skip = s[p - 1] == (byte)'%';
                    }
                    if (skip)
                    {
                        next = p;
                        newFloor = p;
                    }
                }
            }
            c = s[next];
            cur = next;
            floor = newFloor;
        }
        FixSlashes(s, sep);
        return true;
    }

    // '/', ':' or '\': the characters the ".." fold stops at (mask 0x200000000801 from '/').
    private static bool Anchor(byte c) => c == (byte)'/' || c == (byte)':' || c == (byte)'\\';
}
