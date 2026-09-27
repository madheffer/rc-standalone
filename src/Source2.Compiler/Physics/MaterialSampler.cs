using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// The CPU half of physicsbuilder's material sampler (CMaterialSampler, 0924:
/// entry 18064c460): the sample points it renders, the record each point's
/// vertices carry, the render target's size, and how the read-back colours
/// become a layer per triangle. The render itself runs the material's
/// ToolsVis programs on the GPU (docs/WORLD_PHYSICS.md).
/// </summary>
public static class MaterialSampler
{
    /// <summary>One sample point: the interpolated texcoords, paint and position.</summary>
    public readonly record struct Sample(Vector2 Uv0, Vector2 Uv1, Vector4 Paint, Vector3 Position);

    // DAT_1809cc940: 53 barycentric points (weights for corners 0, 1, 2), as bits.
    private static readonly uint[] PointBits =
    [
        0x3eaaaa9f, 0x3eaaaa9f, 0x3eaaaa9f, 0x3e2779ea, 0x3f28ae75, 0x3e35cc42, 0x3e26d3fa, 0x3e3a4229,
        0x3f27ba77, 0x3f2ad480, 0x3e35742e, 0x3e1f398f, 0x3f01bef5, 0x3dfbcd36, 0x3ebd8ec9, 0x3dec1ebd,
        0x3ef60f1b, 0x3ecee957, 0x3ed5c0ba, 0x3eef92d1, 0x3deab1d5, 0x3eac994a, 0x3e146562, 0x3f049a13,
        0x3da7fbad, 0x3db1b79e, 0x3f54c986, 0x3f014b8c, 0x3e8e1fc1, 0x3e5e924f, 0x3db080b6, 0x3f525919,
        0x3dbcb685, 0x3da352eb, 0x3eb3226c, 0x3f12046c, 0x3e8c96cc, 0x3ef93426, 0x3e746a1a, 0x3f186152,
        0x3eaad256, 0x3d91ac15, 0x3d8e7792, 0x3f1dd2d0, 0x3ea0bc9d, 0x3f56ed24, 0x3dbb762e, 0x3d8d20b0,
        0x3e7c3611, 0x3e8edb0e, 0x3ef309c8, 0x3e987853, 0x3f1c55eb, 0x3dbb6f5d, 0x3f27673c, 0x3d88e475,
        0x3e8ef86a, 0x3e8eff7e, 0x3d88e8a7, 0x3f27632c, 0x3e596356, 0x3f3a555c, 0x3d751ce3, 0x3ee576ab,
        0x3d849b20, 0x3ef962ae, 0x3ed068ba, 0x3ec03947, 0x3e5ebbfd, 0x3e6c2ce4, 0x3ed71c32, 0x3eb2cd5b,
        0x3d6d4802, 0x3e54317b, 0x3f3c1f21, 0x3f459e1f, 0x3d5bee3d, 0x3e328bb1, 0x3ec939a8, 0x3e6874c9,
        0x3ec28c15, 0x3e3a3ec0, 0x3d608073, 0x3f436849, 0x3e366d7a, 0x3f10a7d6, 0x3e837996, 0x3f3ad9ae,
        0x3e586a0a, 0x3d70bbf5, 0x3d473366, 0x3f3ff7be, 0x3e4e542e, 0x3d624aae, 0x3ee2e664, 0x3f006823,
        0x3e2b5419, 0x3e8be78e, 0x3f0f3733, 0x3ec135db, 0x3d477681, 0x3f12ed9a, 0x3f0f21c4, 0x3e257fb7,
        0x3e8efc9c, 0x3e7a29c7, 0x3e34a772, 0x3f144bb2, 0x3f0216b5, 0x3ed9409a, 0x3d8a47ed, 0x3f160232,
        0x3d3ab756, 0x3ebca490, 0x3ed88e58, 0x3e9513b6, 0x3e925df2, 0x3ea4547a, 0x3f06dd05, 0x3e1be2f8,
        0x3ecd115a, 0x3f0cc09c, 0x3d4b6b6e, 0x3d94dc66, 0x3e8ba0a5, 0x3f279421, 0x3e2d6170, 0x3ecbc5df,
        0x3edd898b, 0x3ef3d274, 0x3e4e6320, 0x3ea4fbfc, 0x3f068b1a, 0x3eaac9b0, 0x3e10403a, 0x3e04bc27,
        0x3e07481b, 0x3f3cfeef, 0x3f26b895, 0x3e8116a9, 0x3dc5e0b5, 0x3ed2048e, 0x3e128f19, 0x3ee4b3e5,
        0x3e9a569b, 0x3ed27d46, 0x3e932c1f, 0x3f138013, 0x3e5d944b, 0x3e546b6a, 0x3ea43351, 0x3e6f1bef,
        0x3ee43eb8, 0x3e127a63, 0x3f06afff, 0x3ea962ae, 0x3f6ae0d2, 0x3d3c4d23, 0x3d15a5b9,
    ];

