namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The fixed sample tables of phonon's Embree reflection simulator (EmbreeReflectionSimulator's constructor):
/// the listener ray directions (Sampling::generateSphereSamples), the diffuse bounce directions
/// (generateHemisphereSamples) and each ray's real SH coefficients (SphericalHarmonics::evaluate over Google's
/// spherical-harmonics hardcoded terms, evaluated in double on the direction moved to its axes: (-z, -x, y)).
/// Each table is stored as phonon's simulator holds it: one row per component, a column per sample.
/// </summary>
public static class Sampling
{
    // Math::kPi is 3.141592f in phonon 0923 (2 kPi = 0x40c90fd8 in generateSphereSamples, 180a58220).
    private const float Pi = 3.141592f;

    private static float Hammersley(int i)
    {
        var value = 0.0f;
        var p = 0.5f;
        for (var k = i; k > 0; p *= 0.5f, k >>= 1)
            if ((k & 1) != 0)
                value += p;
        return value;
    }

    /// <summary>generateSphereSamples: rows x, y, z of <paramref name="n"/> directions.</summary>
    public static float[][] Sphere(int n)
    {
        var rows = new[] { new float[n], new float[n], new float[n] };
        for (var i = 0; i < n; i++)
        {
            var u = (i + 0.5f) / n;
            var v = Hammersley(i);
            var azimuth = 2.0f * Pi * u;
            var height = 2.0f * v - 1.0f;
            var horizontal = MathF.Sqrt(1.0f - height * height);
            rows[0][i] = horizontal * CrtMath.Cos(azimuth);
            rows[1][i] = height;
            rows[2][i] = horizontal * -CrtMath.Sin(azimuth);
        }
        return rows;
    }

    /// <summary>generateHemisphereSamples: rows x, y, z of <paramref name="n"/> directions with z &lt;= 0.</summary>
    public static float[][] Hemisphere(int n)
    {
        var rows = new[] { new float[n], new float[n], new float[n] };
        for (var i = 0; i < n; i++)
        {
            var u = (i + 0.5f) / n;
            var v = Hammersley(i);
            var r = MathF.Sqrt(u);
            var theta = 2.0f * Pi * v;
            var x = r * CrtMath.Cos(theta);
            var y = r * CrtMath.Sin(theta);
            rows[0][i] = x;
            rows[1][i] = y;
            rows[2][i] = -MathF.Sqrt(MathF.Max(0.0f, 1.0f - x * x - y * y));
        }
        return rows;
    }

    /// <summary>The (order + 1)^2 SH coefficient rows for the sphere table's directions.</summary>
    public static float[][] Coefficients(float[][] sphere, int order)
    {
        if (order > 1)
            throw new NotSupportedException($"Steam Audio ambisonics order {order}: only orders 0 and 1 are ported");
        var n = sphere[0].Length;
        var rows = new float[(order + 1) * (order + 1)][];
        for (var j = 0; j < rows.Length; j++)
            rows[j] = new float[n];
        for (var i = 0; i < n; i++)
        {
            // convertedDirection: (-z, -x, y), in double.
            double x = -(double)sphere[2][i], y = -(double)sphere[0][i], z = sphere[1][i];
            rows[0][i] = (float)0.282095;
            if (order >= 1)
            {
                rows[1][i] = (float)(0.488603 * y);
                rows[2][i] = (float)(0.488603 * z);
                rows[3][i] = (float)(0.488603 * x);
            }
        }
        return rows;
    }
}
