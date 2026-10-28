namespace Source2.Compiler.SteamAudio;

/// <summary>
/// Steam Audio's automatic probes, as resourcecompiler 0923 (180100e40), steamaudio 1023 (180087940) and phonon 0923
/// (180a86090) make them for an "Automatic, Everywhere" bake:
/// <list type="bullet">
/// <item>the box is the acoustic geometry's vertex bounds in Source inches (centre (min + max) 0.5, size max - min)
/// with a null rotation (180100e40);</item>
/// <item>steamaudio turns it into phonon's row-major box matrix (18006b2d0): the unit axes rotated by the box's
/// quaternion, moved to Steam Audio's axes, scaled by the size in metres, the centre in metres as translation;</item>
/// <item>phonon's API transposes it (180ab3a20) and generateUniformFloorProbes runs inline (180a86090): a grid of
/// floor(s / spacing) + 1 columns over the box's x and z extents, each column cast down from the box top with a
/// single-ray closest hit in [height, height + remaining], a probe at hit - height above it with radius = spacing,
/// and the cast continued 0.01 below the hit;</item>
/// <item>each probe goes back to Source inches (18006b260: axes, then x 39.37008; radius x 39.37008), through
/// resourcecompiler's filters, and into the bake as metres again (180063600: x 0.0254, then axes; radius x 0.0254).</item>
/// </list>
/// No FMA anywhere here (MSVC, /fp:precise).
/// </summary>
public static class ProbeGeneration
{
    /// <summary>Source (x, y, z) to Steam Audio: (-y, z, -x) (steamaudio 1800635d0).</summary>
    public static (float, float, float) ToSteamAudio(float x, float y, float z) => (-y, z, -x);

    /// <summary>Steam Audio to Source: (-z, -x, y) (1800636b0).</summary>
    public static (float, float, float) ToSource(float x, float y, float z) => (-z, -x, y);

    /// <summary>180063600: inches to metres, then the axes.</summary>
    public static (float, float, float) SourceToMeters(float x, float y, float z) => ToSteamAudio(x * 0.0254f, y * 0.0254f, z * 0.0254f);

    /// <summary>18006b260: the axes, then metres to inches.</summary>
    public static (float, float, float) MetersToSource(float x, float y, float z)
    {
        var (a, b, c) = ToSource(x, y, z);
        return (a * 39.37008f, b * 39.37008f, c * 39.37008f);
    }

    /// <summary>
    /// 18006b2d0: the box (quaternion q, centre, size, Source inches) as phonon's row-major 4x4. The unit vectors it
    /// rotates are Y, Z and X (the runtime constants at 180339040, 180339070, 180339020, as the captured matrices
    /// show them) for the matrix's columns 0, 1 and 2.
    /// </summary>
    public static float[] BoxMatrix(float qx, float qy, float qz, float qw, float cx, float cy, float cz, float sx, float sy, float sz)
    {
        (float, float, float) Rotate(float vx, float vy, float vz)
        {
            var t0 = vz * qy - vy * qz;
            var t1 = vx * qz - vz * qx;
            var t2 = vy * qx - vx * qy;
            t0 += t0; t1 += t1; t2 += t2;
            return ((t2 * qy - t1 * qz) + qw * t0 + vx, (t0 * qz - t2 * qx) + qw * t1 + vy, (t1 * qx - t0 * qy) + qw * t2 + vz);
        }
        // 1800339040 / 70 / 20 hold Y, Z and X.
        var (ax, ay, az) = Rotate(0, 1, 0);
        var a0 = (0.0f - ax, 0.0f - ay, 0.0f - az);
        var a1 = Rotate(0, 0, 1);
        var a2 = Rotate(1, 0, 0);
        var t = ToSteamAudio(cx * 0.0254f, cy * 0.0254f, cz * 0.0254f);
        var c0 = ToSteamAudio(a0.Item1, a0.Item2, a0.Item3);
        var c1 = ToSteamAudio(a1.Item1, a1.Item2, a1.Item3);
        var c2 = ToSteamAudio(a2.Item1, a2.Item2, a2.Item3);
        var s = ToSteamAudio(sx * 0.0254f, sy * 0.0254f, sz * 0.0254f);
        var m = new float[16];
        m[0] = s.Item1 * c0.Item1; m[4] = s.Item1 * c0.Item2; m[8] = s.Item1 * c0.Item3;
        m[1] = s.Item2 * c1.Item1; m[5] = c1.Item2 * s.Item2; m[9] = c1.Item3 * s.Item2;
        m[2] = -c2.Item1 * s.Item3; m[6] = -c2.Item2 * s.Item3; m[10] = -c2.Item3 * s.Item3;
        m[3] = t.Item1; m[7] = t.Item2; m[11] = t.Item3;
        m[12] = 0; m[13] = 0; m[14] = 0; m[15] = 1;
        return m;
    }

