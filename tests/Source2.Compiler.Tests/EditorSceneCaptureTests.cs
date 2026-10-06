using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>RAYSCENE=&lt;capture&gt;|&lt;addon&gt;|&lt;map&gt;</c>): the editor ray
/// scene captured by tools/entities/capture_rayscene.py against
/// <see cref="EditorTraceScene"/>, as world-space triangles keyed by their
/// centroid on a 1/64 grid: how many each side has that the other lacks,
/// by our instance and by Valve's child scene.
/// </summary>
public class EditorSceneCaptureTests(ITestOutputHelper output)
{
    private sealed record ValveInstance(ulong Parent, ulong Child, ulong Handle, float[] Matrix, uint Flags = 0);

    [Fact]
    public void AgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("RAYSCENE") is not { } spec)
            return;
        var p = spec.Split('|');
        var data = File.ReadAllBytes(p[0]);
        var triangles = new Dictionary<ulong, List<(Vector3 A, Vector3 B, Vector3 C, ushort Flags)>>();
        var instances = new List<ValveInstance>();
        var byHandle = new Dictionary<ulong, ValveInstance>();
        var live = new List<ValveInstance>();
        // Addresses are reused: a scene's key is its address and its generation
        // (bumped when one is made there), a triangle clear empties it.
        var generation = new Dictionary<ulong, ulong>();
        ulong Scene(ulong address) => address == 0 ? 0 : (address << 12) ^ generation.GetValueOrDefault(address);
        for (var at = 0; at + 96 <= data.Length; at += 96)
        {
            var kind = BitConverter.ToUInt32(data, at);
            ulong a = BitConverter.ToUInt64(data, at + 8), b = BitConverter.ToUInt64(data, at + 16), c = BitConverter.ToUInt64(data, at + 24);
            if (kind == 4)
            {
                generation[a] = generation.GetValueOrDefault(a) + 1;
                continue;
            }
            if (kind == 5)
            {
                triangles.Remove(Scene(a));
                continue;
            }
            if (kind is 1 or 2)
            {
                a = Scene(a);
                if (kind == 1)
                    b = Scene(b);
            }
            var f = new float[14];
            for (var i = 0; i < 14; i++)
                f[i] = BitConverter.ToSingle(data, at + 40 + (i * 4));
            if (kind == 2)
            {
                if (!triangles.TryGetValue(a, out var list))
                    triangles[a] = list = [];
                list.Add((new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]), new Vector3(f[6], f[7], f[8]), (ushort)c));
            }
            else if (kind == 1)
            {
                var inst = new ValveInstance(a, b, c, f[..12]);
                instances.Add(inst);
                byHandle[c] = inst;
            }
            else if (kind == 3 && byHandle.TryGetValue(b, out var moved))
                f[..12].CopyTo(moved.Matrix, 0);
            else if (kind == 8)
                live.Add(new ValveInstance(Scene(a), Scene(b), c, f[..12], (uint)BitConverter.ToUInt64(data, at + 32)));
        }
        // The scene as the first light ray found it (capture_rayscene.py's kind
        // 8), when the capture has it: instance handles are reused, so the
        // add/move history alone can leave a freed instance in place.
        if (live.Count > 0)
            instances = live;
        var childScenes = instances.Select(i => i.Child).ToHashSet();
        var tops = instances.Select(i => i.Parent).Where(x => !childScenes.Contains(x)).Distinct().ToList();
        var byParent = instances.GroupBy(i => i.Parent).ToDictionary(g => g.Key, g => g.ToList());
        output.WriteLine($"valve: {triangles.Values.Sum(l => l.Count)} triangles in {triangles.Count} scenes, {instances.Count} instances, top scenes {tops.Count}: "
                         + string.Join(", ", tops.Select(t => $"0x{t:x} ({byParent[t].Count})")));

        static Vector3 Apply(float[] m, Vector3 v) => new(m[0] * v.X + m[1] * v.Y + m[2] * v.Z + m[3], m[4] * v.X + m[5] * v.Y + m[6] * v.Z + m[7],
                                                          m[8] * v.X + m[9] * v.Y + m[10] * v.Z + m[11]);
        static float[] Compose(float[] a, float[] b) => MapMeshes.Concat(a, b);
        static (int, int, int) Key(Vector3 a, Vector3 b, Vector3 c)
        {
            var m = (a + b + c) / 3f;
            return ((int)MathF.Round(m.X * 64f), (int)MathF.Round(m.Y * 64f), (int)MathF.Round(m.Z * 64f));
        }

        // Valve's world triangles under one top scene, by the child scene they came from.
        var valveTris = new Dictionary<(int, int, int), string>();
        var valveFlags = new Dictionary<(int, int, int), ushort>();
        var valveNormal = new Dictionary<(int, int, int), Vector3>();
        // RAYSCENE_MASK=<hex>: only what a ray with that mask can hit, on both
        // sides: an object whose flag word meets it is skipped whole, and so
        // is a triangle whose own flags do (the light pass: c00060b1).
        var mask = Environment.GetEnvironmentVariable("RAYSCENE_MASK") is { Length: > 0 } mt ? Convert.ToUInt32(mt, 16) : 0u;
        void Walk(ulong scene, float[] m, string owner, int depth)
        {
            if (triangles.TryGetValue(scene, out var list))
                foreach (var (a, b, c, fl) in list)
                    if ((fl & mask) == 0)
                    {
                        var key = Key(Apply(m, a), Apply(m, b), Apply(m, c));
                        valveTris.TryAdd(key, owner);
                        valveFlags.TryAdd(key, fl);
                        valveNormal.TryAdd(key, Vector3.Cross(Apply(m, b) - Apply(m, a), Apply(m, c) - Apply(m, a)));
                    }
            if (depth < 4 && byParent.TryGetValue(scene, out var kids))
                foreach (var k in kids)
                    if ((k.Flags & mask) == 0)
                        Walk(k.Child, Compose(m, k.Matrix), depth == 0 ? $"0x{k.Child:x}" : owner, depth + 1);
        }
        var top = Environment.GetEnvironmentVariable("RAYSCENE_TOP") is { } t ? Convert.ToUInt64(t, 16) : tops.OrderByDescending(x => byParent[x].Count).First();
        Walk(top, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], "top", 0);

        // Ours, the same way.
        var source = MapFixtures.VmapSource(p[1], p[2])!;
        var document = MapSource.Read(source);
        var scene = (EditorTraceScene)SettleLumpTests.LightScene(document, source)!;
        var ourTris = new Dictionary<(int, int, int), string>();
        var ourFlags = new Dictionary<(int, int, int), ushort>();
        var ourNormal = new Dictionary<(int, int, int), Vector3>();
        var prefabLocal = new List<EditorTraceScene.Instance>();
        if (Environment.GetEnvironmentVariable("RAYSCENE_PREFABLOCAL") == "1" && CS2Fixtures.StockPak() is { } pak)
        {
            var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
            using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", p[1]));
            ushort Flags(string m) => TraceScene.MaterialFlags(content.Material(m));
            foreach (var prefab in document.OfType("CMapPrefab"))
                if (prefab.Attributes.GetValueOrDefault(MapPrefabs.DocumentKey) is DmxBinary.Document loaded)
                    prefabLocal.AddRange(EditorTraceScene.MapMeshInstances(MapMeshes.Read(loaded), Flags));
        }
        void Ours(EditorTraceScene.Instance inst, float[] m, string owner)
        {
            if ((inst.ObjectFlags & mask) != 0)
                return;
            if (inst.Children is { } kids)
            {
                foreach (var k in kids)
                    Ours(k, Compose(m, k.ToWorld), owner);
                return;
            }
            for (var i = 0; i < inst.Flags.Length; i++)
            {
                if ((inst.Flags[i] & mask) != 0)
                    continue;
                var r = inst.Records.AsSpan(i * 22 + 13, 9);
                var key = Key(Apply(m, new Vector3(r[0], r[1], r[2])), Apply(m, new Vector3(r[3], r[4], r[5])), Apply(m, new Vector3(r[6], r[7], r[8])));
                ourTris.TryAdd(key, owner);
                ourFlags.TryAdd(key, inst.Flags[i]);
                Vector3 pa = Apply(m, new Vector3(r[0], r[1], r[2])), pb = Apply(m, new Vector3(r[3], r[4], r[5])), pc = Apply(m, new Vector3(r[6], r[7], r[8]));
                ourNormal.TryAdd(key, Vector3.Cross(pb - pa, pc - pa));
            }
        }
        foreach (var inst in scene.Instances)
            Ours(inst, inst.ToWorld, inst.Source);
        // RAYSCENE_PREFABLOCAL=1: each prefab's own map meshes, unmoved, as a second copy.
        foreach (var inst in prefabLocal)
            Ours(inst, inst.ToWorld, "prefab-local " + inst.Source);
        output.WriteLine($"valve top 0x{top:x}: {valveTris.Count} distinct triangles; ours {ourTris.Count}");
        output.WriteLine($"our instances: {string.Join(", ", scene.Instances.GroupBy(i => i.Source.Split(' ')[0]).Select(g => $"{g.Key} {g.Count()}"))}");
        output.WriteLine($"valve top children: {byParent[top].Count(k => triangles.ContainsKey(k.Child))} with triangles, {byParent[top].Count(k => byParent.ContainsKey(k.Child))} with instances, {byParent[top].Count(k => !triangles.ContainsKey(k.Child) && !byParent.ContainsKey(k.Child))} empty");
        // RAYSCENE_CHILD=<hex scene>: that child scene's mesh scenes, each with its centre and the nearest
        // of our instances with as many triangles.
        if (Environment.GetEnvironmentVariable("RAYSCENE_CHILD") is { } childHex)
        {
            var child = Convert.ToUInt64(childHex, 16);
            var ourCentres = scene.Instances.Concat(prefabLocal).Where(i => i.Children is null && i.Flags.Length > 0).Select(i =>
            {
                var sum = Vector3.Zero;
                for (var k = 0; k < i.Flags.Length; k++)
                    for (var c = 0; c < 3; c++)
                        sum += Apply(i.ToWorld, new Vector3(i.Records[k * 22 + 13 + c * 3], i.Records[k * 22 + 14 + c * 3], i.Records[k * 22 + 15 + c * 3]));
                return (i.Source, Count: i.Flags.Length, Centre: sum / (3 * i.Flags.Length));
            }).ToList();
            foreach (var k in byParent[child].Where(k => triangles.ContainsKey(k.Child)).Take(12))
            {
                var list = triangles[k.Child];
                var sum = Vector3.Zero;
                foreach (var (a, b, c, _) in list)
                    sum += Apply(k.Matrix, a) + Apply(k.Matrix, b) + Apply(k.Matrix, c);
                var centre = sum / (3 * list.Count);
                var near = ourCentres.Where(o => o.Count == list.Count).OrderBy(o => (o.Centre - centre).LengthSquared()).FirstOrDefault();
                output.WriteLine($"  child mesh 0x{k.Child:x} tris {list.Count} centre {centre}; nearest ours {near.Source} {near.Centre} delta {near.Centre - centre}");
            }
        }
        // Triangles both sides hold whose flag words differ, by (Valve's, ours) and owner.
        var flagPairs = valveFlags.Where(kv => ourFlags.TryGetValue(kv.Key, out var f) && f != kv.Value)
            .GroupBy(kv => $"valve 0x{kv.Value:x} ours 0x{ourFlags[kv.Key]:x}").OrderByDescending(g => g.Count()).ToList();
        output.WriteLine($"shared triangles with other flags: {flagPairs.Sum(g => g.Count())}");
        foreach (var g in flagPairs.Take(10))
            output.WriteLine($"  {g.Key}: {g.Count()} e.g. {ourTris[g.First().Key]} at {g.First().Key}");
        // And those wound the other way (normals opposed), by owner kind.
        var flipped = valveNormal.Where(kv => ourNormal.TryGetValue(kv.Key, out var n) && Vector3.Dot(n, kv.Value) < 0).ToList();
        output.WriteLine($"shared triangles wound the other way: {flipped.Count}");
        foreach (var g in flipped.GroupBy(kv => ourTris[kv.Key]).OrderByDescending(g => g.Count()).Take(10))
            output.WriteLine($"  flipped {g.Key}: {g.Count()} e.g. at {g.First().Key}");
        var valveOnly = valveTris.Where(kv => !ourTris.ContainsKey(kv.Key)).ToList();
        var oursOnly = ourTris.Where(kv => !valveTris.ContainsKey(kv.Key)).ToList();
        output.WriteLine($"valve only {valveOnly.Count}, ours only {oursOnly.Count}");
        output.WriteLine($"ours only by kind: {string.Join(", ", oursOnly.GroupBy(kv => kv.Value.Split(' ')[0]).Select(g => $"{g.Key} {g.Count()} in {g.Select(x => x.Value).Distinct().Count()}"))}");
        output.WriteLine($"ours by kind: {string.Join(", ", ourTris.GroupBy(kv => kv.Value.Split(' ')[0]).Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var inst in instances.Where(i => i.Parent == top && triangles.TryGetValue(i.Child, out var l) && l.Count > 1000))
        {
            var l = triangles[inst.Child];
            output.WriteLine($"  big child 0x{inst.Child:x}: {l.Count} triangles, matrix {string.Join(" ", inst.Matrix.Select(x => x.ToString("G6")))}, first {l[0].A} {l[0].B} {l[0].C} flags 0x{l[0].Flags:x}");
        }
        foreach (var g in oursOnly.Where(kv => !kv.Value.StartsWith("prop")).GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).Take(25))
            output.WriteLine($"  ours only: {g.Key} x{g.Count()} e.g. {g.First().Key}");
        foreach (var g in valveOnly.GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).Take(25))
            output.WriteLine($"  valve only: {g.Key} x{g.Count()} e.g. {g.First().Key}");
    }
}