    // DAT_1809ccbc0: how many points a triangle gets, by rounded area / 128.
    private static readonly int[] Counts =
    [
        0, 1, 5, 5, 5, 5, 5, 7, 7, 7, 7, 11, 11, 13, 13, 13, 13, 17, 17, 19, 19, 19, 19, 23, 23, 23, 23,
        23, 23, 29, 29, 31, 31, 31, 31, 31, 31, 37, 37, 37, 37, 41, 41, 43, 43, 43, 43, 47, 47, 47, 47, 47, 47, 53,
    ];

    /// <summary>
    /// FUN_18064cd80: each triangle's sample points and how many it has. The
    /// count comes from the triangle's area times 1/128, rounded to nearest
    /// even and clamped to 1..53, through the count table; the points are the
    /// first that many of the 53, blended from the corners in Valve's order.
    /// <paramref name="uv1"/> null uses uv0 for both.
    /// </summary>
    public static (List<Sample> Samples, int[] PerTriangle) Samples(Vector3[] positions, int[] indices, Vector2[] uv0, Vector2[]? uv1, Vector4[] paint)
    {
        var samples = new List<Sample>();
        var perTriangle = new int[indices.Length / 3];
        var second = uv1 ?? uv0;
        for (var t = 0; t < perTriangle.Length; t++)
        {
            int a = indices[t * 3], b = indices[(t * 3) + 1], c = indices[(t * 3) + 2];
            var scaled = Area(positions[a], positions[b], positions[c]) * (1f / 128f);
            // cvtss2si: nearest even; out of range gives int.MinValue, so 1.
            var index = float.IsNaN(scaled) || scaled >= 2147483648f || scaled < -2147483648f ? int.MinValue : (int)MathF.Round(scaled);
            index = index < 2 ? 1 : index > 52 ? 53 : index;
            var count = Counts[index];
            perTriangle[t] = count;
            for (var k = 0; k < count; k++)
            {
                float w0 = BitConverter.UInt32BitsToSingle(PointBits[k * 3]);
                float w1 = BitConverter.UInt32BitsToSingle(PointBits[(k * 3) + 1]);
                float w2 = BitConverter.UInt32BitsToSingle(PointBits[(k * 3) + 2]);
                Vector3 p0 = positions[a], p1 = positions[b], p2 = positions[c];
                var position = new Vector3((w0 * p0.X) + (w1 * p1.X) + (w2 * p2.X), (w1 * p1.Y) + (w0 * p0.Y) + (w2 * p2.Y), (w1 * p1.Z) + (w0 * p0.Z) + (w2 * p2.Z));
                Vector2 u0 = uv0[a], u1 = uv0[b], u2 = uv0[c];
                var t0 = new Vector2((w0 * u0.X) + (w1 * u1.X) + (w2 * u2.X), (w0 * u0.Y) + (w1 * u1.Y) + (w2 * u2.Y));
                Vector2 s0 = second[a], s1 = second[b], s2 = second[c];
                var t1 = new Vector2((w0 * s0.X) + (w1 * s1.X) + (w2 * s2.X), (w0 * s0.Y) + (w1 * s1.Y) + (w2 * s2.Y));
                Vector4 q0 = paint[a], q1 = paint[b], q2 = paint[c];
                var blend = new Vector4((w2 * q2.X) + (w1 * q1.X) + (w0 * q0.X), (w2 * q2.Y) + (w1 * q1.Y) + (w0 * q0.Y),
                                        (w2 * q2.Z) + (w1 * q1.Z) + (w0 * q0.Z), (w2 * q2.W) + (w1 * q1.W) + (w0 * q0.W));
                samples.Add(new Sample(t0, t1, blend, position));
            }
        }
        return (samples, perTriangle);
    }

