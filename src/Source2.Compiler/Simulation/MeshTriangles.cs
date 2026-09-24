using System.Numerics;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

public static partial class MeshCollision
{
    /// <summary>Half-edges of the triangle hull (0x1803e3188): next, twin, origin, face.</summary>
    private static readonly (byte, byte, byte, byte)[] TriangleEdges =
        [(2, 1, 0, 0), (5, 0, 1, 1), (4, 3, 1, 0), (1, 2, 2, 1), (0, 5, 2, 0), (3, 4, 0, 1)];

    /// <summary>
    /// The hull's bounding sphere (FUN_1802515a0, vtable +0xc0): the scaled
    /// centroid in the world and the scaled radius grown by 1/16.
    /// </summary>
    internal static (Vec3 Centre, float Radius) BoundingSphere(HullRef hull, in RnTransform xf)
    {
        var s = hull.Scale;
        var c = hull.Hull.Centroid;
        return (HullCollision.ToWorld(xf, s * c.X, s * c.Y, s * c.Z), s * hull.Hull.MaxAngularRadius + ShapeMargin);
    }

    /// <summary>
    /// The two-faced hull of the triangle {v0, v1, v2} (FUN_1802f5c20), as
    /// the narrowphase reads it: vertices, the planes n and -n through v0,
    /// the static half-edge tables and the centroid.
    /// </summary>
    internal static RnHull TriangleHull(Vec3 v0, Vec3 v1, Vec3 v2)
    {
        var centroid = new Vector3(((v1.X + v0.X) + v2.X) * 0.33333334f,
                                   ((v1.Y + v0.Y) + v2.Y) * 0.33333334f,
                                   ((v1.Z + v0.Z) + v2.Z) * 0.33333334f);
        float ax = v1.X - v0.X, ay = v1.Y - v0.Y, az = v1.Z - v0.Z;
        float bx = v2.X - v0.X, by = v2.Y - v0.Y, bz = v2.Z - v0.Z;
        var nx = bz * ay - az * by;
        var ny = bx * az - ax * bz;
        var nz = ax * by - bx * ay;
        var length = MathF.Sqrt((nz * nz + ny * ny) + nx * nx);
        Vec3 n;
        if (1e-17f > length || length > 1e17f)
            n = length == 0f ? default : NormalizeDouble(nx, ny, nz);
        else
        {
            var inv = 1f / length;
            n = new Vec3(inv * nx, inv * ny, inv * nz);
        }
        var d0 = (v0.Z * n.Z + v0.Y * n.Y) + v0.X * n.X;
        var d1 = (v0.Z * -n.Z + v0.Y * -n.Y) + -n.X * v0.X;
        return new RnHull
        {
            Centroid = centroid,
            VertexPositions = [new(v0.X, v0.Y, v0.Z), new(v1.X, v1.Y, v1.Z), new(v2.X, v2.Y, v2.Z)],
            Planes = [(new Vector3(n.X, n.Y, n.Z), d0), (new Vector3(-n.X, -n.Y, -n.Z), d1)],
            Vertices = [0, 2, 4],
            Faces = [0, 1],
            Edges = TriangleEdges,
            Flags = 8,
        };
    }

    /// <summary>Normalisation in double (FUN_18008d4e0).</summary>
    private static Vec3 NormalizeDouble(float x, float y, float z)
    {
        double dx = x, dy = y, dz = z;
        var inv = 1.0 / Math.Sqrt((dy * dy + dx * dx) + dz * dz);
        return new((float)(inv * dx), (float)(inv * dy), (float)(inv * dz));
    }

    /// <summary>
    /// The hull against each candidate triangle (FUN_1802ff110). A triangle
    /// is tried only if the hull's bounding sphere centre is on its front
    /// side and the sphere, grown by 3/32, reaches its plane. The triangle
    /// becomes a hull {0, e1, e2} at v0 in the mesh's rotation
    /// (FUN_1802f23f0) and goes through the hull-hull narrowphase with no old
    /// manifold and its own SAT cache; a hit's mesh-side points are moved
    /// back by v0.
    /// </summary>
    internal static List<CachedManifold> CollideTriangles(MeshContactState state, in RnTransform xfHull, HullRef hull,
                                                          in RnTransform xfMesh, RnMesh mesh, Vec3 scale)
    {
        var result = new List<CachedManifold>();
        var (centre, radius) = BoundingSphere(hull, xfHull);
        var c = HullCollision.ToLocal(xfMesh, centre.X, centre.Y, centre.Z);
        var reach = (radius + PlaneSlack) * (radius + PlaneSlack);
        var q = RnMath.Matrix(Quat.Identity);
        ref readonly var rb = ref xfMesh.R;
        var rotation = new Mat3();
        // The triangle's rotation is the mesh's times the identity quaternion's
        // matrix, summed out in full, so a zero entry can change sign.
        for (var j = 0; j < 3; j++)
        {
            float x = q[3 * j], y = q[3 * j + 1], z = q[3 * j + 2];
            rotation[3 * j] = (x * rb.M0 + y * rb.M3) + z * rb.M6;
            rotation[3 * j + 1] = (x * rb.M1 + y * rb.M4) + z * rb.M7;
            rotation[3 * j + 2] = (x * rb.M2 + y * rb.M5) + z * rb.M8;
        }
        for (var k = 0; k < state.Triangles.Count; k++)
        {
            var triangle = state.Triangles[k];
            var (ia, ib, ic) = mesh.Triangles[triangle];
            var p0 = mesh.Vertices[ia];
            var p1 = mesh.Vertices[ib];
            var p2 = mesh.Vertices[ic];
            var v0x = scale.X * p0.X;
            var v0y = p0.Y * scale.Y;
            var v0z = p0.Z * scale.Z;
            var e1x = scale.X * p1.X - v0x;
            var e1y = p1.Y * scale.Y - v0y;
            var e1z = p1.Z * scale.Z - v0z;
            var e2x = scale.X * p2.X - v0x;
            var e2y = p2.Y * scale.Y - v0y;
            var e2z = p2.Z * scale.Z - v0z;
            var nx = e2z * e1y - e2y * e1z;
            var ny = e2x * e1z - e1x * e2z;
            var nz = e2y * e1x - e2x * e1y;
            var h = (ny * c.Y + nz * c.Z) + nx * c.X - ((nz * v0z + ny * v0y) + nx * v0x);
            if (0f > h || (h * h) / ((ny * ny + nx * nx) + nz * nz) > reach)
                continue;

            var xfTriangle = new RnTransform { R = rotation, T = HullCollision.ToWorld(xfMesh, v0x, v0y, v0z) };
            var triangleHull = TriangleHull(default, new Vec3(e1x, e1y, e1z), new Vec3(e2x, e2y, e2z));
            var manifold = default(CachedManifold);
            var cache = state.Caches[k];
            var hit = HullCollision.Collide(default, ref manifold, xfHull, hull, xfTriangle,
                                            new HullRef(triangleHull, 1f), ref cache.Sat, triangle);
            state.Caches[k] = cache;
            if (!hit)
                continue;
            var points = manifold.Points;
            for (var i = 0; i < manifold.PointCount; i++)
                points[i].LocalB = new Vec3(v0x + points[i].LocalB.X, v0y + points[i].LocalB.Y, v0z + points[i].LocalB.Z);
            result.Add(manifold);
        }
        return result;
    }
}
