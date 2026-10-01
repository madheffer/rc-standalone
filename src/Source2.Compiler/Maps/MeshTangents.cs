using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// CMesh tangent generation (CMesh_ComputeTangents, 1812db920) with
/// csgo_core's UseMikkTSpace: the binary's own MikkTSpace-style generator
/// (182080a60), not the reference mikktspace.c.
/// <list type="number">
/// <item>Vertices are welded to the first earlier one with equal position,
/// normal and texcoord (float equality, so -0 equals 0; 18207fc00).</item>
/// <item>Each triangle gets its texture tangent and handedness
/// (182080440); a triangle with two equal corners has none.</item>
/// <item>At each welded vertex, corners of triangles sharing an edge with
/// it in the same winding join, unless the triangles' handedness groups
/// would mix both signs (1820807b0).</item>
/// <item>Each corner adds the triangle tangent projected off its vertex
/// normal, weighted by the corner angle (a polynomial acos), to its group
/// (18207fe20); groups are normalised, a zero one becomes (1, 0, 0), and
/// w is +1 or -1 from the triangle's handedness group.</item>
/// </list>
/// </summary>
internal static class MeshTangents
{
    /// <summary>One tangent per corner of <paramref name="indices"/>.</summary>
    public static Vector4[] Corners(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<Vector2> texcoords, IReadOnlyList<int> indices)
    {
        var vertexCount = positions.Count;
        var cornerCount = indices.Count;
        var triangles = cornerCount / 3;

        // 1. Weld.
        var weld = new int[vertexCount];
        var first = new Dictionary<(float, float, float, float, float, float, float, float), int>();
        for (var v = 0; v < vertexCount; v++)
        {
            var key = (Z(positions[v].X), Z(positions[v].Y), Z(positions[v].Z), Z(normals[v].X), Z(normals[v].Y), Z(normals[v].Z), Z(texcoords[v].X), Z(texcoords[v].Y));
            if (!first.TryGetValue(key, out var w))
                first[key] = w = v;
            weld[v] = w;
        }

        // Corners per welded vertex, in triangle order.
        var start = new int[vertexCount + 1];
        for (var c = 0; c < cornerCount; c++)
            start[weld[indices[c]] + 1]++;
        for (var v = 0; v < vertexCount; v++)
            start[v + 1] += start[v];
        var fill = (int[])start.Clone();
        var cornerList = new int[cornerCount];
        for (var t = 0; t < triangles; t++)
            for (var k = 0; k < 3; k++)
                cornerList[fill[weld[indices[(t * 3) + k]]]++] = (t * 4) | k;

        // 2. Triangle tangents and handedness.
        var tri = new Vector4[triangles];
        var flags = new byte[triangles];
        for (var t = 0; t < triangles; t++)
        {
            int i0 = indices[t * 3], i1 = indices[(t * 3) + 1], i2 = indices[(t * 3) + 2];
            Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
            Vector2 u0 = texcoords[i0], u1 = texcoords[i1], u2 = texcoords[i2];
            var dv1 = u1.Y - u0.Y;
            var dv2 = u2.Y - u0.Y;
            var tx = ((p1.X - p0.X) * dv2) - ((p2.X - p0.X) * dv1);
            var ty = ((p1.Y - p0.Y) * dv2) - ((p2.Y - p0.Y) * dv1);
            var tz = ((p1.Z - p0.Z) * dv2) - ((p2.Z - p0.Z) * dv1);
            var det = ((u1.X - u0.X) * dv2) - ((u2.X - u0.X) * dv1);
            var s = det != 0f ? (det <= 0f ? -1f : 1f) : 0f;
            if ((p0.X == p1.X && p0.Y == p1.Y && p0.Z == p1.Z) || (p0.X == p2.X && p0.Y == p2.Y && p0.Z == p2.Z) || (p1.X == p2.X && p1.Y == p2.Y && p1.Z == p2.Z))
                s = 0f;
            var len = MathF.Sqrt((ty * ty) + (tx * tx) + (tz * tz));
            var f = len != 0f ? s / len : 0f;
            tri[t] = new Vector4(f * tx, f * ty, f * tz, s);
            flags[t] = (byte)((s < 0f ? 2 : 0) | (0f < s ? 1 : 0));
        }

        // 3. Groups.
        var cornerParent = new int[cornerCount];
        for (var c = 0; c < cornerCount; c++)
            cornerParent[c] = c;
        var triParent = new int[triangles];
        for (var t = 0; t < triangles; t++)
            triParent[t] = t;
        int[] other = [1, 2, 0, 1];
        int Find(int[] parent, int x)
        {
            if (x == parent[x])
                return x;
            int p;
            do
            {
                p = parent[x];
                parent[x] = parent[p];
                x = p;
            }
            while (p != parent[p]);
            return x;
        }
        for (var v = 0; v < vertexCount; v++)
        {
            for (var i = start[v]; i < start[v + 1]; i++)
            {
                var a = cornerList[i];
                int ta = a >> 2, ka = a & 3;
                var aNext = weld[indices[(ta * 3) + other[ka]]];
                var aPrev = weld[indices[(ta * 3) + other[ka + 1]]];
                for (var j = i + 1; j < start[v + 1]; j++)
                {
                    var b = cornerList[j];
                    int tb = b >> 2, kb = b & 3;
                    var bNext = weld[indices[(tb * 3) + other[kb]]];
                    var bPrev = weld[indices[(tb * 3) + other[kb + 1]]];
                    if (bNext != aPrev && bPrev != aNext)
                        continue;
                    if ((flags[ta] | flags[tb]) == 3)
                        continue;
                    if ((flags[tb] & flags[ta]) == 0)
                    {
                        var ra = Find(triParent, ta);
                        var rb = Find(triParent, tb);
                        if (ra != rb)
                        {
                            if ((flags[ra] | flags[rb]) == 3)
                                continue;
                            triParent[rb] = ra;
                            flags[ra] |= flags[rb];
                        }
                    }
                    var ca = Find(cornerParent, (ta * 3) + ka);
                    var cb = Find(cornerParent, (tb * 3) + kb);
                    if (ca != cb)
                        cornerParent[cb] = ca;
                }
            }
        }
        var root = new int[cornerCount];
        for (var c = 0; c < cornerCount; c++)
            root[c] = Find(cornerParent, c);

        // 4. Accumulate.
        var sum = new Vector4[cornerCount];
        for (var t = 0; t < triangles; t++)
        {
            var T = tri[t];
            if (T.W == 0f)
                continue;
            for (var k = 0; k < 3; k++)
            {
                var c = (t * 3) + k;
                var v = weld[indices[c]];
                var a = weld[indices[(t * 3) + other[k]]];
                var b = weld[indices[(t * 3) + other[k + 1]]];
                var n = normals[v];
                var d = (n.Y * T.Y) + (T.X * n.X) + (T.Z * n.Z);
                var px = T.X - (n.X * d);
                var py = T.Y - (n.Y * d);
                var pz = T.Z - (n.Z * d);
                var tl = MathF.Sqrt((py * py) + (px * px) + (pz * pz));
                var p = positions[v];
                float e1x = positions[a].X - p.X, e1y = positions[a].Y - p.Y, e1z = positions[a].Z - p.Z;
                float e2x = positions[b].X - p.X, e2y = positions[b].Y - p.Y, e2z = positions[b].Z - p.Z;
                var d1 = (n.Y * e1y) + (n.X * e1x) + (n.Z * e1z);
                var d2 = (n.X * e2x) + (n.Y * e2y) + (n.Z * e2z);
                e1x -= n.X * d1;
                e1y -= n.Y * d1;
                e1z -= n.Z * d1;
                e2y -= n.Y * d2;
                e2x -= n.X * d2;
                e2z -= n.Z * d2;
                var r = MathF.Sqrt(((e2y * e2y) + (e2x * e2x) + (e2z * e2z)) * ((e1y * e1y) + (e1x * e1x) + (e1z * e1z)));
                var inv = r != 0f ? 1f / r : 0f;
                var cos = ((e2y * e1y) + (e2x * e1x) + (e2z * e1z)) * inv;
                var x = MathF.Min(MathF.Abs(cos), 1f);
                var angle = MathF.Sqrt(1f - x) * ((((x * 0.05147786f) - 0.2053972f) * x) + 1.570337f);
                if (cos < 0f)
                    angle = 3.1415925f - angle;
                var w = (tl != 0f ? 1f / tl : 0f) * angle;
                var g = root[c];
                sum[g] = new Vector4((w * px) + sum[g].X, (w * py) + sum[g].Y, (w * pz) + sum[g].Z, 0f);
            }
        }

        // 5. Normalise the groups; w from the triangle's handedness group.
        var result = new Vector4[cornerCount];
        for (var c = 0; c < cornerCount; c++)
        {
            if (root[c] != c)
                continue;
            var s = sum[c];
            var len = MathF.Sqrt((s.X * s.X) + (s.Y * s.Y) + (s.Z * s.Z));
            var inv = len != 0f ? 1f / len : 0f;
            result[c] = new Vector4(inv == 0f ? 1f : s.X * inv, s.Y * inv, s.Z * inv, 0f);
        }
        for (var t = 0; t < triangles; t++)
        {
            var hand = (flags[Find(triParent, t)] & 1) == 0 ? -1f : 1f;
            for (var k = 0; k < 3; k++)
            {
                var c = (t * 3) + k;
                var src = result[root[c]];
                result[c] = new Vector4(src.X, src.Y, src.Z, hand);
            }
        }
        return result;
    }

    private static float Z(float x) => x == 0f ? 0f : x;
}
