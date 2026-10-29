using System.Buffers.Binary;
using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// PerViewLightProbeVolumeConstantBuffer_t (57600 bytes, set 1 binding 2 of the probe-lit programs; CUBEMAPS.md A.1):
/// 400 records of 0x90. scenesystem 1801ba530 fills every slot with the default record (1801ba3e0), then one per
/// scene probe-volume object, each the object's +0x130 block, which client 1.41.9.3 1802819e0 (the probe volume
/// entity's update) builds:
/// <list type="bullet">
/// <item>+0x00: the box-to-UVW 3 x 4 (18026fea0: 1 / size, -min / size) times the inverse of the entity's world
/// 3 x 4 (181745ff0, a rigid inverse), concatenated by 181743d40. The box is box_mins / box_maxs
/// (1802755e0; grown by edge_fade_dists only with WorldRenderer/LPVEdgeBlending, off in CS2).</item>
/// <item>+0x30: half-texel borders of the probe texture (0.5 / its width, height, depth; 1 - those; 1/6 - the depth
/// one), +0x3c the texture residency bits, +0x4c / +0x6c / +0x7c / +0x88 / +0x8c bindless indices.</item>
/// <item>+0x50: the entity's +0x70 .. +0x78, +0x5c the SDF fade; +0x60 / +0x70 the probe texture against the
/// entity's light_probe_size counts; +0x80 the second texture's half texel in depth.</item>
/// </list>
/// Only an unbaked volume is ported: every probe texture is the engine's default (vfn 0x198 type 6: a 2 x 2 x 2
/// R8G8B8A8 image of zeros, measured in atixref's capture), so the borders are 0.25 / 0.75, the residency bits 0
/// and the count ratios 1. The entity's +0x70 .. +0x78 (1, 1, 1) and the SDF fade (0) are measured on all 42 of
/// atixref's volumes, not mapped to keys. A rotated volume is refused: atixref has none, so the angle matrix is
/// not checked.
/// </summary>
public static class LightProbeVolumeRecords
{
    public const int Count = 400, Size = 0x90;

    /// <summary>The engine's default probe texture's side (type 6, measured: 2 x 2 x 2).</summary>
    public const int DefaultTextureSide = 2;

    /// <summary>One volume as the entity gives it, in the scene's object order.</summary>
    public sealed record Volume(Vector3 Origin, Vector3 Angles, Vector3 BoxMins, Vector3 BoxMaxs, int ProbesX, int ProbesY, int ProbesZ);

    /// <summary>
    /// The whole buffer: <paramref name="volumes"/>' records, then default records. <paramref name="defaultTexture"/>
    /// is our bindless index for the default probe texture, <paramref name="defaultTexture15"/> for type 0x15's
    /// (the +0x8c slot; no program on atixref reads it).
    /// </summary>
    public static byte[] Buffer(IReadOnlyList<Volume> volumes, int defaultTexture, int defaultTexture15)
    {
        if (volumes.Count > Count)
            throw new NotSupportedException($"{volumes.Count} light probe volumes: the buffer holds {Count}");
        var b = new byte[Count * Size];
        for (var i = 0; i < Count; i++)
        {
            var r = b.AsSpan(i * Size, Size);
            if (i < volumes.Count)
                Record(volumes[i], defaultTexture, defaultTexture15).CopyTo(r);
            else
                Default(r, defaultTexture, defaultTexture15);
        }
        return b;
    }

    /// <summary>1801ba3e0: zeros and the default textures' indices at +0x4c, +0x6c, +0x88 (type 0xd) and +0x8c (0x15).</summary>
    public static void Default(Span<byte> r, int defaultTexture, int defaultTexture15)
    {
        r.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(r[0x4c..], defaultTexture);
        BinaryPrimitives.WriteInt32LittleEndian(r[0x6c..], defaultTexture);
        BinaryPrimitives.WriteInt32LittleEndian(r[0x88..], defaultTexture);
        BinaryPrimitives.WriteInt32LittleEndian(r[0x8c..], defaultTexture15);
    }

