using ValvePak;

namespace Source2.Compiler.Tests;

/// <summary>
/// Ground-truth map resources, lifted at test time from whatever CS2 workshop
/// maps the tester has subscribed to.
///
/// <para>A published map ships as a VPK inside the item's VPK, so a map resource
/// is two archives deep. Nothing here is committed: workshop maps are other
/// people's work, and the survey in <c>docs/MAP_RESOURCES.md</c> is reproducible
/// from any install that has some. Without maps the map tests skip and say so,
/// the same bargain <see cref="CS2Fixtures"/> makes for game content.</para>
/// </summary>
internal static class MapFixtures
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The workshop content directory, or null when there is none.</summary>
    public static string? WorkshopDir()
    {
        if (Environment.GetEnvironmentVariable("CS2_WORKSHOP_DIR") is { Length: > 0 } dir)
            return Directory.Exists(dir) ? dir : null;

        var guesses = new[]
        {
            @"D:\Steam\steamapps\workshop\content\730",
            @"C:\Program Files (x86)\Steam\steamapps\workshop\content\730",
        };
        return guesses.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// The first resource whose path ends with <paramref name="suffix"/> (for
    /// example <c>"/world.vrman_c"</c>), searched in a deterministic order so a
    /// rerun reads the same bytes. Null when no subscribed map has one.
    /// </summary>
    public static byte[]? Resource(string suffix)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(suffix, out var cached))
                cached = Cache[suffix] = Find(suffix);

            // A gate that self-skips is not a gate. xUnit 2.x cannot skip
            // dynamically, so a test that returns early still reports green -
            // and a fixture lookup broken for any reason other than "no maps
            // installed" would vanish into a passing run.
            if (cached is null && WorkshopDir() is not null)
                throw new InvalidOperationException(
                    $"Workshop maps are installed but none provides {suffix}, so this test "
                  + "cannot verify anything. Subscribe to a map, or unset CS2_WORKSHOP_DIR.");
            return cached;
        }
    }

    private static byte[]? Find(string suffix)
    {
        var root = WorkshopDir();
        if (root is null)
            return null;

        foreach (var item in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var outerPath = Path.Combine(item, Path.GetFileName(item) + ".vpk");
            if (!File.Exists(outerPath))
                continue;

            using var outer = new Package();
            try { outer.Read(outerPath); }
            catch { continue; }

            foreach (var mapEntry in MapArchives(outer))
            {
                byte[] mapBytes;
                try { mapBytes = Io.VpkEntries.Read(outer, mapEntry); }
                catch { continue; }

                using var map = new Package();
                // ValvePak resolves sibling archives by file name, so reading
                // from a stream needs one even though a map VPK is self-contained.
                map.SetFileName(Path.GetFileName(mapEntry.GetFullPath()));
                try { map.Read(new MemoryStream(mapBytes)); }
                catch { continue; }

                var entry = Io.VpkEntries.FirstEndingWith(map, suffix);
                if (entry is null)
                    continue;
                try { return Io.VpkEntries.Read(map, entry); }
                catch { /* try the next map */ }
            }
        }
        return null;
    }

    private static IEnumerable<PackageEntry> MapArchives(Package outer)
        => Io.VpkEntries.ByExtension(outer).TryGetValue("vpk", out var vpks)
            ? vpks.OrderBy(e => e.GetFullPath(), StringComparer.Ordinal)
            : [];

    /// <summary>
    /// Valve's own <c>.vmap</c> SOURCES, which ship uncompiled under the CS2
    /// content tree's addons. Ground truth for the DMX reader that needs no
    /// network and nothing checked in.
    ///
    /// <para>Smallest first, and capped, because this corpus is 3.2 GB with single
    /// files near 800 MB: a test that read them all would measure the disk. The
    /// small ones are the same format.</para>
    /// </summary>
    /// <param name="maxBytes">Skip anything larger.</param>
    /// <param name="limit">At most this many files.</param>
    public static IReadOnlyList<string> VmapSources(long maxBytes = 32L * 1024 * 1024, int limit = 8)
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null)
            return [];
        // <cs2>/game/csgo/pak01_dir.vpk -> <cs2>/content/csgo_addons
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, "..", "..", "content", "csgo_addons"));
        if (!Directory.Exists(root))
            return [];
        return [.. Directory.EnumerateFiles(root, "*.vmap", SearchOption.AllDirectories)
                            .Select(p => (Path: p, Length: new FileInfo(p).Length))
                            .Where(f => f.Length <= maxBytes)
                            .OrderBy(f => f.Length)
                            .ThenBy(f => f.Path, StringComparer.Ordinal)
                            .Take(limit)
                            .Select(f => f.Path)];
    }

    /// <summary>Report a skipped map test and why. Always returns true.</summary>
    public static bool Skip(string needed)
    {
        Console.WriteLine($"[SKIP] no subscribed CS2 workshop map provides {needed}. "
                        + "Subscribe to one, or set CS2_WORKSHOP_DIR, to run this test.");
        return true;
    }
}
