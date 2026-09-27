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
}
