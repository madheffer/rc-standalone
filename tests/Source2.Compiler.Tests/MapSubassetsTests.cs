using ValveResourceFormat.Serialization.KeyValues;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// MapSubassets against the m_SubassetReferences of every map in s2c_rc_probe that
/// resourcecompiler has compiled: the pairs, their counts and their order.
/// </summary>
public class MapSubassetsTests(ITestOutputHelper output)
{
    [Fact]
    public void ReferencesEqualValves()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
        {
            MapFixtures.Skip("CS2");
            return;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        var maps = Path.Combine(game, "csgo_addons", "s2c_rc_probe", "maps");
        if (!Directory.Exists(maps))
        {
            MapFixtures.Skip("s2c_rc_probe's compiled maps");
            return;
        }
        var failed = new List<string>();
        var compared = 0;
        foreach (var vpk in Directory.GetFiles(maps, "*.vpk").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileNameWithoutExtension(vpk);
            if (MapFixtures.VmapSource("s2c_rc_probe", map) is not { } source)
                continue;
            using var package = new Package();
            package.Read(vpk);
            if (package.FindEntry($"maps/{map}.vmap_c") is not { } entry)
                continue;
            var trees = WorldPhysicsAuthorTests.Trees(Io.VpkEntries.Read(package, entry));
            var valve = new List<(string, string, int)>();
            if (trees["RED2"]["m_SubassetReferences"] is { ValueType: KVValueType.Collection } refs)
                foreach (var (type, names) in refs)
                    foreach (var (name, count) in names)
                        valve.Add((type, name, count.ToInt32(System.Globalization.CultureInfo.InvariantCulture)));
            var document = DmxBinary.ReadFile(source);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
            var ours = MapSubassets.References(MapEntities.From(document), schema, document);
            // The RED2 groups by type, each in the order its names first appear.
            var grouped = ours.GroupBy(r => r.Type).SelectMany(g => g).ToList();
            compared++;
            var same = grouped.SequenceEqual(valve);
            output.WriteLine($"{map}: {valve.Count} pairs, {(same ? "equal" : "DIFFER")}");
            if (!same)
            {
                failed.Add(map);
                var o = grouped.ToDictionary(r => (r.Type, r.Name), r => r.Count);
                var v = valve.ToDictionary(r => (r.Item1, r.Item2), r => r.Item3);
                var counts = o.Keys.Union(v.Keys).Where(k => o.GetValueOrDefault(k) != v.GetValueOrDefault(k)).Select(k => $"{k.Item2} {o.GetValueOrDefault(k)}/{v.GetValueOrDefault(k)}").ToList();
                output.WriteLine($"  counts (ours/valve): {(counts.Count == 0 ? "same" : string.Join(", ", counts.Take(20)))}");
                if (counts.Count == 0)
                {
                    output.WriteLine("  ours  " + string.Join(" ", grouped.Select(r => r.Name)));
                    output.WriteLine("  valve " + string.Join(" ", valve.Select(r => r.Item2)));
                }
            }
        }
        output.WriteLine($"{compared} maps compared, {failed.Count} differ");
        Assert.Empty(failed);
    }
}

