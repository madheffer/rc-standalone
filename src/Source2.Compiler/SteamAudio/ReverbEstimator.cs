namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The reverb times phonon 0923's bake stores beside each energy field (the loop at 180a8c910: per band, channel
/// 0's histogram through ReverbEstimator::reverbTime, 180a84df0, then maxss with 0.1, so a NaN stays NaN). No FMA
/// (MSVC, /fp:precise). reverbTime weights each bin by the air absorption expf(-coefficient t) at its time t
/// (time, not distance, as the source does), skips histograms under 1e-4 in total, walks the energy decay curve
/// backwards from t = bins x binDuration (t keeps counting from the first loop), fits a line to log10(EDC) between
/// -2.5 and -0.5 by least squares, and returns max(-6 (N Sxx - Sx^2) / (N Sxy - Sy Sx), 0), or 0 when the slope's
/// numerator is not above FLT_MIN.
/// </summary>
public static class ReverbEstimator
{
    /// <summary>AirAbsorptionModel's defaults, as the bake passes them (captured, no callback).</summary>
    public static readonly float[] DefaultAirAbsorption = [0.0002f, 0.0017f, 0.0182f];

    public static float ReverbTime(float[] histogram, float binDuration, float coefficient)
    {
        var numBins = histogram.Length;
        var total = 0.0f;
        var x = 0.0f;
        for (var i = 0; i < numBins; i++)
        {
            total += CrtMath.Exp(-coefficient * x) * histogram[i];
            x += binDuration;
        }
        if (0.0001f > total)
            return 0.0f;
        float energy = 0, sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        var n = 0;
        for (var i = numBins - 1; i >= 0; i--)
        {
            energy += CrtMath.Exp(-coefficient * x) * histogram[i];
            var y = CrtMath.Log10(energy / total);
            if (y >= -2.5f && -0.5f >= y)
            {
                sumX += x;
                sumY += y;
                n++;
                sumXX += x * x;
                sumXY += y * x;
            }
            x -= binDuration;
        }
        var numerator = (float)n * sumXY - sumY * sumX;
        if (!(MathF.Abs(numerator) > 1.1754943508222875e-38f))
            return 0.0f;
        var t = ((float)n * sumXX - sumX * sumX) / numerator * -6.0f;
        return t > 0.0f ? t : 0.0f; // maxss t, 0: 0 unless t > 0 (NaN gives 0)
    }

    /// <summary>The three reverb times of an energy field ([channel][band][bin]).</summary>
    public static float[] Estimate(float[][][] field, float binDuration)
    {
        var times = new float[3];
        for (var b = 0; b < 3; b++)
        {
            var rt = ReverbTime(field[0][b], binDuration, DefaultAirAbsorption[b]);
            times[b] = 0.1f > rt ? 0.1f : rt; // maxss 0.1, rt
        }
        return times;
    }
}
