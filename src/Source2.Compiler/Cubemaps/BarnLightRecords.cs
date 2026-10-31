using System.Buffers.Binary;
using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// One g_BarnLights record (0x1e0 bytes; CUBEMAPS.md A.6) from the binner's 0x640-byte CPU light entry, as
/// scenesystem 1801b14b0 writes it (1801b2450 calls it once per merged entry into a zeroed array and uploads that).
/// <list type="bullet">
/// <item>1804a65f0 first copies the light description at entry +0x18 and moves its spatial fields into the view's
/// camera-relative space through the entry's 3 x 4 at +0x144 (translation minus the camera, binner +0xe540):
/// +0x00 the description's 4 x 4 times that transform's rigid inverse (18049b3b0, 180499080), +0x40 the transform
/// times the description's +0x40 (180499080), +0x90 a point (18049dd60; translation dropped when +0x9c is 0),
/// +0xd0 the rotation (18049d6d0, 180499f90, 18049d420), +0xf0 .. the shape's points and vectors (18049c360).
/// Its +0x80 (1804a32f0) is not read by the record, so it is not ported.</item>
/// <item>1801b14b0 then lays the record out from that block and the entry's own fields.</item>
/// </list>
/// Checked byte for byte against 64 (entry, record) pairs captured in cubehost (tools/cubehost/barn_record_io.js):
/// shape types 0, 1 and 2, with and without an illumination volume (atixref), and 218 more on ze_hold_em_p with baked
/// shadow slots 0 and 1 (+0x1ea: the record's +0xf0 row and flag 8; ledger 193). Types 3 to 5 (18049d390), a
/// shadow-mapped light (entry +0xc4 + 0x18 >= 0, +0x203), slot 4 and a cookie (+0x5a0, a bindless texture index) are
/// refused: none was captured.
/// </summary>
public static class BarnLightRecords
{
    public const int EntrySize = 0x640, RecordSize = 0x1e0;

    /// <summary>The binner state 1801b14b0 reads beside the entry.</summary>
    /// <param name="Camera">Binner +0xe540 .. +0xe548 (the view's camera, the origin of camera-relative space).</param>
    /// <param name="ViewOrigin">The view's +0x10 .. +0x18 (through binner +0xe1e8): where a directional light is placed from.</param>
    /// <param name="Flags">The view's flags word +0x48 (through binner +0xe1f0); bit 25 picks the second intensity set.</param>
    /// <param name="Reversed">Global 180922570: the 4 x 4 at +0x40 is multiplied by 1805fe720 (z flipped) instead of the identity.</param>
    public sealed record Context(Vector3 Camera, Vector3 ViewOrigin, uint Flags, bool Reversed);

