namespace Source2.Compiler.Io;

/// <summary>
/// Path-traversal safety for "combine a trusted root with an untrusted
/// relative path." The combined-and-canonicalised result must sit strictly
/// inside the root — anything else (parent escapes, rooted
/// paths, drive-letter swaps on Windows) is rejected.
///
/// <para>Use this everywhere a file/dir name from an external source (VPK
/// entries, RERL references, archive entries, request bodies) is joined to
/// one of our managed directories. The codebase has the same pattern repeated
/// across <c>archive extraction</c>, <c>upload quarantine</c>,
/// <c>the compile API</c> and others — new call sites
/// should reach for this helper, and old ones should migrate when they're
/// next touched.</para>
/// </summary>
public static class SafePath
{
    /// <summary>Combine <paramref name="root"/> with <paramref name="rel"/>
    /// and verify the result stays inside <paramref name="root"/>. Returns the
    /// canonicalised absolute path on success, throws
    /// <see cref="InvalidOperationException"/> on traversal / rooted-path /
    /// empty-rel attempts.</summary>
    public static string JoinUnderRoot(string root, string rel)
    {
        if (TryJoinUnderRoot(root, rel, out var full)) return full;
        throw new InvalidOperationException(
            $"Unsafe path '{rel}' would escape root '{root}'.");
    }

    /// <summary>Non-throwing variant — returns <c>false</c> (and a null
    /// <paramref name="full"/>) on any unsafe input. Prefer this when the
    /// caller wants to skip + log rather than abort a batch.</summary>
    public static bool TryJoinUnderRoot(string root, string rel, out string full)
    {
        full = null!;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(rel)) return false;

        // Cheap pre-checks: reject rooted paths and obvious parent segments
        // before letting Path.GetFullPath normalise. The post-check below is
        // the authoritative guard — these are belt-and-braces against
        // pathological inputs that confuse one or the other.
        var normalisedRel = rel.Replace('\\', '/');
        if (Path.IsPathRooted(rel) || Path.IsPathRooted(normalisedRel)) return false;
        foreach (var seg in normalisedRel.Split('/'))
            if (seg == "..") return false;

        // Canonicalise both sides and require the combined path to live
        // strictly under root (the trailing separator stops "/srv/dataX"
        // matching root "/srv/data").
        var rootFull = EnsureTrailingSeparator(Path.GetFullPath(root));
        string combinedFull;
        try { combinedFull = Path.GetFullPath(Path.Combine(root, normalisedRel.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return false; }  // GetFullPath throws on invalid chars / too-long paths

        // OrdinalIgnoreCase: Windows file systems are case-insensitive; on
        // Linux the casing will already match so the comparison is exact.
        if (!combinedFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
        full = combinedFull;
        return true;
    }

    private static string EnsureTrailingSeparator(string p) =>
        p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
}
