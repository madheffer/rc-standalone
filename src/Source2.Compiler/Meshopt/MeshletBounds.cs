// Ported from meshoptimizer (src/meshletutils.cpp),
// Copyright (C) 2016-2026, by Arseny Kapoulkine (arseny.kapoulkine@gmail.com),
// distributed under the MIT License:
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System.Numerics;

namespace Source2.Compiler.Meshopt;

/// <summary>
/// A meshlet's descriptor as the node model compile writes it (resourcecompiler
/// FUN_180304180): the normal cone from meshoptimizer's
/// <c>meshopt_computeMeshletBounds</c> (FUN_1812bd110), 8-bit axis and cutoff,
/// and the box packed 10 bits an axis inside the draw's bounds (FUN_18126a830).
/// </summary>
public static class MeshletBounds
{
    /// <summary>
    /// The quantized normal cone of the triangles <paramref name="indices"/>
    /// names, in their order: axis x, y, z and cutoff, each a signed byte.
    /// Only the triangles' normals decide it, so the meshlet's vertex order
    /// does not matter here.
    /// </summary>
    public static (int X, int Y, int Z, int Cutoff) Cone(IReadOnlyList<Vector3> positions, ReadOnlySpan<int> indices)
    {
        var normals = new List<Vector4>();
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 p0 = positions[indices[i]], p1 = positions[indices[i + 1]], p2 = positions[indices[i + 2]];
            float p10x = p1.X - p0.X, p10y = p1.Y - p0.Y, p10z = p1.Z - p0.Z;
            float p20x = p2.X - p0.X, p20y = p2.Y - p0.Y, p20z = p2.Z - p0.Z;
            var nx = p10y * p20z - p10z * p20y;
            var ny = p10z * p20x - p10x * p20z;
            var nz = p10x * p20y - p10y * p20x;
            // The binary sums y first (FUN_1812bc740); meshopt's source sums x first.
            var area = MathF.Sqrt(ny * ny + nx * nx + nz * nz);
            if (area == 0f)
                continue;
            nx /= area;
            ny /= area;
            nz /= area;
            normals.Add(new Vector4(nx, ny, nz, -(nx * p0.X + ny * p0.Y + nz * p0.Z)));
        }
        if (normals.Count == 0)
            return (0, 0, 0, 0);
        var sphere = BoundingSphere(normals.Select(n => new Vector3(n.X, n.Y, n.Z)).ToList(), 3);
        float ax = sphere.X, ay = sphere.Y, az = sphere.Z;
        var length = MathF.Sqrt(ay * ay + ax * ax + az * az);
        var inverse = length == 0f ? 0f : 1f / length;
        ax *= inverse;
        ay *= inverse;
        az *= inverse;
        // The binary's loop takes four normals at a time: the first summed x, y,
        // z, the other three y, x, z; the tail x, y, z.
        var mindp = 1f;
        var unrolled = normals.Count >= 4 ? normals.Count / 4 * 4 : 0;
        for (var i = 0; i < normals.Count; i++)
        {
            var n = normals[i];
            var dp = i < unrolled && i % 4 != 0 ? ay * n.Y + ax * n.X + az * n.Z : ax * n.X + ay * n.Y + az * n.Z;
            mindp = dp < mindp ? dp : mindp;
        }
        if (mindp <= 0.1f)
            return (0, 0, 0, 127);
        var cutoff = MathF.Sqrt(1 - mindp * mindp);
        int sx = QuantizeSnorm8(ax), sy = QuantizeSnorm8(ay), sz = QuantizeSnorm8(az);
        var e0 = MathF.Abs(sx / 127f - ax);
        var e1 = MathF.Abs(sy / 127f - ay);
        var e2 = MathF.Abs(sz / 127f - az);
        var s8 = (int)((e0 + cutoff + e1 + e2) * 127f + 1f);
        return (sx, sy, sz, s8 > 127 ? 127 : s8);
    }

    /// <summary>
    /// A box packed inside the draw's bounds: x, y, z at 10 bits each (x lowest),
    /// the minimum rounded down and the maximum up; a flat axis of the bounds
    /// divides by float.MaxValue.
    /// </summary>
    public static (uint Min, uint Max) Pack(Vector3 boundsMin, Vector3 boundsMax, Vector3 min, Vector3 max)
    {
        var sx = boundsMax.X - boundsMin.X;
        var sy = boundsMax.Y - boundsMin.Y;
        var sz = boundsMax.Z - boundsMin.Z;
        if (sx == 0f)
            sx = float.MaxValue;
        if (sy == 0f)
            sy = float.MaxValue;
        if (sz == 0f)
            sz = float.MaxValue;
        static float Unit(float v) => v <= 0f ? 0f : 1f <= v ? 1f : v;
        var maxZ = (int)MathF.Ceiling(Unit((max.Z - boundsMin.Z) / sz) * 1023f);
        var maxY = (int)MathF.Ceiling(Unit((max.Y - boundsMin.Y) / sy) * 1023f);
        var maxX = (int)MathF.Ceiling(Unit((max.X - boundsMin.X) / sx) * 1023f);
        var minZ = (int)MathF.Floor(Unit((min.Z - boundsMin.Z) / sz) * 1023f);
        var minY = (int)MathF.Floor(Unit((min.Y - boundsMin.Y) / sy) * 1023f);
        var minX = (int)MathF.Floor(Unit((min.X - boundsMin.X) / sx) * 1023f);
        return ((uint)(minX | ((minY | (minZ << 10)) << 10)), (uint)((((maxZ << 10) | maxY) << 10) | maxX));
    }

    private static int QuantizeSnorm8(float v)
    {
        var round = v >= 0 ? 0.5f : -0.5f;
        v = v >= -1f ? v : -1f;
        v = v <= 1f ? v : 1f;
        return (int)(v * 127f + round);
    }

    // computeBoundingSphere with no radii, over the first axisCount axes.
    private static Vector3 BoundingSphere(List<Vector3> points, int axisCount)
    {
        ReadOnlySpan<float> axes =
        [
            1, 0, 0, 0, 1, 0, 0, 0, 1,
            0.57735026f, 0.57735026f, 0.57735026f, -0.57735026f, 0.57735026f, 0.57735026f,
            0.57735026f, -0.57735026f, 0.57735026f, 0.57735026f, 0.57735026f, -0.57735026f,
        ];
        Span<int> pmin = stackalloc int[7], pmax = stackalloc int[7];
        Span<float> tmin = stackalloc float[7], tmax = stackalloc float[7];
        for (var a = 0; a < axisCount; a++)
        {
            pmin[a] = pmax[a] = 0;
            tmin[a] = float.MaxValue;
            tmax[a] = -float.MaxValue;
        }
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            for (var a = 0; a < axisCount; a++)
            {
                var tp = axes[a * 3] * p.X + axes[a * 3 + 1] * p.Y + axes[a * 3 + 2] * p.Z;
                float tpmin = tp - 0f, tpmax = tp + 0f;
                pmin[a] = tpmin < tmin[a] ? i : pmin[a];
                pmax[a] = tpmax > tmax[a] ? i : pmax[a];
                tmin[a] = tpmin < tmin[a] ? tpmin : tmin[a];
                tmax[a] = tpmax > tmax[a] ? tpmax : tmax[a];
            }
        }
        var paxis = 0;
        var paxisdr = 0f;
        for (var a = 0; a < axisCount; a++)
        {
            Vector3 p1 = points[pmin[a]], p2 = points[pmax[a]];
            var d2 = (p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y) + (p2.Z - p1.Z) * (p2.Z - p1.Z);
            var dr = MathF.Sqrt(d2) + 0f + 0f;
            if (dr > paxisdr)
            {
                paxisdr = dr;
                paxis = a;
            }
        }
        Vector3 q1 = points[pmin[paxis]], q2 = points[pmax[paxis]];
        var paxisd = MathF.Sqrt((q2.X - q1.X) * (q2.X - q1.X) + (q2.Y - q1.Y) * (q2.Y - q1.Y) + (q2.Z - q1.Z) * (q2.Z - q1.Z));
        var paxisk = paxisd > 0 ? (paxisd + 0f - 0f) / (2 * paxisd) : 0f;
        float cx = q1.X + (q2.X - q1.X) * paxisk, cy = q1.Y + (q2.Y - q1.Y) * paxisk, cz = q1.Z + (q2.Z - q1.Z) * paxisk;
        var radius = paxisdr / 2;
        foreach (var p in points)
        {
            var d = MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) + (p.Z - cz) * (p.Z - cz));
            if (d + 0f > radius)
            {
                var k = d > 0 ? (d + 0f - radius) / (2 * d) : 0f;
                cx += k * (p.X - cx);
                cy += k * (p.Y - cy);
                cz += k * (p.Z - cz);
                radius = (radius + d + 0f) / 2;
            }
        }
        return new Vector3(cx, cy, cz);
    }
}
