namespace Source2.Compiler.Simulation;

/// <summary>
/// The float cosf, sinf and expf that tier0's V_cosf, V_sinf/V_sincosf and
/// V_expf forward to: the CRT's AMD-derived libm, statically linked in
/// tier0.dll. They evaluate in double and round once to float, but they are
/// not correctly rounded, so about one argument in a thousand comes out a bit
/// away from (float)Math.Cos and friends.
///
/// <para>The CRT picks between two builds at load, on CPUID. This is the FMA3
/// build (tier0 1802e0f10, 1802e67d0, 1802e1ef0), which every CPU with FMA3
/// runs; the SSE2 build rounds its polynomials differently and is not ported.
/// Arguments past the Cody-Waite range (|x| &gt;= 3373259520 for cos, 16779436
/// for sin) and exp's overflow and underflow go to paths that are not read, and
/// throw.</para>
/// </summary>
public static class CrtMath
{
    private const double PiOver4 = 0.78539816339744828;
    private const double TwoOverPi = 0.63661977236758138;
    private const double PiOver2Hi = 1.5707963267341256;
    private const double PiOver2Lo = 6.0771005065061922e-11;

    // cos(x) - (1 - x^2 / 2) = x^4 (C4 + x^2 (C6 + x^2 (C8 + x^2 C10)))
    private const double C4 = 0.041666666666666664;
    private const double C6 = -0.0013888888888888887;
    private const double C8 = 2.4801587301587298e-05;
    private const double C10 = -2.7557319223985888e-07;

    // sin(x) - x = x^3 (S3 + x^2 (S5 + x^2 (S7 + x^2 S9)))
    private const double S3 = -0.16666666666666666;
    private const double S5 = 0.0083333333333333332;
    private const double S7 = -0.00019841269841269841;
    private const double S9 = 2.7557319223985893e-06;

    /// <summary>cosf, FMA3 build (tier0 1802e0f10).</summary>
    public static float Cos(float x)
    {
        if (!float.IsFinite(x))
            throw new NotSupportedException("cosf of a non-finite argument");
        var xd = (double)x;
        var ax = Math.Abs(xd);
        if (ax <= PiOver4)
        {
            if (ax < 0.0078125)
            {
                if (ax < 0.0001220703125)
                    return 1f;
                return (float)Math.FusedMultiplyAdd(-(xd * 0.5), xd, 1.0);
            }
            return (float)CosPolynomial(xd, xd * xd, fusedHead: false);
        }
        var (r, region) = Reduce(ax, 3373259520.0);
        var value = (region & 1) != 0 ? SinPolynomial(r) : CosPolynomial(r, r * r, fusedHead: false);
        // Quadrants 1 and 2 are negative.
        return (float)(((region + 1) >> 1 & 1) != 0 ? -value : value);
    }

    /// <summary>sinf, FMA3 build (tier0 1802e67d0).</summary>
    public static float Sin(float x)
    {
        if (!float.IsFinite(x))
            throw new NotSupportedException("sinf of a non-finite argument");
        var xd = (double)x;
        var ax = Math.Abs(xd);
        if (ax <= PiOver4)
        {
            if (ax < 0.0078125)
            {
                if (ax < 0.0001220703125)
                    return x;
                return (float)Math.FusedMultiplyAdd(-((xd * xd) * xd), 1.0 / 6.0, xd);
            }
            return (float)SinPolynomial(xd);
        }
        var (r, region) = Reduce(ax, 16779436.0);
        // Here the cosine's head 1 - r^2/2 is fused, unlike cosf's.
        var value = (region & 1) != 0 ? CosPolynomial(r, r * r, fusedHead: true) : SinPolynomial(r);
        // Quadrants 2 and 3 are negative, and the reduction worked on |x|.
        return (float)((region >= 2) != double.IsNegative(xd) ? -value : value);
    }

    /// <summary>
    /// Cody-Waite reduction of |x| by pi/2 in two parts: r and the quadrant.
    /// </summary>
    private static (double R, int Region) Reduce(double ax, double limit)
    {
        if (ax >= limit)
            throw new NotSupportedException("large-argument trig reduction is not ported");
        var n = (int)Math.FusedMultiplyAdd(TwoOverPi, ax, 0.5);
        var nd = (double)n;
        var head = Math.FusedMultiplyAdd(-nd, PiOver2Hi, ax);
        var tail = nd * PiOver2Lo;
        return (head - tail, n & 3);
    }

    private static double CosPolynomial(double x, double x2, bool fusedHead)
    {
        var head = fusedHead ? Math.FusedMultiplyAdd(x2, -0.5, 1.0) : 1.0 - x2 * 0.5;
        var p = Math.FusedMultiplyAdd(x2, C10, C8);
        p = Math.FusedMultiplyAdd(p, x2, C6);
        p = Math.FusedMultiplyAdd(p, x2, C4);
        return Math.FusedMultiplyAdd(p, x2 * x2, head);
    }

