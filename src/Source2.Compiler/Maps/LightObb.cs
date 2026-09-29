using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The oriented box the light precompute fits around what a light reaches
/// (FUN_180f1aa70): every orientation on a 5 degree grid over pitch, yaw and
/// roll in [-45, 45] is scored by the volume of the box aligned with it, a box
/// whose axes are all within 0.99 of a world axis counting 0.95 of its volume;
/// then every whole degree within 5 of the best, the window following the best
/// as it moves. Float operations are in the binary's order.
/// </summary>
/// <remarks>
/// Like the binary, scoring an orientation stops once the points so far, in
/// 32-point chunks, already give a volume that is not below the best: the
/// extents only grow and rounding is monotone, so such an orientation could
/// not have won. The binary also shuffles the points first (mt19937 seeded
/// 0x1571), which only changes how soon that happens.
/// </remarks>
public static class LightObb
{
    /// <summary>A box: rotation, centre, half extents (the binary's 0x28-byte record).</summary>
    public readonly record struct Box(Quaternion Rotation, Vector3 Origin, Vector3 Extent);

    private struct Search
    {
        public float BestVolume;
        public Vector3 BestAngles;
        public Box Best;
    }

    public static Box Fit(IReadOnlyList<Vector3> list)
    {
        var points = list as Vector3[] ?? [.. list];
        var s = new Search { BestVolume = float.MaxValue };
        // The coarse pass: roll outermost, then pitch, yaw innermost.
        for (var roll = -45f; roll <= 45f; roll += 5f)
            for (var pitch = -45f; pitch <= 45f; pitch += 5f)
                for (var yaw = -45f; yaw <= 45f; yaw += 5f)
                    Try(points, ref s, pitch, yaw, roll);
        // The fine pass: each loop starts and ends 5 either side of the best
        // as it stands when the loop (re)starts or is tested.
        for (var roll = s.BestAngles.Z - 5f; roll <= s.BestAngles.Z + 5f; roll += 1f)
            for (var pitch = s.BestAngles.X - 5f; pitch <= s.BestAngles.X + 5f; pitch += 1f)
                for (var yaw = s.BestAngles.Y - 5f; yaw <= s.BestAngles.Y + 5f; yaw += 1f)
                    Try(points, ref s, pitch, yaw, roll);
        return s.Best;
    }

    private static void Try(Vector3[] points, ref Search s, float pitch, float yaw, float roll)
    {
        var q = CTransform.AngleQuaternion(new Vector3(pitch, yaw, roll));
        var r0 = Rotate(q, 1f, 0f, 0f);
        var r1 = Rotate(q, 0f, 1f, 0f);
        var r2 = Rotate(q, 0f, 0f, 1f);
        var penalty = MaxAbs(r0) > 0.99f || MaxAbs(r1) > 0.99f || MaxAbs(r2) > 0.99f ? 0.95f : 1f;
        float min0 = float.MaxValue, min1 = float.MaxValue, min2 = float.MaxValue;
        float max0 = -float.MaxValue, max1 = -float.MaxValue, max2 = -float.MaxValue;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            var d0 = ((p.Y * r0.Y) + (p.Z * r0.Z)) + (p.X * r0.X);
            var d1 = ((p.Y * r1.Y) + (p.Z * r1.Z)) + (p.X * r1.X);
            var d2 = ((p.Y * r2.Y) + (p.Z * r2.Z)) + (p.X * r2.X);
            min0 = MinPs(min0, d0);
            min1 = MinPs(min1, d1);
            min2 = MinPs(min2, d2);
            max0 = MaxPs(max0, d0);
            max1 = MaxPs(max1, d1);
            max2 = MaxPs(max2, d2);
            if ((i & 31) == 31 && !(((max2 - min2) * ((max1 - min1) * (max0 - min0))) * penalty < s.BestVolume))
                return;
        }
        float e0 = max0 - min0, e1 = max1 - min1, e2 = max2 - min2;
        var volume = (e2 * (e1 * e0)) * penalty;
        if (!(volume < s.BestVolume))
            return;
        s.BestVolume = volume;
        s.BestAngles = new Vector3(pitch, yaw, roll);
        float c0 = max0 + min0, c1 = max1 + min1, c2 = max2 + min2;
        var origin = new Vector3(
            ((c0 * r0.X) * 0.5f + (r1.X * c1) * 0.5f) + (r2.X * c2) * 0.5f,
            ((c0 * r0.Y) * 0.5f + (r1.Y * c1) * 0.5f) + (r2.Y * c2) * 0.5f,
            ((c0 * r0.Z) * 0.5f + (r1.Z * c1) * 0.5f) + (r2.Z * c2) * 0.5f);
        s.Best = new Box(q, origin, new Vector3(e0 * 0.5f, e1 * 0.5f, e2 * 0.5f));
    }

    /// <summary>minps a, b: b unless a is below it.</summary>
    private static float MinPs(float a, float b) => a < b ? a : b;

    /// <summary>maxps a, b: b unless a is above it.</summary>
    private static float MaxPs(float a, float b) => a > b ? a : b;

    private static float MaxAbs(Vector3 v)
    {
        float x = MathF.Abs(v.X), y = MathF.Abs(v.Y), z = MathF.Abs(v.Z);
        var m = x > y ? x : y;
        return m > z ? m : z;
    }

    /// <summary>
    /// A unit axis through q, lane for lane: c = q x v, t = c + c, then
    /// (q x t) + (w t + v).
    /// </summary>
    private static Vector3 Rotate(Quaternion q, float vx, float vy, float vz)
    {
        var cx = vz * q.Y - vy * q.Z;
        var cy = vx * q.Z - vz * q.X;
        var cz = vy * q.X - vx * q.Y;
        float tx = cx + cx, ty = cy + cy, tz = cz + cz;
        return new Vector3(
            (q.Y * tz - q.Z * ty) + (q.W * tx + vx),
            (q.Z * tx - q.X * tz) + (q.W * ty + vy),
            (q.X * ty - q.Y * tx) + (q.W * tz + vz));
    }
}
