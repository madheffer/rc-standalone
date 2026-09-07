using SteamDatabase.ValvePak;

namespace Source2.Compiler.Tests;

/// <summary>
/// Structural templates and ground-truth samples, taken from the tester's own
/// CS2 install rather than committed to this repository.
///
/// <para>Two of the compiled types this project emits (textures, sounds, vector
/// graphics) reuse an existing compiled file of the same type for its header
/// frame, and several tests want a genuine Valve compile to diff their output
/// against. Shipping those files here would mean redistributing game content,
/// so instead they are lifted out of <c>pak01_dir.vpk</c> at test time. Point
/// <c>CS2_DIR</c> at the install (or have it in a default Steam location) and
/// the full suite runs; without it, the tests that need game bytes skip and
/// say so, and the donor-free paths - the container author, the KV3 compiler,
/// the RERL ids - still run, because those need nothing from Valve.</para>
/// </summary>
internal static class CS2Fixtures
{
    private static readonly object Gate = new();
    private static Package? _pak;
    private static bool _tried;
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Path to the installed <c>pak01_dir.vpk</c>, or null.
    ///
    /// <para><c>CS2_DIR</c> is authoritative: when it is set, only it is
    /// consulted, and an install that is not there means "no CS2" rather than
    /// falling through to a guessed Steam location. That is what makes
    /// "this compiler needs nothing from the game" a claim you can test on a
    /// machine that happens to have CS2 installed - point <c>CS2_DIR</c> at
    /// somewhere empty and the game-dependent tests really do go away.</para>
    /// </summary>
    public static string? StockPak()
    {
        if (Environment.GetEnvironmentVariable("CS2_DIR") is { Length: > 0 } dir)
        {
            var explicitPath = Path.Combine(dir, "game", "csgo", "pak01_dir.vpk");
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var guesses = new[]
        {
            @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk",
            @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk",
        };
        return guesses.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Bytes of the first entry whose path ends with <paramref name="suffix"/>
    /// (e.g. <c>".vtex_c"</c>), picked deterministically so a rerun reads the
    /// same file. Null when CS2 is not installed or the pak holds no such type.
    /// </summary>
    public static byte[]? Template(string suffix)
    {
        lock (Gate)
        {
            var bytes = TemplateLocked(suffix);
            if (bytes is null && AssetsRequired)
                throw new InvalidOperationException(
                    $"No {suffix} is reachable, but the asset-gated tests are required to run "
                  + "(a CS2 install was found, or S2C_REQUIRE_ASSETS is set). A gate that "
                  + "self-skips is not a gate, so this fails instead.");
            return bytes;
        }
    }

    private static byte[]? TemplateLocked(string suffix)
    {
        {
            if (Cache.TryGetValue(suffix, out var cached)) return cached;

            if (!_tried)
            {
                _tried = true;
                var path = StockPak();
                if (path is not null)
                {
                    var p = new Package();
                    try { p.Read(path); _pak = p; }
                    catch { p.Dispose(); }
                }
            }

            byte[]? bytes = null;
            if (_pak is not null)
            {
                var entry = Io.VpkEntries.FirstEndingWith(_pak, suffix);
                if (entry is not null)
                {
                    try { bytes = Io.VpkEntries.Read(_pak, entry); }
                    catch { bytes = null; }
                }
            }
            return Cache[suffix] = bytes;
        }
    }

    /// <summary>
    /// True when the asset-gated tests must actually run rather than skip.
    /// Fixture lookups then throw instead of returning null, because a gate that
    /// silently self-skips is not a gate.
    ///
    /// <para>It is on whenever a CS2 install is reachable, not only under
    /// <c>S2C_REQUIRE_ASSETS=1</c>. xUnit 2.x has no dynamic skip, so a test
    /// that returns early still reports as a pass; without this, a fixture
    /// lookup that broke for some reason other than "no game installed" would
    /// disappear into a green run. The env var stays, for a CI runner that has
    /// mounted the game and wants to assert it is really being used.</para>
    /// </summary>
    public static bool AssetsRequired =>
        Environment.GetEnvironmentVariable("S2C_REQUIRE_ASSETS") is "1" or "true"
        || StockPak() is not null;

    /// <summary>
    /// <see cref="Template"/>, but written once to a temp file and handed back
    /// as a path, for the tests that want to read a fixture off disk.
    /// </summary>
    public static string? TemplatePath(string suffix)
    {
        lock (Gate)
        {
            if (PathCache.TryGetValue(suffix, out var cached)) return cached;
            var bytes = TemplateLocked(suffix);
            string? path = null;
            if (bytes is not null)
            {
                path = Path.Combine(Path.GetTempPath(), $"s2c_fixture_{suffix.TrimStart('.')}");
                File.WriteAllBytes(path, bytes);
            }
            else if (AssetsRequired)
            {
                throw new InvalidOperationException(
                    $"S2C_REQUIRE_ASSETS=1 but no {suffix} is reachable (install CS2 or set CS2_DIR). "
                  + "The container gate must FAIL rather than self-skip.");
            }
            return PathCache[suffix] = path;
        }
    }

    private static readonly Dictionary<string, string?> PathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Like <see cref="TemplatePath"/>, but keeps scanning entries of that type
    /// until <paramref name="accept"/> is happy. Some tests need more than "a
    /// file of the right type": a ground-truth assertion about material groups,
    /// say, is vacuous against a model that has none, so the fixture has to be
    /// chosen by content rather than by being first in the archive.
    /// </summary>
    public static string? TemplatePathWhere(string cacheKey, string suffix, Func<byte[], bool> accept, int budget = 400)
    {
        lock (Gate)
        {
            if (PathCache.TryGetValue(cacheKey, out var cached)) return cached;

            TemplateLocked(suffix);              // opens the pak on first use
            string? path = null;

            if (_pak is not null)
            {
                var ext = suffix.TrimStart('.');
                if (_pak.Entries is not null && _pak.Entries.TryGetValue(ext, out var entries))
                {
                    var seen = 0;
                    foreach (var e in entries.OrderBy(x => x.GetFullPath(), StringComparer.Ordinal))
                    {
                        if (seen++ >= budget) break;
                        byte[] bytes;
                        try { _pak.ReadEntry(e, out bytes); } catch { continue; }
                        bool good;
                        try { good = accept(bytes); } catch { good = false; }
                        if (!good) continue;
                        path = Path.Combine(Path.GetTempPath(), $"s2c_fixture_{cacheKey}");
                        File.WriteAllBytes(path, bytes);
                        break;
                    }
                }
            }

            if (path is null && AssetsRequired)
                throw new InvalidOperationException(
                    $"S2C_REQUIRE_ASSETS=1 but no {suffix} matching '{cacheKey}' is reachable "
                  + "(install CS2 or set CS2_DIR). The gate must FAIL rather than self-skip.");

            return PathCache[cacheKey] = path;
        }
    }

    /// <summary>Report a skipped test and why. Always returns true so callers can
    /// write <c>if (x is null) { CS2Fixtures.Skip(".vtex_c"); return; }</c>.</summary>
    public static bool Skip(string needed)
    {
        Console.WriteLine($"[SKIP] {needed} is not available. Install CS2, or set CS2_DIR, to run this test.");
        return true;
    }
}