/// <summary>MapAssets against each probe compile's .vmap_c RERL, the children taken out.</summary>
public class MapAssetsTests(ITestOutputHelper output)
{
    [Fact]
    public void AssetReferencesEqualValves()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
        {
            MapFixtures.Skip("CS2");
            return;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        var maps = Path.Combine(game, "csgo_addons", "s2c_rc_probe", "maps");
        var failed = new List<string>();
        foreach (var vpk in Directory.GetFiles(maps, "*.vpk").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileNameWithoutExtension(vpk);
            if (MapFixtures.VmapSource("s2c_rc_probe", map) is not { } source)
                continue;
            using var package = new Package();
            package.Read(vpk);
            if (package.FindEntry($"maps/{map}.vmap_c") is not { } entry)
                continue;
            using var res = ResourceTrees.Read(Io.VpkEntries.Read(package, entry), "map.vmap_c");
            var red2 = ((ValveResourceFormat.Blocks.ResourceEditInfo2)res.EditInfo!).Data!.Root;
            var children = red2.GetArray<string>("m_ChildResourceList")!.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var valve = res.ExternalReferences!.ResourceRefInfoList.Select(r => r.Name).Where(n => !children.Contains(n)).ToList();
            var document = DmxBinary.ReadFile(source);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
            var entities = MapEntities.From(document);
            var ours = MapAssets.References(document, MapSubassets.LumpOrder(entities, document), schema, $"maps/{map}").ToList();
            var same = ours.SequenceEqual(valve);
            output.WriteLine($"{map}: {valve.Count} externals, {(same ? "equal" : "DIFFER")}");
            if (!same)
            {
                failed.Add(map);
                output.WriteLine("  missing: " + string.Join(", ", valve.Except(ours).Take(15)));
                output.WriteLine("  extra:   " + string.Join(", ", ours.Except(valve).Take(15)));
                if (valve.ToHashSet().SetEquals(ours))
                    output.WriteLine("  (order only)");
                // Where each extra comes from: the entity, the key and whether the value is the class default.
                foreach (var extra in ours.Except(valve).Take(3))
                    foreach (var e in MapSubassets.LumpOrder(entities, document))
                        foreach (var (k, v) in EntityLumpAuthor.KeyTable(e, schema))
                            if (v.Length > 3 && v.Contains(Path.GetFileNameWithoutExtension(extra), StringComparison.OrdinalIgnoreCase))
                            {
                                var own = e.Keys.Any(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                                output.WriteLine($"    {extra}: {e.ClassName} {e.NodeId} key {k} ({(own ? "own" : "default")}, hidden {e.Hidden}, type {schema.KeyOf(e.ClassName, k)?.TypeId})");
                            }
            }
        }
        Assert.Empty(failed);
    }
}

/// <summary><c>VMAPC=&lt;vpk&gt;|&lt;map&gt;</c>: a package's .vmap_c RERL against its child list, and which package files carry each extra.</summary>
public class MapRootProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("VMAPC") is not { Length: > 0 } spec || spec.Split('|') is not [var vpk, var map])
            return;
        using var package = new Package();
        package.Read(vpk);
        var bytes = Io.VpkEntries.Read(package, package.FindEntry($"maps/{map}.vmap_c")!);
        using var res = ResourceTrees.Read(bytes, "map.vmap_c");
        var red2 = ((ValveResourceFormat.Blocks.ResourceEditInfo2)res.EditInfo!).Data!.Root;
        var children = red2.GetArray<string>("m_ChildResourceList")!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rerl = res.ExternalReferences!.ResourceRefInfoList;
        output.WriteLine($"{rerl.Count} references, {children.Count} children; blocks {string.Join(" ", ResourceTrees.BlockTable(bytes).Select(b => $"{b.FourCC}:{b.Size}"))}");
        // Which compiled files reference each external.
        var refsBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, entries) in package.Entries)
            foreach (var e in entries)
            {
                var path = e.GetFullPath();
                if (!path.EndsWith("_c", StringComparison.Ordinal) || path.EndsWith(".vmap_c", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var r = ResourceTrees.Read(Io.VpkEntries.Read(package, e), Path.GetFileName(path));
                    foreach (var x in r.ExternalReferences?.ResourceRefInfoList ?? [])
                        (refsBy.TryGetValue(x.Name, out var l) ? l : refsBy[x.Name] = []).Add(Path.GetFileName(path));
                }
                catch (Exception) { }
            }
        var i = 0;
        foreach (var r in rerl)
            output.WriteLine($"  [{i++}] {r.Name} {(children.Contains(r.Name) ? "child" : "external")} {r.Id:x16}"
                             + (children.Contains(r.Name) ? "" : $" from {string.Join(",", refsBy.GetValueOrDefault(r.Name) ?? [])}"));
        var allExternal = refsBy.Keys.Where(k => !children.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = rerl.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"externals referenced by children but not in the RERL: {string.Join(", ", allExternal.Where(x => !listed.Contains(x)).Take(30))}");
    }
}

