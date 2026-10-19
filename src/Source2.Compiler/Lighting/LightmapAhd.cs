namespace Source2.Compiler.Lighting;

/// <summary>
/// lightmap_image_encode_ahd (Run 18004ce00, rows 18004cfc0): the SH lightmap
/// to ambient + highlight direction. Everything is single precision in vrad3's
/// operation order (read from the disassembly where Ghidra's print was unsure).
/// </summary>
public static class LightmapAhd
{
    private static readonly float Tolerance = BitConverter.Int32BitsToSingle(0x1e3ce508);

    /// <summary>
    /// Per valid luxel, rows in order and left to right (the left neighbour's
    /// output is read):
    /// <list type="number">
    /// <item>The SH of each colour goes to tangent space (<see cref="RotateSh"/>
    /// by the inverse of the luxel's frame, <see cref="InverseFrame"/>) and a
    /// luminance SH is formed: g 0.7152 + (r 0.2126 + b 0.0722).</item>
    /// <item>Its L0 under 1e-5: the irradiance becomes the three colours' L0
    /// and channel 1 (0, 0, 1, 1).</item>
    /// <item>Else d = normalised (-l3, -l1, l2). Pointing down with xy under
    /// 1e-4: the left luxel's channel 1 xyz when x &gt; 0 and it is the same
    /// chart, else z flips and the irradiance is zeroed. d.z under 0.0872 is
    /// raised to it with xy rescaled to length sqrt(0.992396176).</item>
    /// <item>Over the 256 directions w, c = max(w.d, 0) / d.z and L the
    /// irradiance luminance: A = (1 - c) L, b = max(SH(w), 0) - c L, solved
    /// by <see cref="Solve"/> and clamped to 0..1. Channel 1 = (d, solution).</item>
    /// </list>
    /// Then, as Run does: channels 2 to 7 are freed, channel 0 moves to 14,
    /// channel 15 (mask 1) takes channel 1's w and channel 13 (mask 7) its xyz,
    /// and channel 1 is freed.
    /// </summary>
    public static void Encode(LightmapImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var irradiance = image.Channels[LightmapImage.Ch.Irradiance]!;
        var direction = image.Channels[LightmapImage.Ch.ShR]!;
        var mask = irradiance.Mask;
        Span<float> sh = stackalloc float[27];
        Span<float> m = stackalloc float[12];
        Span<float> lum = stackalloc float[9];
        var a = new float[256];
        var b = new float[256];
        var dirs = AhdDirections.Xyz;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                if (!image.Valid(p))
                    continue;
                var r = (mask & 1) != 0 ? irradiance.Data[p * 4] : 0f;
                var g = (mask & 2) != 0 ? irradiance.Data[p * 4 + 1] : 0f;
                var bl = (mask & 4) != 0 ? irradiance.Data[p * 4 + 2] : 0f;
                LightmapPost.ReadSh(image, p, sh);
                InverseFrame(image, p, m);
                RotateSh(sh[..9], m);
                RotateSh(sh.Slice(9, 9), m);
                RotateSh(sh.Slice(18, 9), m);
                for (var k = 0; k < 9; k++)
                    lum[k] = sh[9 + k] * 0.7152f + (sh[k] * 0.2126f + sh[18 + k] * 0.0722f);
                if (1e-05f > lum[0])
                {
                    SetMasked(irradiance, p, sh[0], sh[9], sh[18]);
                    SetMasked(direction, p, 0f, 0f, 1f, 1f);
                    continue;
                }
                float dx = -lum[3], dy = -lum[1], dz = lum[2];
                // 18004d64e: dx dx + (dz dz + dy dy); Ghidra prints it without the grouping.
                Normalize(ref dx, ref dy, ref dz, dz * dz + dy * dy + dx * dx, fallbackZ: 1f);
                if (0f > dz && MathF.Sqrt(dy * dy + dx * dx) < 0.0001f)
                {
                    if (x < 1 || image.ChartId(p - 1) != image.ChartId(p))
                    {
                        dz = -dz;
                        r = g = bl = 0f;
                    }
                    else
                    {
                        dx = (direction.Mask & 1) != 0 ? direction.Data[(p - 1) * 4] : 0f;
                        dy = (direction.Mask & 2) != 0 ? direction.Data[(p - 1) * 4 + 1] : 0f;
                        dz = (direction.Mask & 4) != 0 ? direction.Data[(p - 1) * 4 + 2] : 0f;
                    }
                }
                var xyLength = MathF.Sqrt(0.992396176f);
                if (0.0872f > dz)
                {
                    dz = 0.0872f;
                    var scale = xyLength / MathF.Sqrt(dy * dy + dx * dx);
                    dx *= scale;
                    dy *= scale;
                }
                var l = g * 0.7152f + r * 0.2126f + bl * 0.0722f;
                float k0 = lum[0] * 0.282095f, k1 = lum[1] * 0.325735f, k2 = lum[2] * 0.325735f, k3 = lum[3] * 0.325735f;
                float k4 = lum[4] * 0.273137f, k5 = lum[5] * 0.273137f, k6 = lum[6] * 0.078848f;
                float k7 = lum[7] * 0.273137f, k8 = lum[8] * 0.136569f;
                for (var i = 0; i < 256; i++)
                {
                    float wx = dirs[i * 3], wy = dirs[i * 3 + 1], wz = dirs[i * 3 + 2];
                    var dot = wy * dy + wz * dz + wx * dx;
                    var c = (0f > dot ? 0f : dot) / dz;
                    a[i] = (1f - c) * l;
                    var e = k0 - k1 * wy + k2 * wz - k3 * wx + k4 * wx * wy - k5 * wy * wz
                        + (wz * 3f * wz - 1f) * k6 - k7 * wx * wz + (wx * wx - wy * wy) * k8;
                    b[i] = (e > 0f ? e : 0f) - c * l;
                }
                var solution = Solve(a, b);
                if (solution <= 0f)
                    solution = 0f;
                if (1f <= solution)
                    solution = 1f;
                SetMasked(irradiance, p, r, g, bl);
                SetMasked(direction, p, dx, dy, dz, solution);
            }
        for (var c = 2; c <= 7; c++)
            image.Channels[c] = null;
        image.Channels[14] = irradiance;
        image.Channels[0] = null;
        var ambient = image.AddChannel(15, 1);
        var vector = image.AddChannel(13, 7);
        for (var p = 0; p < w * h; p++)
        {
            ambient.Data[p * 4] = direction.Data[p * 4 + 3];
            for (var k = 0; k < 3; k++)
                vector.Data[p * 4 + k] = direction.Data[p * 4 + k];
        }
        image.Channels[1] = null;
    }

    /// <summary>
    /// 18005aed0 / 18005e7f0: conjugate gradient on AᵀA x = Aᵀb for the one
    /// unknown, from x = 1, stopping when the squared residual is under 1e-20
    /// or after 100 steps. Sums over the 256 rows run in order from 0.
    /// </summary>
    public static float Solve(float[] a, float[] b)
    {
        var atb = 0f;
        for (var i = 0; i < a.Length; i++)
            atb += b[i] * a[i];
        var x = 1f;
        var r = -AtA(a, x) + atb;
        var p = r;
        var rs = r * r;
        if (!(Tolerance < rs))
            return x;
        for (var step = 0; step < 100; step++)
        {
            var ap = AtA(a, p);
            var alpha = rs / (ap * p);
            x = p * alpha + x;
            r = ap * -alpha + r;
            var rsNew = r * r;
            if (rsNew < Tolerance)
                break;
            p = p * (rsNew / rs) + r;
            rs = rsNew;
        }
        return x;
    }

    // Aᵀ(A v) as 18005e580 forms it: each A v row is 0 + v A_i, then the sum in order.
    private static float AtA(float[] a, float v)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
            sum += (0f + v * a[i]) * a[i];
        return sum;
    }

    // 1800281b0: x, y, z of the components the mask has.
    private static void SetMasked(LightmapImage.Channel channel, int p, float x, float y, float z)
    {
        if ((channel.Mask & 1) != 0)
            channel.Data[p * 4] = x;
        if ((channel.Mask & 2) != 0)
            channel.Data[p * 4 + 1] = y;
        if ((channel.Mask & 4) != 0)
            channel.Data[p * 4 + 2] = z;
    }

    // 1800282c0: the same with w.
    private static void SetMasked(LightmapImage.Channel channel, int p, float x, float y, float z, float w)
    {
        SetMasked(channel, p, x, y, z);
        if ((channel.Mask & 8) != 0)
            channel.Data[p * 4 + 3] = w;
    }

    // vrad3's inline VectorNormalize: 1/len times each, a zero vector below
    // 1e-17 (or 0, 0, fallbackZ). The slow path for a nonzero length out of
    // 1e-17..1e17 (1801f51e0) is approximated in double; not met on real data.
    private static void Normalize(ref float x, ref float y, ref float z, float squared, float fallbackZ = 0f)
    {
        var length = MathF.Sqrt(squared);
        if (length < 1e-17f || 1e+17f < length)
        {
            if (length != 0f)
            {
                var l = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
                x = (float)(x / l);
                y = (float)(y / l);
                z = (float)(z / l);
                return;
            }
            x = y = 0f;
            z = fallbackZ;
            return;
        }
        var inv = 1f / length;
        x = inv * x;
        y = inv * y;
        z = inv * z;
    }

    /// <summary>
    /// 180044bc0 then 1801f4250: the frame's columns are the normalised tangent
    /// (channel 11 xyz), cross(n, t) normalised times the tangent's w, and the
    /// normalised normal (channel 9); the inverse is its transpose (rows t, b w,
    /// n), scaled by 1 / |t|² when that is off 1 by 0.001 or more. Row major 3x4.
    /// </summary>
    public static void InverseFrame(LightmapImage image, int p, Span<float> m)
    {
        Span<float> frame = stackalloc float[12];
        Frame(image, p, frame);
        Invert(frame, m);
    }

    /// <summary>180044bc0: the tangent frame as a 3x4 row-major matrix (zero translation).</summary>
    public static void Frame(LightmapImage image, int p, Span<float> m)
    {
        var normal = image.Channels[LightmapImage.Ch.Normal]!.Data;
        var tangent = image.Channels[LightmapImage.Ch.Tangent]!;
        float nx = normal[p * 4], ny = normal[p * 4 + 1], nz = normal[p * 4 + 2];
        Normalize(ref nx, ref ny, ref nz, nz * nz + ny * ny + nx * nx);
        var td = tangent.Data;
        var tm = tangent.Mask;
        float tx = (tm & 1) != 0 ? td[p * 4] : 0f, ty = (tm & 2) != 0 ? td[p * 4 + 1] : 0f;
        float tz = (tm & 4) != 0 ? td[p * 4 + 2] : 0f, sign = (tm & 8) != 0 ? td[p * 4 + 3] : 0f;
        Normalize(ref tx, ref ty, ref tz, tz * tz + ty * ty + tx * tx);
        float bx = tz * ny - nz * ty, by = nz * tx - tz * nx, bz = ty * nx - ny * tx;
        Normalize(ref bx, ref by, ref bz, by * by + bz * bz + bx * bx);
        m[0] = tx; m[1] = bx * sign; m[2] = nx; m[3] = 0f;
        m[4] = ty; m[5] = by * sign; m[6] = ny; m[7] = 0f;
        m[8] = tz; m[9] = bz * sign; m[10] = nz; m[11] = 0f;
    }

    /// <summary>
    /// 1801f4250 (MatrixInvert for a rotation): the transpose, translation
    /// -(R^T t), all scaled by 1 / |column 0|^2 when that is off 1 by 0.001 or more.
    /// </summary>
    public static void Invert(ReadOnlySpan<float> a, Span<float> o)
    {
        o[0] = a[0]; o[1] = a[4]; o[2] = a[8];
        o[4] = a[1]; o[5] = a[5]; o[6] = a[9];
        o[8] = a[2]; o[9] = a[6]; o[10] = a[10];
        float t0 = a[3], t1 = a[7], t2 = a[11];
        o[3] = -(t2 * o[2] + t1 * o[1] + t0 * o[0]);
        o[7] = -(o[6] * t2 + o[5] * t1 + t0 * o[4]);
        o[11] = -(o[10] * t2 + o[9] * t1 + t0 * o[8]);
        var scale = a[4] * a[4] + a[0] * a[0] + a[8] * a[8];
        if (0.001f <= MathF.Abs(scale - 1f))
        {
            var f = scale != 0f ? 1f / scale : 1f;
            for (var i = 0; i < 12; i++)
                o[i] = f * o[i];
        }
    }

    /// <summary>
    /// 1801f7ad0 / 1801f52b0: m times (x, y, z, 1) with SSE, each component
    /// (w m3 + y m1) + (z m2 + x m0) of its row.
    /// </summary>
    public static (float X, float Y, float Z) Transform(ReadOnlySpan<float> m, float x, float y, float z)
    {
        return ((m[3] + y * m[1]) + (z * m[2] + x * m[0]),
            (m[7] + y * m[5]) + (z * m[6] + x * m[4]),
            (m[11] + y * m[9]) + (z * m[10] + x * m[8]));
    }

    /// <summary>
    /// 18005d2e0: rotates 9 SH coefficients in place by the 3x4 rotation m as
    /// z, x +90, z, x -90, z rotations (180062a70), mirroring the bitangent row
    /// when the determinant is negative.
    /// </summary>
    public static void RotateSh(Span<float> sh, ReadOnlySpan<float> m)
    {
        var mirrored = (m[5] * m[0] - m[1] * m[4]) * m[10] + (m[2] * m[4] - m[6] * m[0]) * m[9]
            + (m[6] * m[1] - m[2] * m[5]) * m[8] < 0f;
        float s1 = m[4], c1 = m[5], row6 = m[6];
        if (mirrored)
        {
            s1 = -s1;
            c1 = -c1;
            row6 = -row6;
        }
        float c3 = 1f, s3 = 0f;
        var sinBeta = MathF.Sqrt(1f - m[10] * m[10]);
        if (1.1920929e-07f <= sinBeta)
        {
            var inv = 1f / sinBeta;
            c3 = -m[2] * inv;
            c1 = inv * m[8];
            s1 = inv * m[9];
            s3 = inv * row6;
        }
        else
            s1 = -s1;
        RotateZ(sh, s1, c1);
        float o1 = sh[1], o4 = sh[4], o6 = sh[6], o8 = sh[8];
        sh[1] = sh[2];
        sh[2] = -o1;
        sh[4] = sh[7];
        sh[7] = -o4;
        sh[5] = -sh[5];
        sh[8] = o8 * 0.5f - o6 * 0.8660254f;
        sh[6] = o6 * -0.5f - o8 * 0.8660254f;
        RotateZ(sh, sinBeta, m[10]);
        o1 = sh[1];
        o4 = sh[4];
        o6 = sh[6];
        o8 = sh[8];
        sh[1] = -sh[2];
        sh[4] = -sh[7];
        sh[2] = o1;
        sh[7] = o4;
        sh[8] = o8 * 0.5f - o6 * 0.8660254f;
        sh[5] = -sh[5];
        sh[6] = o6 * -0.5f - o8 * 0.8660254f;
        RotateZ(sh, s3, c3);
        if (mirrored)
        {
            sh[1] = -sh[1];
            sh[4] = -sh[4];
            sh[5] = -sh[5];
        }
    }

    // 180062a70: band l's coefficients l(l+1) -/+ m rotate by m times the angle
    // with cos/sin (1, c, 2c c - 1) / (0, s, 2s c - 0).
    private static void RotateZ(Span<float> sh, float s, float c)
    {
        Span<float> cos = [1f, c, (c + c) * c - 1f];
        Span<float> sin = [0f, s, (s + s) * c - 0f];
        for (var l = 1; l <= 2; l++)
        {
            var center = l * l + l;
            for (var k = 1; k <= l; k++)
            {
                float lo = sh[center - k], hi = sh[center + k];
                sh[center - k] = cos[k] * lo - sin[k] * hi;
                sh[center + k] = sin[k] * lo + cos[k] * hi;
            }
        }
    }
}