    private static double SinPolynomial(double x)
    {
        var x2 = x * x;
        var p = Math.FusedMultiplyAdd(x2, S9, S7);
        p = Math.FusedMultiplyAdd(p, x2, S5);
        p = Math.FusedMultiplyAdd(p, x2, S3);
        return Math.FusedMultiplyAdd(p, x * x2, x);
    }

    /// <summary>expf, FMA3 build (tier0 1802e1ef0).</summary>
    public static float Exp(float x)
    {
        if (!float.IsFinite(x))
            throw new NotSupportedException("expf of a non-finite argument");
        var xd = (double)x;
        var t = xd * 92.332482616893657;
        if (t >= 8192.0 || t < -9600.0)
            throw new NotSupportedException("expf overflow and underflow are not ported");
        // cvtpd2dq: round to nearest, ties to even.
        var n = (int)Math.Round(t, MidpointRounding.ToEven);
        var r = Math.FusedMultiplyAdd(-(double)n, 0.010830424696249145, xd);
        var j = n & 63;
        var m = (n - j) >> 6;
        var p = Math.FusedMultiplyAdd(1.0 / 6.0, r, 0.5);
        var q = Math.FusedMultiplyAdd(r * r, p, r);
        var power = BitConverter.UInt64BitsToDouble(TwoToTheJOver64[j]);
        q = Math.FusedMultiplyAdd(q, power, power);
        return (float)(q * BitConverter.UInt64BitsToDouble((ulong)(m + 1023) << 52));
    }

    /// <summary>2^(j/64), j = 0..63, as tier0 stores them (tier0 18033e3e0).</summary>
    private static readonly ulong[] TwoToTheJOver64 =
    [
        0x3FF0000000000000UL, 0x3FF02C9A3E778061UL, 0x3FF059B0D3158574UL, 0x3FF0874518759BC8UL,
        0x3FF0B5586CF9890FUL, 0x3FF0E3EC32D3D1A2UL, 0x3FF11301D0125B51UL, 0x3FF1429AAEA92DE0UL,
        0x3FF172B83C7D517BUL, 0x3FF1A35BEB6FCB75UL, 0x3FF1D4873168B9AAUL, 0x3FF2063B88628CD6UL,
        0x3FF2387A6E756238UL, 0x3FF26B4565E27CDDUL, 0x3FF29E9DF51FDEE1UL, 0x3FF2D285A6E4030BUL,
        0x3FF306FE0A31B715UL, 0x3FF33C08B26416FFUL, 0x3FF371A7373AA9CBUL, 0x3FF3A7DB34E59FF7UL,
        0x3FF3DEA64C123422UL, 0x3FF4160A21F72E2AUL, 0x3FF44E086061892DUL, 0x3FF486A2B5C13CD0UL,
        0x3FF4BFDAD5362A27UL, 0x3FF4F9B2769D2CA7UL, 0x3FF5342B569D4F82UL, 0x3FF56F4736B527DAUL,
        0x3FF5AB07DD485429UL, 0x3FF5E76F15AD2148UL, 0x3FF6247EB03A5585UL, 0x3FF6623882552225UL,
        0x3FF6A09E667F3BCDUL, 0x3FF6DFB23C651A2FUL, 0x3FF71F75E8EC5F74UL, 0x3FF75FEB564267C9UL,
        0x3FF7A11473EB0187UL, 0x3FF7E2F336CF4E62UL, 0x3FF82589994CCE13UL, 0x3FF868D99B4492EDUL,
        0x3FF8ACE5422AA0DBUL, 0x3FF8F1AE99157736UL, 0x3FF93737B0CDC5E5UL, 0x3FF97D829FDE4E50UL,
        0x3FF9C49182A3F090UL, 0x3FFA0C667B5DE565UL, 0x3FFA5503B23E255DUL, 0x3FFA9E6B5579FDBFUL,
        0x3FFAE89F995AD3ADUL, 0x3FFB33A2B84F15FBUL, 0x3FFB7F76F2FB5E47UL, 0x3FFBCC1E904BC1D2UL,
        0x3FFC199BDD85529CUL, 0x3FFC67F12E57D14BUL, 0x3FFCB720DCEF9069UL, 0x3FFD072D4A07897CUL,
        0x3FFD5818DCFBA487UL, 0x3FFDA9E603DB3285UL, 0x3FFDFC97337B9B5FUL, 0x3FFE502EE78B3FF6UL,
        0x3FFEA4AFA2A490DAUL, 0x3FFEFA1BEE615A27UL, 0x3FFF50765B6E4540UL, 0x3FFFA7C1819E90D8UL,
    ];
}
