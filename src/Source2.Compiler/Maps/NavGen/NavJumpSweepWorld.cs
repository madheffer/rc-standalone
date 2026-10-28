using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// AddJumps' world (180072ef0, world vfn 0x580 = 180202b00, type 4 = 180201b00): the hull kind's
/// cylinder (physics interface vfn 0x1e8 = <see cref="RnHullBuilder.CreateCylinder"/>, a GJK proxy
/// of its vertices at scale 1, radius 1/16) swept from <c>from</c> to <c>to</c> with no turn. The
/// swept box (180128ff0 at both ends, joined) meets the static tree's leaves (1801dea00, touching
/// counts); each static body's shapes that pass the query (layers 0x3021, group 4, 1802facf0) are
/// swept against with the time of impact (1802dddd0, to 1, 32 iterations; 1801ebc00 with its
/// overlap check off): hulls (18012e550), spheres (18012e650) and capsules (18012e600) whole,
/// meshes per triangle (1801ee7b0: RnMesh_QueryBox over the swept box in the body's frame grown by
/// 1/16, each triangle at the shape's scale, radius 1/16). Blocked when any time is below 1.
/// </summary>
public sealed class NavJumpSweepWorld : INavJumpWorld
{
    private readonly record struct Shape(GjkProxy? Proxy, RnMesh? Mesh, Vec3 MeshScale, Sweep Sweep, float[] T, Vec3 Min, Vec3 Max);

    private readonly List<Shape> _shapes = [];
    private readonly Dictionary<NavJumpCapsule, (GjkProxy Proxy, System.Numerics.Vector3[] Vertices)> _cylinders = [];

    public int Casts, Blocked;

    /// <summary>The world from <paramref name="bodies"/> (all static), those <paramref name="inNavWorld"/> keeps.</summary>
    public NavJumpSweepWorld(IReadOnlyList<SettleWorld.BodyBuild> bodies, Func<SettleWorld.BodyBuild, bool>? inNavWorld = null)
    {
        var table = CollisionGroupTable.Build();
        var kept = bodies.Where(b => inNavWorld?.Invoke(b) != false).ToList();
        var world = SettleWorld.CreateWorld(kept, []);
        foreach (var body in world.Bodies)
        {
            ref var state = ref body.State;
            // 1801ecb30: both ends from the body (1800092f0 and the same terms inline), the scaled mass centre.
            var t = BodyFrame(state);
            var centre = new Vec3(state.Scale * state.LocalMassCenter.X, state.Scale * state.LocalMassCenter.Y, state.Scale * state.LocalMassCenter.Z);
            var sweep = SweepOf(centre, t, t);
            foreach (var s in body.Shapes)
            {
                if (!NavGenInput.ShapePasses(s.Attributes, state.BodyType, 0x3021ul, 1, 4, table, 0, 0) || s.ProxyId < 0)
                    continue;
                var tree = world.Broadphase!.Trees[s.ProxyId & 7];
                if ((tree.Kind & 1) == 0)
                    continue;
                ref readonly var leaf = ref tree.Nodes.Nodes[s.ProxyId >> 3];
                GjkProxy? proxy = s.Type switch
                {
                    BroadphaseShape.HullType => GjkProxy.OfHull([.. s.Hull.Hull.VertexPositions.Select(v => new Vec3(v.X, v.Y, v.Z))], s.Hull.Hull.Flags,
                        new Vec3(s.Hull.Hull.BoundsMin.X, s.Hull.Hull.BoundsMin.Y, s.Hull.Hull.BoundsMin.Z),
                        new Vec3(s.Hull.Hull.BoundsMax.X, s.Hull.Hull.BoundsMax.Y, s.Hull.Hull.BoundsMax.Z), s.Hull.Scale),
                    BroadphaseShape.SphereType => Round([s.Proxy.CentreA], s.Proxy.Radius),
                    BroadphaseShape.CapsuleType => Round([s.Proxy.CentreA, s.Proxy.CentreB], s.Proxy.Radius),
                    BroadphaseShape.MeshType => null,
                    _ => throw new NotSupportedException($"AddJumps' sweep against a type {s.Type} shape is not ported"),
                };
                _shapes.Add(new Shape(proxy, s.Mesh, s.MeshScale, sweep, t, leaf.Min, leaf.Max));
            }
        }
    }

