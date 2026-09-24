using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Tests;

/// <summary>
/// A settle world captured from a live compile (tools/settle, probe5.js): at
/// the first step, every body (0x280 bytes, in world +0x678 order) with every
/// shape (the 0x110-byte CRnShape header, the hull or mesh arrays) and the
/// world's first 0xc00 bytes. <see cref="Build"/> turns it into an
/// <see cref="RnWorld"/> that can step.
/// </summary>
internal sealed class SettleCapture
{
    /// <summary>A shape: its CRnShape header, and for a hull or mesh the header and arrays as captured (by name, raw).</summary>
    public sealed record Shape(int Id, int Type, byte[] Head, RnHull? Hull, RnMesh? Mesh, Dictionary<string, byte[]> Raw);

    public sealed record Body(byte[] State, Shape[] Shapes);

    public readonly List<Body> Bodies = [];
    public byte[] Head = [];

    /// <summary>
    /// From a bundle capture's "build" events, per body: the transform the
    /// settle gave it at creation (pos, scale, quat; null for the world's own
    /// first body), and the bodies turned dynamic, in the order it did so.
    /// </summary>
    public readonly List<(Vec3 Position, float Scale, Quat Orientation)?> Spawn = [];
    public readonly List<int> MadeDynamic = [];

    /// <summary>Dynamic bodies captured before a step ("bodies" events), and after the last ("bodies_end").</summary>
    public readonly SortedDictionary<int, List<(int Index, byte[] State)>> BodiesAt = [];
    public List<(int Index, byte[] State)>? BodiesEnd;