    /// <summary>180a71a30: phonon's column-major matrix p times (v0, v1, v2, v3).</summary>
    private static (float, float, float) Transform(float[] p, float v0, float v1, float v2, float v3)
        => ((((p[0] * v0 + 0.0f) + p[4] * v1) + p[8] * v2) + p[12] * v3,
            (((p[1] * v0 + 0.0f) + p[5] * v1) + p[9] * v2) + p[13] * v3,
            (((p[2] * v0 + 0.0f) + p[6] * v1) + p[10] * v2) + p[14] * v3);

    private static float Length(float x, float y, float z) => MathF.Sqrt(((x * x + 0.0f) + y * y) + z * z);

    /// <summary>
    /// generateUniformFloorProbes as phonon 0923 inlines it: <paramref name="rowMajor"/> is the API's matrix.
    /// Probes are (x, y, z, radius) in metres, Steam Audio axes.
    /// </summary>
    public static List<Sphere> UniformFloor(TriangleScene scene, float[] rowMajor, float spacing, float height)
    {
        var p = new float[16];
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                p[c * 4 + r] = rowMajor[r * 4 + c];
        var probes = new List<Sphere>();
        var sx = Length(p[0], p[1], p[2]);
        var sy = Length(p[4], p[5], p[6]);
        var sz = Length(p[8], p[9], p[10]);
        if (!(sx >= 1.1754943508222875e-38f) || !(sy >= 1.1754943508222875e-38f) || !(sz >= 1.1754943508222875e-38f))
            return probes;
        var fx = (int)MathF.Floor(sx / spacing);
        var fz = (int)MathF.Floor(sz / spacing);
        int nx = fx + 1, nz = fz + 1;
        var residualX = (sx - (float)fx * spacing) * 0.5f;
        var residualZ = (sz - (float)fz * spacing) * 0.5f;
        var (dx, dy, dz) = Transform(p, 0.0f, -1.0f, 0.0f, 0.0f);
        var dl = Length(dx, dy, dz);
        if (!(1e-5f < dl))
            throw new NotSupportedException("a degenerate probe generation box is not ported");
        var inv = 1.0f / dl;
        dx *= inv; dy *= inv; dz *= inv;
        for (var i = 0; i < nx; i++)
        {
            var xPos = ((float)i * spacing + residualX) / sx - 0.5f;
            for (var j = 0; j < nz; j++)
            {
                var zPos = ((float)j * spacing + residualZ) / sz - 0.5f;
                var (ox, oy, oz) = Transform(p, xPos, 0.5f, zPos, 1.0f);
                var distance = Length(p[4], p[5], p[6]);
                while (distance > 0.0f)
                {
                    var hit = scene.ClosestHit1(ox, oy, oz, dx, dy, dz, height, height + distance);
                    if (!(float.PositiveInfinity > hit))
                        break;
                    var f = hit - height;
                    probes.Add(new Sphere(dx * f + ox, oy + dy * f, oz + dz * f, spacing));
                    var step = hit + 0.01f;
                    ox += dx * step;
                    oy += dy * step;
                    oz += dz * step;
                    distance -= step;
                }
            }
        }
        return probes;
    }

    /// <summary>A generated probe through resourcecompiler (Source inches) and back to the bake's metres.</summary>
    public static Sphere RoundTrip(Sphere probe)
    {
        var (x, y, z) = MetersToSource(probe.X, probe.Y, probe.Z);
        var r = probe.Radius * 39.37008f;
        var (a, b, c) = SourceToMeters(x, y, z);
        return new Sphere(a, b, c, r * 0.0254f);
    }
}
