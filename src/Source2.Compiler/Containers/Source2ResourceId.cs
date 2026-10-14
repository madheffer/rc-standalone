using System.Text;

namespace Source2.Compiler;

/// <summary>
/// Computes the 64-bit resource identifier Source 2 stores in a compiled
/// resource's RERL block (<c>ResourceReferenceInfo.Id</c>) for every external
/// reference.
///
/// <para>Reverse-engineered 2026-05-15 by brute-forcing 25 known
/// (path, Id) pairs lifted from stock CS2 <c>vmat_c</c>/<c>vmdl_c</c> RERL
/// blocks. The function is <b>MurmurHash64B</b> over the lowercase resource
/// path - forward slashes, and the <em>uncompiled</em> extension as RERL
/// stores it (<c>.vtex</c> / <c>.vmat</c>, never <c>.vtex_c</c>) - with seed
/// <c>0xEDABCDEF</c>. All 25 pairs matched exactly.</para>
///
/// <para>This is what lets <see cref="ResourceBuilder"/> emit a working
/// <c>.vmat_c</c> that references brand-new texture paths without routing
/// through Valve's <c>resourcecompiler.exe</c> - the engine looks resources up
/// by this 64-bit id and FATAL-errors on a wrong/zero one.</para>
/// </summary>
public static class Source2ResourceId
{
    private const ulong Seed = 0xEDABCDEF;

    /// <summary>
    /// The RERL id for a resource path. The path is lowercased and slash-
    /// normalized; a trailing compiled <c>_c</c> suffix is stripped (RERL
    /// stores the uncompiled extension).
    /// </summary>
    public static ulong ForPath(string resourcePath)
    {
        var p = resourcePath.Replace('\\', '/').ToLowerInvariant();
        if (p.EndsWith("_c", StringComparison.Ordinal))
            p = p[..^2];
        return MurmurHash2.Hash64B(Encoding.UTF8.GetBytes(p), Seed);
    }
}
