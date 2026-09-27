using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a compiled map's entity models (maps/&lt;map&gt;/entities/*.vmdl_c)
/// one line each: blocks, the entity class RED2 fingerprints, the part's flags,
/// shape counts, tool material hashes, surfaces, attributes, RED2's subasset
/// references and DATA's flags. <c>ENTMODELS=&lt;compiled .vpk&gt;</c>.
/// </summary>
public class EntityModelSurvey(ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("ENTMODELS") is not { Length: > 0 } vpk)
            return;
        using var package = new Package();
        package.Read(vpk);
        var groups = new Dictionary<string, List<string>>();
        foreach (var entry in package.Entries!["vmdl_c"].Where(e => e.GetFullPath().Contains("/entities/", StringComparison.Ordinal)).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(entry, out var bytes);
            using var res = new Resource();
            res.Read(new MemoryStream(bytes));
            var blocks = string.Join(" ", res.Blocks.Select(b => b.Type.ToString()).Distinct());
            var red2 = (res.EditInfo as ResourceEditInfo2)?.Data?.Root;
            var args = red2?.GetArray("m_ArgumentDependencies")?.Select(a => $"{a.GetStringProperty("m_ParameterName")}:{a.GetStringProperty("m_ParameterType")}:{a.GetUInt32Property("m_nFingerprint")}") ?? [];
            var user = red2?.GetSubCollection("m_SearchableUserData");
            var subassets = red2?.ContainsKey("m_SubassetReferences") == true && red2.GetSubCollection("m_SubassetReferences") is { } sr ? string.Join(",", sr.Children.Select(c => c.Key + "=" + string.Join("/", c.Value.Children.Select(x => x.Key)))) : "null";
            var model = res.DataBlock as Model;
            var flags = model?.Data.GetSubCollection("m_modelInfo")?.GetUInt32Property("m_nFlags");
            var phys = model?.GetEmbeddedPhys();
            string Tools(ValveKeyValue.KVObject shape, string key) => string.Join("/", (shape.GetArray(key) ?? []).Select(d => d.GetUInt32Property("m_nToolMaterialHash")).Distinct());
            var parts = phys == null ? "no phys" : string.Join(" ", (phys.Data.GetArray("m_parts") ?? []).Select(p =>
            {
                var shape = p.GetSubCollection("m_rnShape");
                int N(string key) => shape.GetArray(key)?.Count ?? 0;
                return $"[flags {p.GetUInt32Property("m_nFlags")} s{N("m_spheres")} c{N("m_capsules")} h{N("m_hulls")} m{N("m_meshes")} tools {Tools(shape, "m_hulls")};{Tools(shape, "m_meshes")}]";
            }));
            var attrs = phys == null ? "" : string.Join(" | ", phys.CollisionAttributes.Select(a => $"{a.GetStringProperty("m_CollisionGroupString")}:{string.Join(",", a.GetArray<string>("m_InteractAsStrings") ?? [])}"));
            var surfaces = phys == null ? "" : string.Join(",", phys.SurfacePropertyHashes);
            var shapeCounts = user == null ? "" : string.Join(" ", user.Children.Where(c => c.Key.StartsWith("physics_", StringComparison.Ordinal)).Select(c => $"{c.Key}={c.Value}"));
            var name = Path.GetFileNameWithoutExtension(entry.FileName);
            output.WriteLine($"{name}: {blocks} | {string.Join(" ", args)} | data flags {flags} | {parts} | surf {surfaces} | attrs {attrs} | subassets {subassets} | {shapeCounts}");
        }
    }
}
