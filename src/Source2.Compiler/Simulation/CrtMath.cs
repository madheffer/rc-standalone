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

    /// <summary>
    /// exp2f (tier0 V_exp2f, 1802bdd90), the CRT's single precision path:
    /// x split into its integer part n (truncated) and f = x - n, moved into
    /// [-0.5, 0.5]; y = f ln2 (0.6931472f); then the Cody-Waite float exp
    /// (1802bdb54): k = trunc(y / ln2 +- 0.5), g = (y - k 0.69287109375) -
    /// k 0.00027608682, and for |g| at least 2^-25 the rational
    /// ((g P + Q) + 1) / ((Q - g P) + 1) with g P = (0.0083208261 g^2 + 0.5) g
    /// and Q = 0.099974856 g^2; scaled by 2^(k + n). Every step is a float.
    /// Overflow, underflow and |x| of 2^31 or more are not read, and throw.
    /// </summary>
    public static float Exp2(float x)
    {
        if (x == 0f)
            return 1f;
        if (!float.IsFinite(x) || x <= -2147483648f || 2147483648f <= x)
            throw new NotSupportedException("exp2f of a non-finite or huge argument is not ported");
        var whole = MathF.Truncate(x);
        var n = (int)whole;
        var f = x - whole;
        if (f > 0.5f)
        {
            f -= 1f;
            n++;
        }
        else if (-0.5f > f)
        {
            f += 1f;
            n--;
        }
        var y = f * 0.6931472f;
        var t = y * 1.442695f;
        var k = (int)((0f > t ? -0.5f : 0.5f) + t);
        var kf = (float)k;
        var lo = kf * 0.00027608682f;
        var hi = kf * 0.69287109375f;
        var g = y - hi;
        g -= lo;
        var r = 1f;
        if (!(-2.9802322e-08f < g && 2.9802322e-08f > g))
        {
            var z = g * g;
            var gp = (z * 0.0083208261f + 0.5f) * g;
            var q = z * 0.099974856f;
            r = (gp + q + 1f) / (q - gp + 1f);
        }
        var e = k + n;
        if (e is < -126 or > 127)
            throw new NotSupportedException("exp2f overflow and underflow are not ported");
        return r * BitConverter.Int32BitsToSingle((e + 127) << 23);
    }

    /// <summary>
    /// log2f (tier0 V_log2f 180008130 -> the CRT's 1802e3540, the float log family 1802e33b0 with
    /// selector -1). All in float, no CPU dispatch: x = m 2^e with m in [0.5, 1) (1802e93e0; a denormal
    /// is normalised first by 1802dc380, exactly), doubled with e less one when m is under sqrt(1/2);
    /// f = m - 1, s = f / (m + 1), w = s^2, R = ((w A + B) w + C) w, q = s (f - R); log2 x = ((f C2 -
    /// q C1) + f C3) + e, with C1 = C3 = log2(e) and C2 its tail. Zero gives -Inf, +Inf and NaN come
    /// back as they are, and a negative x (or -Inf) gives the NaN 0x7fc00000; errno is not modelled.
    /// </summary>
    public static float Log2(float x)
    {
        var bits = BitConverter.SingleToUInt32Bits(x);
        var exponent = (int)(bits >> 23 & 0xff);
        if (exponent == 0xff)
            return (bits & 0x7fffff) != 0 || (int)bits >= 0 ? x : BitConverter.UInt32BitsToSingle(0x7fc00000);
        if ((bits & 0x7fffffff) == 0)
            return float.NegativeInfinity;
        if ((int)bits < 0)
            return BitConverter.UInt32BitsToSingle(0x7fc00000);
        float m;
        int e;
        if (exponent == 0)
        {
            // Denormal: m 2^e exactly, m in [0.5, 1).
            var shift = System.Numerics.BitOperations.LeadingZeroCount(bits) - 8;
            m = BitConverter.UInt32BitsToSingle((bits << shift) & 0x007fffff | 0x3f000000);
            e = -125 - shift;
        }
        else
        {
            m = BitConverter.UInt32BitsToSingle(bits & 0x807fffff | 0x3f000000);
            e = exponent - 0x7e;
        }
        if (BitConverter.UInt32BitsToSingle(0x3f3504f3) > m)
        {
            m += m;
            e--;
        }
        var ef = (float)e;
        var f = m - 1f;
        var s = f / (m + 1f);
        var w = s * s;
        var r = ((w * BitConverter.UInt32BitsToSingle(0x3e990215) + BitConverter.UInt32BitsToSingle(0x3eccae11)) * w
                 + BitConverter.UInt32BitsToSingle(0x3f2aaac0)) * w;
        var q = s * (f - r);
        var log2e = BitConverter.UInt32BitsToSingle(0x3fb8aa3b);
        return f * BitConverter.UInt32BitsToSingle(0x32a57060) - q * log2e + f * log2e + ef;
    }

    /// <summary>
    /// acosf (tier0 V_acosf, 180007e80: the argument clamped to [-1, 1], then the CRT's
    /// 1802df0a4). |x| below 2^-26 gives pi / 2; |x| of 1 gives 0 or pi. Otherwise
    /// r = x^2 (|x| below 0.5) or r = (1 - |x|) / 2 with s = sqrt(r), and the float rational
    /// u = r ((((-0.013381929 - 0.0039613745 r) r - 0.05652987) r + 0.1841616)) /
    /// (1.1049696 - 0.8364113 r). Small |x|: pi/2 - (x - (pio2_lo - u x)) in double; x at or
    /// past 0.5: 2 s u + 2 (r - c^2) / (c + s) + 2 c with c = s with its low 16 bits cleared;
    /// x at or below -0.5: pi - 2 ((u s - pio2_lo) + s) in double.
    /// </summary>
    public static float Acos(float x)
    {
        x = MathF.Max(x, -1f);
        x = MathF.Min(x, 1f);
        var ux = BitConverter.SingleToUInt32Bits(x);
        var exponent = (int)((ux >> 23) & 0xff);
        if (exponent < 0x65)
            return 1.5707964f;
        if (exponent >= 0x7f)
            return x == 1f ? 0f : x == -1f ? 3.1415927f : float.NaN;
        var negative = (int)ux < 0;
        var a = negative ? -x : x;
        float r, s = 0f;
        var large = exponent >= 0x7e;
        if (large)
        {
            r = (1f - a) * 0.5f;
            s = MathF.Sqrt(r);
            a = s;
        }
        else
            r = a * a;
        var num = -0.013381929f - r * 0.0039613745f;
        num *= r;
        num -= 0.05652987f;
        num *= r;
        num += 0.1841616f;
        num *= r;
        var den = 1.1049696f - r * 0.8364113f;
        var u = num / den;
        const double pio2Hi = 1.5707963267948966, pio2Lo = 6.123233995736766e-17, pi = 3.141592653589793;
        if (!large)
            return (float)(pio2Hi - ((double)x - (pio2Lo - (double)(u * x))));
        if (negative)
        {
            var t = (double)(u * a) - pio2Lo + (double)s;
            return (float)(pi - (t + t));
        }
        var c = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(s) & 0xffff0000);
        var correction = (r - c * c) / (c + s);
        var twoS = a + a;
        return twoS * u + (correction + correction) + c * 2f;
    }

    /// <summary>
    /// atanf (tier0 V_atanf 180007ef0 -> the CRT's 1802e057c; one build, no CPU dispatch): in double,
    /// a = |x| reduced by range: below 0.4375 as is (hi 0); below 0.6875 to (2a - 1) / (a + 2) (hi
    /// atan 0.5); below 1.1875 to (a - 1) / (a + 1) (hi atan 1); below 2.4375 to (a - 1.5) / (a 1.5 +
    /// 1) (hi atan 1.5); else -1 / a (hi pi / 2), past 5.5e18 (infinity too) straight to the float pi / 2. Then
    /// hi - ((((z2 p2 + p1) z2 + p0) (z2 z)) / ((z2 q2 + q1) z2 + q0) - z), the sign put back, rounded
    /// once to float. NaN goes to a path that is not read and throws.
    /// </summary>
    public static float Atan(float x)
    {
        double a = x;
        var negative = BitConverter.DoubleToInt64Bits(a) < 0;
        if (negative)
            a = -a;
        var bits = BitConverter.DoubleToInt64Bits(a);
        double hi, z;
        if (bits < 0x3fdc000000000000)
        {
            hi = 0.0;
            z = a;
        }
        else if (bits < 0x3fe6000000000000)
        {
            hi = BitConverter.Int64BitsToDouble(0x3fddac670561bb4f);
            z = (a + a - 1.0) / (a + 2.0);
        }
        else if (bits < 0x3ff3000000000000)
        {
            hi = BitConverter.Int64BitsToDouble(0x3fe921fb54442d18);
            z = (a - 1.0) / (a + 1.0);
        }
        else if (bits < 0x4003800000000000)
        {
            hi = BitConverter.Int64BitsToDouble(0x3fef730bd281f69b);
            z = (a - 1.5) / (a * 1.5 + 1.0);
        }
        else if (bits > 0x7ff0000000000000)
            throw new NotSupportedException("atanf of NaN (the CRT's error path) is not ported");
        else if (a > BitConverter.Int64BitsToDouble(0x43d3200000000000))
            return negative ? -1.5707964f : 1.5707964f;
        else
        {
            hi = BitConverter.Int64BitsToDouble(0x3ff921fb54442d18);
            z = -1.0 / a;
        }
        var z2 = z * z;
        var num = (z2 * BitConverter.Int64BitsToDouble(0x3f73476a758da22a) + BitConverter.Int64BitsToDouble(0x3fc89e173a81ee7f)) * z2
                  + BitConverter.Int64BitsToDouble(0x3fd2fa5316907834);
        num *= z2 * z;
        var den = (z2 * BitConverter.Int64BitsToDouble(0x3fd327e3df2cf2aa) + BitConverter.Int64BitsToDouble(0x3ff1c58793da6ea4)) * z2
                  + BitConverter.Int64BitsToDouble(0x3fec777ca21053f0);
        var r = hi - (num / den - z);
        return (float)(negative ? -r : r);
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

    /// <summary>1 / (0.5 + j / 512), j = 0..256: powf's reciprocal table (tier0 180305a30).</summary>
    private static readonly ulong[] PowRecip =
    [
        0x4000000000000000UL, 0x3FFFE01FE01FE020UL, 0x3FFFC07F01FC07F0UL, 0x3FFFA11CAA01FA12UL,
        0x3FFF81F81F81F820UL, 0x3FFF6310ACA0DBB5UL, 0x3FFF44659E4A4271UL, 0x3FFF25F644230AB5UL,
        0x3FFF07C1F07C1F08UL, 0x3FFEE9C7F8458E02UL, 0x3FFECC07B301ECC0UL, 0x3FFEAE807ABA01EBUL,
        0x3FFE9131ABF0B767UL, 0x3FFE741AA59750E4UL, 0x3FFE573AC901E574UL, 0x3FFE3A9179DC1A73UL,
        0x3FFE1E1E1E1E1E1EUL, 0x3FFE01E01E01E01EUL, 0x3FFDE5D6E3F8868AUL, 0x3FFDCA01DCA01DCAUL,
        0x3FFDAE6076B981DBUL, 0x3FFD92F2231E7F8AUL, 0x3FFD77B654B82C34UL, 0x3FFD5CAC807572B2UL,
        0x3FFD41D41D41D41DUL, 0x3FFD272CA3FC5B1AUL, 0x3FFD0CB58F6EC074UL, 0x3FFCF26E5C44BFC6UL,
        0x3FFCD85689039B0BUL, 0x3FFCBE6D9601CBE7UL, 0x3FFCA4B3055EE191UL, 0x3FFC8B265AFB8A42UL,
        0x3FFC71C71C71C71CUL, 0x3FFC5894D10D4986UL, 0x3FFC3F8F01C3F8F0UL, 0x3FFC26B5392EA01CUL,
        0x3FFC0E070381C0E0UL, 0x3FFBF583EE868D8BUL, 0x3FFBDD2B899406F7UL, 0x3FFBC4FD65883E7BUL,
        0x3FFBACF914C1BAD0UL, 0x3FFB951E2B18FF23UL, 0x3FFB7D6C3DDA338BUL, 0x3FFB65E2E3BEEE05UL,
        0x3FFB4E81B4E81B4FUL, 0x3FFB37484AD806CEUL, 0x3FFB2036406C80D9UL, 0x3FFB094B31D922A4UL,
        0x3FFAF286BCA1AF28UL, 0x3FFADBE87F94905EUL, 0x3FFAC5701AC5701BUL, 0x3FFAAF1D2F87EBFDUL,
        0x3FFA98EF606A63BEUL, 0x3FFA82E65130E159UL, 0x3FFA6D01A6D01A6DUL, 0x3FFA574107688A4AUL,
        0x3FFA41A41A41A41AUL, 0x3FFA2C2A87C51CA0UL, 0x3FFA16D3F97A4B02UL, 0x3FFA01A01A01A01AUL,
        0x3FF9EC8E951033D9UL, 0x3FF9D79F176B682DUL, 0x3FF9C2D14EE4A102UL, 0x3FF9AE24EA5510DAUL,
        0x3FF999999999999AUL, 0x3FF9852F0D8EC0FFUL, 0x3FF970E4F80CB872UL, 0x3FF95CBB0BE377AEUL,
        0x3FF948B0FCD6E9E0UL, 0x3FF934C67F9B2CE6UL, 0x3FF920FB49D0E229UL, 0x3FF90D4F120190D5UL,
        0x3FF8F9C18F9C18FAUL, 0x3FF8E6527AF1373FUL, 0x3FF8D3018D3018D3UL, 0x3FF8BFCE8062FF3AUL,
        0x3FF8ACB90F6BF3AAUL, 0x3FF899C0F601899CUL, 0x3FF886E5F0ABB04AUL, 0x3FF87427BCC092B9UL,
        0x3FF8618618618618UL, 0x3FF84F00C2780614UL, 0x3FF83C977AB2BEDDUL, 0x3FF82A4A0182A4A0UL,
        0x3FF8181818181818UL, 0x3FF8060180601806UL, 0x3FF7F405FD017F40UL, 0x3FF7E225515A4F1DUL,
        0x3FF7D05F417D05F4UL, 0x3FF7BEB3922E017CUL, 0x3FF7AD2208E0ECC3UL, 0x3FF79BAA6BB6398BUL,
        0x3FF78A4C8178A4C8UL, 0x3FF77908119AC60DUL, 0x3FF767DCE434A9B1UL, 0x3FF756CAC201756DUL,
        0x3FF745D1745D1746UL, 0x3FF734F0C541FE8DUL, 0x3FF724287F46DEBCUL, 0x3FF713786D9C7C09UL,
        0x3FF702E05C0B8170UL, 0x3FF6F26016F26017UL, 0x3FF6E1F76B4337C7UL, 0x3FF6D1A62681C861UL,
        0x3FF6C16C16C16C17UL, 0x3FF6B1490AA31A3DUL, 0x3FF6A13CD1537290UL, 0x3FF691473A88D0C0UL,
        0x3FF6816816816817UL, 0x3FF6719F3601671AUL, 0x3FF661EC6A5122F9UL, 0x3FF6524F853B4AA3UL,
        0x3FF642C8590B2164UL, 0x3FF63356B88AC0DEUL, 0x3FF623FA77016240UL, 0x3FF614B36831AE94UL,
        0x3FF6058160581606UL, 0x3FF5F66434292DFCUL, 0x3FF5E75BB8D015E7UL, 0x3FF5D867C3ECE2A5UL,
        0x3FF5C9882B931057UL, 0x3FF5BABCC647FA91UL, 0x3FF5AC056B015AC0UL, 0x3FF59D61F123CCAAUL,
        0x3FF58ED2308158EDUL, 0x3FF5805601580560UL, 0x3FF571ED3C506B3AUL, 0x3FF56397BA7C52E2UL,
        0x3FF5555555555555UL, 0x3FF54725E6BB82FEUL, 0x3FF5390948F40FEBUL, 0x3FF52AFF56A8054BUL,
        0x3FF51D07EAE2F815UL, 0x3FF50F22E111C4C5UL, 0x3FF5015015015015UL, 0x3FF4F38F62DD4C9BUL,
        0x3FF4E5E0A72F0539UL, 0x3FF4D843BEDC2C4CUL, 0x3FF4CAB88725AF6EUL, 0x3FF4BD3EDDA68FE1UL,
        0x3FF4AFD6A052BF5BUL, 0x3FF4A27FAD76014AUL, 0x3FF49539E3B2D067UL, 0x3FF4880522014880UL,
        0x3FF47AE147AE147BUL, 0x3FF46DCE34596066UL, 0x3FF460CBC7F5CF9AUL, 0x3FF453D9E2C776CAUL,
        0x3FF446F86562D9FBUL, 0x3FF43A2730ABEE4DUL, 0x3FF42D6625D51F87UL, 0x3FF420B5265E5951UL,
        0x3FF4141414141414UL, 0x3FF40782D10E6566UL, 0x3FF3FB013FB013FBUL, 0x3FF3EE8F42A5AF07UL,
        0x3FF3E22CBCE4A902UL, 0x3FF3D5D991AA75C6UL, 0x3FF3C995A47BABE7UL, 0x3FF3BD60D9232955UL,
        0x3FF3B13B13B13B14UL, 0x3FF3A524387AC822UL, 0x3FF3991C2C187F63UL, 0x3FF38D22D366088EUL,
        0x3FF3813813813814UL, 0x3FF3755BD1C945EEUL, 0x3FF3698DF3DE0748UL, 0x3FF35DCE5F9F2AF8UL,
        0x3FF3521CFB2B78C1UL, 0x3FF34679ACE01346UL, 0x3FF33AE45B57BCB2UL, 0x3FF32F5CED6A1DFAUL,
        0x3FF323E34A2B10BFUL, 0x3FF3187758E9EBB6UL, 0x3FF30D190130D190UL, 0x3FF301C82AC40260UL,
        0x3FF2F684BDA12F68UL, 0x3FF2EB4EA1FED14BUL, 0x3FF2E025C04B8097UL, 0x3FF2D50A012D50A0UL,
        0x3FF2C9FB4D812CA0UL, 0x3FF2BEF98E5A3711UL, 0x3FF2B404AD012B40UL, 0x3FF2A91C92F3C105UL,
        0x3FF29E4129E4129EUL, 0x3FF293725BB804A5UL, 0x3FF288B01288B013UL, 0x3FF27DFA38A1CE4DUL,
        0x3FF27350B8812735UL, 0x3FF268B37CD60127UL, 0x3FF25E22708092F1UL, 0x3FF2539D7E9177B2UL,
        0x3FF2492492492492UL, 0x3FF23EB79717605BUL, 0x3FF23456789ABCDFUL, 0x3FF22A0122A0122AUL,
        0x3FF21FB78121FB78UL, 0x3FF21579804855E6UL, 0x3FF20B470C67C0D9UL, 0x3FF2012012012012UL,
        0x3FF1F7047DC11F70UL, 0x3FF1ECF43C7FB84CUL, 0x3FF1E2EF3B3FB874UL, 0x3FF1D8F5672E4ABDUL,
        0x3FF1CF06ADA2811DUL, 0x3FF1C522FC1CE059UL, 0x3FF1BB4A4046ED29UL, 0x3FF1B17C67F2BAE3UL,
        0x3FF1A7B9611A7B96UL, 0x3FF19E0119E0119EUL, 0x3FF19453808CA29CUL, 0x3FF18AB083902BDBUL,
        0x3FF1811811811812UL, 0x3FF1778A191BD684UL, 0x3FF16E0689427379UL, 0x3FF1648D50FC3201UL,
        0x3FF15B1E5F75270DUL, 0x3FF151B9A3FDD5C9UL, 0x3FF1485F0E0ACD3BUL, 0x3FF13F0E8D344724UL,
        0x3FF135C81135C811UL, 0x3FF12C8B89EDC0ACUL, 0x3FF12358E75D3033UL, 0x3FF11A3019A74826UL,
        0x3FF1111111111111UL, 0x3FF107FBBE011080UL, 0x3FF0FEF010FEF011UL, 0x3FF0F5EDFAB325A2UL,
        0x3FF0ECF56BE69C90UL, 0x3FF0E40655826011UL, 0x3FF0DB20A88F4696UL, 0x3FF0D24456359E3AUL,
        0x3FF0C9714FBCDA3BUL, 0x3FF0C0A7868B4171UL, 0x3FF0B7E6EC259DC8UL, 0x3FF0AF2F722EECB5UL,
        0x3FF0A6810A6810A7UL, 0x3FF09DDBA6AF8360UL, 0x3FF0953F39010954UL, 0x3FF08CABB37565E2UL,
        0x3FF0842108421084UL, 0x3FF07B9F29B8EAE2UL, 0x3FF073260A47F7C6UL, 0x3FF06AB59C7912FBUL,
        0x3FF0624DD2F1A9FCUL, 0x3FF059EEA0727586UL, 0x3FF05197F7D73404UL, 0x3FF04949CC1664C5UL,
        0x3FF0410410410410UL, 0x3FF038C6B78247FCUL, 0x3FF03091B51F5E1AUL, 0x3FF02864FC7729E9UL,
        0x3FF0204081020408UL, 0x3FF0182436517A37UL, 0x3FF0101010101010UL, 0x3FF0080402010080UL,
        0x3FF0000000000000UL,
    ];

    /// <summary>ln(1 + j / 256), j = 0..256: powf's log table (tier0 18033c370).</summary>
    private static readonly ulong[] PowLog =
    [
        0x0000000000000000UL, 0x3F6FF00AA2B10BC0UL, 0x3F7FE02A6B106789UL, 0x3F87DC475F810A77UL,
        0x3F8FC0A8B0FC03E4UL, 0x3F93CEA44346A575UL, 0x3F97B91B07D5B11BUL, 0x3F9B9FC027AF9198UL,
        0x3F9F829B0E783300UL, 0x3FA1B0D98923D980UL, 0x3FA39E87B9FEBD60UL, 0x3FA58A5BAFC8E4D5UL,
        0x3FA77458F632DCFCUL, 0x3FA95C830EC8E3EBUL, 0x3FAB42DD711971BFUL, 0x3FAD276B8ADB0B52UL,
        0x3FAF0A30C01162A6UL, 0x3FB075983598E471UL, 0x3FB16536EEA37AE1UL, 0x3FB253F62F0A1417UL,
        0x3FB341D7961BD1D1UL, 0x3FB42EDCBEA646F0UL, 0x3FB51B073F06183FUL, 0x3FB60658A93750C4UL,
        0x3FB6F0D28AE56B4CUL, 0x3FB7DA766D7B12CDUL, 0x3FB8C345D6319B21UL, 0x3FB9AB42462033ADUL,
        0x3FBA926D3A4AD563UL, 0x3FBB78C82BB0EDA1UL, 0x3FBC5E548F5BC743UL, 0x3FBD4313D66CB35DUL,
        0x3FBE27076E2AF2E6UL, 0x3FBF0A30C01162A6UL, 0x3FBFEC9131DBEABBUL, 0x3FC0671512CA596EUL,
        0x3FC0D77E7CD08E59UL, 0x3FC14785846742ACUL, 0x3FC1B72AD52F67A0UL, 0x3FC2266F190A5ACBUL,
        0x3FC29552F81FF523UL, 0x3FC303D718E47FD3UL, 0x3FC371FC201E8F74UL, 0x3FC3DFC2B0ECC62AUL,
        0x3FC44D2B6CCB7D1EUL, 0x3FC4BA36F39A55E5UL, 0x3FC526E5E3A1B438UL, 0x3FC59338D9982086UL,
        0x3FC5FF3070A793D4UL, 0x3FC66ACD4272AD51UL, 0x3FC6D60FE719D21DUL, 0x3FC740F8F54037A5UL,
        0x3FC7AB890210D909UL, 0x3FC815C0A14357EBUL, 0x3FC87FA06520C911UL, 0x3FC8E928DE886D41UL,
        0x3FC9525A9CF456B4UL, 0x3FC9BB362E7DFB83UL, 0x3FCA23BC1FE2B563UL, 0x3FCA8BECFC882F19UL,
        0x3FCAF3C94E80BFF3UL, 0x3FCB5B519E8FB5A4UL, 0x3FCBC286742D8CD6UL, 0x3FCC2968558C18C1UL,
        0x3FCC8FF7C79A9A22UL, 0x3FCCF6354E09C5DCUL, 0x3FCD5C216B4FBB91UL, 0x3FCDC1BCA0ABEC7DUL,
        0x3FCE27076E2AF2E6UL, 0x3FCE8C0252AA5A60UL, 0x3FCEF0ADCBDC5936UL, 0x3FCF550A564B7B37UL,
        0x3FCFB9186D5E3E2BUL, 0x3FD00E6C45AD501DUL, 0x3FD0402594B4D041UL, 0x3FD071B85FCD590DUL,
        0x3FD0A324E27390E3UL, 0x3FD0D46B579AB74BUL, 0x3FD1058BF9AE4AD5UL, 0x3FD136870293A8B0UL,
        0x3FD1675CABABA60EUL, 0x3FD1980D2DD4236FUL, 0x3FD1C898C16999FBUL, 0x3FD1F8FF9E48A2F3UL,
        0x3FD22941FBCF7966UL, 0x3FD2596010DF763AUL, 0x3FD2895A13DE86A3UL, 0x3FD2B9303AB89D25UL,
        0x3FD2E8E2BAE11D31UL, 0x3FD31871C9544185UL, 0x3FD347DD9A987D55UL, 0x3FD3772662BFD85BUL,
        0x3FD3A64C556945EAUL, 0x3FD3D54FA5C1F710UL, 0x3FD404308686A7E4UL, 0x3FD432EF2A04E814UL,
        0x3FD4618BC21C5EC2UL, 0x3FD49006804009D1UL, 0x3FD4BE5F957778A1UL, 0x3FD4EC973260026AUL,
        0x3FD51AAD872DF82DUL, 0x3FD548A2C3ADD263UL, 0x3FD5767717455A6CUL, 0x3FD5A42AB0F4CFE2UL,
        0x3FD5D1BDBF5809CAUL, 0x3FD5FF3070A793D4UL, 0x3FD62C82F2B9C795UL, 0x3FD659B57303E1F3UL,
        0x3FD686C81E9B14AFUL, 0x3FD6B3BB2235943EUL, 0x3FD6E08EAA2BA1E4UL, 0x3FD70D42E2789236UL,
        0x3FD739D7F6BBD007UL, 0x3FD7664E1239DBCFUL, 0x3FD792A55FDD47A2UL, 0x3FD7BEDE0A37AFC0UL,
        0x3FD7EAF83B82AFC3UL, 0x3FD816F41DA0D496UL, 0x3FD842D1DA1E8B17UL, 0x3FD86E919A330BA0UL,
        0x3FD89A3386C1425BUL, 0x3FD8C5B7C858B48BUL, 0x3FD8F11E873662C7UL, 0x3FD91C67EB45A83EUL,
        0x3FD947941C2116FBUL, 0x3FD972A341135158UL, 0x3FD99D958117E08BUL, 0x3FD9C86B02DC0863UL,
        0x3FD9F323ECBF984CUL, 0x3FDA1DC064D5B995UL, 0x3FDA484090E5BB0AUL, 0x3FDA72A4966BD9EAUL,
        0x3FDA9CEC9A9A084AUL, 0x3FDAC718C258B0E4UL, 0x3FDAF1293247786BUL, 0x3FDB1B1E0EBDFC5BUL,
        0x3FDB44F77BCC8F63UL, 0x3FDB6EB59D3CF35EUL, 0x3FDB9858969310FBUL, 0x3FDBC1E08B0DAD0AUL,
        0x3FDBEB4D9DA71B7CUL, 0x3FDC149FF115F027UL, 0x3FDC3DD7A7CDAD4DUL, 0x3FDC66F4E3FF6FF8UL,
        0x3FDC8FF7C79A9A22UL, 0x3FDCB8E0744D7ACAUL, 0x3FDCE1AF0B85F3EBUL, 0x3FDD0A63AE721E64UL,
        0x3FDD32FE7E00EBD5UL, 0x3FDD5B7F9AE2C684UL, 0x3FDD83E7258A2F3EUL, 0x3FDDAC353E2C5954UL,
        0x3FDDD46A04C1C4A1UL, 0x3FDDFC859906D5B5UL, 0x3FDE24881A7C6C26UL, 0x3FDE4C71A8687704UL,
        0x3FDE744261D68788UL, 0x3FDE9BFA659861F5UL, 0x3FDEC399D2468CC0UL, 0x3FDEEB20C640DDF4UL,
        0x3FDF128F5FAF06EDUL, 0x3FDF39E5BC811E5CUL, 0x3FDF6123FA7028ACUL, 0x3FDF884A36FE9EC2UL,
        0x3FDFAF588F78F31FUL, 0x3FDFD64F20F61572UL, 0x3FDFFD2E0857F498UL, 0x3FE011FAB125FF8AUL,
        0x3FE02552A5A5D0FFUL, 0x3FE0389EEFCE633BUL, 0x3FE04BDF9DA926D2UL, 0x3FE05F14BD26459CUL,
        0x3FE0723E5C1CDF40UL, 0x3FE0855C884B450EUL, 0x3FE0986F4F573521UL, 0x3FE0AB76BECE14D2UL,
        0x3FE0BE72E4252A83UL, 0x3FE0D163CCB9D6B8UL, 0x3FE0E44985D1CC8CUL, 0x3FE0F7241C9B497DUL,
        0x3FE109F39E2D4C97UL, 0x3FE11CB81787CCF8UL, 0x3FE12F719593EFBCUL, 0x3FE1422025243D45UL,
        0x3FE154C3D2F4D5EAUL, 0x3FE1675CABABA60EUL, 0x3FE179EABBD899A1UL, 0x3FE18C6E0FF5CF06UL,
        0x3FE19EE6B467C96FUL, 0x3FE1B154B57DA29FUL, 0x3FE1C3B81F713C25UL, 0x3FE1D610FE677003UL,
        0x3FE1E85F5E7040D0UL, 0x3FE1FAA34B87094CUL, 0x3FE20CDCD192AB6EUL, 0x3FE21F0BFC65BEECUL,
        0x3FE23130D7BEBF43UL, 0x3FE2434B6F483934UL, 0x3FE2555BCE98F7CBUL, 0x3FE26762013430E0UL,
        0x3FE2795E1289B11BUL, 0x3FE28B500DF60783UL, 0x3FE29D37FEC2B08BUL, 0x3FE2AF15F02640ADUL,
        0x3FE2C0E9ED448E8CUL, 0x3FE2D2B4012EDC9EUL, 0x3FE2E47436E40268UL, 0x3FE2F62A99509546UL,
        0x3FE307D7334F10BEUL, 0x3FE3197A0FA7FE6AUL, 0x3FE32B1339121D71UL, 0x3FE33CA2BA328995UL,
        0x3FE34E289D9CE1D3UL, 0x3FE35FA4EDD36EA0UL, 0x3FE37117B54747B6UL, 0x3FE38280FE58797FUL,
        0x3FE393E0D3562A1AUL, 0x3FE3A5373E7EBDFAUL, 0x3FE3B68449FFFC23UL, 0x3FE3C7C7FFF73206UL,
        0x3FE3D9026A7156FBUL, 0x3FE3EA33936B2F5CUL, 0x3FE3FB5B84D16F42UL, 0x3FE40C7A4880DCE9UL,
        0x3FE41D8FE84672AEUL, 0x3FE42E9C6DDF80BFUL, 0x3FE43F9FE2F9CE67UL, 0x3FE4509A5133BB0AUL,
        0x3FE4618BC21C5EC2UL, 0x3FE472743F33AAADUL, 0x3FE48353D1EA88DFUL, 0x3FE4942A83A2FC07UL,
        0x3FE4A4F85DB03EBBUL, 0x3FE4B5BD6956E274UL, 0x3FE4C679AFCCEE3AUL, 0x3FE4D72D3A39FD00UL,
        0x3FE4E7D811B75BB1UL, 0x3FE4F87A3F5026E9UL, 0x3FE50913CC01686BUL, 0x3FE519A4C0BA3446UL,
        0x3FE52A2D265BC5ABUL, 0x3FE53AAD05B99B7DUL, 0x3FE54B2467999498UL, 0x3FE55B9354B40BCDUL,
        0x3FE56BF9D5B3F399UL, 0x3FE57C57F336F191UL, 0x3FE58CADB5CD7989UL, 0x3FE59CFB25FAE87EUL,
        0x3FE5AD404C359F2DUL, 0x3FE5BD7D30E71C73UL, 0x3FE5CDB1DC6C1765UL, 0x3FE5DDDE57149923UL,
        0x3FE5EE02A9241675UL, 0x3FE5FE1EDAD18919UL, 0x3FE60E32F44788D9UL, 0x3FE61E3EFDA46467UL,
        0x3FE62E42FEFA39EFUL,
    ];

    /// <summary>
    /// powf (tier0 V_powf 1800081a0 -> the CRT's 1802e5130), FMA3 build (from 1802e5896, which every CPU
    /// with FMA3 runs), for x above 0 and finite, y finite and neither 0 nor 1 (the special cases and
    /// the overflow and underflow paths are not read, and throw). In double: x = 2^e m (m in [0.5, 1)),
    /// j = the top 8 bits of m's fraction rounded on bit 43, c = 0.5 + j / 512, r = (c - m) / c, ln x =
    /// e ln2 + ln(2c) - fma(fma(r / 3, r, 1 / 2), r, 1) r. An x within 1 / 16 of 1 (from 1062 / 1000 down)
    /// takes the series: u = x - 1, s = u / (u + 2), v = 2s, ln x = u + ((v^3 (A v^2 + C) + v^7 (B v^2 +
    /// D)) - s u). Then t = y ln x, n = round(t 64 / ln2), r = t - n ln2 / 64 (fused here; the series path
    /// joins the SSE2 build's exp, which is not), 2^(n / 64) (r + r^2 / 2 + r^3 / 6 + 1), rounded once.
    /// </summary>
    public static float Pow(float x, float y)
    {
        uint xb = BitConverter.SingleToUInt32Bits(x), yb = BitConverter.SingleToUInt32Bits(y);
        if ((int)xb <= 0 || xb >= 0x7f800000 || (yb & 0x7fffffff) >= 0x7f800000 || (yb & 0x7fffffff) == 0 || yb == 0x3f800000)
            throw new NotSupportedException("powf outside x > 0 finite, y finite and not 0 or 1 (the CRT's special paths) is not ported");
        double xd = x, yd = y;
        double lnx;
        var near = xb < 0x3f880000 && Math.Abs(xd - 1.0) < 0.0625;
        if (near)
        {
            var u = xd - 1.0;
            var s = u / (u + 2.0);
            var su = s * u;
            var v = s + s;
            var v2 = v * v;
            var v3 = v * v2;
            var v7 = v2 * v2 * v3;
            var lo = v3 * (v2 * BitConverter.Int64BitsToDouble(0x3f89999999bac6d4) + BitConverter.Int64BitsToDouble(0x3fb55555555554e6));
            var hi = v7 * (v2 * BitConverter.Int64BitsToDouble(0x3f3c8034c85dfff0) + BitConverter.Int64BitsToDouble(0x3f62492307f1519f));
            lnx = u + (hi + lo - su);
        }
        else
        {
            var bits = BitConverter.DoubleToInt64Bits(xd);
            var mant = bits & 0x000fffffffffffffL;
            var j = (int)(mant >> 44) + (int)((mant >> 43) & 1);
            var c = BitConverter.Int64BitsToDouble((long)(0x3fe00 | j) << 44);
            var m = BitConverter.Int64BitsToDouble(mant | 0x3fe0000000000000L);
            double e = (int)((bits >> 52) & 0x7ff) - 1023;
            var r = (c - m) * BitConverter.Int64BitsToDouble((long)PowRecip[j]);
            var p = Math.FusedMultiplyAdd(Math.FusedMultiplyAdd(BitConverter.Int64BitsToDouble(0x3fd5555555555555), r, 0.5), r, 1.0);
            lnx = e * BitConverter.Int64BitsToDouble(0x3fe62e42fefa39ef) + BitConverter.Int64BitsToDouble((long)PowLog[j]) - r * p;
        }
        var t = yd * lnx;
        if (t > 88.72283935546875 || t <= -103.2789306640625)
            throw new NotSupportedException("powf overflow or underflow (the CRT's error paths) is not ported");
        var n = (int)Math.Round(t * BitConverter.Int64BitsToDouble(0x40571547652b82fe), MidpointRounding.ToEven);
        double nd = n;
        const double L = 0.010830424696249145;
        const double P1 = 0.16666666666666666;
        var tbl = BitConverter.Int64BitsToDouble((long)TwoToTheJOver64[n & 63]);
        double q;
        if (near)
        {
            var rr = t - nd * L;
            q = (rr * rr * (P1 * rr + 0.5) + rr) * tbl + tbl;
        }
        else
        {
            var rr = Math.FusedMultiplyAdd(-nd, L, t);
            var poly = rr * Math.FusedMultiplyAdd(Math.FusedMultiplyAdd(P1, rr, 0.5), rr, 1.0);
            q = Math.FusedMultiplyAdd(poly, tbl, tbl);
        }
        var scaled = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(q) + ((long)(n >> 6) << 52));
        return (float)scaled;
    }

    /// <summary>
    /// atan2f (tier0 V_atan2f 180007f10 -> the CRT's 1802e0184; one build, no CPU dispatch), in double on
    /// |y| and |x| widened from float. y = 0 returns y (x not negative) or +-pi; x = 0 +-pi/2; |y| more than
    /// 2^26 times |x| (exponent difference over 26) +-pi/2; a ratio past 2^-13 with x positive is y / x
    /// rounded once (past 2^-126 and 2^-150 the denormal paths, not ported); with x negative +-pi; both
    /// infinite +-pi/4 or +-3pi/4. Otherwise, with the larger magnitude below: r = small / large; above
    /// 1/16, k = trunc(256 r + 1/2) and t = (256 small - k large) / (k small + 256 large), atan r =
    /// (t + atan(k/256)) - t^3 0.33333333333224097 (the table is Valve's, not correctly rounded); else r
    /// below 1e-4 as is, or r - (0.3333333333333317 - (0.19999999999393223 - r^2 0.1428571356180717) r^2)
    /// r^3. Swapped: pi/2 - a; x negative: pi - a; y negative: -a. NaN takes the CRT's error path (throws).
    /// </summary>
    public static float Atan2(float y, float x)
    {
        double yd = y, xd = x;
        long yb = BitConverter.DoubleToInt64Bits(yd), xb = BitConverter.DoubleToInt64Bits(xd);
        long ya = yb & long.MaxValue, xa = xb & long.MaxValue;
        const long Inf = 0x7ff0000000000000;
        if (xa > Inf || ya > Inf)
            throw new NotSupportedException("atan2f of NaN (the CRT's error path) is not ported");
        var diff = (int)((yb >> 52) & 0x7ff) - (int)((xb >> 52) & 0x7ff);
        if (ya == 0)
            return xb < 0 ? (yb < 0 ? -3.1415927f : 3.1415927f) : y;
        if (xa == 0 && yb < 0)
            return -1.5707964f;
        if (diff > 26)
            return yb < 0 ? -1.5707964f : 1.5707964f;
        if (diff < -13 && xb >= 0)
        {
            if (diff < -150)
                return yb < 0 ? -0f : 0f;
            if (diff < -126)
                throw new NotSupportedException("atan2f's denormal quotient path is not ported");
            return (float)(yd / xd);
        }
        if (diff < -26 && xb < 0)
            return yb < 0 ? -3.1415927f : 3.1415927f;
        if (xa == Inf && ya == Inf)
            return xb < 0 ? (yb < 0 ? -2.3561945f : 2.3561945f) : (yb < 0 ? -0.7853982f : 0.7853982f);
        double ay = Math.Abs(yd), ax = Math.Abs(xd);
        var swapped = ay > ax;
        if (swapped)
            (ay, ax) = (ax, ay);
        var r = ay / ax;
        double a;
        if (r > 0.0625)
        {
            var k = (int)(r * 256.0 + 0.5);
            double c = k;
            var t = (ay * 256.0 - c * ax) / (c * ay + ax * 256.0);
            a = (t + BitConverter.Int64BitsToDouble((long)AtanKOver256[k - 16])) - t * t * t * BitConverter.Int64BitsToDouble(0x3fd5555555550877);
        }
        else if (r < BitConverter.Int64BitsToDouble(0x3f1a36e2eb1c432d))
            a = r;
        else
        {
            var r2 = r * r;
            var inner = (BitConverter.Int64BitsToDouble(0x3fc99999999643a3) - r2 * BitConverter.Int64BitsToDouble(0x3fc2492482bd6be1)) * r2;
            a = r - (BitConverter.Int64BitsToDouble(0x3fd5555555555538) - inner) * (r2 * r);
        }
        if (swapped)
            a = BitConverter.Int64BitsToDouble(0x3ff921fb54442d18) - a;
        if (xb < 0)
            a = BitConverter.Int64BitsToDouble(0x400921fb54442d18) - a;
        if (yb < 0)
            a = -a;
        return (float)a;
    }

    /// <summary>atan(k / 256), k = 16..256, as tier0 stores them (tier0 180338cc0).</summary>
    private static readonly ulong[] AtanKOver256 =
    [
        0x3FAFF55BB72CFDE9UL, 0x3FB0F99EA71D52A6UL, 0x3FB1F86DBF082D58UL, 0x3FB2F719318A4A9AUL,
        0x3FB3F59F0E7C559DUL, 0x3FB4F3FD677292FBUL, 0x3FB5F2324FD2D7B2UL, 0x3FB6F03BDCEA4B0CUL,
        0x3FB7EE182602F10EUL, 0x3FB8EBC54478FB28UL, 0x3FB9E94153CFDCF1UL, 0x3FBAE68A71C722B8UL,
        0x3FBBE39EBE6F07C3UL, 0x3FBCE07C5C3CCA32UL, 0x3FBDDD21701EBA6EUL, 0x3FBED98C2190043AUL,
        0x3FBFD5BA9AAC2F6DUL, 0x3FC068D584212B3DUL, 0x3FC0E6ADCCF40881UL, 0x3FC1646541060850UL,
        0x3FC1E1FAFB043726UL, 0x3FC25F6E171A535CUL, 0x3FC2DCBDB2FBA1FFUL, 0x3FC359E8EDEB99A3UL,
        0x3FC3D6EEE8C6626CUL, 0x3FC453CEC6092A9EUL, 0x3FC4D087A9DA4F17UL, 0x3FC54D18BA11570AUL,
        0x3FC5C9811E3EC269UL, 0x3FC645BFFFB3AA73UL, 0x3FC6C1D4898933D8UL, 0x3FC73DBDE8A7D201UL,
        0x3FC7B97B4BCE5B02UL, 0x3FC8350BE398EBC7UL, 0x3FC8B06EE2879C28UL, 0x3FC92BA37D050271UL,
        0x3FC9A6A8E96C8626UL, 0x3FCA217E601081A5UL, 0x3FCA9C231B403279UL, 0x3FCB1696574D780BUL,
        0x3FCB90D7529260A2UL, 0x3FCC0AE54D768466UL, 0x3FCC84BF8A742E6DUL, 0x3FCCFE654E1D5395UL,
        0x3FCD77D5DF205736UL, 0x3FCDF110864C9D9DUL, 0x3FCE6A148E96EC4DUL, 0x3FCEE2E1451D980CUL,
        0x3FCF5B75F92C80DDUL, 0x3FCFD3D1FC40DBE4UL, 0x3FD025FA510665B5UL, 0x3FD061EEA03D6290UL,
        0x3FD09DC597D86362UL, 0x3FD0D97EE509ACB3UL, 0x3FD1151A362431C9UL, 0x3FD150973A9CE546UL,
        0x3FD18BF5A30BF178UL, 0x3FD1C735212DD883UL, 0x3FD2025567E47C95UL, 0x3FD23D562B381041UL,
        0x3FD278372057EF45UL, 0x3FD2B2F7FD9B5FE2UL, 0x3FD2ED987A823CFEUL, 0x3FD328184FB58951UL,
        0x3FD362773707EBCBUL, 0x3FD39CB4EB76157BUL, 0x3FD3D6D129271134UL, 0x3FD410CBAD6C7D32UL,
        0x3FD44AA436C2AF09UL, 0x3FD4845A84D0C21BUL, 0x3FD4BDEE586890E6UL, 0x3FD4F75F73869978UL,
        0x3FD530AD9951CD49UL, 0x3FD569D88E1B4CD7UL, 0x3FD5A2E0175E0F4EUL, 0x3FD5DBC3FBBE768DUL,
        0x3FD614840309CFE1UL, 0x3FD64D1FF635C1C5UL, 0x3FD685979F5FA6FDUL, 0x3FD6BDEAC9CBD76CUL,
        0x3FD6F61941E4DEF0UL, 0x3FD72E22D53AA2A9UL, 0x3FD7660752817501UL, 0x3FD79DC6899118D1UL,
        0x3FD7D5604B63B3F7UL, 0x3FD80CD46A14B1D0UL, 0x3FD84422B8DF95D7UL, 0x3FD87B4B0C1EBEDBUL,
        0x3FD8B24D394A1B25UL, 0x3FD8E92916F5CDE8UL, 0x3FD91FDE7CD0C662UL, 0x3FD9566D43A34907UL,
        0x3FD98CD5454D6B18UL, 0x3FD9C3165CC58107UL, 0x3FD9F93066168001UL, 0x3FDA2F233E5E530BUL,
        0x3FDA64EEC3CC23FCUL, 0x3FDA9A92D59E98CFUL, 0x3FDAD00F5422058BUL, 0x3FDB056420AE9343UL,
        0x3FDB3A911DA65C6CUL, 0x3FDB6F962E737EFBUL, 0x3FDBA473378624A5UL, 0x3FDBD9281E528191UL,
        0x3FDC0DB4C94EC9EFUL, 0x3FDC42191FF11EB6UL, 0x3FDC76550AAD71F8UL, 0x3FDCAA6872F3631BUL,
        0x3FDCDE53432C1350UL, 0x3FDD121566B7F2ADUL, 0x3FDD45AEC9EC862BUL, 0x3FDD791F5A1226F4UL,
        0x3FDDAC670561BB4FUL, 0x3FDDDF85BB026974UL, 0x3FDE127B6B0744AFUL, 0x3FDE4548066CF51AUL,
        0x3FDE77EB7F175A34UL, 0x3FDEAA65C7CF28C4UL, 0x3FDEDCB6D43F8434UL, 0x3FDF0EDE98F393CFUL,
        0x3FDF40DD0B541417UL, 0x3FDF72B221A4E495UL, 0x3FDFA45DD3029258UL, 0x3FDFD5E0175FDF83UL,
        0x3FE0039C73C1A40BUL, 0x3FE01C341E82422DUL, 0x3FE034B709250488UL, 0x3FE04D25314342E5UL,
        0x3FE0657E94DB30CFUL, 0x3FE07DC3324E9B38UL, 0x3FE095F30861A58FUL, 0x3FE0AE0E1639866CUL,
        0x3FE0C6145B5B43DAUL, 0x3FE0DE05D7AA6F7CUL, 0x3FE0F5E28B67E295UL, 0x3FE10DAA77307A0DUL,
        0x3FE1255D9BFBD2A8UL, 0x3FE13CFBFB1B056EUL, 0x3FE1548596376469UL, 0x3FE16BFA6F5137E1UL,
        0x3FE1835A88BE7C13UL, 0x3FE19AA5E5299F99UL, 0x3FE1B1DC87904284UL, 0x3FE1C8FE7341F64FUL,
        0x3FE1E00BABDEFEB3UL, 0x3FE1F7043557138AUL, 0x3FE20DE813E823B1UL, 0x3FE224B74C1D192AUL,
        0x3FE23B71E2CC9E6AUL, 0x3FE25217DD17E501UL, 0x3FE268A940696DA6UL, 0x3FE27F261273D1B3UL,
        0x3FE2958E59308E30UL, 0x3FE2ABE21ADED073UL, 0x3FE2C2215E024465UL, 0x3FE2D84C2961E48BUL,
        0x3FE2EE628406CBCAUL, 0x3FE30464753B090AUL, 0x3FE31A52048874BEUL, 0x3FE3302B39B78856UL,
        0x3FE345F01CCE37BBUL, 0x3FE35BA0B60ECCCEUL, 0x3FE3713D0DF6C503UL, 0x3FE386C52D3DB11EUL,
        0x3FE39C391CD41719UL, 0x3FE3B198E5E2564AUL, 0x3FE3C6E491C78DC4UL, 0x3FE3DC1C2A188504UL,
        0x3FE3F13FB89E96F4UL, 0x3FE4064F47569F48UL, 0x3FE41B4AE06FEA41UL, 0x3FE430328E4B26D5UL,
        0x3FE445065B795B55UL, 0x3FE459C652BADC7FUL, 0x3FE46E727EFE4715UL, 0x3FE4830AEB5F7BFDUL,
        0x3FE4978FA3269EE1UL, 0x3FE4AC00B1C71762UL, 0x3FE4C05E22DE94E4UL, 0x3FE4D4A8023414E8UL,
        0x3FE4E8DE5BB6EC04UL, 0x3FE4FD013B7DD17EUL, 0x3FE51110ADC5ED81UL, 0x3FE5250CBEF1E9FAUL,
        0x3FE538F57B89061EUL, 0x3FE54CCAF0362C8FUL, 0x3FE5608D29C70C34UL, 0x3FE5743C352B33B9UL,
        0x3FE587D81F732FBAUL, 0x3FE59B60F5CFAB9DUL, 0x3FE5AED6C5909517UL, 0x3FE5C2399C244260UL,
        0x3FE5D58987169B18UL, 0x3FE5E8C6941043CFUL, 0x3FE5FBF0D0D5CC49UL, 0x3FE60F084B46E05EUL,
        0x3FE6220D115D7B8DUL, 0x3FE634FF312D1F3BUL, 0x3FE647DEB8E20B8FUL, 0x3FE65AABB6C07B02UL,
        0x3FE66D663923E086UL, 0x3FE6800E4E7E2857UL, 0x3FE692A40556FB6AUL, 0x3FE6A5276C4B0575UL,
        0x3FE6B798920B3D98UL, 0x3FE6C9F7855C3198UL, 0x3FE6DC44551553AEUL, 0x3FE6EE7F10204AEFUL,
        0x3FE700A7C5784633UL, 0x3FE712BE84295198UL, 0x3FE724C35B4FAE7BUL, 0x3FE736B65A172DFFUL,
        0x3FE748978FBA8E0FUL, 0x3FE75A670B82D8D8UL, 0x3FE76C24DCC6C6C0UL, 0x3FE77DD112EA22C7UL,
        0x3FE78F6BBD5D315EUL, 0x3FE7A0F4EB9C19A2UL, 0x3FE7B26CAD2E50FDUL, 0x3FE7C3D311A6092BUL,
        0x3FE7D528289FA093UL, 0x3FE7E66C01C114FDUL, 0x3FE7F79EACB97898UL, 0x3FE808C03940694AUL,
        0x3FE819D0B7158A4CUL, 0x3FE82AD036000005UL, 0x3FE83BBEC5CDEE22UL, 0x3FE84C9C7653F7EAUL,
        0x3FE85D69576CC2C5UL, 0x3FE86E2578F87AE5UL, 0x3FE87ED0EADC5A2AUL, 0x3FE88F6BBD023118UL,
        0x3FE89FF5FF57F1F7UL, 0x3FE8B06FC1CF3DFEUL, 0x3FE8C0D9145CF49DUL, 0x3FE8D13206F8C4CAUL,
        0x3FE8E17AA99CC05DUL, 0x3FE8F1B30C44F167UL, 0x3FE901DB3EEEF187UL, 0x3FE911F35199833BUL,
        0x3FE921FB54442D18UL,
    ];
}