    public static byte[] Record(byte[] entry, Context view)
    {
        if (entry.Length < EntrySize)
            throw new ArgumentException($"a light entry is {EntrySize:x} bytes", nameof(entry));
        float E(int o) => BinaryPrimitives.ReadSingleLittleEndian(entry.AsSpan(o));
        var shape = entry[0x18 + 0xec];
        if (shape is >= 3 and <= 5)
            throw new NotSupportedException($"1801b14b0: shape type {shape} (18049d390) is not ported");
        if (BinaryPrimitives.ReadInt32LittleEndian(entry[(0x18 + 0xc4)..]) >= 0 || entry[0x203] != 0)
            throw new NotSupportedException("1801b14b0: a shadow-mapped barn light is not ported");
        // The baked shadow slot (+0x1ea) clamped to -1 .. 4 picks a row of 1805fe770; slot 4 reads the 16 bytes after
        // the table's last row (the next .rdata constant), which no captured light has.
        var slot = Math.Clamp((int)(sbyte)entry[0x1ea], -1, 4);
        if (slot == 4)
            throw new NotSupportedException("1801b14b0: baked shadow slot 4 reads past 1805fe770's rows");
        if (entry[0x207] != 0)
            throw new NotSupportedException("1801b14b0: entry +0x207 (flag 0x10, with binner +0xf3e8 +0x6c) is not ported");
        if (BinaryPrimitives.ReadInt64LittleEndian(entry[0x5a0..]) != 0)
            throw new NotSupportedException("1801b14b0: a barn light with a cookie is not ported");

        var xf = new float[12];
        for (var i = 0; i < 12; i++)
            xf[i] = E(0x144 + 4 * i);
        xf[3] -= view.Camera.X;
        xf[7] -= view.Camera.Y;
        xf[11] -= view.Camera.Z;
        var o = Block(entry.AsSpan(0x18, 0x130).ToArray(), xf);
        float O(int off) => BinaryPrimitives.ReadSingleLittleEndian(o.AsSpan(off));

        var r = new byte[RecordSize];
        void Put(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(at), v);
        void PutU(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), v);
        o.AsSpan(0, 0x40).CopyTo(r);
        float[] table = view.Reversed ? [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 1, 0, 0, 0, 1] : [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        var m40 = new float[16];
        for (var i = 0; i < 16; i++)
            m40[i] = O(0x40 + 4 * i);
        // 1804983c0: each element a DPPS of the row and the column.
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                Put(0x40 + 4 * (i * 4 + j), CubemapFaceViews.Dot4(m40[i * 4], m40[i * 4 + 1], m40[i * 4 + 2], m40[i * 4 + 3], table[j], table[4 + j], table[8 + j], table[12 + j]));
        o.AsSpan(0x90, 0x10).CopyTo(r.AsSpan(0x80));
        // +0xf0: the slot's row of 1805fe770, the light's channel in direct_light_shadows ((0, 0, 0, 0) for -1, then the
        // unit rows for 0 .. 3).
        if (slot >= 0)
            Put(0xf0 + 4 * slot, 1f);
        Put(0x90, O(0xc8));
        Put(0x94, O(0xcc));
        Put(0x98, O(0xac));
        Put(0x9c, O(0xb0));
        entry.AsSpan(0x174, 0x10).CopyTo(r.AsSpan(0xd0));
        entry.AsSpan(0x1dc, 4).CopyTo(r.AsSpan(0x19c));
        var second = (view.Flags >> 25 & 1) != 0;
        Put(0x104, second ? E(0x1e4) : E(0x1e0));
        var intensity = E(0x1b8);
        Put(0xa0, O(0xb4) * intensity);
        Put(0xa4, O(0xb8) * intensity);
        Put(0xa8, O(0xbc) * intensity);
        Put(0xe0, O(0xa0));
        Put(0xe8, O(0xa8));
        Put(0xe4, O(0xa4));
        var falloff = Falloff(O(0x9c), 100f);
        Put(0x190, falloff * E(0x1ec) * intensity);
        Put(0x194, falloff * E(0x1f0) * intensity);
        Put(0x198, falloff * E(0x1f4) * intensity);
        var scale = second ? E(0x1c4) : E(0x1c0);
        Put(0xa8, scale * BitConverter.ToSingle(r, 0xa8));
        Put(0xa4, scale * BitConverter.ToSingle(r, 0xa4));
        Put(0xa0, scale * BitConverter.ToSingle(r, 0xa0));
        float R(int at) => BitConverter.ToSingle(r, at);
        if ((R(0xd0) == 0f && R(0xd4) == 0f && R(0xd8) == 0f && R(0xdc) == 0f) || R(0x104) == 0f || entry[0x205] != 0)
        {
            Put(0x104, 0f);
            Array.Clear(r, 0xd0, 0x10);
        }
        if ((BinaryPrimitives.ReadUInt32LittleEndian(entry[0x214..]) & 0x100) != 0)
            Illumination(entry, view.Camera, r);
        Put(0x100, 0.04f <= E(0x210) ? E(0x210) : 0.04f);
        PutU(0xcc, shape);
        var bits = 0u;
        if (entry[0x206] != 0)
            bits |= 1;
        if (entry[0x208] != 0)
            bits |= 4;
        if ((sbyte)entry[0x1ea] != -1)
            bits |= 8;
        PutU(0x108, (uint)BinaryPrimitives.ReadUInt16LittleEndian(entry[0x1fc..]) << 16 | bits);
        PutU(0x10c, 0xffffffff);
        o.AsSpan(0xd0, 0x1c).CopyTo(r.AsSpan(0xb0));
        Shape(shape, O, Put, R);
        var radius = R(0x14c) <= 0.25f ? 0.25f : R(0x14c);
        Put(0x15c, O(0xc0));
        Put(0x18c, 1f / radius);
        Put(0x14c, radius);
        if (R(0x8c) == 0f)
        {
            Put(0x140, R(0x140) * 1e6f + view.ViewOrigin.X);
            Put(0x144, R(0x144) * 1e6f + view.ViewOrigin.Y);
            Put(0x148, R(0x148) * 1e6f + view.ViewOrigin.Z);
        }
        return r;
    }

