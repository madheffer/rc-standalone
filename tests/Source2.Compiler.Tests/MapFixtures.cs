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

    /// <summary>
    /// Every installed map's resource ending with <paramref name="suffix"/>, as
    /// (map name, bytes), in a deterministic order. Where <see cref="Resource"/>
    /// takes one specimen, this is for gates that have to hold on the whole
    /// corpus - a byte-exact codec is only interesting if it survives all of it.
    ///
    /// <para>Maps published under more than one workshop id are yielded once.</para>
    /// </summary>
    public static IEnumerable<(string Map, byte[] Bytes)> AllResources(string suffix, int limit = int.MaxValue)
    {
        var root = WorkshopDir();
        if (root is null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                var name = Path.GetFileNameWithoutExtension(mapEntry.GetFullPath());
                if (!seen.Add(name))
                    continue;

                byte[]? bytes = null;
                try
                {
                    using var map = new Package();
                    map.SetFileName(Path.GetFileName(mapEntry.GetFullPath()));
                    map.Read(new MemoryStream(Io.VpkEntries.Read(outer, mapEntry)));
                    if (Io.VpkEntries.FirstEndingWith(map, suffix) is { } entry)
                        bytes = Io.VpkEntries.Read(map, entry);
                }
                catch { bytes = null; }

                if (bytes is null)
                    continue;
                yield return (name, bytes);
                if (--limit <= 0)
                    yield break;
            }
        }
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

    /// <summary>
    /// The game's entity schema: <c>csgo.fgd</c> plus everything it includes,
    /// which lives in <c>game/core</c> beside it. Null without a CS2 install.
    /// </summary>
    public static FgdSchema? GameSchema()
    {
        lock (Gate)
        {
            if (_schema is not null)
                return _schema;
            var cs2 = CS2Fixtures.StockPak();
            if (cs2 is null)
                return null;
            var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, ".."));
            var fgd = Path.Combine(game, "csgo", "csgo.fgd");
            if (!File.Exists(fgd))
                return null;
            return _schema = FgdSchema.Load(fgd, [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        }
    }

    private static FgdSchema? _schema;

    /// <summary>
    /// A map that exists locally as BOTH a source and a compile: the addon's
    /// <c>.vmap</c> under <c>content/csgo_addons</c>, and the entity lump out of the
    /// VPK <c>resourcecompiler.exe</c> produced from it under <c>game/csgo_addons</c>.
    /// </summary>
    public static string? VmapSource(string addon, string map)
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null)
            return null;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, "..", ".."));
        var maps = Path.Combine(root, "content", "csgo_addons", addon, "maps");
        var source = Path.Combine(maps, map + ".vmap");
        if (File.Exists(source))
            return source;

        // A prefab source does not sit beside the map: Hammer writes it under
        // maps/prefabs/<parent>/<name>.vmap, and a map that places prefabs keeps
        // its own there too. Looking only beside the map skipped two of the
        // corpus, and a skipped map cannot be told apart from a passing one.
        return !Directory.Exists(maps) ? null
             : Directory.EnumerateFiles(maps, map + ".vmap", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// Compile a map source with the game's own <c>resourcecompiler.exe</c> and hand
    /// back the entity lump it produced.
    ///
    /// <para>Ground truth has to be CURRENT, not whatever an addon's stale VPK
    /// holds: a compile from April writes an entity's origin as a fixed-decimal
    /// string, and one from August writes an array of doubles. Both load, so a test
    /// pinned to the old output would pin the compiler to a version of Valve's that
    /// no longer exists.</para>
    ///
    /// <para>The compile goes to a scratch addon inside the content tree, because
    /// that is where the compiler insists on writing, and it is cached: a map takes
    /// about 30 seconds. Null when the Workshop Tools are not installed.</para>
    /// </summary>
    public static byte[]? RcCompiledLump(string sourcePath, string lump = "default_ents")
    {
        lock (Gate)
        {
            if (RcCache.TryGetValue(sourcePath, out var cached))
                return cached;

            // Cache on disk as well as in memory: a map costs about 30 seconds of
            // Valve's compiler, and the answer only changes when the source or the
            // compiler does, so both go in the key.
            var disk = DiskCachePath(sourcePath);
            if (disk is not null && File.Exists(disk))
                return RcCache[sourcePath] = File.ReadAllBytes(disk);

            var bytes = CompileWithRc(sourcePath, lump);
            if (bytes is not null && disk is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                File.WriteAllBytes(disk, bytes);
            }
            return RcCache[sourcePath] = bytes;
        }
    }

    /// <summary>Where resourcecompiler leaves the map it built from this source.
    /// The compile itself runs in <see cref="RcCompiledLump"/>.</summary>
    private static string? RcOutputPath(string sourcePath)
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null)
            return null;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, "..", ".."));
        return Path.Combine(root, "game", "csgo_addons", "s2c_rc_probe", "maps",
                            Path.GetFileNameWithoutExtension(sourcePath) + ".vpk");
    }

    /// <summary>
    /// EVERY entity lump resourcecompiler produced for the map, by its path in the
    /// package. A map with point_templates compiles to more than default_ents, and
    /// comparing only that one makes the template members look invented.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]>? RcCompiledLumps(string sourcePath)
    {
        lock (Gate)
        {
            if (RcCompiledLump(sourcePath) is null)
                return null;

            var compiled = RcOutputPath(sourcePath);
            if (compiled is null || !File.Exists(compiled) || OlderThanCompiler(compiled))
                return null;

            using var pkg = new Package();
            pkg.Read(compiled);
            var lumps = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in pkg.Entries.GetValueOrDefault("vents_c") ?? [])
                lumps[entry.GetFullPath()] = Io.VpkEntries.Read(pkg, entry);
            return lumps;
        }
    }

    /// <summary>Whether a compiled package predates the installed resourcecompiler,
    /// which makes it a different compiler's answer. atixref's package from
    /// 2026-09-21 still drops empty keys that the 2026-09-23 compiler keeps.</summary>
    private static bool OlderThanCompiler(string compiled)
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null)
            return false;
        var rc = Path.Combine(Path.GetDirectoryName(cs2)!, "..", "bin", "win64", "resourcecompiler.exe");
        return File.Exists(rc) && File.GetLastWriteTimeUtc(compiled) < File.GetLastWriteTimeUtc(rc);
    }

    private static string? DiskCachePath(string sourcePath)
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null || !File.Exists(sourcePath))
            return null;
        var rc = Path.Combine(Path.GetDirectoryName(cs2)!, "..", "bin", "win64", "resourcecompiler.exe");
        var stamp = $"{new FileInfo(sourcePath).LastWriteTimeUtc.Ticks:x}-"
                  + $"{(File.Exists(rc) ? new FileInfo(rc).LastWriteTimeUtc.Ticks : 0):x}";
        return Path.Combine(Path.GetTempPath(), "s2c_rc_cache",
                            $"{Path.GetFileNameWithoutExtension(sourcePath)}.{stamp}.vents_c");
    }

    private static byte[]? CompileWithRc(string sourcePath, string lump)
    {
        var cs2 = CS2Fixtures.StockPak();
        if (cs2 is null || !File.Exists(sourcePath))
            return null;
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, "..", ".."));
        var rc = Path.Combine(root, "game", "bin", "win64", "resourcecompiler.exe");
        if (!File.Exists(rc))
            return null;

        // Never compile beside a running game (the project's standing rule) or
        // beside another compile, which shares the addon's output package.
        if (System.Diagnostics.Process.GetProcessesByName("cs2").Length > 0
            || System.Diagnostics.Process.GetProcessesByName("resourcecompiler").Length > 0)
            return null;

        var addon = "s2c_rc_probe";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var content = Path.Combine(root, "content", "csgo_addons", addon, "maps");
        var compiled = Path.Combine(root, "game", "csgo_addons", addon, "maps", name + ".vpk");
        Directory.CreateDirectory(content);
        var staged = Path.Combine(content, name + ".vmap");
        File.Copy(sourcePath, staged, overwrite: true);

        var started = DateTime.UtcNow;
        var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rc)
        {
            ArgumentList = { "-nop4", "-f", "-game", Path.Combine(root, "game", "csgo"), "-i", staged },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        // Drain both pipes. A map compile writes plenty, and a redirected stream
        // nobody reads fills its buffer and deadlocks the compiler - which looks
        // exactly like a slow map.
        var stdout = run.StandardOutput.ReadToEndAsync();
        var stderr = run.StandardError.ReadToEndAsync();
        run.WaitForExit(milliseconds: 10 * 60 * 1000);
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10));
        // A package the run did not write is an OLDER compile's, and caching it
        // under this compiler's stamp passed untitled_1's pre-2026-09-23 lump off
        // as current.
        if (!File.Exists(compiled) || File.GetLastWriteTimeUtc(compiled) < started)
            return null;

        using var pkg = new Package();
        pkg.Read(compiled);
        var entry = Io.VpkEntries.FirstEndingWith(pkg, $"/{lump}.vents_c");
        return entry is null ? null : Io.VpkEntries.Read(pkg, entry);
    }

    private static readonly Dictionary<string, byte[]?> RcCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Report a skipped map test and why. Always returns true.</summary>
    public static bool Skip(string needed)
    {
        Console.WriteLine($"[SKIP] no subscribed CS2 workshop map provides {needed}. "
                        + "Subscribe to one, or set CS2_WORKSHOP_DIR, to run this test.");
        return true;
    }
}
