using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The settle's physics world built from atixref's map and models
/// (<see cref="SettleWorld"/>) against the bundle capture's "build" events and
/// step-0 world: per map node, its bodies' transforms and their shapes'
/// geometry, scale, collision attributes and materials.
/// Set SETTLE to the bundle's events.jsonl; the map is read from the CS2
/// content addon s2c_rc_probe and the models from the game's pak01.
/// </summary>
public sealed class SettleBuildTests(ITestOutputHelper output)
{
    /// <summary>The map the bundle compiled (SETTLE_VMAP, else atixref) and its addon's game folder (SETTLE_ADDON).</summary>
    private static string Vmap => Environment.GetEnvironmentVariable("SETTLE_VMAP")
        ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\atixref.vmap";

    private static string AddonGame => Environment.GetEnvironmentVariable("SETTLE_ADDON")
        ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    /// <summary>A captured shape: the build event's kind and its world dump entry.</summary>
    internal sealed record CapturedShape(string Kind, byte[] Head, Dictionary<string, byte[]> Raw, byte[]? Scale);

    internal sealed record CapturedBody(byte[] Xf, int Type, List<CapturedShape> Shapes)
    {
        /// <summary>The body's rn pointer, which names it in the step-0 world dump.</summary>
        public string Rn { get; init; } = "";

        /// <summary>Its RnBodyState in the step-0 world dump.</summary>
        public byte[] State { get; set; } = [];

        /// <summary>Its place among the SetType(2) calls, or -1 when it stays static.</summary>
        public int DynamicOrder { get; set; } = -1;
    }

    internal sealed record CapturedObject(int Node, List<CapturedBody> Bodies);

