using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The CUBEMAP_RADIANCE_SH extra data a cube texture's compile writes (in
/// CResourceCompilerTexture::CompileResourceInternal, rc 0923 1801378c0 .. 180138100): per cube of the
/// frame, the L2 spherical harmonics of its mip 0, 27 floats (red's nine coefficients, then green's, then
/// blue's). Each texel's direction comes from its face and centre (<see cref="Direction"/>), is normalised
/// (VectorNormalize, rc 18013e340), and weighs (m k)(m m) with m its largest absolute component and k =
/// 4 / size^2; each channel times the weight times each basis value (rc 18125d4d0) adds to its sum, and
/// the sums are scaled by 4pi over the summed weights. Faces in frame order, rows then columns, all in
/// float. The extra block holds an offset of 8 and the float count (27 a cube), then the floats.
/// </summary>
public static class CubemapRadianceSh
{
    public const int FloatsPerCube = 27;

    /// <summary>
    /// The SH of one cube: <paramref name="faces"/> the six faces' red, green and blue planes, each
    /// size x size floats, row major.
    /// </summary>
    public static float[] Project(IReadOnlyList<(float[] R, float[] G, float[] B)> faces, int size)
    {
        if (faces.Count != 6)
            throw new InvalidOperationException($"Cubemap texture frame has face count {faces.Count}, expected 6");
        var red = new float[9];
        var green = new float[9];
        var blue = new float[9];
        var basis = new float[9];
        var weightSum = 0f;
        var k = 4f / (size * size);
        for (var face = 0; face < 6; face++)
        {
            var (r, g, b) = faces[face];
            for (var row = 0; row < size; row++)
                for (var col = 0; col < size; col++)
                {
                    var at = row * size + col;
                    float cr = r[at], cg = g[at], cb = b[at];
                    if (!float.IsFinite(cr) || !float.IsFinite(cg) || !float.IsFinite(cb))
                        throw new InvalidOperationException($"Cubemap face {face} has Inf/NaN/extremely bright pixel at {col},{row}: {cr} {cg} {cb}");
                    var d = Normalize(Direction(face, ((float)row + 0.5f) / size, ((float)col + 0.5f) / size));
                    var m = MaxSs(MaxSs(MathF.Abs(d.X), MathF.Abs(d.Y)), MathF.Abs(d.Z));
                    var mm = m * m;
                    var w = m * k * mm;
                    Basis(d, basis);
                    var gw = w * cg;
                    var rw = cr * w;
                    var bw = w * cb;
                    for (var i = 0; i < 9; i++)
                    {
                        red[i] += rw * basis[i];
                        green[i] += gw * basis[i];
                        blue[i] += bw * basis[i];
                    }
                    weightSum += w;
                }
        }
        var scale = 12.566371f / weightSum;
        var sh = new float[FloatsPerCube];
        for (var i = 0; i < 9; i++)
        {
            sh[i] = red[i] * scale;
            sh[9 + i] = green[i] * scale;
            sh[18 + i] = blue[i] * scale;
        }
        return sh;
    }

    /// <summary>
    /// A texel's unnormalised direction on <paramref name="face"/> from its row and column centres as
    /// fractions of the side (the jump table at rc 0923 180139788).
    /// </summary>
    public static Vector3 Direction(int face, float row, float col) => face switch
    {
        0 => new(1f, 1f - (row + row), 1f - (col + col)),
        1 => new(-1f, 1f - (row + row), col + col - 1f),
        2 => new(col + col - 1f, 1f, row + row - 1f),
        3 => new(col + col - 1f, -1f, 1f - (row + row)),
        4 => new(col + col - 1f, 1f - (row + row), 1f),
        5 => new(1f - (col + col), 1f - (row + row), -1f),
        _ => default,
    };

    /// <summary>The nine L2 basis values (rc 0923 18125d4d0).</summary>
    public static void Basis(Vector3 d, Span<float> b)
    {
        b[0] = BitConverter.UInt32BitsToSingle(0x3e906ebb);
        b[1] = d.Y * -0.48860252f;
        b[2] = d.Z * 0.48860252f;
        b[3] = d.X * -0.48860252f;
        b[4] = d.X * 1.0925485f * d.Y;
        b[5] = d.Y * -1.0925485f * d.Z;
        b[6] = (d.Z * 3f * d.Z - 1f) * 0.31539157f;
        b[7] = d.X * -1.0925485f * d.Z;
        b[8] = (d.X * d.X - d.Y * d.Y) * 0.54627424f;
    }

    // VectorNormalize (rc 18013e340): sqrt((y y + z z) + x x), each component times its reciprocal
    // when the length lies within [1e-17, 1e17].
    private static Vector3 Normalize(Vector3 v)
    {
        var length = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (length < 1e-17f || length > 1e17f)
            throw new NotSupportedException("VectorNormalize outside [1e-17, 1e17] is not read");
        var inverse = 1f / length;
        return new(inverse * v.X, inverse * v.Y, inverse * v.Z);
    }

    // maxss a, b: a when a > b, else b.
    private static float MaxSs(float a, float b) => a > b ? a : b;
}
