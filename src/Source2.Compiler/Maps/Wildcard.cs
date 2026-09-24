namespace Source2.Compiler;

/// <summary>
/// tier0's V_CompareNameWithWildcards as the template pass calls it: the
/// wildcards are in the FIRST argument, which is the entity's own targetname,
/// and the second is the literal the template names.
///
/// <para>'*' matches any run, with a single backtrack point as the binary keeps
/// it; '?' matches one character; the rest compares without regard to ASCII
/// case. Trailing '*' in the name still matches an exhausted literal.</para>
/// </summary>
internal static class Wildcard
{
    public static bool Matches(string name, string literal)
    {
        int n = 0, l = 0, starName = -1, starLiteral = -1;
        while (l < literal.Length)
        {
            var c = n < name.Length ? name[n] : '\0';
            if (c == '*')
            {
                while (n < name.Length && name[n] == '*')
                    n++;
                if (n >= name.Length)
                    return true;
                starName = n;
                starLiteral = l;
                continue;
            }
            if (c == '?' || Lower(c) == Lower(literal[l]))
            {
                n++;
                l++;
                continue;
            }
            if (starName < 0)
                return false;
            l = ++starLiteral;
            n = starName;
        }
        while (n < name.Length)
            if (name[n++] != '*')
                return false;
        return true;
    }

    private static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
