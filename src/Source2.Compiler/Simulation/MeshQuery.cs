using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

public static partial class MeshCollision
{
    /// <summary>
    /// The triangles whose BVH leaf box meets the query box and which pass
    /// the triangle-box test (FUN_18024a4a0 with the test on). The box is in
    /// scaled mesh space and divided back by the scale. The walk is depth
    /// first, left child first, a stack for the right ones.
    /// </summary>
    internal static void QueryTriangles(RnMesh mesh, Vec3 scale, Vec3 boxMin, Vec3 boxMax, List<int> output)
    {
        var minX = boxMin.X * (1f / scale.X);
        var minY = (1f / scale.Y) * boxMin.Y;
        var minZ = (1f / scale.Z) * boxMin.Z;
        var maxX = boxMax.X * (1f / scale.X);
        var maxY = (1f / scale.Y) * boxMax.Y;
        var maxZ = (1f / scale.Z) * boxMax.Z;
        var centre = new Vec3((minX + maxX) * 0.5f, (maxY + minY) * 0.5f, (minZ + maxZ) * 0.5f);
        var half = new Vec3((maxX - minX) * 0.5f, (maxY - minY) * 0.5f, (maxZ - minZ) * 0.5f);
        if (mesh.Nodes.Length == 0)
            return;
        var stack = new Stack<int>();
        var node = 0;
        while (true)
        {
            var n = mesh.Nodes[node];
            var culled = n.Max.X < minX || n.Max.Y < minY || n.Max.Z < minZ
                         || maxX < n.Min.X || maxY < n.Min.Y || maxZ < n.Min.Z;
            if (!culled)
            {
                if ((n.Children & 0xc0000000u) != 0xc0000000u)
                {
                    stack.Push(node + (int)(n.Children & 0x3fffffffu));
                    node++;
                    continue;
                }
                var t = (int)n.TriangleOffset;
                for (var k = (int)(n.Children & 0x3fffffffu); k > 0; k--, t++)
                {
                    var (a, b, c) = mesh.Triangles[t];
                    if (TriangleOverlapsBox(centre, half, V(mesh.Vertices[a]), V(mesh.Vertices[b]), V(mesh.Vertices[c])))
                        output.Add(t);
                }
            }
            if (stack.Count == 0)
                return;
            node = stack.Pop();
        }
    }

    private static Vec3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private static float Min(float a, float b) => a < b ? a : b;

    private static float Max(float a, float b) => a > b ? a : b;

    /// <summary>
    /// Triangle against box, separating axes (FUN_18024b230): the box's three
    /// axes, the nine edge cross axes, then the triangle's normal. Each test
    /// is written as the DLL's SSE lanes compute it; NaN never separates.
    /// </summary>
    internal static bool TriangleOverlapsBox(Vec3 centre, Vec3 half, Vec3 p, Vec3 q, Vec3 r)
    {
        float ax = p.X - centre.X, ay = p.Y - centre.Y, az = p.Z - centre.Z;
        float bx = q.X - centre.X, by = q.Y - centre.Y, bz = q.Z - centre.Z;
        float cx = r.X - centre.X, cy = r.Y - centre.Y, cz = r.Z - centre.Z;
        float hx = half.X, hy = half.Y, hz = half.Z;

        if (AxisSeparates(Min(ax, Min(bx, cx)), Max(ax, Max(bx, cx)), hx)
            || AxisSeparates(Min(ay, Min(by, cy)), Max(ay, Max(by, cy)), hy)
            || AxisSeparates(Min(az, Min(bz, cz)), Max(az, Max(bz, cz)), hz))
            return false;

        float e0x = bx - ax, e0y = by - ay, e0z = bz - az;
        float e1x = cx - bx, e1y = cy - by, e1z = cz - bz;
        float e2x = ax - cx, e2y = ay - cy, e2z = az - cz;
        float acx = cx + ax, acy = cy + ay, acz = cz + az;
        float abx = bx + ax, aby = by + ay, abz = bz + az;
        float bcx = cx + bx, bcy = cy + by, bcz = cz + bz;

        if (0f < (MathF.Abs(acz * e0y - acy * e0z) - MathF.Abs(e2z * e0y - e2y * e0z)) - (hz * MathF.Abs(e0y) + hy * MathF.Abs(e0z)) * 2f
            || 0f < (MathF.Abs(acx * e0z - acz * e0x) - MathF.Abs(e2x * e0z - e2z * e0x)) - (hx * MathF.Abs(e0z) + hz * MathF.Abs(e0x)) * 2f
            || 0f < (MathF.Abs(acy * e0x - acx * e0y) - MathF.Abs(e2y * e0x - e2x * e0y)) - (hy * MathF.Abs(e0x) + hx * MathF.Abs(e0y)) * 2f)
            return false;
        if (0f < (MathF.Abs(abz * e1y - aby * e1z) - MathF.Abs(e0z * e1y - e0y * e1z)) - 2f * (hz * MathF.Abs(e1y) + hy * MathF.Abs(e1z))
            || 0f < (MathF.Abs(abx * e1z - abz * e1x) - MathF.Abs(e0x * e1z - e0z * e1x)) - 2f * (hx * MathF.Abs(e1z) + hz * MathF.Abs(e1x))
            || 0f < (MathF.Abs(aby * e1x - abx * e1y) - MathF.Abs(e0y * e1x - e0x * e1y)) - 2f * (hy * MathF.Abs(e1x) + hx * MathF.Abs(e1y)))
            return false;
        if (0f < (MathF.Abs(bcz * e2y - bcy * e2z) - MathF.Abs(e1z * e2y - e1y * e2z)) - 2f * (hz * MathF.Abs(e2y) + hy * MathF.Abs(e2z))
            || 0f < (MathF.Abs(bcx * e2z - bcz * e2x) - MathF.Abs(e1x * e2z - e1z * e2x)) - 2f * (hx * MathF.Abs(e2z) + hz * MathF.Abs(e2x))
            || 0f < (MathF.Abs(bcy * e2x - bcx * e2y) - MathF.Abs(e1y * e2x - e1x * e2y)) - 2f * (hy * MathF.Abs(e2x) + hx * MathF.Abs(e2y)))
            return false;

        var nx = e0z * e2y - e0y * e2z;
        var ny = e0x * e2z - e0z * e2x;
        var nz = e0y * e2x - e0x * e2y;
        var d = MathF.Abs((nx * ax + ny * ay) + nz * az);
        var rad = (MathF.Abs(nx) * hx + MathF.Abs(ny) * hy) + MathF.Abs(nz) * hz;
        return !(0f < d - rad);
    }

    /// <summary>maxps(min - h, 0 - (max + h)) &gt; 0.</summary>
    private static bool AxisSeparates(float min, float max, float h)
    {
        var lo = min - h;
        var hi = 0f - (max + h);
        return 0f < (lo > hi ? lo : hi);
    }
}
