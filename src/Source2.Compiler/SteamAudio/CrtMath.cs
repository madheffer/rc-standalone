namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The CRT <c>cosf</c>, <c>sinf</c>, <c>expf</c>, <c>log10f</c> (and <c>atan2f</c>, below) statically linked into phonon 0923 (182683c40,
/// 182685260, 1827a2450, 1827a2700), on their FMA path
/// (taken when the CRT's FMA flag at 1832ad734 is set): the float argument widened to double, Cody-Waite reduced by
/// pi/2 above pi/4, and the double polynomials (expf and log10f are table driven; the tables are copied from the
/// DLL) evaluated with the fused steps the disassembly shows, then rounded
/// to float. Math.FusedMultiplyAdd is exact on every platform, so the results are the same on Linux.
/// Arguments beyond the short reduction (|x| at or above 3373259520 for cosf, 16779436 for sinf) and non-finite
/// ones are refused: phonon's sample tables never pass them.
/// </summary>
public static class CrtMath
{
    private const double TwoOverPi = 0.6366197723675814;        // 0x3fe45f306dc9c883
    private const double PiOver2Hi = 1.5707963267341256;        // 0x3ff921fb54400000
    private const double PiOver2Lo = 6.077100506506192e-11;     // 0x3dd0b4611a626331

    private static double Fma(double a, double b, double c) => Math.FusedMultiplyAdd(a, b, c);

    /// <summary>The sine polynomial: r + p(r^2) r^3.</summary>
    private static double SinPoly(double r)
    {
        var x2 = r * r;
        var p = Fma(x2, 2.7557319223985893e-06, -0.0001984126984126984);
        p = Fma(p, x2, 0.008333333333333333);
        p = Fma(p, x2, -0.16666666666666666);
        return Fma(p, r * x2, r);
    }

    /// <summary>The cosine polynomial's tail: q(r^2) r^4.</summary>
    private static double CosTail(double x2, out double x4)
    {
        var p = Fma(x2, -2.755731922398589e-07, 2.4801587301587298e-05);
        p = Fma(p, x2, -0.0013888888888888887);
        p = Fma(p, x2, 0.041666666666666664);
        x4 = x2 * x2;
        return p;
    }

    /// <summary>cosf's cosine: 1 - r^2 / 2 unfused, then the tail fused in.</summary>
    private static double CosfPoly(double r)
    {
        var x2 = r * r;
        var h = 1.0 - x2 * 0.5;
        var p = CosTail(x2, out var x4);
        return Fma(p, x4, h);
    }

    /// <summary>sinf's cosine: 1 - r^2 / 2 fused.</summary>
    private static double SinfCosPoly(double r)
    {
        var x2 = r * r;
        var h = Fma(x2, -0.5, 1.0);
        var p = CosTail(x2, out var x4);
        return Fma(p, x4, h);
    }

    private static (double R, long Region) Reduce(double ax)
    {
        var n = (int)Fma(ax, TwoOverPi, 0.5); // cvttpd2dq
        var nd = (double)n;
        var hi = Fma(-nd, PiOver2Hi, ax);
        return (hi - nd * PiOver2Lo, (long)n & 3);
    }

    public static float Cos(float x)
    {
        if (!float.IsFinite(x))
            throw new NotSupportedException("cosf of a non-finite value is not ported");
        var d = (double)x;
        var bits = BitConverter.DoubleToInt64Bits(d) & long.MaxValue;
        if (bits > 0x3fe921fb54442d18)
        {
            if (bits >= 0x41e921fb60000000)
                throw new NotSupportedException("cosf's long argument reduction is not ported");
            var (r, region) = Reduce(BitConverter.Int64BitsToDouble(bits));
            var v = (region & 1) != 0 ? SinPoly(r) : CosfPoly(r);
            var sign = (ulong)((region + 1) >> 1) << 63;
            return (float)BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v) ^ (long)sign);
        }
        if (bits >= 0x3f80000000000000)
            return (float)CosfPoly(d);
        if (bits >= 0x3f20000000000000)
            return (float)Fma(-(d * 0.5), d, 1.0);
        return 1.0f;
    }

    public static float Sin(float x)
    {
        if (!float.IsFinite(x))
            throw new NotSupportedException("sinf of a non-finite value is not ported");
        var d = (double)x;
        var bits = BitConverter.DoubleToInt64Bits(d) & long.MaxValue;
        if (bits > 0x3fe921fb54442d18)
        {
            if (bits >= 0x4170008ac0000000)
                throw new NotSupportedException("sinf's long argument reduction is not ported");
            var (r, region) = Reduce(BitConverter.Int64BitsToDouble(bits));
            var v = (region & 1) != 0 ? SinfCosPoly(r) : SinPoly(r);
            // Negated when the region is 0 or 1, and again when x is positive (vandnpd twice).
            if (region is 0 or 1)
                v = -v;
            if (BitConverter.DoubleToInt64Bits(d) >= 0)
                v = -v;
            return (float)v;
        }
        if (bits >= 0x3f80000000000000)
            return (float)SinPoly(d);
        if (bits >= 0x3f20000000000000)
            return (float)Fma(-(d * d * d), 0.16666666666666666, d);
        return x;
    }

    /// <summary>expf: 2^(n/64) for n = round(x 64 / ln 2), the remainder by a fused Cody-Waite step, a quadratic
    /// in double, the table entry and the power of two.</summary>
    public static float Exp(float x)
    {
        if (!float.IsFinite(x))
            throw new NotSupportedException("expf of a non-finite value is not ported");
        var d = (double)x;
        var t = d * 92.33248261689366;
        if (t >= 8192.0 || t < -9600.0)
            throw new NotSupportedException("expf's overflow and underflow paths are not ported");
        var n = (int)Math.Round(t, MidpointRounding.ToEven); // cvtpd2dq
        var r = Fma(-(double)n, 0.010830424696249145, d);
        var j = n & 63;
        var m = (n - j) >> 6;
        var p = Fma(0.16666666666666666, r, 0.5);
        var q = Fma(r * r, p, r);
        var e = BitConverter.Int64BitsToDouble((long)Exp2Table[j]);
        var v = Fma(q, e, e) * BitConverter.Int64BitsToDouble((long)(m + 1023) << 52);
        return (float)v;
    }

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    /// <summary>log10f for a positive normal x: near 1 (|x - 1| &lt; 1/16) the 2 atanh series split in high and low
    /// halves, else the mantissa rounded to 7 bits, its tabled log10 and reciprocal, and a cubic.</summary>
    public static float Log10(float x)
    {
        var bits = BitConverter.SingleToUInt32Bits(x);
        if (!(x > 0.0f) || !float.IsFinite(x))
            throw new NotSupportedException("log10f of zero, a negative or a non-finite value is not ported");
        var e = (float)((int)(bits >> 23) - 127);
        if (e == -127.0f)
            throw new NotSupportedException("log10f of a denormal is not ported");
        var xm1 = x - 1.0f;
        if (MathF.Abs(xm1) < 0.0625f)
        {
            var r = xm1;
            var s = r / (2.0f + r);
            var rs = r * s;
            var s2 = s + s;
            var s22 = s2 * s2;
            var s23 = s2 * s22;
            var poly = MathF.FusedMultiplyAdd(s22, 0.012500000186264515f, 0.0833333358168602f);
            poly = MathF.FusedMultiplyAdd(poly, s23, -rs);
            var hi = F(BitConverter.SingleToUInt32Bits(r) & 0xffff0000u);
            poly += r - hi;
            var a = poly * 0.43359375f;
            var b = MathF.FusedMultiplyAdd(poly, 0.0007007318781688809f, hi * 0.0007007318781688809f);
            return b + a + hi * 0.43359375f;
        }
        var mant = bits & 0x7fffffu;
        var round = (bits & 0x7f0000u) + ((bits & 0x8000u) << 1);
        var index = (int)(round >> 16);
        var f = F(mant | 0x3f000000u);
        var big = F(round | 0x3f000000u);
        var u = (big - f) * F(Log10Rcp[index]);
        var p = MathF.FusedMultiplyAdd(0.3333333432674408f, u, 0.5f);
        var w = MathF.FusedMultiplyAdd(p, u * u, u) * 0.4342944920063019f;
        var low = MathF.FusedMultiplyAdd(0.0002487456367816776f, e, -w) + F(Log10Lo[index]);
        var high = MathF.FusedMultiplyAdd(0.30078125f, e, F(Log10Hi[index]));
        return high + low;
    }

    /// <summary>
    /// atan2f (182683840; one path, no FMA dispatch): both arguments widened to double. Past NaNs, zeros, infinities
    /// and the exponent gaps (y's exponent above x's by more than 26 gives the float pi/2; below by more than 13 with
    /// x positive gives (float)(y / x)), the smaller magnitude over the larger as u: above 1/16 the table entry
    /// atan(c / 256) for c = trunc(256 u + 0.5) plus v = (256 small - c big) / (c small + 256 big) less v^3 / 3;
    /// below 1e-4 u itself; otherwise u - (1/3 - (1/5 - u^2 / 7) u^2) u^3. Then pi/2 minus it when the arguments were
    /// swapped, pi minus it for a negative x, negated for a negative y, rounded to float.
    /// </summary>
    public static float Atan2(float y, float x)
    {
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
            throw new NotSupportedException("atan2f of a NaN or an infinity is not ported");
        double dy = y, dx = x;
        var by = BitConverter.DoubleToInt64Bits(dy);
        var bx = BitConverter.DoubleToInt64Bits(dx);
        var gap = (int)((by >> 52) & 0x7ff) - (int)((bx >> 52) & 0x7ff);
        if ((by & long.MaxValue) == 0)
        {
            if (bx < 0)
                throw new NotSupportedException("atan2f of a zero y with a negative x is not ported");
            return (float)dy;
        }
        if ((bx & long.MaxValue) == 0 && by < 0)
            throw new NotSupportedException("atan2f of a negative y with a zero x is not ported");
        if (gap > 26)
            return by < 0 ? -1.5707963705062866f : 1.5707963705062866f;
        if (gap < -13 && bx >= 0)
        {
            if (gap < -126)
                throw new NotSupportedException("atan2f's underflow paths are not ported");
            return (float)(dy / dx);
        }
        if (gap < -26 && bx < 0)
            throw new NotSupportedException("atan2f near pi is not ported");
        dx = Math.Abs(dx);
        dy = Math.Abs(dy);
        var swap = dy > dx;
        var big = swap ? dy : dx;
        var small = swap ? dx : dy;
        var u = small / big;
        double r;
        if (u > 0.0625)
        {
            var c = (int)(u * 256.0 + 0.5);
            var cd = (double)c;
            var v = (small * 256.0 - cd * big) / (cd * small + big * 256.0);
            r = (v + BitConverter.Int64BitsToDouble((long)AtanTable[c - 16])) - v * v * v * 0.33333333333224097;
        }
        else if (0.0001 > u)
            r = u;
        else
        {
            var u2 = u * u;
            var t = (0.19999999999393223 - u2 * 0.1428571356180717) * u2;
            r = u - (0.3333333333333317 - t) * (u2 * u);
        }
        if (swap)
            r = 1.5707963267948966 - r;
        if (bx < 0)
            r = 3.141592653589793 - r;
        if (by < 0)
            r = -r;
        return (float)r;
    }

    /// <summary>atan2f's atan(c / 256) for c = 16 .. 256 (182b64e60), copied from the DLL.</summary>
    private static readonly ulong[] AtanTable =
    [
        0x3faff55bb72cfde9UL, 0x3fb0f99ea71d52a6UL, 0x3fb1f86dbf082d58UL, 0x3fb2f719318a4a9aUL,
        0x3fb3f59f0e7c559dUL, 0x3fb4f3fd677292fbUL, 0x3fb5f2324fd2d7b2UL, 0x3fb6f03bdcea4b0cUL,
        0x3fb7ee182602f10eUL, 0x3fb8ebc54478fb28UL, 0x3fb9e94153cfdcf1UL, 0x3fbae68a71c722b8UL,
        0x3fbbe39ebe6f07c3UL, 0x3fbce07c5c3cca32UL, 0x3fbddd21701eba6eUL, 0x3fbed98c2190043aUL,
        0x3fbfd5ba9aac2f6dUL, 0x3fc068d584212b3dUL, 0x3fc0e6adccf40881UL, 0x3fc1646541060850UL,
        0x3fc1e1fafb043726UL, 0x3fc25f6e171a535cUL, 0x3fc2dcbdb2fba1ffUL, 0x3fc359e8edeb99a3UL,
        0x3fc3d6eee8c6626cUL, 0x3fc453cec6092a9eUL, 0x3fc4d087a9da4f17UL, 0x3fc54d18ba11570aUL,
        0x3fc5c9811e3ec269UL, 0x3fc645bfffb3aa73UL, 0x3fc6c1d4898933d8UL, 0x3fc73dbde8a7d201UL,
        0x3fc7b97b4bce5b02UL, 0x3fc8350be398ebc7UL, 0x3fc8b06ee2879c28UL, 0x3fc92ba37d050271UL,
        0x3fc9a6a8e96c8626UL, 0x3fca217e601081a5UL, 0x3fca9c231b403279UL, 0x3fcb1696574d780bUL,
        0x3fcb90d7529260a2UL, 0x3fcc0ae54d768466UL, 0x3fcc84bf8a742e6dUL, 0x3fccfe654e1d5395UL,
        0x3fcd77d5df205736UL, 0x3fcdf110864c9d9dUL, 0x3fce6a148e96ec4dUL, 0x3fcee2e1451d980cUL,
        0x3fcf5b75f92c80ddUL, 0x3fcfd3d1fc40dbe4UL, 0x3fd025fa510665b5UL, 0x3fd061eea03d6290UL,
        0x3fd09dc597d86362UL, 0x3fd0d97ee509acb3UL, 0x3fd1151a362431c9UL, 0x3fd150973a9ce546UL,
        0x3fd18bf5a30bf178UL, 0x3fd1c735212dd883UL, 0x3fd2025567e47c95UL, 0x3fd23d562b381041UL,
        0x3fd278372057ef45UL, 0x3fd2b2f7fd9b5fe2UL, 0x3fd2ed987a823cfeUL, 0x3fd328184fb58951UL,
        0x3fd362773707ebcbUL, 0x3fd39cb4eb76157bUL, 0x3fd3d6d129271134UL, 0x3fd410cbad6c7d32UL,
        0x3fd44aa436c2af09UL, 0x3fd4845a84d0c21bUL, 0x3fd4bdee586890e6UL, 0x3fd4f75f73869978UL,
        0x3fd530ad9951cd49UL, 0x3fd569d88e1b4cd7UL, 0x3fd5a2e0175e0f4eUL, 0x3fd5dbc3fbbe768dUL,
        0x3fd614840309cfe1UL, 0x3fd64d1ff635c1c5UL, 0x3fd685979f5fa6fdUL, 0x3fd6bdeac9cbd76cUL,
        0x3fd6f61941e4def0UL, 0x3fd72e22d53aa2a9UL, 0x3fd7660752817501UL, 0x3fd79dc6899118d1UL,
        0x3fd7d5604b63b3f7UL, 0x3fd80cd46a14b1d0UL, 0x3fd84422b8df95d7UL, 0x3fd87b4b0c1ebedbUL,
        0x3fd8b24d394a1b25UL, 0x3fd8e92916f5cde8UL, 0x3fd91fde7cd0c662UL, 0x3fd9566d43a34907UL,
        0x3fd98cd5454d6b18UL, 0x3fd9c3165cc58107UL, 0x3fd9f93066168001UL, 0x3fda2f233e5e530bUL,
        0x3fda64eec3cc23fcUL, 0x3fda9a92d59e98cfUL, 0x3fdad00f5422058bUL, 0x3fdb056420ae9343UL,
        0x3fdb3a911da65c6cUL, 0x3fdb6f962e737efbUL, 0x3fdba473378624a5UL, 0x3fdbd9281e528191UL,
        0x3fdc0db4c94ec9efUL, 0x3fdc42191ff11eb6UL, 0x3fdc76550aad71f8UL, 0x3fdcaa6872f3631bUL,
        0x3fdcde53432c1350UL, 0x3fdd121566b7f2adUL, 0x3fdd45aec9ec862bUL, 0x3fdd791f5a1226f4UL,
        0x3fddac670561bb4fUL, 0x3fdddf85bb026974UL, 0x3fde127b6b0744afUL, 0x3fde4548066cf51aUL,
        0x3fde77eb7f175a34UL, 0x3fdeaa65c7cf28c4UL, 0x3fdedcb6d43f8434UL, 0x3fdf0ede98f393cfUL,
        0x3fdf40dd0b541417UL, 0x3fdf72b221a4e495UL, 0x3fdfa45dd3029258UL, 0x3fdfd5e0175fdf83UL,
        0x3fe0039c73c1a40bUL, 0x3fe01c341e82422dUL, 0x3fe034b709250488UL, 0x3fe04d25314342e5UL,
        0x3fe0657e94db30cfUL, 0x3fe07dc3324e9b38UL, 0x3fe095f30861a58fUL, 0x3fe0ae0e1639866cUL,
        0x3fe0c6145b5b43daUL, 0x3fe0de05d7aa6f7cUL, 0x3fe0f5e28b67e295UL, 0x3fe10daa77307a0dUL,
        0x3fe1255d9bfbd2a8UL, 0x3fe13cfbfb1b056eUL, 0x3fe1548596376469UL, 0x3fe16bfa6f5137e1UL,
        0x3fe1835a88be7c13UL, 0x3fe19aa5e5299f99UL, 0x3fe1b1dc87904284UL, 0x3fe1c8fe7341f64fUL,
        0x3fe1e00babdefeb3UL, 0x3fe1f7043557138aUL, 0x3fe20de813e823b1UL, 0x3fe224b74c1d192aUL,
        0x3fe23b71e2cc9e6aUL, 0x3fe25217dd17e501UL, 0x3fe268a940696da6UL, 0x3fe27f261273d1b3UL,
        0x3fe2958e59308e30UL, 0x3fe2abe21aded073UL, 0x3fe2c2215e024465UL, 0x3fe2d84c2961e48bUL,
        0x3fe2ee628406cbcaUL, 0x3fe30464753b090aUL, 0x3fe31a52048874beUL, 0x3fe3302b39b78856UL,
        0x3fe345f01cce37bbUL, 0x3fe35ba0b60eccceUL, 0x3fe3713d0df6c503UL, 0x3fe386c52d3db11eUL,
        0x3fe39c391cd41719UL, 0x3fe3b198e5e2564aUL, 0x3fe3c6e491c78dc4UL, 0x3fe3dc1c2a188504UL,
        0x3fe3f13fb89e96f4UL, 0x3fe4064f47569f48UL, 0x3fe41b4ae06fea41UL, 0x3fe430328e4b26d5UL,
        0x3fe445065b795b55UL, 0x3fe459c652badc7fUL, 0x3fe46e727efe4715UL, 0x3fe4830aeb5f7bfdUL,
        0x3fe4978fa3269ee1UL, 0x3fe4ac00b1c71762UL, 0x3fe4c05e22de94e4UL, 0x3fe4d4a8023414e8UL,
        0x3fe4e8de5bb6ec04UL, 0x3fe4fd013b7dd17eUL, 0x3fe51110adc5ed81UL, 0x3fe5250cbef1e9faUL,
        0x3fe538f57b89061eUL, 0x3fe54ccaf0362c8fUL, 0x3fe5608d29c70c34UL, 0x3fe5743c352b33b9UL,
        0x3fe587d81f732fbaUL, 0x3fe59b60f5cfab9dUL, 0x3fe5aed6c5909517UL, 0x3fe5c2399c244260UL,
        0x3fe5d58987169b18UL, 0x3fe5e8c6941043cfUL, 0x3fe5fbf0d0d5cc49UL, 0x3fe60f084b46e05eUL,
        0x3fe6220d115d7b8dUL, 0x3fe634ff312d1f3bUL, 0x3fe647deb8e20b8fUL, 0x3fe65aabb6c07b02UL,
        0x3fe66d663923e086UL, 0x3fe6800e4e7e2857UL, 0x3fe692a40556fb6aUL, 0x3fe6a5276c4b0575UL,
        0x3fe6b798920b3d98UL, 0x3fe6c9f7855c3198UL, 0x3fe6dc44551553aeUL, 0x3fe6ee7f10204aefUL,
        0x3fe700a7c5784633UL, 0x3fe712be84295198UL, 0x3fe724c35b4fae7bUL, 0x3fe736b65a172dffUL,
        0x3fe748978fba8e0fUL, 0x3fe75a670b82d8d8UL, 0x3fe76c24dcc6c6c0UL, 0x3fe77dd112ea22c7UL,
        0x3fe78f6bbd5d315eUL, 0x3fe7a0f4eb9c19a2UL, 0x3fe7b26cad2e50fdUL, 0x3fe7c3d311a6092bUL,
        0x3fe7d528289fa093UL, 0x3fe7e66c01c114fdUL, 0x3fe7f79eacb97898UL, 0x3fe808c03940694aUL,
        0x3fe819d0b7158a4cUL, 0x3fe82ad036000005UL, 0x3fe83bbec5cdee22UL, 0x3fe84c9c7653f7eaUL,
        0x3fe85d69576cc2c5UL, 0x3fe86e2578f87ae5UL, 0x3fe87ed0eadc5a2aUL, 0x3fe88f6bbd023118UL,
        0x3fe89ff5ff57f1f7UL, 0x3fe8b06fc1cf3dfeUL, 0x3fe8c0d9145cf49dUL, 0x3fe8d13206f8c4caUL,
        0x3fe8e17aa99cc05dUL, 0x3fe8f1b30c44f167UL, 0x3fe901db3eeef187UL, 0x3fe911f35199833bUL,
        0x3fe921fb54442d18UL,
    ];

    private static readonly ulong[] Exp2Table =
    [
        0x3ff0000000000000UL, 0x3ff02c9a3e778061UL, 0x3ff059b0d3158574UL, 0x3ff0874518759bc8UL,
        0x3ff0b5586cf9890fUL, 0x3ff0e3ec32d3d1a2UL, 0x3ff11301d0125b51UL, 0x3ff1429aaea92de0UL,
        0x3ff172b83c7d517bUL, 0x3ff1a35beb6fcb75UL, 0x3ff1d4873168b9aaUL, 0x3ff2063b88628cd6UL,
        0x3ff2387a6e756238UL, 0x3ff26b4565e27cddUL, 0x3ff29e9df51fdee1UL, 0x3ff2d285a6e4030bUL,
        0x3ff306fe0a31b715UL, 0x3ff33c08b26416ffUL, 0x3ff371a7373aa9cbUL, 0x3ff3a7db34e59ff7UL,
        0x3ff3dea64c123422UL, 0x3ff4160a21f72e2aUL, 0x3ff44e086061892dUL, 0x3ff486a2b5c13cd0UL,
        0x3ff4bfdad5362a27UL, 0x3ff4f9b2769d2ca7UL, 0x3ff5342b569d4f82UL, 0x3ff56f4736b527daUL,
        0x3ff5ab07dd485429UL, 0x3ff5e76f15ad2148UL, 0x3ff6247eb03a5585UL, 0x3ff6623882552225UL,
        0x3ff6a09e667f3bcdUL, 0x3ff6dfb23c651a2fUL, 0x3ff71f75e8ec5f74UL, 0x3ff75feb564267c9UL,
        0x3ff7a11473eb0187UL, 0x3ff7e2f336cf4e62UL, 0x3ff82589994cce13UL, 0x3ff868d99b4492edUL,
        0x3ff8ace5422aa0dbUL, 0x3ff8f1ae99157736UL, 0x3ff93737b0cdc5e5UL, 0x3ff97d829fde4e50UL,
        0x3ff9c49182a3f090UL, 0x3ffa0c667b5de565UL, 0x3ffa5503b23e255dUL, 0x3ffa9e6b5579fdbfUL,
        0x3ffae89f995ad3adUL, 0x3ffb33a2b84f15fbUL, 0x3ffb7f76f2fb5e47UL, 0x3ffbcc1e904bc1d2UL,
        0x3ffc199bdd85529cUL, 0x3ffc67f12e57d14bUL, 0x3ffcb720dcef9069UL, 0x3ffd072d4a07897cUL,
        0x3ffd5818dcfba487UL, 0x3ffda9e603db3285UL, 0x3ffdfc97337b9b5fUL, 0x3ffe502ee78b3ff6UL,
        0x3ffea4afa2a490daUL, 0x3ffefa1bee615a27UL, 0x3fff50765b6e4540UL, 0x3fffa7c1819e90d8UL,
    ];
    private static readonly uint[] Log10Rcp =
    [
        0x40000000U, 0x3ffe03f8U, 0x3ffc0fc1U, 0x3ffa232dU, 0x3ff83e10U, 0x3ff6603eU, 0x3ff4898dU, 0x3ff2b9d6U,
        0x3ff0f0f1U, 0x3fef2eb7U, 0x3fed7304U, 0x3febbdb3U, 0x3fea0ea1U, 0x3fe865acU, 0x3fe6c2b4U, 0x3fe52598U,
        0x3fe38e39U, 0x3fe1fc78U, 0x3fe07038U, 0x3fdee95cU, 0x3fdd67c9U, 0x3fdbeb62U, 0x3fda740eU, 0x3fd901b2U,
        0x3fd79436U, 0x3fd62b81U, 0x3fd4c77bU, 0x3fd3680dU, 0x3fd20d21U, 0x3fd0b6a0U, 0x3fcf6475U, 0x3fce168aU,
        0x3fcccccdU, 0x3fcb8728U, 0x3fca4588U, 0x3fc907daU, 0x3fc7ce0cU, 0x3fc6980cU, 0x3fc565c8U, 0x3fc43730U,
        0x3fc30c31U, 0x3fc1e4bcU, 0x3fc0c0c1U, 0x3fbfa030U, 0x3fbe82faU, 0x3fbd6910U, 0x3fbc5264U, 0x3fbb3ee7U,
        0x3fba2e8cU, 0x3fb92144U, 0x3fb81703U, 0x3fb70fbbU, 0x3fb60b61U, 0x3fb509e7U, 0x3fb40b41U, 0x3fb30f63U,
        0x3fb21643U, 0x3fb11fd4U, 0x3fb02c0bU, 0x3faf3adeU, 0x3fae4c41U, 0x3fad602bU, 0x3fac7692U, 0x3fab8f6aU,
        0x3faaaaabU, 0x3fa9c84aU, 0x3fa8e83fU, 0x3fa80a81U, 0x3fa72f05U, 0x3fa655c4U, 0x3fa57eb5U, 0x3fa4a9cfU,
        0x3fa3d70aU, 0x3fa3065eU, 0x3fa237c3U, 0x3fa16b31U, 0x3fa0a0a1U, 0x3f9fd80aU, 0x3f9f1166U, 0x3f9e4cadU,
        0x3f9d89d9U, 0x3f9cc8e1U, 0x3f9c09c1U, 0x3f9b4c70U, 0x3f9a90e8U, 0x3f99d723U, 0x3f991f1aU, 0x3f9868c8U,
        0x3f97b426U, 0x3f97012eU, 0x3f964fdaU, 0x3f95a025U, 0x3f94f209U, 0x3f944581U, 0x3f939a86U, 0x3f92f114U,
        0x3f924925U, 0x3f91a2b4U, 0x3f90fdbcU, 0x3f905a38U, 0x3f8fb824U, 0x3f8f177aU, 0x3f8e7835U, 0x3f8dda52U,
        0x3f8d3dcbU, 0x3f8ca29cU, 0x3f8c08c1U, 0x3f8b7034U, 0x3f8ad8f3U, 0x3f8a42f8U, 0x3f89ae41U, 0x3f891ac7U,
        0x3f888889U, 0x3f87f781U, 0x3f8767abU, 0x3f86d905U, 0x3f864b8aU, 0x3f85bf37U, 0x3f853408U, 0x3f84a9faU,
        0x3f842108U, 0x3f839930U, 0x3f83126fU, 0x3f828cc0U, 0x3f820821U, 0x3f81848eU, 0x3f810204U, 0x3f808081U,
        0x3f800000U,
    ];
    private static readonly uint[] Log10Hi =
    [
        0x00000000U, 0x3b5d4000U, 0x3bdc8000U, 0x3c24c000U, 0x3c5ac000U, 0x3c884000U, 0x3ca2c000U, 0x3cbd4000U,
        0x3cd78000U, 0x3cf1c000U, 0x3d05c000U, 0x3d128000U, 0x3d1f4000U, 0x3d2c0000U, 0x3d388000U, 0x3d450000U,
        0x3d518000U, 0x3d5dc000U, 0x3d6a0000U, 0x3d760000U, 0x3d810000U, 0x3d870000U, 0x3d8d0000U, 0x3d92c000U,
        0x3d98c000U, 0x3d9e8000U, 0x3da44000U, 0x3daa0000U, 0x3dafc000U, 0x3db58000U, 0x3dbb4000U, 0x3dc0c000U,
        0x3dc64000U, 0x3dcc0000U, 0x3dd18000U, 0x3dd6c000U, 0x3ddc4000U, 0x3de1c000U, 0x3de70000U, 0x3dec8000U,
        0x3df1c000U, 0x3df70000U, 0x3dfc4000U, 0x3e00c000U, 0x3e034000U, 0x3e05c000U, 0x3e088000U, 0x3e0b0000U,
        0x3e0d8000U, 0x3e100000U, 0x3e128000U, 0x3e150000U, 0x3e178000U, 0x3e1a0000U, 0x3e1c8000U, 0x3e1ec000U,
        0x3e214000U, 0x3e23c000U, 0x3e260000U, 0x3e288000U, 0x3e2ac000U, 0x3e2d4000U, 0x3e2f8000U, 0x3e31c000U,
        0x3e344000U, 0x3e368000U, 0x3e38c000U, 0x3e3b0000U, 0x3e3d4000U, 0x3e3fc000U, 0x3e420000U, 0x3e440000U,
        0x3e464000U, 0x3e488000U, 0x3e4ac000U, 0x3e4d0000U, 0x3e4f4000U, 0x3e514000U, 0x3e538000U, 0x3e55c000U,
        0x3e57c000U, 0x3e5a0000U, 0x3e5c0000U, 0x3e5e4000U, 0x3e604000U, 0x3e624000U, 0x3e648000U, 0x3e668000U,
        0x3e688000U, 0x3e6ac000U, 0x3e6cc000U, 0x3e6ec000U, 0x3e70c000U, 0x3e72c000U, 0x3e74c000U, 0x3e76c000U,
        0x3e78c000U, 0x3e7ac000U, 0x3e7cc000U, 0x3e7ec000U, 0x3e804000U, 0x3e814000U, 0x3e824000U, 0x3e834000U,
        0x3e840000U, 0x3e850000U, 0x3e860000U, 0x3e870000U, 0x3e880000U, 0x3e88c000U, 0x3e89c000U, 0x3e8ac000U,
        0x3e8bc000U, 0x3e8c8000U, 0x3e8d8000U, 0x3e8e8000U, 0x3e8f4000U, 0x3e904000U, 0x3e914000U, 0x3e920000U,
        0x3e930000U, 0x3e93c000U, 0x3e94c000U, 0x3e958000U, 0x3e968000U, 0x3e978000U, 0x3e984000U, 0x3e994000U,
        0x3e9a0000U,
    ];
    private static readonly uint[] Log10Lo =
    [
        0x00000000U, 0x367a8e44U, 0x368ed49fU, 0x36c21451U, 0x375211d6U, 0x3720ea11U, 0x37e9eb59U, 0x37b87be7U,
        0x37bf2560U, 0x33d597a0U, 0x37806a05U, 0x3820581fU, 0x38223334U, 0x378e3bacU, 0x3810684fU, 0x37feb7aeU,
        0x36a9d609U, 0x37a68163U, 0x376a8b27U, 0x384c8fd6U, 0x3885183eU, 0x3874a760U, 0x380d1154U, 0x38ea42bdU,
        0x384c1571U, 0x38ba66b8U, 0x38e7da3bU, 0x38eee632U, 0x38d00911U, 0x388bbedeU, 0x378a0512U, 0x3894c7a0U,
        0x38e30710U, 0x36db2829U, 0x3729d609U, 0x38fa0e82U, 0x38bc9a75U, 0x383a9297U, 0x38dc83c8U, 0x37eac335U,
        0x38706ac3U, 0x389574c2U, 0x3892d068U, 0x38615032U, 0x3917acf4U, 0x3967a126U, 0x38217840U, 0x38b420abU,
        0x38f9c7b2U, 0x391103bdU, 0x39169a6bU, 0x390dd194U, 0x38eda471U, 0x38a38950U, 0x37f6844aU, 0x395e1cdbU,
        0x390fcffcU, 0x38503e9dU, 0x394b00fdU, 0x38a9910aU, 0x39518a31U, 0x3882d2c2U, 0x392488e4U, 0x397b0affU,
        0x388a22d8U, 0x3902bd5eU, 0x39342f85U, 0x39598811U, 0x3972e6b1U, 0x34d53654U, 0x360ca25eU, 0x39785cc0U,
        0x39630710U, 0x39424ed7U, 0x39165101U, 0x38be5421U, 0x37e7b0c0U, 0x394fd0c3U, 0x38efaaaaU, 0x37a8f566U,
        0x3927c744U, 0x383fa4d5U, 0x392d9e39U, 0x3803feaeU, 0x390a268cU, 0x39692b80U, 0x38789b4fU, 0x3909307dU,
        0x394a601cU, 0x35e67edcU, 0x383e386dU, 0x38a7743dU, 0x38dccec3U, 0x38ff57e0U, 0x39079d8bU, 0x390651a6U,
        0x38f7bad9U, 0x38d0ab82U, 0x38979e7dU, 0x381978eeU, 0x397816c8U, 0x39410cb2U, 0x39015384U, 0x3863fa28U,
        0x39f41065U, 0x39c7668aU, 0x39968afaU, 0x39430db9U, 0x38a18cf3U, 0x39eb2907U, 0x39a9e10cU, 0x39492800U,
        0x385a53d1U, 0x39ce0cf7U, 0x3979c7b2U, 0x389f5d99U, 0x39ceefcbU, 0x39646a39U, 0x380d7a9bU, 0x39ad6650U,
        0x390ac3b8U, 0x39d9a9a8U, 0x39548a99U, 0x39f73c4bU, 0x3980960eU, 0x374b3d5aU, 0x39888f1eU, 0x37679a07U,
        0x39826a13U,
    ];

}