    /// <summary>1804a6570 (a point's w, 100): (sqrt(w) + k)^2 / w, i.e. 1 / (w / (sqrt(w) + k)^2); 1 unless w > 0.</summary>
    public static float Falloff(float w, float k)
    {
        if (!(w > 0f))
            return 1f;
        var s = MathF.Sqrt(w) + k;
        return 1f / (w / (s * s));
    }

    /// <summary>The record's +0x140 .. +0x14c (centre and radius) by shape type, 1801b14b0's tail.</summary>
    private static void Shape(byte shape, Func<int, float> o, Action<int, float> put, Func<int, float> r)
    {
        float P(int i) => o(0xf0 + 4 * i);
        if (shape == 1)
        {
            // Four points, laid out third, first, second, fourth; the centre is their mean, the radius the farthest.
            put(0x150, P(6)); put(0x154, P(7)); put(0x158, P(8));
            put(0x160, P(0)); put(0x164, P(1)); put(0x168, P(2));
            put(0x170, P(3)); put(0x174, P(4)); put(0x178, P(5));
            put(0x180, P(9)); put(0x184, P(10)); put(0x188, P(11));
            var cy = (P(4) + P(1) + P(7) + P(10)) * 0.25f;
            var cx = (P(3) + P(0) + P(6) + P(9)) * 0.25f;
            var cz = (P(5) + P(2) + P(8) + P(11)) * 0.25f;
            float D(int i) => (cy - P(i * 3 + 1)) * (cy - P(i * 3 + 1)) + (cx - P(i * 3)) * (cx - P(i * 3)) + (cz - P(i * 3 + 2)) * (cz - P(i * 3 + 2));
            var a = D(0);
            var b = D(1);
            if (a <= b)
                a = b;
            var c = D(3);
            var d = D(2);
            if (d <= c)
                d = c;
            if (a <= d)
                a = d;
            put(0x140, cx); put(0x144, cy); put(0x148, cz);
            put(0x14c, MathF.Sqrt(a));
            return;
        }
        if (shape == 2)
        {
            // A point and three vectors: the four corners (p + u - v ...), radius the longer of u and v.
            float px = P(0), py = P(1), pz = P(2), ux = P(3), uy = P(4), uz = P(5), vx = P(6), vy = P(7), vz = P(8);
            put(0x150, (ux + px) - vx); put(0x154, (uy + py) - vy); put(0x158, (uz + pz) - vz);
            put(0x160, (px - ux) - vx); put(0x164, (py - uy) - vy); put(0x168, (pz - uz) - vz);
            put(0x170, vx + (px - ux)); put(0x174, vy + (py - uy)); put(0x178, vz + (pz - uz));
            put(0x180, vx + ux + px); put(0x184, vy + uy + py); put(0x188, vz + uz + pz);
            var lv = vx * vx + vy * vy + vz * vz;
            var lu = uy * uy + ux * ux + uz * uz;
            if (lu <= lv)
                lu = lv;
            put(0x140, px); put(0x144, py); put(0x148, pz);
            put(0x14c, MathF.Sqrt(lu));
            return;
        }
        // Any other type: the position, radius 0 (then clamped to 0.25).
        put(0x140, r(0x80));
        put(0x144, r(0x84));
        put(0x148, r(0x88));
        put(0x14c, 0f);
    }

