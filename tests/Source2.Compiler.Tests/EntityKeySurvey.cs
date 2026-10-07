using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch survey (KEYSURVEY=key[|folder]): every entity lump of every map
/// package in game/csgo/maps (or the folder given), and each entity holding
/// that key: map, lump, class, hammer id, model and the value (cut at 300).
/// </summary>
public class EntityKeySurvey(ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("KEYSURVEY") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var p = spec.Split('|');
        var folder = p.Length > 1 ? p[1] : Path.Combine(Path.GetDirectoryName(pak)!, "maps");
        var hits = 0;
        foreach (var vpk in Directory.GetFiles(folder, "*.vpk").Where(f => !f.EndsWith("_dir.vpk", StringComparison.Ordinal) || true).OrderBy(f => f))
        {
            using var package = new ValvePak.Package();
            try
            {
                package.Read(vpk);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var entry in package.Entries?.GetValueOrDefault("vents_c") ?? [])
            {
                package.ReadEntry(entry, out var bytes);
                IReadOnlyList<EntityLumpComparison.Entity> entities;
                try
                {
                    entities = EntityLumpComparison.Read(bytes, entry.GetFileName());
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var e in entities)
                {
                    var key = e.Values.Keys.FirstOrDefault(k => k.Equals(p[0], StringComparison.OrdinalIgnoreCase));
                    if (key is null)
                        continue;
                    var value = e.Values[key].Value;
                    var model = e.Values.TryGetValue("model", out var m) ? m.Value : "";
                    output.WriteLine($"{Path.GetFileName(vpk)} {entry.GetFullPath()} {e.ClassName} {e.HammerId} {model} = {value[..Math.Min(300, value.Length)]}");
                    hits++;
                }
            }
        }
        output.WriteLine($"{hits} entities with {p[0]}");
    }
}