/// <summary>MapRootFile against each probe compile's .vmap_c, byte for byte, given Valve's child list.</summary>
public class MapRootFileTests(ITestOutputHelper output)
{
    [Fact]
    public void MapRootEqualsValves()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
        {
            MapFixtures.Skip("CS2");
            return;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        var maps = Path.Combine(game, "csgo_addons", "s2c_rc_probe", "maps");
        var failed = new List<string>();
        foreach (var vpk in Directory.GetFiles(maps, "*.vpk").Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileNameWithoutExtension(vpk);
            if (map == "atixref" || MapFixtures.VmapSource("s2c_rc_probe", map) is not { } source)
                continue;
            using var package = new Package();
            package.Read(vpk);
            if (package.FindEntry($"maps/{map}.vmap_c") is not { } entry)
                continue;
            var valve = Io.VpkEntries.Read(package, entry);
            using var res = ResourceTrees.Read(valve, "map.vmap_c");
            var red2 = ((ValveResourceFormat.Blocks.ResourceEditInfo2)res.EditInfo!).Data!.Root;
            var children = red2.GetArray<string>("m_ChildResourceList")!.ToList();
            var content = Path.GetDirectoryName(Path.GetDirectoryName(source))!;
            var document = DmxBinary.ReadFile(source);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(content));
            var inputs = new List<(string, uint)> { ($"maps/{map}.vmap", MapRootFile.Crc(File.ReadAllBytes(source))) };
            foreach (var path in Prefabs(document).Distinct(StringComparer.OrdinalIgnoreCase))
                inputs.Add((path, MapRootFile.Crc(File.ReadAllBytes(Path.Combine(content, path)))));
            var baked = children.Any(c => c.EndsWith("/lightmaps/irradiance.vtex", StringComparison.Ordinal));
            var probes = children.Any(c => c.EndsWith("env_light_probe_volume_atlas.vtex", StringComparison.Ordinal));
            var entities = MapEntities.From(document);
            var ours = MapRootFile.Write(new MapRootFile.Compile("s2c_rc_probe", inputs, baked, probes, new Dictionary<string, object>()), children,
                                         MapAssets.References(document, MapSubassets.LumpOrder(entities, document), schema, $"maps/{map}"),
                                         MapSubassets.References(entities, schema, document));
            var same = ours.AsSpan().SequenceEqual(valve);
            output.WriteLine($"{map}: {(same ? "equal" : $"DIFFER ({ours.Length} vs {valve.Length} bytes)")}");
            if (!same && ours.Length == valve.Length)
            {
                var first = Enumerable.Range(0, ours.Length).First(i => ours[i] != valve[i]);
                output.WriteLine($"  first byte {first}: ours {Convert.ToHexString(ours, first & ~15, 32)} valve {Convert.ToHexString(valve, first & ~15, 32)}; {Enumerable.Range(0, ours.Length).Count(i => ours[i] != valve[i])} bytes differ");
            }
            if (!same)
            {
                failed.Add(map);
                using var mine = ResourceTrees.Read(ours, "map.vmap_c");
                var a = ValveResourceFormat.Serialization.KeyValues.KVDocumentExtensions.ToKV3String(((ValveResourceFormat.Blocks.ResourceEditInfo2)mine.EditInfo!).Data!.Root);
                var b = ValveResourceFormat.Serialization.KeyValues.KVDocumentExtensions.ToKV3String(red2);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"maproot_{map}_ours.kv3"), a);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"maproot_{map}_valve.kv3"), b);
                var at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i], -1);
                output.WriteLine(at < 0 ? $"  RED2 text equal (lengths {a.Length}/{b.Length}); blocks ours {string.Join(" ", ResourceTrees.BlockTable(ours).Select(x => $"{x.FourCC}:{x.Size}"))} valve {string.Join(" ", ResourceTrees.BlockTable(valve).Select(x => $"{x.FourCC}:{x.Size}"))}"
                                     : $"  RED2 text at {at}: ours ...{a[Math.Max(0, at - 80)..Math.Min(a.Length, at + 80)]}\n  valve ...{b[Math.Max(0, at - 80)..Math.Min(b.Length, at + 80)]}");
            }
        }
        Assert.Empty(failed);
    }

    private static IEnumerable<string> Prefabs(DmxBinary.Document document)
    {
        foreach (var p in document.OfType("CMapPrefab"))
        {
            if (p.Get<string>("targetMapPath") is { Length: > 0 } path && p.Attributes.ContainsKey(MapPrefabs.DocumentKey))
            {
                yield return path;
                foreach (var inner in Prefabs((DmxBinary.Document)p.Attributes[MapPrefabs.DocumentKey]!))
                    yield return inner;
            }
        }
    }
}