    /// <summary>
    /// The illumination volume (entry +0x214 bit 8): the inverse of its rotation (+0x340, over dpps(q, q), the
    /// identity quaternion 180923fb0 when that is 0) with the rotated camera-relative centre (+0x350), rows times
    /// 0.999 / its extents (+0x35c), at +0x110 .. +0x13c.
    /// </summary>
    private static void Illumination(byte[] entry, Vector3 camera, byte[] r)
    {
        float E(int o) => BinaryPrimitives.ReadSingleLittleEndian(entry.AsSpan(o));
        float qx = E(0x340), qy = E(0x344), qz = E(0x348), qw = E(0x34c);
        float vz = camera.Z - E(0x358), vx = camera.X - E(0x350), vy = camera.Y - E(0x354);
        var n = CubemapFaceViews.Dot4(qx, qy, qz, qw, qx, qy, qz, qw);
        var qi = n == 0f ? new Vector4(0, 0, 0, 1) : new Vector4(-qx / n, -qy / n, -qz / n, qw / n);
        float t0 = qz * vy - qy * vz, t1 = qx * vz - qz * vx, t2 = qy * vx - qx * vy;
        t0 += t0;
        t1 += t1;
        t2 += t2;
        // Vectorised at 1801b1a94 .. 1801b1aad: cross(q, t) + (v + w t). The decompile prints it as
        // (cross + v) + w t, which is one ulp off on 8 of the 64 captured pairs.
        var tx = (qz * t1 - qy * t2) + (vx + qw * t0);
        var ty = (qx * t2 - qz * t0) + (vy + qw * t1);
        var tz = (qy * t0 - qx * t1) + (vz + qw * t2);
        var m = Lighting.Bake.PathTraceBounds.Rotation(qi, new Vector3(tx, ty, tz));
        float[] s = [0.999f / E(0x35c), 0.999f / E(0x360), 0.999f / E(0x364)];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 4; col++)
                BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(0x110 + 4 * (row * 4 + col)), s[row] * m[row * 4 + col]);
    }

    /// <summary>
    /// 1804a65f0: the description (copied, 18008beb0) with its spatial fields moved through <paramref name="xf"/>
    /// (camera-relative 3 x 4).
    /// </summary>
    public static byte[] Block(byte[] desc, float[] xf)
    {
        var o = (byte[])desc.Clone();
        float D(int off) => BinaryPrimitives.ReadSingleLittleEndian(desc.AsSpan(off));
        void Put(int off, float v) => BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(off), v);
        var inv = InvertRigid(xf);
        var d0 = new float[16];
        var d40 = new float[16];
        for (var i = 0; i < 16; i++)
        {
            d0[i] = D(4 * i);
            d40[i] = D(0x40 + 4 * i);
        }
        float[] inv4 = [.. inv, 0, 0, 0, 1];
        float[] xf4 = [.. xf, 0, 0, 0, 1];
        var a = Mul4(d0, inv4);
        var b = Mul4(xf4, d40);
        for (var i = 0; i < 16; i++)
        {
            Put(4 * i, a[i]);
            Put(0x40 + 4 * i, b[i]);
        }
        // Points and vectors go through a copy whose translation is dropped when the light is directional (+0x9c 0).
        var t = (float[])xf.Clone();
        if (D(0x9c) == 0f)
            t[3] = t[7] = t[11] = 0f;
        var p = AggregateCulling.TransformPoint(t, new Vector3(D(0x90), D(0x94), D(0x98)));
        Put(0x90, p.X);
        Put(0x94, p.Y);
        Put(0x98, p.Z);
        Vector3 At(int off) => new(D(off), D(off + 4), D(off + 8));
        void Store(int off, Vector3 v)
        {
            Put(off, v.X);
            Put(off + 4, v.Y);
            Put(off + 8, v.Z);
        }
        var shape = desc[0xec];
        if (shape == 1)
        {
            foreach (var off in new[] { 0xf0, 0xfc, 0x108, 0x114 })
                Store(off, AggregateCulling.TransformPoint(t, At(off)));
            Store(0x120, Rotate(t, At(0x120)));
        }
        else if (shape == 2)
        {
            Store(0xf0, AggregateCulling.TransformPoint(t, At(0xf0)));
            foreach (var off in new[] { 0xfc, 0x108, 0x114 })
                Store(off, Rotate(t, At(off)));
        }
        else if (shape is >= 3 and <= 5)
            throw new NotSupportedException($"1804a65f0: shape type {shape} is not ported");
        // +0xd0: the rotation through the transform (quaternion -> 3 x 4 -> concat -> quaternion).
        var q = Lighting.Bake.PathTraceBounds.Rotation(new Vector4(D(0xd0), D(0xd4), D(0xd8), D(0xdc)), Vector3.Zero);
        var c = Concat(xf, q);
        var qq = Quaternion(c);
        Put(0xd0, qq.X);
        Put(0xd4, qq.Y);
        Put(0xd8, qq.Z);
        Put(0xdc, qq.W);
        return o;
    }

    /// <summary>18049b3b0: the rotation transposed, the translation -((t2 r2 + t1 r1) + t0 r0) per row.</summary>
    public static float[] InvertRigid(float[] m)
    {
        float[] d = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0];
        float t0 = m[3], t1 = m[7], t2 = m[11];
        for (var row = 0; row < 3; row++)
            d[row * 4 + 3] = -((t2 * d[row * 4 + 2] + t1 * d[row * 4 + 1]) + t0 * d[row * 4]);
        return d;
    }

    /// <summary>180499080: a b for row-major 4 x 4s, each element ((a0 b0 + a1 b1) + a2 b2) + a3 b3.</summary>
    public static float[] Mul4(float[] a, float[] b)
    {
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = ((a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j]) + a[i * 4 + 2] * b[8 + j]) + a[i * 4 + 3] * b[12 + j];
        return r;
    }

    /// <summary>18049c360: a vector through the rows' rotation, z r2 + (y r1 + x r0).</summary>
    public static Vector3 Rotate(float[] m, Vector3 v)
    {
        float Row(int r) => v.Z * m[r * 4 + 2] + (v.Y * m[r * 4 + 1] + v.X * m[r * 4]);
        return new Vector3(Row(0), Row(1), Row(2));
    }

    /// <summary>180499f90: a b for row-major 3 x 4s, row i ((a_i2 b2 + a_i1 b1) + a_i0 b0) + (0, 0, 0, a_i3).</summary>
    public static float[] Concat(float[] a, float[] b)
    {
        var r = new float[12];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = ((a[i * 4 + 2] * b[8 + j] + a[i * 4 + 1] * b[4 + j]) + a[i * 4] * b[j]) + (j == 3 ? a[i * 4 + 3] : 0f);
        return r;
    }

    /// <summary>18049d420: a 3 x 4's rotation as a quaternion (x, y, z, w), largest-diagonal branch.</summary>
    public static Vector4 Quaternion(float[] m)
    {
        float m0 = m[0], m1 = m[1], m2 = m[2], m4 = m[4], m5 = m[5], m6 = m[6], m8 = m[8], m9 = m[9], m10 = m[10];
        var trace = (m0 + m5) + m10;
        if (0f <= trace || float.IsNaN(trace))
        {
            var s = MathF.Sqrt(trace + 1f);
            var f = 0.5f / s;
            return new Vector4((m9 - m6) * f, (m2 - m8) * f, (m4 - m1) * f, s * 0.5f);
        }
        if (m10 > m0 && m10 > m5)
        {
            var s = MathF.Sqrt((m10 - (m0 + m5)) + 1f);
            var f = 0.5f / s;
            return new Vector4((m8 + m2) * f, (m9 + m6) * f, s * 0.5f, (m4 - m1) * f);
        }
        if (m10 <= m0 && !(m0 < m5))
        {
            var s = MathF.Sqrt((m0 - (m10 + m5)) + 1f);
            var f = 0.5f / s;
            return new Vector4(s * 0.5f, (m4 + m1) * f, (m8 + m2) * f, (m9 - m6) * f);
        }
        {
            var s = MathF.Sqrt((m5 - (m10 + m0)) + 1f);
            var f = 0.5f / s;
            return new Vector4((m4 + m1) * f, s * 0.5f, (m9 + m6) * f, (m2 - m8) * f);
        }
    }
}
