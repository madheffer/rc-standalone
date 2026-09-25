using System.Numerics;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Maps;

/// <summary>
/// The light record the precompute samples (300 bytes, laid out as in
/// resourcecompiler so the port can be compared with it byte for byte):
/// <code>
/// 0x00  4x4   light space to the unit volume (a barn's frustum, an omni face)
/// 0x40  4x4   its inverse
/// 0x80  vec4  the plane the volume opens from
/// 0x90  vec4  position; w 0 for a directional light
/// 0xa0  soft x, soft y (1 - soft), 0xa8 shape, 0xac/0xb0 skirt near/far
/// 0xb4  colour, 0xc0 intensity normalisation, 0xc4 cookie
/// 0xc8  distance falloff a, b; 0xd0 orientation; 0xe0 cone a, b, capsule length
/// 0xec  luminaire type (1 rectangle, 2 disc, 3 sphere, 4/5 capsule), 0xed flags
/// 0xf0  luminaire geometry
/// </code>
/// </summary>
public sealed class LightShape
{
    public const int Size = 300;

    public readonly byte[] Raw = new byte[Size];

    public Span<float> F => MemoryMarshal.Cast<byte, float>(Raw.AsSpan(0, 296));

    public ref byte Type => ref Raw[0xec];

    public ref byte Flags => ref Raw[0xed];

    public float this[int offset]
    {
        get => BitConverter.ToSingle(Raw, offset);
        set => BitConverter.TryWriteBytes(Raw.AsSpan(offset), value);
    }

    public Vector3 V3(int offset) => new(this[offset], this[offset + 4], this[offset + 8]);

    public void SetV3(int offset, Vector3 v)
    {
        this[offset] = v.X;
        this[offset + 4] = v.Y;
        this[offset + 8] = v.Z;
    }

    public Quaternion Orientation
    {
        get => new(this[0xd0], this[0xd4], this[0xd8], this[0xdc]);
        set
        {
            this[0xd0] = value.X;
            this[0xd4] = value.Y;
            this[0xd8] = value.Z;
            this[0xdc] = value.W;
        }
    }

    public Span<float> Matrix => F[..16];

    public Span<float> Inverse => F[16..32];

    public LightShape Clone()
    {
        var c = new LightShape();
        Raw.CopyTo(c.Raw, 0);
        return c;
    }
}
