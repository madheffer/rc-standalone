using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Scratch probe (HANDSHAKE=1): Mako's handshake values, ours against Valve's, by entity.</summary>
public class HandshakeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("HANDSHAKE") is null)
            return;
        const string map = "ze_ffvii_mako_reactor_v6_p";
        var source = MapFixtures.VmapSource("s2c_big", map)!;
        var valve = MapFixtures.AddonLumps("s2c_big", map)!;
        var document = DmxBinary.ReadFile(source);
        var ours = EntityLumpSet.Author(MapEntities.From(document), MapFixtures.GameSchema(), map,
                                        MapEntities.FixupEntityNames(document), document, MapFixtures.SmartPropLocators,
                                        SettleLumpTests.Settle(document, source), EntityLumpAgainstValveTests.BakedIn(valve));
        Dictionary<string, string> Of(IEnumerable<(string Path, byte[] Bytes)> lumps)
            => lumps.SelectMany(l => EntityLumpComparison.Read(l.Bytes, l.Path).Where(e => e.Values.ContainsKey("handshake"))
                    .Select(e => (Id: $"{Path.GetFileName(l.Path)} {e.ClassName}#{e.HammerId}", V: e.Values["handshake"].Value)))
                .ToDictionary(x => x.Id, x => x.V);
        var v = Of(valve.Select(kv => (kv.Key, kv.Value)));
        var o = Of(ours.Select(l => (l.Path, l.Bytes)));
        foreach (var (id, value) in o.OrderBy(x => long.Parse(x.Value.Split(':')[^1])))
            output.WriteLine($"{value} {id} valve {v.GetValueOrDefault(id, "-")}");
        var shipped = v.Keys.Select(k => k.Split('#')[1]).ToHashSet();
        foreach (var e in MapEntities.From(document).Where(e => e.ClassName.Contains("probe") || e.ClassName.Contains("cubemap")))
            if (!shipped.Contains(e.NodeId.ToString()))
                output.WriteLine($"not shipped {e.ClassName}#{e.NodeId} hidden {e.Hidden} layer {e.Layer}");
        foreach (var id in v.Keys.Except(o.Keys))
            output.WriteLine($"valve only {id} {v[id]}");
    }
}