    /// <summary>FUN_1800b9550: half the cross product's length, in its order.</summary>
    public static float Area(Vector3 p0, Vector3 p1, Vector3 p2)
    {
        var x = ((p2.X - p0.X) * (p1.Z - p0.Z)) - ((p2.Z - p0.Z) * (p1.X - p0.X));
        var y = ((p2.Z - p0.Z) * (p1.Y - p0.Y)) - ((p2.Y - p0.Y) * (p1.Z - p0.Z));
        var z = ((p2.Y - p0.Y) * (p1.X - p0.X)) - ((p2.X - p0.X) * (p1.Y - p0.Y));
        return MathF.Sqrt((z * z) + (x * x) + (y * y)) * 0.5f;
    }

    /// <summary>One byte of a packed colour: x * 255 in single precision, truncated, clamped to 0..255 (mulss, cvttss2si).</summary>
    public static byte Unorm8(float x)
    {
        var v = x * 255f;
        var i = float.IsNaN(v) || v >= 2147483648f || v < -2147483648f ? int.MinValue : (int)v;
        return (byte)(i < 1 ? 0 : i >= 0xff ? 0xff : i);
    }

    /// <summary>
    /// FUN_18064e530: a point's 40-byte vertex record (the same for its three
    /// vertices): uv0, uv1, position, packed tangent frame, paint as RGBA8,
    /// tint as RGBA8 (always white here).
    /// </summary>
    public static void Pack(Sample s, uint tangentFrame, Span<byte> record)
    {
        BitConverter.TryWriteBytes(record[0..], s.Uv0.X);
        BitConverter.TryWriteBytes(record[4..], s.Uv0.Y);
        BitConverter.TryWriteBytes(record[8..], s.Uv1.X);
        BitConverter.TryWriteBytes(record[12..], s.Uv1.Y);
        BitConverter.TryWriteBytes(record[16..], s.Position.X);
        BitConverter.TryWriteBytes(record[20..], s.Position.Y);
        BitConverter.TryWriteBytes(record[24..], s.Position.Z);
        BitConverter.TryWriteBytes(record[28..], tangentFrame);
        record[32] = Unorm8(s.Paint.X);
        record[33] = Unorm8(s.Paint.Y);
        record[34] = Unorm8(s.Paint.Z);
        record[35] = Unorm8(s.Paint.W);
        record[36] = record[37] = record[38] = record[39] = 0xff;
    }

    /// <summary>FUN_18064bcb0: the square target's side for a batch, the next power of two of ceil(sqrt(count)), at least 16.</summary>
    public static int TargetSide(int count)
    {
        var side = (uint)MathF.Ceiling(MathF.Sqrt(count));
        var v = side - 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v = (v >> 16 | v) + 1;
        return (int)Math.Max(v, 16u);
    }

    /// <summary>FUN_18064d790: a read-back RGB8 pixel's layer weights, (R - m, G - m, B - m, m) / 255 with m the smallest.</summary>
    public static Vector4 Weights(byte r, byte g, byte b)
    {
        var m = Math.Min(Math.Min(r, g), b);
        return new Vector4((r - m) / 255f, (g - m) / 255f, (b - m) / 255f, m / 255f);
    }

    /// <summary>
    /// FUN_18064c2c0: a triangle's layer from its points' weights. Each point
    /// picks its largest weight, a later one only when strictly larger; the
    /// triangle takes the most picked, again the lower layer on a tie.
    /// </summary>
    public static int Vote(ReadOnlySpan<Vector4> weights)
    {
        Span<int> votes = stackalloc int[4];
        foreach (var w in weights)
        {
            var i = w.X < w.Y ? 1 : 0;
            if (w[i] < w.Z)
                i = 2;
            if (w[i] < w.W)
                i = 3;
            votes[i]++;
        }
        var best = votes[0] < votes[1] ? 1 : 0;
        if (votes[best] < votes[2])
            best = 2;
        if (votes[best] < votes[3])
            best = 3;
        return best;
    }
}