    /// <summary>
    /// 1800092f0 (and 1801ecb30's inline copy): <see cref="NavBoxWorld.Transform"/>'s turn, but the
    /// origin is the mass centre less (k x q) + (c + w k), the order its SIMD sums take.
    /// </summary>
    internal static float[] BodyFrame(in RnBodyState b)
    {
        var t = NavBoxWorld.Transform(b);
        float x = b.Orientation.X, y = b.Orientation.Y, z = b.Orientation.Z, w = b.Orientation.W;
        var s = b.Scale;
        float cx = s * b.LocalMassCenter.X, cy = s * b.LocalMassCenter.Y, cz = s * b.LocalMassCenter.Z;
        float kx = cz * y - cy * z, ky = cx * z - cz * x, kz = cy * x - cx * y;
        kx += kx;
        ky += ky;
        kz += kz;
        t[9] = b.Position.X - ((kz * y - ky * z) + (cx + w * kx));
        t[10] = b.Position.Y - ((kx * z - kz * x) + (cy + w * ky));
        t[11] = b.Position.Z - ((ky * x - kx * y) + (cz + w * kz));
        return t;
    }

    // 18012e650 / 18012e600 at scale 1: the centres, the radius at least 1/16.
    private static GjkProxy Round(Vec3[] centres, float radius)
        => new() { Vertices = centres, Count = centres.Length, Scale = 1f, Radius = radius <= 0.0625f ? 0.0625f : radius };