/// <summary>
/// <c>TEXBIAS=&lt;our vpk&gt;|&lt;valve vpk&gt;|&lt;model substring&gt;</c>: each matching model's texcoords, ours against
/// Valve's, and how many match after (v + B) - B for a bias B taken per draw or per model.
/// </summary>
public class TexcoordBiasProbe(ITestOutputHelper output)
{
    [Fact]
    public void Score()
    {
        if (Environment.GetEnvironmentVariable("TEXBIAS") is not { Length: > 0 } spec || spec.Split('|') is not [var oursVpk, var valveVpk, var part])
            return;
        using var ours = new Package();
        ours.Read(oursVpk);
        using var valve = new Package();
        valve.Read(valveVpk);
        foreach (var entry in valve.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains(part, StringComparison.Ordinal) || ours.FindEntry(path) is not { } mine)
                continue;
            var a = Draws(Io.VpkEntries.Read(ours, mine));
            var b = Draws(Io.VpkEntries.Read(valve, entry));
            if (a.Count != b.Count) { output.WriteLine($"{path}: {a.Count} vs {b.Count} draws"); continue; }
            var variants = new Dictionary<string, int>();
            var total = 0;
            var raw = 0;
            float Apply(float v, float bias) => (v + bias) - bias;
            var modelMin = new float[2];
            modelMin[0] = a.Min(d => d.Min(t => t.X));
            modelMin[1] = a.Min(d => d.Min(t => t.Y));
            for (var d = 0; d < a.Count; d++)
            {
                var drawMin = new[] { a[d].Min(t => t.X), a[d].Min(t => t.Y) };
                for (var i = 0; i < a[d].Count && i < b[d].Count; i++)
                    for (var k = 0; k < 2; k++)
                    {
                        float x = k == 0 ? a[d][i].X : a[d][i].Y, want = k == 0 ? b[d][i].X : b[d][i].Y;
                        total++;
                        raw += x == want ? 1 : 0;
                        if (x != want && Environment.GetEnvironmentVariable("TEXBIAS_LIST") == "1")
                        {
                            var fits = Enumerable.Range(-8, 17).Where(bias => Apply(x, bias) == want).ToList();
                            var other = k == 0 ? a[d][i].Y : a[d][i].X;
                            output.WriteLine($"  draw {d} vertex {i} {(k == 0 ? "u" : "v")}: ours {x:R} ({BitConverter.SingleToInt32Bits(x):X8}) valve {want:R} ({BitConverter.SingleToInt32Bits(want):X8}) other {other:R}; B in {string.Join(",", fits)}");
                        }
                        foreach (var fixedBias in new[] { -4, -3, -2, -1, 1, 2, 3, 4 })
                        {
                            var key = $"{(k == 0 ? "u" : "v")} fixed {fixedBias}";
                            variants[key] = variants.GetValueOrDefault(key) + (Apply(x, fixedBias) == want ? 1 : 0);
                        }
                        foreach (var (name, min) in new[] { ("draw", drawMin[k]), ("model", modelMin[k]) })
                        {
                            void Count(string rule, float bias) { var key = name + " " + rule; variants[key] = variants.GetValueOrDefault(key) + (Apply(x, bias) == want ? 1 : 0); }
                            Count("-floor", -MathF.Floor(min));
                            Count("-floor if <0", min < 0 ? -MathF.Floor(min) : 0);
                            Count("ceil(-min)", MathF.Ceiling(-min));
                        }
                    }
            }
            output.WriteLine($"{path}: {total} values, {raw} equal as they are; " + string.Join(", ", variants.Select(v => $"{v.Key} {v.Value}")));
        }
    }

    private static List<List<System.Numerics.Vector2>> Draws(byte[] bytes)
    {
        using var resource = new ValveResourceFormat.Resource();
        resource.Read(new MemoryStream(bytes));
        var model = (ValveResourceFormat.ResourceTypes.Model)resource.DataBlock!;
        var result = new List<List<System.Numerics.Vector2>>();
        foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
            foreach (var call in mesh.Data.GetArray("m_sceneObjects").SelectMany(o => o.GetArray("m_drawCalls")))
            {
                var m = Lighting.PathTraceProps.DrawMesh(mesh.VBIB, call, "");
                var t = m.Streams.First(s => s.Name == "texcoord").Offset;
                var list = new List<System.Numerics.Vector2>();
                for (var v = 0; v < m.VertexCount; v++)
                    list.Add(new(m.Vertices[v * m.Stride + t], m.Vertices[v * m.Stride + t + 1]));
                result.Add(list);
            }
        return result;
    }
}
