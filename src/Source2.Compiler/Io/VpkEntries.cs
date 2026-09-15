using ValvePak;

namespace Source2.Compiler.Io;

/// <summary>
/// The one accessor the compiler needs against an open VPK: its entry table,
/// keyed by extension (no leading dot, as ValvePak stores it).
///
/// <para>It exists to turn a silent null into a diagnosable throw.
/// <see cref="Package.Entries"/> is null until <c>Read()</c> has succeeded, and
/// a null-propagating caller would just find no template and report "this VPK
/// has no materials" for what is really "this VPK was never opened".</para>
/// </summary>
public static class VpkEntries
{
    /// <summary>Extension (no dot) to the entries carrying it.</summary>
    public static IReadOnlyDictionary<string, List<PackageEntry>> ByExtension(Package pkg)
        => pkg.Entries
           ?? throw new InvalidOperationException(
               "VPK has no entry table. Package.Read() must succeed before the entries are read.");

    /// <summary>Read one entry's raw bytes.</summary>
    public static byte[] Read(Package pkg, PackageEntry entry)
    {
        pkg.ReadEntry(entry, out var data);
        return data;
    }

    /// <summary>
    /// The first entry whose full path ends with <paramref name="suffix"/>
    /// (case-insensitive), in a deterministic order, or null when the VPK holds
    /// none. Used to lift a structural template of a given type out of a game
    /// archive.
    /// </summary>
    public static PackageEntry? FirstEndingWith(Package pkg, string suffix)
        => ByExtension(pkg).Values
            .SelectMany(list => list)
            .Select(e => (Entry: e, Path: e.GetFullPath().Replace(Sep, '/')))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .Where(x => x.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Entry)
            .FirstOrDefault();

    // (char)92 is the backslash. ValvePak joins directory + filename with the
    // platform separator, so a full path can carry either slash; normalise to '/'.
    private const char Sep = (char)92;
}