    public bool CastBlocked(NavJumpCapsule capsule, (float X, float Y, float Z) from, (float X, float Y, float Z) to)
    {
        Casts++;
        if (!_cylinders.TryGetValue(capsule, out var cylinder))
        {
            var hull = RnHullBuilder.CreateCylinder(new(0f, 0f, capsule.Z0), new(0f, 0f, capsule.Z1), capsule.Radius)
                       ?? throw new NotSupportedException("a jump cylinder shorter than 1/32 (RnHullCreateCylinder gives none) is not ported");
            var vertices = hull.VertexPositions.Select(v => new Vec3(v.X, v.Y, v.Z)).ToArray();
            cylinder = (new GjkProxy { Vertices = vertices, Count = vertices.Length, Scale = 1f, Radius = 0.0625f }, hull.VertexPositions);
            _cylinders[capsule] = cylinder;
        }
        // 180201b00: the start and end frames are the identity turn at each point.
        float[] start = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, from.X, from.Y, from.Z];
        float[] end = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, to.X, to.Y, to.Z];
        var sweep = SweepOf(new Vec3(0f, 0f, 0f), start, end);
        var (a0, a1) = Bounds(cylinder.Vertices, from);
        var (b0, b1) = Bounds(cylinder.Vertices, to);
        Vec3 lo = new(MathF.Min(a0.X, b0.X), MathF.Min(a0.Y, b0.Y), MathF.Min(a0.Z, b0.Z));
        Vec3 hi = new(MathF.Max(a1.X, b1.X), MathF.Max(a1.Y, b1.Y), MathF.Max(a1.Z, b1.Z));
        var triangles = new List<int>();
        foreach (var s in _shapes)
        {
            if (s.Max.X < lo.X || s.Max.Y < lo.Y || s.Max.Z < lo.Z || hi.X < s.Min.X || hi.Y < s.Min.Y || hi.Z < s.Min.Z)
                continue;
            if (s.Proxy is { } proxy)
            {
                if (TimeOfImpact.Compute(sweep, cylinder.Proxy, s.Sweep, proxy, 1f, 32).T < 1f)
                    return Hit();
                continue;
            }
            var mesh = s.Mesh!;
            var (min, max) = LocalBox(lo, hi, s.T);
            triangles.Clear();
            MeshCollision.QueryTriangles(mesh, s.MeshScale, min, max, triangles);
            foreach (var k in triangles)
            {
                var (i, j, l) = mesh.Triangles[k];
                Vec3 V(System.Numerics.Vector3 v) => new(v.X * s.MeshScale.X, v.Y * s.MeshScale.Y, v.Z * s.MeshScale.Z);
                var triangle = new GjkProxy { Vertices = [V(mesh.Vertices[i]), V(mesh.Vertices[j]), V(mesh.Vertices[l])], Count = 3, Scale = 1f, Radius = 0.0625f };
                if (TimeOfImpact.Compute(sweep, cylinder.Proxy, s.Sweep, triangle, 1f, 32).T < 1f)
                    return Hit();
            }
        }
        return false;
    }

    private bool Hit()
    {
        Blocked++;
        return true;
    }

    /// <summary>180128ff0 with the identity turn: each vertex plus the point; min and max kept with &lt;=.</summary>
    private static (Vec3 Min, Vec3 Max) Bounds(System.Numerics.Vector3[] vertices, (float X, float Y, float Z) p)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var v in vertices)
        {
            float x = v.X + p.X, y = v.Y + p.Y, z = v.Z + p.Z;
            if (x <= minX) minX = x;
            if (y <= minY) minY = y;
            if (z <= minZ) minZ = z;
            if (maxX <= x) maxX = x;
            if (maxY <= y) maxY = y;
            if (maxZ <= z) maxZ = z;
        }
        return (new(minX, minY, minZ), new(maxX, maxY, maxZ));
    }

    /// <summary>
    /// 1801ee7b0's query box: the swept box's centre and half extents into the body's frame (the
    /// transposed turn, the centre less the translation; the half through |R|), grown by 1/16.
    /// </summary>
    private static (Vec3 Min, Vec3 Max) LocalBox(Vec3 lo, Vec3 hi, float[] t)
    {
        float cx = (hi.X + lo.X) * 0.5f, cy = (lo.Y + hi.Y) * 0.5f, cz = (lo.Z + hi.Z) * 0.5f;
        float hx = (hi.X - lo.X) * 0.5f, hy = (hi.Y - lo.Y) * 0.5f, hz = (hi.Z - lo.Z) * 0.5f;
        float nx = -t[9], ny = -t[10], nz = -t[11];
        var x = t[1] * cy + t[0] * cx + t[2] * cz + ny * t[1] + t[0] * nx + t[2] * nz;
        var y = t[4] * ny + nx * t[3] + t[5] * nz + t[3] * cx + t[4] * cy + t[5] * cz;
        var z = cx * t[6] + t[7] * cy + t[8] * cz + nx * t[6] + t[7] * ny + nz * t[8];
        var ex = MathF.Abs(t[0]) * hx + MathF.Abs(t[1]) * hy + MathF.Abs(t[2]) * hz;
        var ey = MathF.Abs(t[3]) * hx + MathF.Abs(t[4]) * hy + MathF.Abs(t[5]) * hz;
        var ez = MathF.Abs(t[6]) * hx + MathF.Abs(t[7]) * hy + MathF.Abs(t[8]) * hz;
        return (new((x - ex) - 0.0625f, (y - ey) - 0.0625f, (z - ez) - 0.0625f), new(x + ex + 0.0625f, y + ey + 0.0625f, z + ez + 0.0625f));
    }

    /// <summary>
    /// 1801e85a0: a sweep from two 3x4 frames. Each turn by the trace (m4 + m0) + m8 (at or above 0:
    /// (m5 - m7, m6 - m2, m1 - m3, trace + 1); else by the largest diagonal), normalised (DPPS), the
    /// end flipped to the start's side; the centres ((c.y m3 + c.x m0) + c.z m6) + m9 per axis.
    /// </summary>
    internal static Sweep SweepOf(Vec3 c, float[] m0, float[] m1)
    {
        var q0 = TurnOf(m0);
        var q1 = TurnOf(m1);
        if ((q0.X * q1.X + q0.Y * q1.Y) + (q0.Z * q1.Z + q0.W * q1.W) < 0f)
            q1 = new Quat(0f - q1.X, 0f - q1.Y, 0f - q1.Z, 0f - q1.W);
        static Vec3 Centre(Vec3 c, float[] m)
            => new(((c.Y * m[3] + c.X * m[0]) + c.Z * m[6]) + m[9], ((m[1] * c.X + c.Y * m[4]) + c.Z * m[7]) + m[10],
                   ((m[2] * c.X + c.Y * m[5]) + c.Z * m[8]) + m[11]);
        return new Sweep { Q0 = q0, Q = q1, Alpha0 = 0f, LocalCenter = c, C0 = Centre(c, m0), C = Centre(c, m1) };
    }

    private static Quat TurnOf(float[] m)
    {
        var trace = (m[4] + m[0]) + m[8];
        float x, y, z, w;
        if (!(trace < 0f))
            (x, y, z, w) = (m[5] - m[7], m[6] - m[2], m[1] - m[3], trace + 1f);
        else if (m[0] > m[4] && m[0] > m[8])
            (x, y, z, w) = (((m[0] - m[4]) - m[8]) + 1f, m[3] + m[1], m[2] + m[6], m[5] - m[7]);
        else if (m[4] <= m[8])
            (x, y, z, w) = (m[2] + m[6], m[7] + m[5], ((m[8] - m[0]) - m[4]) + 1f, m[1] - m[3]);
        else
            (x, y, z, w) = (m[3] + m[1], ((m[4] - m[8]) - m[0]) + 1f, m[7] + m[5], m[6] - m[2]);
        var n = MathF.Sqrt((x * x + y * y) + (z * z + w * w));
        if (n == 0f)
            throw new NotSupportedException("a zero turn (1801e85a0's fallback constant) is not ported");
        return new Quat(x / n, y / n, z / n, w / n);
    }
}