    /// <summary>The build sequence, per object, with each shape joined to its step-0 dump through its pointer.</summary>
    internal static List<CapturedObject> ReadBuild(string path)
    {
        var objects = new List<CapturedObject>();
        var pending = new List<(CapturedBody Body, List<(string Kind, string Ptr, byte[]? Scale)> Shapes)>();
        var byWrapper = new Dictionary<string, (CapturedBody Body, List<(string, string, byte[]?)> Shapes)>();
        var dynamicOrder = new Dictionary<string, int>();
        Dictionary<string, JsonElement>? shapes = null;
        JsonDocument? world = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("{\"ev\": \"world\""))
            {
                world = JsonDocument.Parse(line);
                break;
            }
            if (!line.Contains("\"build\""))
                continue;
            using var d = JsonDocument.Parse(line);
            var e = d.RootElement;
            var kind = e.GetProperty("kind").GetString()!;
            if (kind == "object")
            {
                objects.Add(new CapturedObject(e.GetProperty("node").GetInt32(), []));
                continue;
            }
            var w = e.GetProperty("wrapper").GetString()!;
            switch (kind)
            {
                case "body":
                    var body = new CapturedBody([], 0, []) { Rn = e.GetProperty("rn").GetString()! };
                    objects[^1].Bodies.Add(body);
                    byWrapper[w] = (body, []);
                    pending.Add(byWrapper[w]);
                    break;
                case "xf":
                    var (b, s) = byWrapper[w];
                    byWrapper[w] = (b with { Xf = Convert.FromHexString(e.GetProperty("xf").GetString()!) }, s);
                    ReplaceBody(objects, b, byWrapper[w].Body);
                    pending[pending.FindIndex(p => p.Body == b)] = byWrapper[w];
                    break;
                case "type" when e.GetProperty("type").GetInt32() == 2:
                    dynamicOrder[byWrapper[w].Body.Rn] = dynamicOrder.Count;
                    break;
                case "hull" or "mesh":
                    byWrapper[w].Shapes.Add((kind, e.GetProperty("shape").GetString()!,
                                             e.TryGetProperty("scale", out var sc) ? Convert.FromHexString(sc.GetString()!) : null));
                    break;
            }
        }
        if (world == null)
            return objects;
        shapes = [];
        var states = new Dictionary<string, byte[]>();
        foreach (var b in world!.RootElement.GetProperty("bodies").EnumerateArray())
            if (b.TryGetProperty("ptr", out var bp))
                states[bp.GetString()!] = Convert.FromHexString(b.GetProperty("body").GetString()!);
        foreach (var o in objects)
            foreach (var b in o.Bodies)
            {
                b.State = states.GetValueOrDefault(b.Rn, []);
                b.DynamicOrder = dynamicOrder.GetValueOrDefault(b.Rn, -1);
            }
        foreach (var b in world!.RootElement.GetProperty("bodies").EnumerateArray())
            foreach (var s in b.GetProperty("shapes").EnumerateArray())
                if (s.TryGetProperty("ptr", out var p))
                    shapes[p.GetString()!] = s.Clone();
        foreach (var (body, list) in pending)
            foreach (var (kind, ptr, scale) in list)
            {
                var s = shapes[ptr];
                var raw = new Dictionary<string, byte[]>();
                foreach (var prop in s.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Name is not ("head" or "ptr"))
                        raw[prop.Name] = Convert.FromHexString(prop.Value.GetString()!);
                body.Shapes.Add(new CapturedShape(kind, Convert.FromHexString(s.GetProperty("head").GetString()!), raw, scale));
            }
        return objects;
    }

    private static void ReplaceBody(List<CapturedObject> objects, CapturedBody old, CapturedBody now)
    {
        for (var i = objects.Count - 1; i >= 0; i--)
        {
            var k = objects[i].Bodies.IndexOf(old);
            if (k >= 0)
            {
                objects[i].Bodies[k] = now;
                return;
            }
        }
    }

    /// <summary>Models from the game's pak01, by path, each read once.</summary>
    internal sealed class PakModels : SettleWorld.IModels, IDisposable
    {
        private readonly Package _pak = new();
        private readonly Dictionary<string, PhysAggregateData?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Resource> _keep = [];

        private Dictionary<uint, Simulation.ContactSolver.Material>? _surfaces;

        private readonly Package? _core;
        private readonly string[] _loose;
        private readonly Dictionary<string, SettleWorld.MaterialInfo?> _materials = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SettleWorld.CollisionProperty>? _collision;

        /// <summary>The stock csgo pak, core's pak beside it, and loose files under the given game folders.</summary>
        public PakModels(string pak, params string[] loose)
        {
            _pak.Read(pak);
            var core = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..", "core", "pak01_dir.vpk"));
            if (File.Exists(core))
            {
                _core = new Package();
                _core.Read(core);
            }
            _loose = loose;
        }

        private byte[]? Read(string path)
        {
            path = path.Replace((char)92, '/');
            foreach (var dir in _loose)
                if (File.Exists(Path.Combine(dir, path)))
                    return File.ReadAllBytes(Path.Combine(dir, path));
            foreach (var pak in new[] { _pak, _core })
                if (pak?.FindEntry(path) is { } entry)
                {
                    pak.ReadEntry(entry, out var bytes);
                    return bytes;
                }
            return null;
        }

        public SettleWorld.MaterialInfo? Material(string path)
        {
            if (_materials.TryGetValue(path, out var found))
                return found;
            if (Read(path + "_c") is not { } bytes)
                return _materials[path] = null;
            using var res = new Resource();
            res.Read(new MemoryStream(bytes));
            var mat = (Material)res.DataBlock!;
            return _materials[path] = new SettleWorld.MaterialInfo(
                mat.IntAttributes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                mat.StringAttributes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
        }

        public SettleWorld.CollisionProperty? CollisionProperty(string name)
        {
            if (_collision == null)
            {
                var text = System.Text.Encoding.UTF8.GetString(Read("scripts/collision_properties.txt") ?? []);
                _collision = new(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\{([^{}]*)\}"))
                {
                    var body = m.Groups[1].Value;
                    string Str(string key) => System.Text.RegularExpressions.Regex.Match(body, key + @"\s*=\s*""([^""]*)""").Groups[1].Value;
                    string List(string key) => string.Join(", ", System.Text.RegularExpressions.Regex.Matches(
                        System.Text.RegularExpressions.Regex.Match(body, key + @"\s*=\s*\[([^\]]*)\]").Groups[1].Value, @"""([^""]*)""").Select(x => x.Groups[1].Value));
                    if (Str("name") is { Length: > 0 } n)
                        _collision[n] = new SettleWorld.CollisionProperty(Str("collision_group"), List("interact_as"), List("interact_with"), List("interact_exclude"));
                }
            }
            return _collision.GetValueOrDefault(name);
        }

        public Simulation.ContactSolver.Material? Surface(uint nameHash)
        {
            if (_surfaces == null)
            {
                _pak.ReadEntry(_pak.FindEntry("surfaceproperties/surfaceproperties.vsurf_c")!, out var bytes);
                using var res = new Resource();
                res.Read(new MemoryStream(bytes));
                _surfaces = SettleWorld.SurfaceMaterials(((BinaryKV3)res.DataBlock!).Data);
            }
            return _surfaces.TryGetValue(nameHash, out var m) ? m : null;
        }

        private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);

        public ValveKeyValue.KVObject? ModelKeyValues(string model)
        {
            Physics(model);
            return _models.GetValueOrDefault(model)?.KeyValues;
        }

        public PhysAggregateData? Physics(string model)
        {
            if (_cache.TryGetValue(model, out var phys))
                return phys;
            var entry = _pak.FindEntry(Path.ChangeExtension(model, ".vmdl_c"));
            if (entry != null)
            {
                _pak.ReadEntry(entry, out var bytes);
                var res = new Resource();
                res.Read(new MemoryStream(bytes));
                _keep.Add(res);
                _models[model] = res.DataBlock as Model;
                phys = (res.DataBlock as Model)?.GetEmbeddedPhys();
            }
            return _cache[model] = phys;
        }

        public void Dispose()
        {
            foreach (var r in _keep)
                r.Dispose();
            _pak.Dispose();
            _core?.Dispose();
        }
    }

    [Fact]
    public void BuildMatchesTheCapture()
    {
        if (Environment.GetEnvironmentVariable("SETTLE") is not { Length: > 0 } path || !File.Exists(path)
            || CS2Fixtures.StockPak() is not { } pak || !File.Exists(Vmap))
            return;
        var captured = ReadBuild(path);
        var byNode = new Dictionary<int, CapturedObject>();
        foreach (var o in captured)
            byNode.TryAdd(o.Node, o);
        using var models = new PakModels(pak, AddonGame);
        var document = DmxBinary.ReadFile(Vmap);
        var bodies = SettleWorld.Build(document, models, MapFixtures.GameSchema(), SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));

        var counts = new Dictionary<string, int>();
        void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
        var examples = new Dictionary<string, string>();
        void Example(string what, string text) => examples.TryAdd(what, text);
        var seen = new Dictionary<int, int>();
        foreach (var body in bodies)
        {
            var k = seen[body.NodeId] = seen.GetValueOrDefault(body.NodeId);
            seen[body.NodeId]++;
            if (!byNode.TryGetValue(body.NodeId, out var obj) || k >= obj.Bodies.Count)
            {
                var node = document.Elements.First(e => e.GetValue<int>("nodeID") == body.NodeId && e.Type.StartsWith("CMap"));
                var hidden = MapEntities.HiddenNodes(document).Contains(body.NodeId);
                Count($"body not in the capture: {node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")}{(hidden ? " (hidden)" : "")}");
                continue;
            }
            var cb = obj.Bodies[k];
            Count("bodies");
            if (cb.State.Length >= 0x280)
                foreach (var (field, same) in MassDifferences(body, cb))
                    if (same)
                        Count($"mass update exact: {field}");
                    else
                    {
                        Count($"mass update differs: {field}");
                        Example($"mass {field}", $"node {body.NodeId} type {BitConverter.ToInt32(cb.State, 0x54)}");
                    }
            var xf = Xf(body);
            if (xf.AsSpan().SequenceEqual(cb.Xf))
                Count("transform exact");
            else
                Count("transform differs in " + string.Join("+", new[] { ("position", 0, 12), ("w", 12, 4), ("quaternion", 16, 16) }
                    .Where(f => !xf.AsSpan(f.Item2, f.Item3).SequenceEqual(cb.Xf.AsSpan(f.Item2, f.Item3))).Select(f => f.Item1)));
            if (!xf.AsSpan().SequenceEqual(cb.Xf))
                Example("transform", $"node {body.NodeId}: ours {Convert.ToHexString(xf)} valve {Convert.ToHexString(cb.Xf)}");
            // The build log names hulls and meshes only; capsules are compared by the mass update.
            var listed = body.Shapes.Where(x => x.Type != SettleWorld.CapsuleType).ToList();
            if (listed.Count != cb.Shapes.Count || !listed.Select(s => s.Type).SequenceEqual(cb.Shapes.Select(s => s.Kind == "hull" ? 2 : 3)))
            {
                Example("shape list", $"node {body.NodeId}: ours {string.Join(",", listed.Select(s => s.Type))} valve {string.Join(",", cb.Shapes.Select(s => s.Kind))}");
                continue;
            }
            Count("shape list same");
            for (var i = 0; i < listed.Count; i++)
            {
                var ours = listed[i];
                var theirs = cb.Shapes[i];
                Count("shapes");
                var geometry = ours.Type == 2 ? HullDifference(ours.Hull!, theirs.Raw) : MeshDifference(ours.Mesh!, theirs.Raw);
                if (geometry == null)
                    Count("geometry exact");
                else
                {
                    Count($"geometry differs: {geometry.Split(':')[0]}");
                    Example($"geometry {geometry.Split(':')[0]}", $"node {body.NodeId} shape {i}: {geometry}");
                }
                var scale = ours.Type == 2 ? BitConverter.GetBytes(ours.HullScale) : Bytes(ours.MeshScale);
                if (scale.AsSpan().SequenceEqual(theirs.Head.AsSpan(0xb8, scale.Length)))
                    Count("scale exact");
                var attributes = Raw(ours.Attributes);
                if (attributes.AsSpan().SequenceEqual(theirs.Head.AsSpan(0x50, 0x28)))
                    Count("attributes exact");
                else
                {
                    Count($"attributes differ: ours {Convert.ToHexString(attributes)} valve {Convert.ToHexString(theirs.Head, 0x50, 0x28)}");
                    Example("attributes", $"node {body.NodeId}: ours {Convert.ToHexString(attributes)} valve {Convert.ToHexString(theirs.Head, 0x50, 0x28)}");
                }
                var material = Raw(ours.Material);
                if (material.AsSpan().SequenceEqual(theirs.Head.AsSpan(0x20, material.Length)))
                    Count("material exact");
                else
                {
                    Count($"material differs: ours {Convert.ToHexString(material)} valve {Convert.ToHexString(theirs.Head, 0x20, material.Length)}");
                    Example("material", $"node {body.NodeId}: ours {Convert.ToHexString(material)} valve {Convert.ToHexString(theirs.Head, 0x20, material.Length)}");
                }
            }
        }
        // Which bodies the settle makes dynamic, against the step-0 body types.
        var dynOurs = SettleWorld.Settled(document, bodies, models, MapFixtures.GameSchema()!)
            .Select(i => (bodies[i].NodeId, bodies.Take(i).Count(b => b.NodeId == bodies[i].NodeId))).ToHashSet();
        var dynValve = captured.SelectMany(o => o.Bodies.Select((b, k) => (o.Node, k, b)))
            .Where(x => x.b.State.Length >= 0x58 && BitConverter.ToInt32(x.b.State, 0x54) == 2).Select(x => (x.Node, x.k)).ToHashSet();
        var valveOrder = captured.SelectMany(o => o.Bodies.Select((b, k) => (o.Node, k, b))).Where(x => x.b.DynamicOrder >= 0)
            .OrderBy(x => x.b.DynamicOrder).Select(x => (x.Node, x.k)).ToList();
        var ourDynamic = SettleWorld.Settled(document, bodies, models, MapFixtures.GameSchema()!)
            .Select(i => (bodies[i].NodeId, bodies.Take(i).Count(b => b.NodeId == bodies[i].NodeId))).ToList();
        output.WriteLine($"SetType(2) order: same {ourDynamic.SequenceEqual(valveOrder)}; valve's nodes {string.Join(",", valveOrder.Select(x => x.Node))}");
        output.WriteLine($"                   ours {string.Join(",", ourDynamic.Select(x => x.NodeId))}");
        output.WriteLine($"dynamic: ours {dynOurs.Count}, valve {dynValve.Count}, both {dynOurs.Intersect(dynValve).Count()}; only ours {string.Join(",", dynOurs.Except(dynValve).Take(10))}; only valve {string.Join(",", dynValve.Except(dynOurs).Take(10))}");
        // Build order: the capture's body order against ours, as the share of
        // our consecutive pairs that are consecutive there too.
        var capturedOrder = captured.SelectMany(o => o.Bodies.Select((b, k) => (o.Node, k))).ToList();
        var position = capturedOrder.Select((x, i) => (x, i)).ToDictionary(t => t.x, t => t.i);
        var ourOrder = bodies.Select((b, i) => (b.NodeId, bodies.Take(i).Count(x => x.NodeId == b.NodeId))).ToList();
        var adjacent = ourOrder.Zip(ourOrder.Skip(1)).Count(p => position.TryGetValue(p.First, out var a) && position.TryGetValue(p.Second, out var c) && c == a + 1);
        output.WriteLine($"order: {adjacent} of {ourOrder.Count - 1} consecutive pairs as in the capture; same sequence {ourOrder.SequenceEqual(capturedOrder)}");
        var missing = captured.Where(o => !seen.ContainsKey(o.Node)).ToList();
        output.WriteLine($"{captured.Count} captured objects ({captured.Sum(o => o.Bodies.Count)} bodies); ours {bodies.Count} bodies; {missing.Count} captured objects we do not build");
        foreach (var o in missing)
        {
            var node = document.Elements.FirstOrDefault(e => e.GetValue<int>("nodeID") == o.Node && e.Type.StartsWith("CMap"));
            Count($"not built: {node?.Type ?? "not in file"} with {string.Join(",", o.Bodies.Select(b => b.Shapes.Count == 0 ? "none" : string.Join("", b.Shapes.Select(x => x.Kind[0]))).Distinct().Take(3))}");
        }
        foreach (var (k, v) in counts.OrderBy(x => x.Key))
            output.WriteLine($"  {k}: {v}");
        foreach (var (k, v) in examples)
            output.WriteLine($"  first {k}: {v}");
    }

    /// <summary>
    /// The mass update run on the captured step-0 state with its mass fields
    /// cleared and our shapes, field by field against the capture.
    /// </summary>
    private static IEnumerable<(string Field, bool Same)> MassDifferences(SettleWorld.BodyBuild body, CapturedBody cb)
    {
        var captured = MemoryMarshal.Read<Simulation.RnBodyState>(cb.State);
        var b = captured;
        b.Flags4A = (ushort)(b.Flags4A & ~0x700);
        b.InertiaDivisor = 0;
        b.LocalInvInertia = default;
        b.LocalMassCenter = default;
        b.InvMass = 0;
        b.WorldInvInertia = default;
        b.InnerRadius = 0;
        b.OuterRadius = 0;
        var q = new Simulation.Quat { X = body.Orientation.X, Y = body.Orientation.Y, Z = body.Orientation.Z, W = body.Orientation.W };
        b.Orientation = q;
        var shapes = body.Shapes.Select(s => new Simulation.RnMassUpdate.Shape(s.Type, s.Hull, s.HullScale, s.Mesh, s.MeshScale, s.Material,
            (((s.Attributes.MaskIsDirect == 1 ? s.Attributes.FunctionMask : ~s.Attributes.FunctionMask)) & 1) != 0) { Capsule = s.Capsule }).ToList();
        Simulation.RnMassUpdate.Run(ref b, shapes, new Simulation.Vec3(body.Position.X, body.Position.Y, body.Position.Z), q);
        byte[] Of(in Simulation.RnBodyState x, int at, int n) => MemoryMarshal.AsBytes(new ReadOnlySpan<Simulation.RnBodyState>(in x)).Slice(at, n).ToArray();
        foreach (var (name, at, n) in new[] { ("mass", 0xa0, 4), ("local inverse inertia", 0xa4, 36), ("mass centre", 0xc8, 12), ("inverse mass", 0xd4, 4),
                                               ("world inverse inertia", 0xd8, 36), ("position", 0xfc, 12), ("previous position", 0x1dc, 12),
                                               ("radii", 0x1ec, 8) })
        {
            var same = Of(b, at, n).AsSpan().SequenceEqual(cb.State.AsSpan(at, n));
            yield return (name, same);
        }
    }

    /// <summary>The 0x20 bytes body vfn 0x1b8 takes: position, scale, quaternion.</summary>
    private static byte[] Xf(SettleWorld.BodyBuild b)
    {
        float[] v = [b.Position.X, b.Position.Y, b.Position.Z, b.Scale, b.Orientation.X, b.Orientation.Y, b.Orientation.Z, b.Orientation.W];
        return MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    }

    /// <summary>The first field of an RnHull_t (header and arrays) that differs from the dump.</summary>
    private static string? HullDifference(RnHull h, Dictionary<string, byte[]> raw)
    {
        var header = raw["hull"];
        var fields = new (string Name, int Offset, byte[] Ours)[]
        {
            ("centroid", 0, Bytes(h.Centroid)), ("max angular radius", 0xc, BitConverter.GetBytes(h.MaxAngularRadius)),
            ("min centroid radius", 0x10, BitConverter.GetBytes(h.MinCentroidRadius)),
            ("bounds", 0x14, [.. Bytes(h.BoundsMin), .. Bytes(h.BoundsMax)]),
            ("orthographic areas", 0x2c, Bytes(h.OrthographicAreas)),
            ("mass properties", 0x38, MemoryMarshal.AsBytes(h.MassProperties.AsSpan()).ToArray()),
            ("volume", 0x68, BitConverter.GetBytes(h.Volume)), ("surface area", 0x6c, BitConverter.GetBytes(h.SurfaceArea)),
            ("flags", 0xa0, BitConverter.GetBytes(h.Flags)),
        };
        foreach (var (name, offset, ours) in fields)
            if (!ours.AsSpan().SequenceEqual(header.AsSpan(offset, ours.Length)))
                return $"{name}: ours {Convert.ToHexString(ours)} valve {Convert.ToHexString(header, offset, ours.Length)}";
        var arrays = new (string Name, byte[] Ours)[]
        {
            ("pos", h.VertexPositions.SelectMany(Bytes).ToArray()),
            ("planes", h.Planes.SelectMany(p => (byte[])[.. Bytes(p.Normal), .. BitConverter.GetBytes(p.Offset)]).ToArray()),
            ("verts", h.Vertices), ("edges", h.Edges.SelectMany(e => (byte[])[e.Next, e.Twin, e.Origin, e.Face]).ToArray()),
            ("faces", h.Faces),
        };
        foreach (var (name, ours) in arrays)
            if (!ours.AsSpan().SequenceEqual(raw[name]))
            {
                if (name == "mverts" && ours.Length == raw[name].Length)
                {
                    var a = MemoryMarshal.Cast<byte, Vector3>(ours).ToArray();
                    var b = MemoryMarshal.Cast<byte, Vector3>(raw[name]).ToArray();
                    var same = a.ToHashSet().SetEquals(b);
                    var first = Enumerable.Range(0, a.Length).First(i => a[i] != b[i]);
                    return $"{name}: {(same ? "same set" : "different set")}, first at {first}/{a.Length}: ours {a[first]} valve {b[first]}";
                }
                return $"{name}: {ours.Length} bytes, valve {raw[name].Length}";
            }
        return null;
    }

    private static string? MeshDifference(RnMesh m, Dictionary<string, byte[]> raw)
    {
        var header = raw["mesh"];
        if (!Bytes(m.Min).AsSpan().SequenceEqual(header.AsSpan(0, 12)) || !Bytes(m.Max).AsSpan().SequenceEqual(header.AsSpan(12, 12)))
            return "bounds";
        var arrays = new (string Name, byte[] Ours)[]
        {
            ("nodes", m.Nodes.SelectMany(n => (byte[])[.. Bytes(n.Min), .. BitConverter.GetBytes(n.Children), .. Bytes(n.Max), .. BitConverter.GetBytes(n.TriangleOffset)]).ToArray()),
            ("mverts", m.Vertices.SelectMany(Bytes).ToArray()),
            ("tris", m.Triangles.SelectMany(t => (byte[])[.. BitConverter.GetBytes(t.A), .. BitConverter.GetBytes(t.B), .. BitConverter.GetBytes(t.C)]).ToArray()),
            ("mats", m.Materials),
        };
        foreach (var (name, ours) in arrays)
            if (!ours.AsSpan().SequenceEqual(raw[name]))
            {
                if (name == "mverts" && ours.Length == raw[name].Length)
                {
                    var a = MemoryMarshal.Cast<byte, Vector3>(ours).ToArray();
                    var b = MemoryMarshal.Cast<byte, Vector3>(raw[name]).ToArray();
                    var same = a.ToHashSet().SetEquals(b);
                    var first = Enumerable.Range(0, a.Length).First(i => a[i] != b[i]);
                    return $"{name}: {(same ? "same set" : "different set")}, first at {first}/{a.Length}: ours {a[first]} valve {b[first]}";
                }
                return $"{name}: {ours.Length} bytes, valve {raw[name].Length}";
            }
        return null;
    }

    private static byte[] Raw<T>(T value) where T : unmanaged => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();

    private static byte[] Bytes(Vector3 v) => [.. BitConverter.GetBytes(v.X), .. BitConverter.GetBytes(v.Y), .. BitConverter.GetBytes(v.Z)];
}
