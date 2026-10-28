using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The filters resourcecompiler 0923 runs on the automatic probes after generation (180100e40, in this order: nav mesh
/// 180fbea90 when filtering by nav mesh, ladders 180fbc630, missing nav areas 180fbcb10, exclusion volumes 180fbde60
/// when filtering by volumes, citadel ziplines 180fbba50, inside shapes 180fbe450, map probes 180fbc2d0, and
/// snd_opvar_set_point probes 180fbd800 for a static bake). The probes are Source inches (18006b260 has turned them
/// back from Steam Audio's metres).
/// </summary>
public static class ProbeFilters
{
    /// <summary>
    /// 180fbe450 for the static bake: each probe kept unless a point query at it (radius 0) finds a shape. The query is
    /// world vfn 0x5d8 (vphysics2 1800655d0 -> 1801fcf60) with InteractsWith 0x400 (blocksound), no excluded or
    /// queried-as layers, every id -1, function mask 0xffff at index 0, static bodies only (+0x2f = 1), group 3 and
    /// flags 0x49 (the stack byte's bit 0x80 is never read by the filter); the exact test flag is set.
    /// <list type="bullet">
    /// <item>the broadphase walk (1801dda70): each tree whose kind (+0x50) meets the body mask, from its root, a node
    /// entered when the squared distance from the point to its box (min(p, max) - max(p, min) per axis) is at most
    /// r^2; each leaf's shape to the callback;</item>
    /// <item>the callback (1801ec3c0): the body's type against the mask, the collision filter (1802facf0,
    /// <see cref="Maps.NavGen.NavGenInput.ShapePasses(in CollisionAttributes, int, ulong, byte, byte, ushort[], ulong, ulong)"/>),
    /// the point into the body's frame, then the shape's vfn 0x38: a sphere (18024ca60) or capsule (18024fbc0)
    /// within its radius, a hull (180254790) at GJK distance (1802eca10, 32 iterations, a fresh cache, both at the
    /// identity) at most r from the point, a mesh (18024af70 -> 180248c80) holding the point when closed
    /// (180248a30), else a triangle of its BVH within r;</item>
    /// </list>
    /// The world is the compile's physics world (MapCompile_NavPhysicsWorld, 180fb6de0: the one navsystem reads).
    /// </summary>
    public static List<(float X, float Y, float Z, float Radius)> InsideShapes(RnWorld world, IReadOnlyList<(float X, float Y, float Z, float Radius)> probes)
    {
        var table = CollisionGroupTable.Build();
        var kept = new List<(float, float, float, float)>();
        foreach (var p in probes)
            if (!Hit(world, new Vec3(p.X, p.Y, p.Z), 0.0f, table))
                kept.Add(p);
        return kept;
    }

    private const byte BodyMask = 1;
    private const ulong InteractsWith = 0x400;

    private static bool Hit(RnWorld world, Vec3 p, float radius, ushort[] table)
    {
        var broadphase = world.Broadphase ?? throw new InvalidOperationException("the world has no broadphase");
        foreach (var tree in broadphase.Trees)
        {
            if (tree == null || (tree.Kind & BodyMask) == 0 || tree.Nodes.Root == -1)
                continue;
            var nodes = tree.Nodes.Nodes;
            var stack = new Stack<int>();
            stack.Push(tree.Nodes.Root);
            while (stack.Count > 0)
            {
                var n = nodes[stack.Pop()];
                if (!(BoxDistance2(p, n.Min, n.Max) <= radius * radius))
                    continue;
                if (n.Child1 == -1)
                {
                    if (Callback(world, world.ShapesByHandle[n.Shape], p, radius, table))
                        return true;
                    continue;
                }
                stack.Push(n.Child2);
                stack.Push(n.Child1);
            }
        }
        return false;
    }

    // minps(p, max) - maxps(p, min) per axis, squared and summed.
    private static float BoxDistance2(Vec3 p, Vec3 min, Vec3 max)
    {
        static float Min(float a, float b) => a < b ? a : b;
        static float Max(float a, float b) => a > b ? a : b;
        var x = Min(p.X, max.X) - Max(p.X, min.X);
        var y = Min(p.Y, max.Y) - Max(p.Y, min.Y);
        var z = Min(p.Z, max.Z) - Max(p.Z, min.Z);
        return x * x + y * y + z * z;
    }

    private static bool Callback(RnWorld world, RnShape shape, Vec3 p, float radius, ushort[] table)
    {
        var body = shape.Body.State;
        if (body.BodyType is >= 0 and <= 2 && (BodyMask & (1 << body.BodyType)) == 0)
            return false;
        if (body.BodyType is < 0 or > 2)
            throw new NotSupportedException($"a body of type {body.BodyType} in the point query");
        if (!Maps.NavGen.NavGenInput.ShapePasses(shape.Attributes, body.BodyType, InteractsWith, BodyMask, 3, table, excludeAs: 0, queryAs: 0))
            return false;
        var local = ToBody(body, p);
        return shape.Type switch
        {
            BroadphaseShape.SphereType => Sphere(shape.Proxy, local, radius),
            BroadphaseShape.CapsuleType => Capsule(shape.Proxy, local, radius),
            BroadphaseShape.HullType => HullDistance(shape, local) <= radius,
            BroadphaseShape.MeshType => Mesh(shape, local, radius),
            _ => throw new NotSupportedException($"a point query against a shape of type {shape.Type}"),
        };
    }

    /// <summary>
    /// 1801ec3c0's frame: the rotation of q (+0x120) entry by entry, the origin the mass centre (+0xfc) less q times
    /// the scaled local mass centre (+0x88 x +0xc8) as (k x q) + (c + w k) with k = 2 (c x q), then R^T (p - origin)
    /// summed (t1 dy + t0 dx) + t2 dz, (t3 dx + t4 dy) + t5 dz, (t7 dy + t6 dx) + t8 dz.
    /// </summary>
    private static Vec3 ToBody(in RnBodyState b, Vec3 p)
    {
        float x = b.Orientation.X, y = b.Orientation.Y, z = b.Orientation.Z, w = b.Orientation.W;
        static float D(float v) => v + v;
        var t0 = 1.0f - D(z * z + y * y);
        var t1 = D(w * z + y * x);
        var t2 = D(z * x - w * y);
        var t3 = D(y * x - w * z);
        var t4 = 1.0f - D(z * z + x * x);
        var t5 = D(z * y + w * x);
        var t6 = D(w * y + z * x);
        var t7 = D(z * y - w * x);
        var t8 = 1.0f - D(y * y + x * x);
        var s = b.Scale;
        float cx = s * b.LocalMassCenter.X, cy = s * b.LocalMassCenter.Y, cz = s * b.LocalMassCenter.Z;
        var kx = D(cz * y - cy * z);
        var ky = D(cx * z - cz * x);
        var kz = D(cy * x - cx * y);
        var ox = b.Position.X - ((kz * y - ky * z) + (cx + w * kx));
        var oy = b.Position.Y - ((kx * z - kz * x) + (cy + w * ky));
        var oz = b.Position.Z - ((ky * x - kx * y) + (cz + w * kz));
        float dx = p.X - ox, dy = p.Y - oy, dz = p.Z - oz;
        return new Vec3((t1 * dy + t0 * dx) + t2 * dz, (t3 * dx + t4 * dy) + t5 * dz, (t7 * dy + t6 * dx) + t8 * dz);
    }

    // 18024ca60: (dy^2 + dx^2) + dz^2 <= (r + R)^2, d = centre - p.
    private static bool Sphere(BroadphaseShape s, Vec3 p, float radius)
    {
        float dx = s.CentreA.X - p.X, dy = s.CentreA.Y - p.Y, dz = s.CentreA.Z - p.Z;
        var r = radius + s.Radius;
        return r * r >= (dy * dy + dx * dx) + dz * dz;
    }

    // 18024fbc0: the segment parameter ((pz - az) ez + (py - ay) ey) + (px - ax) ex over max(FLT_MIN, (ey^2 + ex^2) + ez^2),
    // clamped by minss with 1 and maxss with 0; the closest point's (x^2 + y^2) + z^2 <= (r + R)^2.
    private static bool Capsule(BroadphaseShape s, Vec3 p, float radius)
    {
        Vec3 a = s.CentreA, b = s.CentreB;
        float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
        var r = radius + s.Radius;
        var len2 = (ey * ey + ex * ex) + ez * ez;
        var den = 1.1754944e-38f > len2 ? 1.1754944e-38f : len2;
        var t = (((p.Z - a.Z) * ez + (p.Y - a.Y) * ey) + (p.X - a.X) * ex) / den;
        t = 1.0f < t ? 1.0f : t;
        t = 0.0f > t ? 0.0f : t;
        var x = (t * ex + a.X) - p.X;
        var y = (t * ey + a.Y) - p.Y;
        var z = (t * ez + a.Z) - p.Z;
        return r * r >= (x * x + y * y) + z * z;
    }

    // 180254790: no vertices is no hit; else GJK between the point (one vertex, scale 1) and the hull's proxy at its scale.
    private static float HullDistance(RnShape shape, Vec3 p)
    {
        if (shape.Hull.Hull.VertexPositions.Length == 0)
            return float.PositiveInfinity;
        var point = new GjkProxy { Vertices = [p], Count = 1, Scale = 1.0f };
        var hull = ContinuousSolve.ProxyOf(shape.Hull);
        var identity = new RnTransform { R = new Mat3 { M0 = 1f, M4 = 1f, M8 = 1f } };
        var cache = new GjkCache();
        return Gjk.Distance(identity, point, identity, hull, ref cache, 0x20).Distance;
    }

    // 180248c80 at the shape's scale: a closed mesh (+0xb4 bit 0) holding the point is a hit, and with radius 0 nothing else is
    // tested; otherwise the BVH from the root (inner nodes to node + 1 first, the other child stacked), each box times the scale
    // within r^2 of the point, each leaf triangle (vertices times the scale) whose nearest point (18008b720) is within r.
    private static bool Mesh(RnShape shape, Vec3 p, float radius)
    {
        var mesh = shape.Mesh ?? throw new InvalidOperationException("a mesh shape without its mesh");
        var scale = shape.MeshScale;
        if ((mesh.Flags & 1) != 0)
        {
            if (BoxCast.InsideClosed(mesh, new System.Numerics.Vector3(scale.X, scale.Y, scale.Z), new System.Numerics.Vector3(p.X, p.Y, p.Z)))
                return true;
            if (radius == 0.0f)
                return false;
        }
        if (mesh.Nodes.Length == 0)
            return false;
        var stack = new Stack<int>();
        var at = 0;
        while (true)
        {
            var n = mesh.Nodes[at];
            var min = new Vec3(n.Min.X * scale.X, n.Min.Y * scale.Y, n.Min.Z * scale.Z);
            var max = new Vec3(n.Max.X * scale.X, n.Max.Y * scale.Y, n.Max.Z * scale.Z);
            if (BoxDistance2(p, min, max) <= radius * radius)
            {
                if ((n.Children & 0xc0000000) != 0xc0000000)
                {
                    stack.Push(at + (int)(n.Children & 0x3fffffff));
                    at++;
                    continue;
                }
                var count = n.Children & 0x3fffffff;
                for (var k = 0u; k < count; k++)
                {
                    var (ia, ib, ic) = mesh.Triangles[(int)n.TriangleOffset + (int)k];
                    Vec3 V(int i) => new(scale.X * mesh.Vertices[i].X, scale.Y * mesh.Vertices[i].Y, scale.Z * mesh.Vertices[i].Z);
                    var c = RoundCollision.ClosestOnTriangle(V(ia), V(ib), V(ic), p, out _);
                    float dy = p.Y - c.Y, dx = p.X - c.X, dz = p.Z - c.Z;
                    if ((dy * dy + dx * dx) + dz * dz <= radius * radius)
                        return true;
                }
            }
            if (stack.Count == 0)
                return false;
            at = stack.Pop();
        }
    }
}
