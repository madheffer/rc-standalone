using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>red2-survey &lt;pak01_dir.vpk&gt; &lt;ext&gt; [--limit N]</c>: tallies the special-dependency lists
/// stock files of one type carry in RED2 (names, fingerprints, user data, in order), so an
/// authored RED2 can be held to what Valve's compiler actually writes.
/// </summary>
internal static class Red2Survey
{
    public static int Run(string[] args)
    {
        var li = Array.IndexOf(args, "--limit");
        var limit = li >= 0 ? int.Parse(args[li + 1]) : int.MaxValue;
        using var pak = new Package();
        pak.Read(args[0]);
        var tally = new Dictionary<string, int>();
        var files = 0;
        foreach (var e in Source2.Compiler.Io.VpkEntries.ByExtension(pak)[args[1].TrimStart('.')])
        {
            if (files++ >= limit) break;
            pak.ReadEntry(e, out var bytes);
            using var res = new Resource { FileName = e.GetFileName() };
            try { res.Read(new MemoryStream(bytes)); } catch { continue; }
            var key = res.EditInfo is { } info
                ? string.Join(" | ", info.SpecialDependencies.Select(d => $"{d.String} fp={d.Fingerprint} ud={d.UserData}"))
                : "(no edit info)";
            tally[key] = tally.GetValueOrDefault(key) + 1;
        }
        foreach (var (k, v) in tally.OrderByDescending(x => x.Value)) Console.WriteLine($"{v,6}  {k}");
        return 0;
    }
}