    /// <summary>Reads an events.jsonl: the world, and when present the build sequence and the body snapshots.</summary>
    public static SettleCapture Read(string path)
    {
        SettleCapture? capture = null;
        var wrappers = new List<string>();
        var xf = new Dictionary<string, byte[]>();
        var shapeWrapper = new Dictionary<string, string>();
        var types = new List<(string Wrapper, int Type)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Contains("\"build\""))
            {
                using var b = JsonDocument.Parse(line);
                var e = b.RootElement;
                switch (e.GetProperty("kind").GetString())
                {
                    case "body":
                        wrappers.Add(e.GetProperty("wrapper").GetString()!);
                        break;
                    case "xf":
                        xf[e.GetProperty("wrapper").GetString()!] = Convert.FromHexString(e.GetProperty("xf").GetString()!);
                        break;
                    case "hull" or "mesh":
                        shapeWrapper[e.GetProperty("shape").GetString()!] = e.GetProperty("wrapper").GetString()!;
                        break;
                    case "type":
                        types.Add((e.GetProperty("wrapper").GetString()!, int.Parse(e.GetProperty("type").ToString())));
                        break;
                }
                continue;
            }
            if (capture != null && (line.StartsWith("{\"ev\": \"bodies") || line.StartsWith("{\"ev\":\"bodies")))
            {
                using var b = JsonDocument.Parse(line);
                var e = b.RootElement;
                var list = JsonDocument.Parse(e.GetProperty("bodies").ValueKind == JsonValueKind.String
                        ? e.GetProperty("bodies").GetString()!.Replace('\'', '"') : e.GetProperty("bodies").GetRawText())
                    .RootElement.EnumerateArray().Select(x => (x[0].GetInt32(), Convert.FromHexString(x[1].GetString()!))).ToList();
                if (e.GetProperty("ev").GetString() == "bodies_end")
                    capture.BodiesEnd = list;
                else
                    capture.BodiesAt[int.Parse(e.GetProperty("step").ToString())] = list;
                continue;
            }
            if (capture != null || (!line.StartsWith("{\"ev\": \"world\"") && !line.StartsWith("{\"ev\":\"world\"")))
                continue;
            using var doc = JsonDocument.Parse(line);
            var world = doc.RootElement;
            capture = new SettleCapture { Head = Convert.FromHexString(world.GetProperty("head").GetString()!) };
            capture.ReadWorld(world);
            capture.ReadBuild(world, wrappers, xf, shapeWrapper, types);
        }
        return capture ?? throw new InvalidDataException("no world event");
    }

    /// <summary>
    /// Matches the build sequence to the world's bodies through the shapes'
    /// pointers: the world's body i + 1 is the i-th body the settle created
    /// (body 0 is the world's own).
    /// </summary>
    private void ReadBuild(JsonElement world, List<string> wrappers, Dictionary<string, byte[]> xf,
                           Dictionary<string, string> shapeWrapper, List<(string Wrapper, int Type)> types)
    {
        if (wrappers.Count == 0)
            return;
        var index = new Dictionary<string, int>();
        for (var i = 0; i < wrappers.Count; i++)
            index[wrappers[i]] = i + 1;
        var bodies = world.GetProperty("bodies").EnumerateArray().ToList();
        for (var i = 0; i < bodies.Count; i++)
            foreach (var s in bodies[i].GetProperty("shapes").EnumerateArray())
                if (s.TryGetProperty("ptr", out var ptr) && shapeWrapper.TryGetValue(ptr.GetString()!, out var w) && index[w] != i)
                    throw new InvalidDataException($"world body {i} was created as body {index[w]}");
        Spawn.Add(null);
        foreach (var w in wrappers)
        {
            var x = xf[w];
            Spawn.Add((MemoryMarshal.Read<Vec3>(x), BitConverter.ToSingle(x, 12), MemoryMarshal.Read<Quat>(x.AsSpan(16))));
        }
        foreach (var (w, type) in types)
            if (type == 2)
                MadeDynamic.Add(index[w]);
    }

    private void ReadWorld(JsonElement e)
    {
        foreach (var b in e.GetProperty("bodies").EnumerateArray())
        {
            var shapes = b.GetProperty("shapes").EnumerateArray().Select(s =>
            {
                var type = s.GetProperty("type").GetInt32();
                var raw = new Dictionary<string, byte[]>();
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Name is not ("head" or "ptr"))
                        raw[p.Name] = Convert.FromHexString(p.Value.GetString()!);
                return new Shape(s.GetProperty("id").GetInt32(), type, Convert.FromHexString(s.GetProperty("head").GetString()!),
                                 type == 2 ? Hull(s) : null, type == 3 ? Mesh(s) : null, raw);
            }).ToArray();
            Bodies.Add(new Body(Convert.FromHexString(b.GetProperty("body").GetString()!), shapes));
        }
    }

    /// <summary>What <see cref="Build"/> found; <c>DerivedOriginsOff</c> counts spawn origins that centre minus turned mass centre does not give back exactly.</summary>
    public sealed record BuildReport(int Shapes, int Mismatches, string? FirstMismatch, int Capsules, int DerivedOriginsOff);

    /// <summary>
    /// The world as a port <see cref="RnWorld"/>, built through its creation
    /// calls the way the settle built it: every body created static at its
    /// spawn frame with its shapes, in the captured order; then the settled
    /// bodies made dynamic (in the build events' order, or the one their
    /// tree 2 leaves show); then their captured state (the mass update is not
    /// ported) and the awake list. Proxy ids, body indices, nodes and awake
    /// indices are checked against the capture.
    /// </summary>
    public RnWorld Build(ushort[] groupTable, ushort collidingMask, out BuildReport report)
    {
        var w = new RnWorld();
        var h = Head;
        w.Gravity = MemoryMarshal.Read<Vec3>(h.AsSpan(0x190));
        w.AirDensity = BitConverter.ToSingle(h, 0x1a0);
        w.Continuous = BitConverter.ToInt32(h, 0x1a4) == 1 && BitConverter.ToInt32(h, 0x1c0) > 0;
        w.Threads = BitConverter.ToInt32(h, 0x1ac);
        w.Priority = h[0x1b0];
        w.MaxCoordinate = BitConverter.ToSingle(h, 0x1b4);
        w.PositionIterations = BitConverter.ToInt32(h, 0x1b8);
        w.VelocityIterations = BitConverter.ToInt32(h, 0x1bc);
        w.Time = BitConverter.ToSingle(h, 0x1cc);
        w.FrameTime = BitConverter.ToSingle(h, 0x1d0);
        w.StepCount = BitConverter.ToInt32(h, 0x1d4);
        w.Sleeping = h[0x208] != 0;
        w.StepFlags = h[0x10c];
        w.Count688 = BitConverter.ToInt32(h, 0x688);
        w.Islands.Colouring = h[0x118 + 0x50] != 0;
        foreach (var off in (ReadOnlySpan<int>)[0x6d8, 0x6e8, 0xa10, 0xa58, 0x758, 0x118 + 0x20, 0x118 + 0x30, 0x118 + 0x40])
            if (BitConverter.ToInt32(h, off) != 0)
                throw new NotSupportedException($"the captured world has a non-empty list at +0x{off:x}");

        var broadphase = new Broadphase(groupTable, collidingMask);
        w.Broadphase = broadphase;
        var shapes = new List<(RnShape Port, Shape Captured)>();
        var capsules = 0;
        var derivedOrigins = 0;
        for (var i = 0; i < Bodies.Count; i++)
        {
            var cb = Bodies[i];
            var final = MemoryMarshal.Read<RnBodyState>(cb.State);
            if (cb.State[0x45] != 0 || final.Controller != 0 || final.JointHead != 0)
                throw new NotSupportedException($"body {i}: island map, controller or joints at step 0");
            // The body is made static at its spawn frame: the origin, with no
            // mass centre yet, which is where its shapes get their proxies.
            var spawn = final;
            spawn.BodyType = 0;
            spawn.Position = RnTransform.Of(final).T;
            spawn.LocalMassCenter = default;
            if (Spawn.Count > 0 && Spawn[i] is { } frame)
            {
                if (!Same(frame.Position, spawn.Position))
                    derivedOrigins++;
                spawn.Position = frame.Position;
                spawn.Scale = frame.Scale;
                spawn.Orientation = frame.Orientation;
            }
            var b = w.AddBody(spawn);
            b.Proxy.Id = BitConverter.ToUInt32(cb.State, 0);
            foreach (var cs in cb.Shapes)
            {
                var attributes = MemoryMarshal.Read<CollisionAttributes>(cs.Head.AsSpan(0x50));
                var material = MemoryMarshal.Read<ContactSolver.Material>(cs.Head.AsSpan(0x20));
                var handle = (ulong)(cs.Id + 1) << 4;
                RnShape s;
                if (cs.Type == 1)
                {
                    // Capsules are not ported: the shape only holds its proxy's place.
                    capsules++;
                    s = new RnShape(b, handle) { Type = 1 };
                    s.Attributes = attributes;
                    b.Shapes.Add(s);
                    w.ShapesByHandle[handle] = s;
                    if (cs.Head[0xae] != 0 && (b.State.Flags249 & 1) != 0)
                        broadphase.CreateProxy(s.Proxy, CapsuleBox(cs.Head, RnTransform.Of(b.State)));
                }
                else
                {
                    s = w.AddShape(b, cs.Type, cs.Type == 2 ? new HullRef(cs.Hull!, BitConverter.ToSingle(cs.Head, 0xb8)) : default,
                                   cs.Mesh, MemoryMarshal.Read<Vec3>(cs.Head.AsSpan(0xb8)), attributes, material, handle);
                    if (cs.Type == 3)
                        s.MeshMode = BitConverter.ToInt32(cs.Head, 0x108);
                }
                s.Reports = cs.Head[0xb0] != 0;
                shapes.Add((s, cs));
            }
        }
        var dynamicBodies = MadeDynamic.Count > 0
            ? MadeDynamic
            : Enumerable.Range(0, Bodies.Count)
                .Where(i => BitConverter.ToInt32(Bodies[i].State, 0x54) != 0 && Bodies[i].Shapes.Length > 0)
                .OrderBy(i => Bodies[i].Shapes.Min(s => BitConverter.ToInt32(s.Head, 0x1c) >> 3))
                .ToList();
        foreach (var i in dynamicBodies)
            w.MakeDynamic(w.Bodies[i]);

        // The mass update and the rest of each body's state come from the
        // capture; the settled bodies wake in their captured awake order.
        for (var i = 0; i < Bodies.Count; i++)
        {
            w.Bodies[i].State = MemoryMarshal.Read<RnBodyState>(Bodies[i].State);
            w.Bodies[i].State.ActiveIndex = -1;
        }
        foreach (var i in Enumerable.Range(0, Bodies.Count).Where(i => BitConverter.ToInt32(Bodies[i].State, 0x18) >= 0)
                                   .OrderBy(i => BitConverter.ToInt32(Bodies[i].State, 0x18)))
            ContactLifecycle.AddAwake(w, w.Bodies[i]);

        var mismatches = 0;
        string? first = null;
        foreach (var (s, cs) in shapes)
        {
            var id = BitConverter.ToInt32(cs.Head, 0x1c);
            if (id == s.ProxyId)
                continue;
            mismatches++;
            first ??= $"shape {cs.Id}: proxy {s.ProxyId:x}, captured {id:x}";
        }
        for (var i = 0; i < Bodies.Count; i++)
        {
            var b = w.Bodies[i];
            var captured = Bodies[i].State;
            var want = (BitConverter.ToInt32(captured, 0x14), BitConverter.ToInt32(captured, 0x18), BitConverter.ToInt32(captured, 0x38),
                        BitConverter.ToInt32(captured, 0x3c), captured[0x44] != 0);
            var got = (b.Index, b.ActiveIndex, b.Node.ManagerIndex, b.Node.ListIndex, b.Static);
            if (want != got)
            {
                mismatches++;
                first ??= $"body {i}: index, awake, node, list, static {got}, captured {want}";
            }
        }
        report = new BuildReport(shapes.Count, mismatches, first, capsules, derivedOrigins);
        return w;
    }

    /// <summary>
    /// A capsule's box for its static proxy: both ends through the frame and
    /// the radius around them (no pad; it equals Valve's box for the one
    /// capsule in atixref). Capsule shapes are otherwise not ported.
    /// </summary>
    private static Aabb CapsuleBox(byte[] head, in RnTransform xf)
    {
        var c0 = MemoryMarshal.Read<Vec3>(head.AsSpan(0xb8));
        var c1 = MemoryMarshal.Read<Vec3>(head.AsSpan(0xc4));
        var r = BitConverter.ToSingle(head, 0xd0);
        var a = Apply(xf, c0);
        var b = Apply(xf, c1);
        return new Aabb(new(MathF.Min(a.X, b.X) - r, MathF.Min(a.Y, b.Y) - r, MathF.Min(a.Z, b.Z) - r),
                        new(MathF.Max(a.X, b.X) + r, MathF.Max(a.Y, b.Y) + r, MathF.Max(a.Z, b.Z) + r));
    }

    private static bool Same(Vec3 a, Vec3 b)
        => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
           && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
           && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);

    private static Vec3 Apply(in RnTransform xf, Vec3 p)
    {
        ref readonly var m = ref xf.R;
        return new(m.M0 * p.X + m.M3 * p.Y + m.M6 * p.Z + xf.T.X,
                   m.M1 * p.X + m.M4 * p.Y + m.M7 * p.Z + xf.T.Y,
                   m.M2 * p.X + m.M5 * p.Y + m.M8 * p.Z + xf.T.Z);
    }

    private static RnHull Hull(JsonElement s)
    {
        var h = Convert.FromHexString(s.GetProperty("hull").GetString()!);
        var planes = Convert.FromHexString(s.GetProperty("planes").GetString()!);
        var edges = Convert.FromHexString(s.GetProperty("edges").GetString()!);
        return new RnHull
        {
            Centroid = MemoryMarshal.Read<Vector3>(h),
            MaxAngularRadius = BitConverter.ToSingle(h, 0xc),
            MinCentroidRadius = BitConverter.ToSingle(h, 0x10),
            BoundsMin = MemoryMarshal.Read<Vector3>(h.AsSpan(0x14)),
            BoundsMax = MemoryMarshal.Read<Vector3>(h.AsSpan(0x20)),
            VertexPositions = MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(s.GetProperty("pos").GetString()!)).ToArray(),
            Planes = Enumerable.Range(0, planes.Length / 16)
                .Select(i => (MemoryMarshal.Read<Vector3>(planes.AsSpan(i * 16)), BitConverter.ToSingle(planes, i * 16 + 12))).ToArray(),
            Vertices = Convert.FromHexString(s.GetProperty("verts").GetString()!),
            Edges = Enumerable.Range(0, edges.Length / 4)
                .Select(i => (edges[i * 4], edges[i * 4 + 1], edges[i * 4 + 2], edges[i * 4 + 3])).ToArray(),
            Faces = Convert.FromHexString(s.GetProperty("faces").GetString()!),
            Flags = BitConverter.ToUInt32(h, 0xa0),
        };
    }

    private static RnMesh Mesh(JsonElement s)
    {
        var m = Convert.FromHexString(s.GetProperty("mesh").GetString()!);
        var nodes = Convert.FromHexString(s.GetProperty("nodes").GetString()!);
        var tris = MemoryMarshal.Cast<byte, int>(Convert.FromHexString(s.GetProperty("tris").GetString()!)).ToArray();
        return new RnMesh
        {
            Min = MemoryMarshal.Read<Vector3>(m),
            Max = MemoryMarshal.Read<Vector3>(m.AsSpan(0xc)),
            Nodes = Enumerable.Range(0, nodes.Length / 32).Select(i => new RnMesh.Node(
                MemoryMarshal.Read<Vector3>(nodes.AsSpan(i * 32)), BitConverter.ToUInt32(nodes, i * 32 + 12),
                MemoryMarshal.Read<Vector3>(nodes.AsSpan(i * 32 + 16)), BitConverter.ToUInt32(nodes, i * 32 + 28))).ToArray(),
            Vertices = MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(s.GetProperty("mverts").GetString()!)).ToArray(),
            Triangles = Enumerable.Range(0, tris.Length / 3).Select(i => (tris[i * 3], tris[i * 3 + 1], tris[i * 3 + 2])).ToArray(),
            Materials = Convert.FromHexString(s.GetProperty("mats").GetString()!),
        };
    }
}
