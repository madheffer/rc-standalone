namespace Source2.Compiler.Io;

/// <summary>
/// tier0's string helpers as the compile calls them (0923 build). Their case
/// folding is ASCII only (A-Z to a-z), unlike .NET's OrdinalIgnoreCase.
/// </summary>
public static class Tier0Strings
{
    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 0x20) : c;

    /// <summary>V_stristr_fast: the first index of <paramref name="find"/>, A-Z folded, or -1.</summary>
    public static int StriStr(string text, string find)
    {
        if (find.Length == 0)
            return text.Length == 0 ? -1 : 0;
        for (var i = 0; i + find.Length <= text.Length; i++)
        {
            var k = 0;
            while (k < find.Length && Fold(text[i + k]) == Fold(find[k]))
                k++;
            if (k == find.Length)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// CUtlString::Remove(text, caseSensitive: false): every match of
    /// <paramref name="find"/> taken out, left to right without overlap, A-Z folded.
    /// </summary>
    public static string RemoveIgnoreCase(string text, string find)
    {
        var result = new System.Text.StringBuilder();
        var at = 0;
        while (true)
        {
            var hit = StriStr(text[at..], find);
            if (hit < 0)
            {
                result.Append(text, at, text.Length - at);
                return result.ToString();
            }
            result.Append(text, at, hit);
            at += hit + find.Length;
        }
    }

    /// <summary>
    /// V_SplitString: pieces between matches of <paramref name="separator"/>
    /// (A-Z folded), not trimmed. An empty piece between separators is kept
    /// only with <paramref name="includeEmpty"/>; an empty tail never is.
    /// </summary>
    public static List<string> SplitString(string text, string separator, bool includeEmpty)
    {
        var pieces = new List<string>();
        var at = 0;
        while (at < text.Length)
        {
            var hit = separator.Length == 0 ? -1 : StriStr(text[at..], separator);
            if (hit < 0)
                break;
            if (hit > 0 || includeEmpty)
                pieces.Add(text.Substring(at, hit));
            at += hit + separator.Length;
        }
        if (at < text.Length)
            pieces.Add(text[at..]);
        return pieces;
    }

    /// <summary>
    /// V_stricmp_fast: A-Z folded to a-z, then the bytes compared as signed
    /// chars (UTF-8), so '_' (0x5f) sorts before 'a' and bytes of 0x80 and up
    /// before ASCII.
    /// </summary>
    public static int StricmpFast(string a, string b)
    {
        var x = System.Text.Encoding.UTF8.GetBytes(a);
        var y = System.Text.Encoding.UTF8.GetBytes(b);
        for (var i = 0; ; i++)
        {
            int p = i < x.Length ? (sbyte)FoldByte(x[i]) : 0, q = i < y.Length ? (sbyte)FoldByte(y[i]) : 0;
            if (p != q)
                return p - q;
            if (p == 0)
                return 0;
        }
    }

    private static byte FoldByte(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 0x20) : c;

    /// <summary>
    /// A collision tag list as the physics part reads it (rc 1802e3e00,
    /// 1802e3c10, 1802e4340): split on whitespace, ',' and '|', each tag
    /// inserted into a vector sorted by <see cref="StricmpFast"/>, a tag equal
    /// to one already there (A-Z folded) dropped, so the first spelling stays.
    /// </summary>
    public static List<string> ParseTags(string list)
    {
        var tags = new List<string>();
        foreach (var tag in list.Split([' ', '\t', '\n', '\v', '\f', '\r', ',', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var at = tags.BinarySearch(tag, Comparer<string>.Create(StricmpFast));
            if (at < 0)
                tags.Insert(~at, tag);
        }
        return tags;
    }

    /// <summary>A string with A-Z folded to a-z and nothing else.</summary>
    public static string FoldAscii(string text)
        => string.Create(text.Length, text, (span, s) => { for (var i = 0; i < s.Length; i++) span[i] = Fold(s[i]); });
}
