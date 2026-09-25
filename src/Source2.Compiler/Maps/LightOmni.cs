using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// FUN_181298170: an omni light's records in its own space. Whole, one record
/// looks down x through a projective box that holds the cone's reach; split
/// (<c>directional</c>), the light is cut into up to six cube-face frusta of
/// size (p9, p9, 1 / p9), fewer when the cone is narrow enough.
/// </summary>
public static class LightOmni
{
    private const float HalfPi = 1.5707964f;
    private const float Degrees = 0.017453292f;

    /// <summary>nexttowardf: one step from <paramref name="x"/> towards <paramref name="to"/> (FUN_182035f5c).</summary>
    public static float NextToward(float x, float to)
    {
        if (float.IsNaN(x) || x == to)
            return x == to ? to : x;
        return to > x ? MathF.BitIncrement(x) : MathF.BitDecrement(x);
    }

    // The rotation that puts face i on the x axis (rows; the fourth is 0 0 0 1)
    // and the extra row of the second pass per face, from the two jump tables
    // at 0x181298a90 and 0x181298aa8.
    private static float[] FaceRotation(int face, float p9) => face switch
    {
        0 => [1, 0, 0, -p9, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        1 => [0, 0, -1, -p9, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1],
        2 => [0, 0, 1, -p9, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1],
        3 => [0, 1, 0, -p9, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        4 => [0, -1, 0, -p9, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        _ => [-1, 0, 0, -p9, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
    };

    private static float[] FaceSquash(int face, float square, float f) => face switch
    {
        0 => [square, 0, 0, 0, 0, square, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        1 => [1, 0, 0, 0, 0, f, 0, 1f - f, 0, 0, 1, 0, 0, 0, 0, 1],
        2 => [1, 0, 0, 0, 0, f, 0, f - 1f, 0, 0, 1, 0, 0, 0, 0, 1],
        3 => [f, 0, 0, 1f - f, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        4 => [f, 0, 0, f - 1f, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
        _ => LightMath.Identity4(),
    };

    /// <summary>
    /// The records: one, or with <paramref name="directional"/> 1, 5 or 6 by
    /// the cone's half angle (at most 45, 135 degrees or more). <paramref name="length"/>
    /// is the capsule's half length (0 for a sphere); <paramref name="p9"/> is
    /// 1 from every caller.
    /// </summary>
    public static LightShape[] Frusta(bool directional, float range, float outer, float inner, float length,
                                      float skirt, float p9, Vector3 colour = default)
    {
        var angle = outer > 1f ? outer : 1f;
        angle = angle < 180f ? angle : 180f;
        var radians = angle * Degrees;
        var cone = radians;
        LightCrt.SinCos(cone, out var sin, out var cos);
        sin = sin > 0f ? sin : 0f;
        var reach = range;
        if (HalfPi > cone)
        {
            reach = range - length;
            reach = reach > 0f ? reach : 0f;
            reach = reach * sin + length;
        }
        if (cone > HalfPi)
            cone = LightCrt.Atan2(sin * 0.70710677f, cos);
        var back = 0f;
        if (cone > HalfPi)
            back = LightCrt.Cos(3.1415927f - cone) * range;
        if (length > 0f)
            cone = HalfPi > cone ? HalfPi : cone;
        var count = 1;
        if (directional)
        {
            count = 6;
            if (2.3561945f >= cone)
                count = 5;
            if (0.7853982f >= cone)
                count = 1;
        }

        var l = new LightShape();
        l.SetV3(0xb4, colour);
        l.Flags |= 2;
        l[0xc4] = BitConverter.Int32BitsToSingle(-1);
        l[0xa4] = 0.99999988f;
        l[0xa0] = 0.99999988f;
        l[0xac] = 0f;
        l[0xb0] = 0f;
        var near = (1f - skirt) * range;
        var next = NextToward(near, float.MaxValue);
        var far = range > next ? range : next;
        l[0xcc] = 1f / (near - far);
        l[0xc8] = far / (far - near);
        l.Orientation = Quaternion.Identity;
        float coneA, coneB = 0f, coneC = length;
        if (inner >= 180f)
            coneA = 1f;
        else
        {
            var cosInner = 0f < inner ? LightCrt.Cos(inner * Degrees) : 1f;
            if (0f >= angle)
            {
                coneA = 0f;
                coneC = 0f;
            }
            else
            {
                var cosOuter = angle < 180f ? LightCrt.Cos(radians) : -1f;
                var below = NextToward(cosInner, -float.MaxValue);
                cosOuter = cosOuter < below ? cosOuter : below;
                var d = cosOuter - cosInner;
                coneA = cosOuter / d;
                coneB = 1f / d;
            }
        }
        l[0xe0] = coneA;
        l[0xe4] = coneB;
        l[0xe8] = coneC;
        l.SetV3(0x90, Vector3.Zero);
        l[0x9c] = 1f;
        l.Flags &= 0xfe;
        var square = 1f;
        var f = float.PositiveInfinity;
        if (cone >= 2.3561945f)
            f = 1f;
        else if (cone > HalfPi)
            f = 1f / (LightCrt.Tan(cone - HalfPi) * 0.5f + 0.5f);
        else if (cone > 0.7853982f)
            f = 1f / (0.5f - LightCrt.Tan(HalfPi - cone) * 0.5f);
        else
            square = 1f / LightCrt.Tan(cone);

        if (!directional)
        {
            var depth = 1f / (back + range);
            var across = 1f / reach;
            float[] m = [0, 0, across, 0, 0, across, 0, 0, depth, 0, 0, depth * back, 0, 0, 0, 1];
            Finish(l, m);
            l.Flags |= 1;
            return [l];
        }

        var records = new LightShape[count];
        var size = new Vector3(p9, p9, 1f / p9);
        for (var i = 0; i < count; i++)
        {
            var r = i == 0 ? l : records[0].Clone();
            var distance = i == 0 ? range : i == 5 ? back : reach;
            var barn = LightBuild.BarnMatrix(size, distance - p9, Vector2.Zero);
            var m = LightMath.Mul4(barn, FaceRotation(i, p9));
            m.CopyTo(r.Matrix);
            LightMath.Mul4(FaceSquash(i, square, f), m).CopyTo(r.Matrix);
            records[i] = r;
        }
        foreach (var r in records)
            Finish(r, r.Matrix.ToArray());
        return records;
    }

    private static void Finish(LightShape l, float[] m)
    {
        m.CopyTo(l.Matrix);
        LightMath.Inverse4(m, l.Inverse);
        var plane = LightBuild.Plane(l.Inverse);
        l[0x80] = plane.X;
        l[0x84] = plane.Y;
        l[0x88] = plane.Z;
        l[0x8c] = plane.W;
    }
}
