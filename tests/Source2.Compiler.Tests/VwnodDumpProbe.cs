using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (VWNODDUMP=reference.vpk|candidate.vpk|maps/x/worldnodes/n0.vwnod_c):
/// both node files' scene objects (model, bounds, flags, draw-call count) and
/// aggregate objects (model, fragment count), one line each, in file order,
/// so the cluster layouts can be lined up.
/// </summary>
public class VwnodDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("VWNODDUMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        foreach (var (label, vpk) in new[] { ("ref", p[0]), ("ours", p[1]) })
        {
            using var package = new ValvePak.Package();
            package.Read(vpk);
            var entry = package.FindEntry(p[2]) ?? throw new FileNotFoundException(p[2]);
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (p[2].EndsWith(".vwrld_c", StringComparison.Ordinal))
            {
                var world = ((World)resource.DataBlock!).Data;
                foreach (var n in world.GetArray("m_worldNodes") ?? [])
                    output.WriteLine($"{label} node {string.Join(" | ", n.Children.Select(c => $"{c.Key}={Show(c.Value)}"))}");
                continue;
            }
            var data = ((WorldNode)resource.DataBlock!).Data;
            output.WriteLine($"== {label}: keys {string.Join(",", data.Children.Select(c => c.Key))}");
            foreach (var key in new[] { "m_aggregateInstanceStreams", "m_extraVertexStreams", "m_vertexAlbedoStreams" })
                output.WriteLine($"{label} {key} count {(data.Children.FirstOrDefault(c => c.Key == key).Value is { IsArray: true } arr ? arr.Values.Count() : -1)}");
            foreach (var so in data.GetArray("m_sceneObjects") ?? [])
            {
                string V(string key) => so.Children.FirstOrDefault(c => c.Key == key).Value is { } v ? Show(v) : "-";
                output.WriteLine($"{label} so {V("m_renderableModel")} min {V("m_vMinBounds")} max {V("m_vMaxBounds")} flags {V("m_nObjectTypeFlags")} layer {V("m_nLayer")}");
            }
            // VWNODDUMP_FULL=<substring>: the whole record of the first two scene objects whose model contains it.
            if (Environment.GetEnvironmentVariable("VWNODDUMP_FULL") is { } full)
                foreach (var so in (data.GetArray("m_sceneObjects") ?? []).Where(o => (o.GetStringProperty("m_renderableModel") ?? "").Contains(full, StringComparison.Ordinal)).Take(int.TryParse(Environment.GetEnvironmentVariable("VWNODDUMP_TAKE"), out var take) ? take : 2))
                    output.WriteLine($"{label} full {string.Join(" | ", so.Children.Select(c => $"{c.Key}={Show(c.Value)}"))}");
            foreach (var ag in data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var fragments = ag.Children.FirstOrDefault(c => c.Key == "m_fragments").Value is { IsArray: true } f ? f.Values.Count() : -1;
                string A(string key) => ag.Children.FirstOrDefault(c => c.Key == key).Value is { } v ? Show(v) : "-";
                output.WriteLine($"{label} agg {ag.GetStringProperty("m_renderableModel")} fragments {fragments} meshes {(ag.Children.FirstOrDefault(c => c.Key == "m_aggregateMeshes").Value is { IsArray: true } ms ? ms.Values.Count() : -1)} instanceStream {A("m_instanceStream")} albedo {A("m_vertexAlbedoStream")}");
            }
        }
    }

    private static string Show(KVObject v) => v.IsArray ? string.Join(" ", v.Values.Select(Show)) : v.ToString() ?? "";
}
