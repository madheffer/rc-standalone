namespace Source2.Compiler.Maps;

/// <summary>
/// The float libm routines tier0 forwards to (V_tanf and friends) that the
/// light precompute reaches, FMA3 build, evaluated in double and rounded once
/// as the CRT does. Arguments past the Cody-Waite range go to a path that is
/// not read and throw.
/// </summary>
public static class LightCrt
{
    private const double PiOver4 = 0.78539816339744828;
    private const double TwoOverPi = 0.63661977236758138;
    private const double PiOver2Hi = 1.5707963267341256;
    private const double PiOver2Lo = 6.0771005065061922e-11;

    // tan(x) ~ x + x^3 (N0 + N1 x^2) / (D0 + x^2 (D1 + x^2 D2))
    private const double N0 = 0.3852960712639954;
    private const double N1 = -0.017203248047148168;
    private const double D0 = 1.1558882143468838;
    private const double D1 = -0.51396505478854537;
    private const double D2 = 0.018442392569016561;

    /// <summary>tanf, FMA3 build (tier0 1802e80c0).</summary>
    public static float Tan(float f)
    {
        if (!float.IsFinite(f))
            throw new NotSupportedException("tanf of a non-finite argument");
        var x = (double)f;
        var ax = Math.Abs(x);
        if (ax <= PiOver4)
        {
            if (ax < 0.0001220703125)
            {
                if (ax < 7.4505805969238281e-09)
                    return f;
                return (float)Math.FusedMultiplyAdd(x * x * x, 0.33333333333333331, x);
            }
            return (float)Poly(x);
        }
        if (ax >= 3373259264.0)
            throw new NotSupportedException("tanf past the Cody-Waite range is not ported");
        var n = (long)Math.FusedMultiplyAdd(ax, TwoOverPi, 0.5);
        var nd = (double)(int)n;
        var hi = Math.FusedMultiplyAdd(-nd, PiOver2Hi, ax);
        var r = hi - nd * PiOver2Lo;
        var t = Poly(r);
        if ((n & 1) != 0)
            t = -1.0 / t;
        if (x < 0 || (x == 0 && double.IsNegative(x)))
            t = -t;
        return (float)t;
    }

    private static double Poly(double x)
    {
        var x2 = x * x;
        var num = Math.FusedMultiplyAdd(x2, N1, N0);
        var den = Math.FusedMultiplyAdd(Math.FusedMultiplyAdd(x2, D2, D1), x2, D0);
        return Math.FusedMultiplyAdd(x2 * x, num / den, x);
    }

    /// <summary>cosf (tier0 V_cosf).</summary>
    public static float Cos(float x) => MathF.Cos(x);

    /// <summary>sinf (tier0 V_sinf).</summary>
    public static float Sin(float x) => MathF.Sin(x);

    /// <summary>sinf and cosf (tier0 V_sincosf).</summary>
    public static void SinCos(float x, out float sin, out float cos)
    {
        sin = MathF.Sin(x);
        cos = MathF.Cos(x);
    }

    /// <summary>atan2f (tier0 V_atan2f).</summary>
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
}
