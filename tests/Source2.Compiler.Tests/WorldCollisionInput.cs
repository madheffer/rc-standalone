using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The world's triangle soups as <see cref="WorldCollision"/> assembles them,
/// against the RnMeshCreate inputs a full compile made
/// (<c>tools/physics/capture_rnmesh.py --full</c>). The soups are compared in
/// order with the captured calls from the first world call on.
/// <c>WORLDCOL=&lt;addon&gt;|&lt;map&gt;|&lt;capture&gt;|&lt;first world call id&gt;</c>.
/// </summary>
public class WorldCollisionInput(ITestOutputHelper output)
{
    [Fact]
    public void SoupsMatchTheCapturedCalls()
    {
        if (Environment.GetEnvironmentVariable("WORLDCOL") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        var packages = new List<Package>();
        foreach (var dir in new[] { "csgo", "core" })
        {
            var package = new Package();
            package.Read(Path.Combine(game, dir, "pak01_dir.vpk"));
            packages.Add(package);
        }
        var addon = Path.Combine(game, "csgo_addons", parts[0]);
        var cache = new Dictionary<string, WorldCollision.MaterialPhysics>(StringComparer.OrdinalIgnoreCase);
        WorldCollision.MaterialPhysics Lookup(string name)
        {
            if (cache.TryGetValue(name, out var known))
                return known;
            var compiled = name.Replace('\\', '/') + "_c";
            byte[]? bytes = null;
            if (File.Exists(Path.Combine(addon, compiled)))
                bytes = File.ReadAllBytes(Path.Combine(addon, compiled));
            foreach (var package in packages)
            {
                if (bytes == null && package.FindEntry(compiled) is { } entry)
                    package.ReadEntry(entry, out bytes);
            }
            var physics = WorldCollision.MaterialPhysics.Default;
            if (bytes != null)
            {
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var mat = (Material)resource.DataBlock!;
                physics = WorldCollision.ReadMaterial(mat.IntAttributes, mat.StringAttributes);
            }
            output.WriteLine($"material {name}: {physics}{(bytes == null ? " (not found)" : "")}");
            return cache[name] = physics;
        }

        var pieces = WorldCollision.Pieces(DmxBinary.ReadFile(vmap), Lookup);
        var attributes = new List<string> { "default|" };
        var surfaces = new List<string> { "default" };
        int IndexOf(List<string> table, string key)
        {
            var i = table.IndexOf(key);
            if (i < 0)
            {
                table.Add(key);
                i = table.Count - 1;
            }
            return i;
        }
        var ordered = WorldCollision.PartOrder(pieces, _ => WorldCollision.MeshType);
        output.WriteLine("part order: " + string.Join(" ", ordered.Select(p => $"{p.NodeId}/{p.Material}")));
        var soups = WorldCollision.Group(ordered.Select(p => (IndexOf(attributes, p.Physics.AttributeKey),
            IndexOf(surfaces, p.Physics.SurfaceProperty.Length == 0 ? "default" : p.Physics.SurfaceProperty), p.Points, p.Indices)));
        output.WriteLine($"attributes: {string.Join(" ; ", attributes)}; surfaces: {string.Join(" ", surfaces)}");

        var first = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
        var allSame = true;
        for (var s = 0; s < soups.Count; s++)
        {
            var soup = soups[s];
            var (indices, vertices, materials) = Input(parts[2], first + s);
            var sameV = soup.Vertices.SequenceEqual(vertices);
            var sameI = soup.Indices.SequenceEqual(indices);
            var sameM = materials == null ? soup.Materials == null : soup.Materials != null && soup.Materials.SequenceEqual(materials);
            allSame &= sameV && sameI && sameM;
            output.WriteLine($"soup {s} attr {soup.Attribute} surf {soup.SurfaceProperty}: {soup.Indices.Count / 3} triangles, {soup.Vertices.Count} vertices, materials {soup.Materials?.Count.ToString() ?? "none"}; " +
                             $"call {first + s}: {indices.Length / 3} triangles, {vertices.Length} vertices, materials {materials?.Length.ToString() ?? "none"}; " +
                             $"vertices {(sameV ? "same" : "differ")}, indices {(sameI ? "same" : "differ")}, materials {(sameM ? "same" : "differ")}");
            if (!sameV)
            {
                var i = Enumerable.Range(0, Math.Min(soup.Vertices.Count, vertices.Length)).FirstOrDefault(k => soup.Vertices[k] != vertices[k], -1);
                if (i >= 0)
                    output.WriteLine($"  first vertex difference at {i}: ours {soup.Vertices[i]:R} captured {vertices[i]:R}");
            }
        }
        Assert.True(allSame);
    }

    private static (int[] Indices, Vector3[] Vertices, byte[]? Materials) Input(string path, int id)
    {
        var data = File.ReadAllBytes(path);
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m);
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "in" || head.GetProperty("id").GetInt32() != id)
                continue;
            var tris = head.GetProperty("tris").GetInt32();
            var vcount = head.GetProperty("vcount").GetInt32();
            var hasMaterials = head.GetProperty("hasMaterials").GetBoolean();
            var off = (tris * 12) + (hasMaterials ? tris : 0);
            return (MemoryMarshal.Cast<byte, int>(blob[..(tris * 12)]).ToArray(), MemoryMarshal.Cast<byte, Vector3>(blob.Slice(off, vcount * 12)).ToArray(),
                    hasMaterials ? blob.Slice(tris * 12, tris).ToArray() : null);
        }
        throw new InvalidDataException($"no call {id}");
    }
}
