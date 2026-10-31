using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// A barn light's scene bounds (CUBEMAPS.md F.1, ledger 193), which the object cull (18003d380) sizes into the
/// entry's screen size and the binner sorts by. scenesystem 20261015 (the installed 20261029 is code-identical):
/// <list type="bullet">
/// <item>1800ce950 (the description's vfunc 0x10): <see cref="Frustum"/> of the light, clipped by
/// <see cref="Box"/> of its illumination box when the object's +0x251 is set (the clip's empty result keeps the
/// box's own bounds), then <see cref="Snap"/>.</item>
/// <item>1804a2480: the 8 clip-box corners (1805fe3a0: x and y -1 / 1, z 0 / 1) through (transform x the
/// description's +0x40), each coordinate (((m0 cx + m1 cy) + m2 cz) + m3) times 1 / w, then their min and max.</item>
/// <item>18049e080: the box's 8 corners (+-half extents) through 18049d6d0's 3 x 4 of its quaternion and position
/// (18049dd60), their min and max.</item>
/// <item>1800d5410 (CSceneSystem vf 0xe0): each min rounded to an integer by +-2^23 and lowered by 1 where the
/// rounding went up, each max raised likewise.</item>
/// </list>
/// </summary>
public static class BarnLightBounds
{
    /// <summary>1805fe3a0: the clip box's corners in 1804a2480's order.</summary>
    private static readonly Vector3[] ClipCorners =
    [
        new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0), new(1, 1, 0), new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1),
    ];

    /// <summary>
    /// 1804a2480: the world box of the light's frustum. <paramref name="description"/> is the 0x12c-byte description
    /// (<see cref="BarnLightDescriptions.Description"/>), <paramref name="transform"/> its world 3 x 4.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) Frustum(byte[] description, float[] transform)
    {
        var projection = new float[16];
        for (var i = 0; i < 16; i++)
            projection[i] = BitConverter.ToSingle(description, 0x40 + 4 * i);
        float[] t4 = [.. transform, 0f, 0f, 0f, 1f];
        var m = BarnLightRecords.Mul4(t4, projection);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        foreach (var c in ClipCorners)
        {
            float Row(int r) => ((m[r * 4] * c.X + m[r * 4 + 1] * c.Y) + m[r * 4 + 2] * c.Z) + m[r * 4 + 3];
            var inverse = 1f / Row(3);
            var p = new Vector3(Row(0) * inverse, Row(1) * inverse, Row(2) * inverse);
            min = Min(min, p);
            max = Max(max, p);
        }
        return (min, max);
    }

    /// <summary>1805fe340: each frustum plane's corners (the first three of four), in 1801aa3f0's order.</summary>
    private static readonly int[][] PlaneCorners = [[3, 1, 5], [0, 2, 6], [2, 3, 7], [1, 0, 4], [0, 1, 3], [4, 6, 7]];

    /// <summary>
    /// 1801aa3f0's light frustum in the light's own frame, for its binner footprint: the clip-box corners through the
    /// description's +0x40 with a divide each (as in <see cref="Frustum"/>, without the transform), then per plane
    /// from corners (a, b, c) the normal ((c.y - a.y)(b.z - a.z) - (b.y - a.y)(c.z - a.z), (b.x - a.x)(c.z - a.z) -
    /// (c.x - a.x)(b.z - a.z), (c.x - a.x)(b.y - a.y) - (b.x - a.x)(c.y - a.y)) through 180053800 and d = (a.z n.z +
    /// n.y a.y) + a.x n.x (the disassembly's order).
    /// </summary>
    public static (float[] Planes, Vector3[] Corners) FrustumPlanes(byte[] description)
    {
        var m = new float[16];
        for (var i = 0; i < 16; i++)
            m[i] = BitConverter.ToSingle(description, 0x40 + 4 * i);
        var corners = new Vector3[8];
        for (var k = 0; k < 8; k++)
        {
            var c = ClipCorners[k];
            float Row(int r) => ((m[r * 4] * c.X + m[r * 4 + 1] * c.Y) + m[r * 4 + 2] * c.Z) + m[r * 4 + 3];
            var inverse = 1f / Row(3);
            corners[k] = new Vector3(Row(0) * inverse, Row(1) * inverse, Row(2) * inverse);
        }
        var planes = new float[24];
        for (var p = 0; p < 6; p++)
        {
            var (a, b, c) = (corners[PlaneCorners[p][0]], corners[PlaneCorners[p][1]], corners[PlaneCorners[p][2]]);
            var (bx, by, bz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (cx, cy, cz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var n = CubemapShadowCascades.Normalize(new Vector3(cy * bz - by * cz, bx * cz - cx * bz, cx * by - bx * cy));
            planes[p * 4] = n.X;
            planes[p * 4 + 1] = n.Y;
            planes[p * 4 + 2] = n.Z;
            planes[p * 4 + 3] = (a.Z * n.Z + n.Y * a.Y) + a.X * n.X;
        }
        return (planes, corners);
    }

    /// <summary>
    /// 18049d6d0: a quaternion (x, y, z, w) and a position as a row-major 3 x 4, lane for lane as its SIMD code
    /// computes them (2a = a + a; products with the doubled factor second).
    /// </summary>
    public static float[] QuaternionMatrix(Vector4 q, Vector3 position)
    {
        float x2 = q.X + q.X, y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        float xx = q.X * x2, yy = q.Y * y2, zz = q.Z * z2;
        float yx = y2 * q.X, zy = z2 * q.Y, zx = z2 * q.X;
        float zw = z2 * q.W, xw = x2 * q.W, yw = y2 * q.W;
        return
        [
            (1f - yy) - zz, yx - zw, yw + zx, position.X,
            zw + yx, (1f - xx) - zz, zy - xw, position.Y,
            zx - yw, xw + zy, (1f - xx) - yy, position.Z,
        ];
    }

    /// <summary>18049e080: the world box of an oriented box (quaternion, position, half extents).</summary>
    public static (Vector3 Min, Vector3 Max) Box(Vector4 rotation, Vector3 position, Vector3 half)
    {
        var m = QuaternionMatrix(rotation, position);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        foreach (var sx in new[] { -1f, 1f })
            foreach (var sy in new[] { -1f, 1f })
                foreach (var sz in new[] { -1f, 1f })
                {
                    var p = AggregateCulling.TransformPoint(m, new Vector3(sx * half.X, sy * half.Y, sz * half.Z));
                    min = Min(min, p);
                    max = Max(max, p);
                }
        return (min, max);
    }

    /// <summary>1800ce950's clip: the frustum's box inside the illumination box, or the box's own when they miss.</summary>
    public static (Vector3 Min, Vector3 Max) Clip((Vector3 Min, Vector3 Max) frustum, (Vector3 Min, Vector3 Max) box)
    {
        static float Hi(float a, float b) => a <= b ? b : a;
        static float Lo(float a, float b) => b <= a ? b : a;
        var min = new Vector3(Hi(box.Min.X, frustum.Min.X), Hi(box.Min.Y, frustum.Min.Y), Hi(box.Min.Z, frustum.Min.Z));
        var max = new Vector3(Lo(box.Max.X, frustum.Max.X), Lo(box.Max.Y, frustum.Max.Y), Lo(box.Max.Z, frustum.Max.Z));
        return max.X < min.X || max.Y < min.Y || max.Z < min.Z ? box : (min, max);
    }

    /// <summary>1800d5410: outward to whole units.</summary>
    public static (Vector3 Min, Vector3 Max) Snap(Vector3 min, Vector3 max)
    {
        static float Round(float v) => v >= 0f ? (v + 8388608f) - 8388608f : (v + -8388608f) - -8388608f;
        static float Down(float v) => Round(v) is var r && v < r ? r - 1f : r;
        static float Up(float v) => Round(v) is var r && r < v ? r + 1f : r;
        return (new Vector3(Down(min.X), Down(min.Y), Down(min.Z)), new Vector3(Up(max.X), Up(max.Y), Up(max.Z)));
    }

    private static Vector3 Min(Vector3 a, Vector3 b) => new(b.X <= a.X ? b.X : a.X, b.Y <= a.Y ? b.Y : a.Y, b.Z <= a.Z ? b.Z : a.Z);

    private static Vector3 Max(Vector3 a, Vector3 b) => new(a.X <= b.X ? b.X : a.X, a.Y <= b.Y ? b.Y : a.Y, a.Z <= b.Z ? b.Z : a.Z);
}