    /// <summary>One volume's record (client 1802819e0's writes, in order).</summary>
    public static byte[] Record(Volume v, int defaultTexture, int defaultTexture15)
    {
        if (v.Angles != Vector3.Zero)
            throw new NotSupportedException("a rotated light probe volume: the entity's angle matrix is not checked");
        var r = new byte[Size];
        void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(at), value);
        void I(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(at), value);

        // 18026fea0's box matrix: each axis 1 / (max - min), its translation ((-inv) * min) * 1.
        float ix = 1f / (v.BoxMaxs.X - v.BoxMins.X), iy = 1f / (v.BoxMaxs.Y - v.BoxMins.Y), iz = 1f / (v.BoxMaxs.Z - v.BoxMins.Z);
        float[] box =
        [
            1f * ix, 0f, 0f, -ix * v.BoxMins.X * 1f,
            0f, 1f * iy, 0f, -iy * v.BoxMins.Y * 1f,
            0f, 0f, 1f * iz, -iz * v.BoxMins.Z * 1f,
        ];
        float[] world = [1f, 0f, 0f, v.Origin.X, 0f, 1f, 0f, v.Origin.Y, 0f, 0f, 1f, v.Origin.Z];
        var m = Concat(box, InvertRigid(world));
        for (var i = 0; i < 12; i++)
            F(i * 4, m[i]);

        // The probe texture is the default: half texels of 2 x 2 x 2, both count-ratio sets left at their start.
        const float side = DefaultTextureSide;
        float hx = 0.5f / side, hy = 0.5f / side, hz = 0.5f / side;
        F(0x30, hx);
        F(0x34, hy);
        F(0x38, hz);
        I(0x3c, 0);
        F(0x40, 1f - hx);
        F(0x44, 1f - hy);
        F(0x48, 1f / 6f - hz);
        I(0x4c, defaultTexture);
        F(0x50, 1f);
        F(0x54, 1f);
        F(0x58, 1f);
        F(0x5c, 0f);
        // +0x60 .. +0x68 become count / dimension only when a count is below the texture's (z counts six slices).
        var smaller = v.ProbesX > 0 && v.ProbesY > 0 && v.ProbesZ > 0
                      && !(v.ProbesX >= DefaultTextureSide && v.ProbesY >= DefaultTextureSide && v.ProbesZ * 6 >= DefaultTextureSide);
        if (smaller)
            throw new NotSupportedException("a light probe volume whose counts are below its probe texture's size");
        F(0x60, 1f);
        F(0x64, 1f);
        F(0x68, 1f);
        I(0x6c, defaultTexture);
        I(0x7c, defaultTexture);
        F(0x80, 0.5f / side);
        F(0x84, 1f - 0.5f / side);
        I(0x88, defaultTexture);
        I(0x8c, defaultTexture15);
        return r;
    }

    /// <summary>
    /// 181745ff0: the rotation transposed, each translation -(((ty * o1) + (tz * o2)) + (tx * o0)) of its new row.
    /// </summary>
    public static float[] InvertRigid(float[] m)
    {
        float[] o = [m[0], m[4], m[8], 0f, m[1], m[5], m[9], 0f, m[2], m[6], m[10], 0f];
        float tx = m[3], ty = m[7], tz = m[11];
        for (var r = 0; r < 3; r++)
            o[r * 4 + 3] = -((ty * o[r * 4 + 1] + tz * o[r * 4 + 2]) + tx * o[r * 4]);
        return o;
    }

    /// <summary>181743d40: row r = ((a_r2 * b_2 + a_r1 * b_1) + a_r0 * b_0) + (0, 0, 0, a_r3), per component.</summary>
    public static float[] Concat(float[] a, float[] b)
    {
        var o = new float[12];
        for (var r = 0; r < 3; r++)
            for (var k = 0; k < 4; k++)
                o[r * 4 + k] = ((a[r * 4 + 2] * b[8 + k] + a[r * 4 + 1] * b[4 + k]) + a[r * 4] * b[k]) + (k == 3 ? a[r * 4 + 3] : 0f);
        return o;
    }
}
