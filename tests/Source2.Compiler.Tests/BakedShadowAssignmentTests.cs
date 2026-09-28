using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="BakedShadowAssignment"/> against Valve's lumps. The precomputed
/// box keys the overlap test reads are fed from Valve's own lump (the
/// precompute runs before the assignment and its port is tested elsewhere),
/// so these check the gather, the shapes, the overlap tests, the slot
/// logic and the written keys in isolation.
/// </summary>
public sealed class BakedShadowAssignmentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_lighting", "ze_hold_em_p")]
    [InlineData("s2probe", "probe_classes")]
    public void SlotsMatchValve(string addon, string map)
    {
        if (MapFixtures.VmapSource(addon, map) is not { } source || MapFixtures.RcCompiledLumps(source) is not { } lumps)
            return;
        var valve = new Dictionary<string, EntityLumpComparison.Entity>();
        foreach (var (path, bytes) in lumps)
            foreach (var e in EntityLumpComparison.Read(bytes, path))
                if (e.ClassName.StartsWith("light_", StringComparison.Ordinal))
                    valve[e.HammerId] = e;
        string? Valve(string id, string key)
            => valve.TryGetValue(id, out var e) && e.Values.TryGetValue(key, out var v) ? v.Value.Split(':', 2)[^1] : null;

        var document = DmxBinary.ReadFile(source);
        var walked = MapEntities.From(document);
        var (copies, templates) = MapInstances.Expand(document, walked,
            SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
        var all = walked.Where((_, i) => !templates.Contains(i))
            .Concat(copies.Select(c => walked[c.Template] with { NodeId = c.NodeId, Origin = c.Origin, Angles = c.Angles, Instanced = true }))
            .Where(e => !e.Hidden && e.ClassName.StartsWith("light_", StringComparison.Ordinal))
            .ToList();
        // The map node carries every key its class declares, so an absent key
        // reads as the FGD default.
        var schema = MapFixtures.GameSchema();
        var lights = all.Select(e =>
        {
            var id = e.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string? Key(string name) => name.StartsWith("precomputed", StringComparison.Ordinal)
                ? Valve(id, name)
                : e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
                  ?? schema?.KeyOf(e.ClassName, name)?.Default;
            return new BakedShadowAssignment.Light(e.ClassName, Key, LightPrecompute.World(e.Origin, e.Angles), [e.NodeId]);
        }).ToList();

        var full = Path.GetFullPath(source).Replace('\\', '/');
        var root = $"csgo_addons/{addon}/";
        var mapPath = full[(full.IndexOf(root, StringComparison.OrdinalIgnoreCase) + root.Length)..];
        var results = BakedShadowAssignment.Assign(lights, mapPath);
        int compared = 0, same = 0;
        for (var i = 0; i < all.Count; i++)
        {
            var id = all[i].NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var (k, v) in results[i].Keys)
            {
                compared++;
                var theirs = Valve(id, k);
                if (theirs == v)
                    same++;
                else
                    output.WriteLine($"{all[i].ClassName}#{id} {k}: valve {theirs ?? "(none)"} ours {v}");
            }
            foreach (var k in new[] { "bakedshadowindex", "light_path_uniqueid", "light_map_uniqueid", "desiredstationary" })
                if (Valve(id, k) is { } theirs && results[i].Keys.All(o => o.Key != k))
                {
                    compared++;
                    output.WriteLine($"{all[i].ClassName}#{id} {k}: valve {theirs} ours (none)");
                }
        }
        // How much the geometry decided: pairs whose spheres touch, and of those
        // how many the boxes and then the hulls keep.
        var records = lights.Select((l, i) => (l, i))
            .Where(x => BakedShadowAssignment.Mode(x.l) is (3, not 0))
            .Select(x => BakedShadowAssignment.Build(x.l, x.i)).ToList();
        int spheres = 0, boxes = 0, hulls = 0;
        for (var i = 0; i < records.Count; i++)
            for (var j = i + 1; j < records.Count; j++)
            {
                BakedShadowAssignment.Record a = records[i], b = records[j];
                if (a.Directional || b.Directional)
                    continue;
                var d = a.SphereCentre - b.SphereCentre;
                if (d.LengthSquared() > (a.SphereRadius + b.SphereRadius) * (a.SphereRadius + b.SphereRadius))
                    continue;
                spheres++;
                if (a.Precomputed is { } x && b.Precomputed is { } y && !BakedShadowAssignment.BoxesOverlap(x, y))
                    continue;
                boxes++;
                if (BakedShadowAssignment.HullDistance(a.Hull, a.Xform, b.Hull, b.Xform) <= 1)
                    hulls++;
            }
        output.WriteLine($"{map}: {same}/{compared} keys; {records.Count} lights, pairs by sphere {spheres}, box {boxes}, hull {hulls}");
        Assert.True(compared > 0);
        Assert.Equal(compared, same);
    }

    /// <summary>
    /// atixref's and Mako's packages hold no lightmaps, carry none of the
    /// step's keys, and atixref's stationary lights that cast no shadow keep
    /// directlight 3: the step did not run there (it needs baked lighting).
    /// The port, told lighting is not baked, writes nothing.
    /// </summary>
    [Theory]
    [InlineData("s2probe", "atixref")]
    [InlineData("s2c_big", "ze_ffvii_mako_reactor_v6_p")]
    public void UnbakedCompilesWriteNothing(string addon, string map)
    {
        var lumps = addon == "s2c_big" ? MapFixtures.AddonLumps(addon, map)
            : MapFixtures.VmapSource(addon, map) is { } source ? MapFixtures.RcCompiledLumps(source) : null;
        if (lumps is null)
            return;
        int lights = 0, untouched = 0;
        bool probeAtlas = false;
        foreach (var (path, bytes) in lumps)
            foreach (var e in EntityLumpComparison.Read(bytes, path))
            {
                probeAtlas |= e.Values.TryGetValue("lightprobetexture", out var t) && t.Value.Split(':', 2)[^1].Length > 0;
                if (!e.ClassName.StartsWith("light_", StringComparison.Ordinal))
                    continue;
                lights++;
                Assert.False(e.Values.ContainsKey("bakedshadowindex"));
                Assert.False(e.Values.ContainsKey("light_path_uniqueid"));
                if (e.Values.TryGetValue("directlight", out var d) && d.Value.EndsWith(":3", StringComparison.Ordinal)
                    && e.Values.TryGetValue("castshadows", out var c) && c.Value.EndsWith(":0", StringComparison.Ordinal))
                    untouched++;
            }
        var none = BakedShadowAssignment.Assign([new BakedShadowAssignment.Light("light_barn", _ => "3", LightPrecompute.World(default, default), [1])],
                                                "maps/x.vmap", baked: false);
        Assert.Empty(none[0].Keys);
        output.WriteLine($"{map}: {lights} lights, {untouched} stationary without shadows left at 3, probe atlas key {probeAtlas}");
        if (MapFixtures.VmapSource(addon, map) is { } vmap)
        {
            var document = DmxBinary.ReadFile(vmap);
            var walked = MapEntities.From(document);
            var (copies, templates) = MapInstances.Expand(document, walked,
                SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
            var direct = walked.Where((e, i) => !templates.Contains(i) && e.ClassName.StartsWith("light_", StringComparison.Ordinal)).ToList();
            var placed = copies.Select(c => walked[c.Template]).Where(e => e.ClassName.StartsWith("light_", StringComparison.Ordinal)).ToList();
            output.WriteLine($"  source: {direct.Count} lights in the map ({direct.Count(e => e.Hidden)} hidden), {placed.Count} placed by instances; "
                             + $"stationary direct {direct.Count(e => e.Keys.Any(k => k.Key == "directlight" && k.Value == "3"))}");
        }
    }

    [Fact]
    public void DumpValveLightKeys()
    {
        var dump = Environment.GetEnvironmentVariable("BSA_DUMP");
        if (dump is null)
            return;
        var maps = new List<(string Name, IReadOnlyDictionary<string, byte[]>? Lumps)>
        {
            ("ze_hold_em_p", MapFixtures.VmapSource("s2c_lighting", "ze_hold_em_p") is { } a ? MapFixtures.RcCompiledLumps(a) : null),
            ("probe_classes", MapFixtures.VmapSource("s2probe", "probe_classes") is { } b ? MapFixtures.RcCompiledLumps(b) : null),
            ("atixref", MapFixtures.VmapSource("s2probe", "atixref") is { } c ? MapFixtures.RcCompiledLumps(c) : null),
            ("mako", MapFixtures.AddonLumps("s2c_big", "ze_ffvii_mako_reactor_v6_p")),
        };
        foreach (var (name, lumps) in maps)
        {
            if (lumps is null)
                continue;
            var lines = new List<string>();
            foreach (var (path, bytes) in lumps)
                foreach (var e in EntityLumpComparison.Read(bytes, path))
                    if (e.ClassName.StartsWith("light_", StringComparison.Ordinal))
                        lines.Add($"{Path.GetFileName(path)} {e.ClassName}#{e.HammerId} "
                                  + string.Join(" | ", e.KeyOrder.Select(k => $"{k}={e.Values[k].Value}")));
            File.WriteAllLines(Path.Combine(dump, $"{name}.lights.txt"), lines);
            output.WriteLine($"{name}: {lines.Count}");
        }
    }
}
