namespace Source2.Compiler;

/// <summary>
/// A resource reference as the entity lump writer fixes it up (FUN_181c1ee80).
///
/// <para>With an extension, the value takes that extension (replacing any it had,
/// so a sprite's ".vmt" becomes ".vmat" and a bare particle name gains ".vpcf"),
/// then FixupResourceName (FUN_181c1e120) refuses an absolute or rooted path
/// outright and otherwise normalises it: dot segments resolved, lower case,
/// forward slashes. Without one, FUN_181c1dca0 does the same without touching
/// the extension. An empty value stays empty.</para>
/// </summary>
internal static class ResourcePath
{
    public static string Fixup(string text, string extension)
    {
        if (text.Length == 0)
            return "";
        var path = extension.Length > 0 ? SetExtension(text, extension) : text;
        if (IsAbsolute(path) || path[0] == '/')
            return "";
        return Normalise(path).ToLowerInvariant();
    }

    /// <summary>CBufferString::SetExtension: drop the file name's extension, if
    /// it has one, and append this one.</summary>
    private static string SetExtension(string path, string extension)
    {
        var name = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
        var dot = path.LastIndexOf('.');
        return (dot >= name ? path[..dot] : path) + "." + extension;
    }

    private static bool IsAbsolute(string path)
        => path.StartsWith('\\') || (path.Length > 1 && path[1] == ':');

    /// <summary>Forward slashes, no empty or "." segments, ".." folded into its
    /// parent.</summary>
    private static string Normalise(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
                parts.RemoveAt(parts.Count - 1);
            else
                parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
