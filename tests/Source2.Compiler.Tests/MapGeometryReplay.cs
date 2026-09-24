using System.Buffers.Binary;
using Source2.Compiler.Maps;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MapGeometry.RteTriangles"/> against the .rte the compile wrote from
/// the same map: each of our triangles found bit for bit (through the tracer
/// record of its corners), each left over, and the file's triangles we did not
/// produce. <c>MAPGEO=&lt;addon&gt;;&lt;map&gt;;&lt;rte&gt;</c>.
/// </summary>
public class MapGeometryReplay(ITestOutputHelper output)
{
    [Fact]
    public void TheRteTriangles()
    {
        if (Environment.GetEnvironmentVariable("MAPGEO") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        var packages = new List<Package>();
        foreach (var dir in new[] { "csgo", "core" })
        {
            var package = new Package();
            package.Read(Path.Combine(game, dir, "pak01_dir.vpk"));
            packages.Add(package);
        }
        var materials = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", parts[0])], packages);
        var meshes = MapMeshes.Read(DmxBinary.ReadFile(vmap));
        bool RendersAsWorld(string c) => schema.IsSolidClass(c) && schema.HasFlag(c, "render_as_world_but_physics_as_entity");
        var ours = MapGeometry.RteTriangles(meshes, m => materials[m], RendersAsWorld);
        var ownerOf = new List<MapMeshes.Mesh>();
        foreach (var mesh in meshes)
            ownerOf.AddRange(Enumerable.Repeat(mesh, MapGeometry.RteTriangles([mesh], m => materials[m], RendersAsWorld).Count));

        var rte = RayTraceEnvironment.ReadFile(parts[2]);
        var file = new Dictionary<string, Queue<int>>();
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            var key = Key(rte.FileRecord(i));
            if (!file.TryGetValue(key, out var q))
                file[key] = q = new Queue<int>();
            q.Enqueue(i);
        }
        var leftover = new Dictionary<string, int>();
        var missed = new List<MapGeometry.Triangle>();
        var found = 0;
        var degenerate = 0;
        Span<float> corners = stackalloc float[9];
        Span<float> record = stackalloc float[13];
        var missedBy = new Dictionary<MapMeshes.Mesh, int>();
        var all = new List<string>();
        for (var i = 0; i < ours.Count; i++)
        {
            var t = ours[i];
            corners[0] = t.A.X; corners[1] = t.A.Y; corners[2] = t.A.Z;
            corners[3] = t.B.X; corners[4] = t.B.Y; corners[5] = t.B.Z;
            corners[6] = t.C.X; corners[7] = t.C.Y; corners[8] = t.C.Z;
            var sound = RayTraceEnvironment.RecordFromCorners(corners, record);
            if (!sound)
                degenerate++;
            all.Add(FormattableString.Invariant($"{t.A.X} {t.A.Y} {t.A.Z} {t.B.X} {t.B.Y} {t.B.Z} {t.C.X} {t.C.Y} {t.C.Z} {t.Material} ") +
                    (sound && file.TryGetValue(Key(record), out var seen) && seen.Count > 0 ? "found" : "missed") + $" {ownerOf[i].NodeId}");
            if (sound && file.TryGetValue(Key(record), out var q) && q.Count > 0)
            {
                q.Dequeue();
                found++;
            }
            else
            {
                var m = Path.GetFileNameWithoutExtension(t.Material);
                leftover[m] = leftover.GetValueOrDefault(m) + 1;
                missed.Add(t);
                missedBy[ownerOf[i]] = missedBy.GetValueOrDefault(ownerOf[i]) + 1;
            }
        }
        if (Environment.GetEnvironmentVariable("MAPGEO_FACES") is { } faceMesh)
        {
            var target = meshes.First(m => m.NodeId.ToString() == faceMesh);
            var keys = file.Keys.ToHashSet();
            foreach (var face in target.Faces)
            {
                int[] tris = face.Corners.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate(face.Corners);
                var hits = 0;
                for (var k = 0; k < tris.Length; k += 3)
                {
                    var a = face.Corners[tris[k]]; var b = face.Corners[tris[k + 1]]; var cc = face.Corners[tris[k + 2]];
                    corners[0] = a.X; corners[1] = a.Y; corners[2] = a.Z; corners[3] = b.X; corners[4] = b.Y; corners[5] = b.Z;
                    corners[6] = cc.X; corners[7] = cc.Y; corners[8] = cc.Z;
                    if (RayTraceEnvironment.RecordFromCorners(corners, record) && keys.Contains(Key(record)))
                        hits++;
                }
                var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(face.Corners[1] - face.Corners[0], face.Corners[2] - face.Corners[0]));
                output.WriteLine($"  face {face.Corners.Length}-gon {Path.GetFileNameWithoutExtension(face.Material)} normal {n} first {face.Corners[0]}: {hits}/{tris.Length / 3}");
            }
        }
        if (Environment.GetEnvironmentVariable("MAPGEO_MESHES") is not null)
            foreach (var (mesh, n) in missedBy.OrderByDescending(kv => kv.Value))
                output.WriteLine($"  mesh {mesh.NodeId} under {mesh.ParentType} {mesh.ParentClass}: {n} of {ownerOf.Count(o => o == mesh)} missed; " +
                                 $"origin {mesh.Origin} angles {mesh.Angles} scales {mesh.Scales} faces {mesh.Faces.Length}");
        if (Environment.GetEnvironmentVariable("MAPGEO_DUMPALL") is { } dumpAll)
            File.WriteAllLines(dumpAll, all);
        if (Environment.GetEnvironmentVariable("MAPGEO_DUMP") is { } dump)
            File.WriteAllLines(dump, missed.Select(t => FormattableString.Invariant($"{t.A.X} {t.A.Y} {t.A.Z} {t.B.X} {t.B.Y} {t.B.Z} {t.C.X} {t.C.Y} {t.C.Z} {t.Material}")));
        foreach (var (m, n) in leftover.OrderByDescending(kv => kv.Value))
            output.WriteLine($"  ours not in the rte: {m} {n}");
        output.WriteLine($"ours {ours.Count}, found {found}, degenerate {degenerate}; rte {rte.TriangleCount}, not produced {rte.TriangleCount - found}");
        if (Environment.GetEnvironmentVariable("MAPGEO_GAPS") is not null)
        {
            var rebuilt = Enumerable.Range(0, rte.TriangleCount).Select(i => rte.Vertices(i)).Where(v => v is not null).ToList();
            var buckets = new SortedDictionary<string, int>();
            foreach (var t in missed)
            {
                System.Numerics.Vector3[] mine = [t.A, t.B, t.C];
                var gap = rebuilt.Min(v => Enumerable.Range(0, 3).Max(k => mine.Min(o => System.Numerics.Vector3.Distance(o, v![k]))));
                var bucket = gap < 0.01f ? "under 0.01" : gap < 1f ? "0.01 to 1" : gap < 10f ? "1 to 10" : "10 or more";
                buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
                if (gap < 0.01f && buckets[bucket] <= 5)
                    output.WriteLine($"    near miss {System.IO.Path.GetFileNameWithoutExtension(t.Material)}: {t.A} {t.B} {t.C} gap {gap:G3}");
            }
            foreach (var (b, n) in buckets)
                output.WriteLine($"  gap {b}: {n}");
        }
    }

    private static string Key(ReadOnlySpan<byte> r)
    {
        Span<float> f = stackalloc float[13];
        for (var k = 0; k < 11; k++)
            f[k] = BinaryPrimitives.ReadSingleLittleEndian(r[(k * 4)..]);
        f[11] = r[0x2c];
        f[12] = r[0x2d];
        return Key(f);
    }

    private static string Key(ReadOnlySpan<float> f)
    {
        var parts = new List<string>(12);
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 })
            parts.Add(BitConverter.SingleToInt32Bits(f[k]).ToString("x8"));
        return string.Join(",", parts);
    }
}
