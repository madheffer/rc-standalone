using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Simulation;

/// <summary>
/// rcpps and rsqrtps, whose 12-bit estimates differ between CPU vendors, so
/// Valve's traces and contact code give different bits on AMD and Intel. By
/// default this is the hardware's, which is what resourcecompiler gives on the
/// same machine. <c>S2C_ESTIMATE=zen4</c> replays a Zen 4's instead, from
/// tables of its results: both depend only on the top 12 mantissa bits (and
/// the exponent's parity for rsqrt) and scale exactly with the exponent, which
/// a sweep of every float confirmed. That lets another machine reproduce
/// references compiled on a Zen 4.
/// </summary>
public static class CpuEstimate
{
    private static readonly bool UseTable = Environment.GetEnvironmentVariable("S2C_ESTIMATE") switch
    {
        null or "" or "hardware" => !Sse.IsSupported, // without SSE (ARM) the table stands in
        "zen4" => true,
        var mode => throw new InvalidOperationException($"S2C_ESTIMATE={mode}: known values are hardware and zen4"),
    };

    private static uint[] RcpTable => field ??= Table("estimate_zen4_rcp.bin", 4096);
    private static uint[] RsqrtTable => field ??= Table("estimate_zen4_rsqrt.bin", 8192);

    /// <summary>Whether the estimates come from the table rather than this CPU.</summary>
    public static bool Emulated => UseTable;

    private static uint[] Table(string name, int count)
    {
        using var stream = typeof(CpuEstimate).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing resource {name}");
        var bytes = new byte[count * 4];
        stream.ReadExactly(bytes);
        var table = new uint[count];
        Buffer.BlockCopy(bytes, 0, table, 0, bytes.Length);
        return table;
    }

    /// <summary>rcpss.</summary>
    public static float Rcp(float x) => UseTable ? TableRcp(x) : Sse.ReciprocalScalar(Vector128.CreateScalarUnsafe(x)).ToScalar();

    /// <summary>rsqrtss.</summary>
    public static float Rsqrt(float x) => UseTable ? TableRsqrt(x) : Sse.ReciprocalSqrtScalar(Vector128.CreateScalarUnsafe(x)).ToScalar();

    /// <summary>Zen 4's rcpss.</summary>
    internal static float TableRcp(float x)
    {
        uint b = BitConverter.SingleToUInt32Bits(x), sign = b & 0x80000000, e = (b >> 23) & 255;
        if (e == 0)
            return BitConverter.UInt32BitsToSingle(sign | 0x7f800000);
        if (e == 255)
            return BitConverter.UInt32BitsToSingle((b & 0x7fffff) != 0 ? b | 0x400000 : sign);
        var t = RcpTable[(b >> 11) & 4095];
        var exponent = (int)(t >> 23) + 127 - (int)e;
        return BitConverter.UInt32BitsToSingle(exponent <= 0 ? sign : sign | ((uint)exponent << 23) | (t & 0x7fffff));
    }

    /// <summary>Zen 4's rsqrtss.</summary>
    internal static float TableRsqrt(float x)
    {
        uint b = BitConverter.SingleToUInt32Bits(x), sign = b & 0x80000000, e = (b >> 23) & 255;
        if (e == 0)
            return BitConverter.UInt32BitsToSingle(sign | 0x7f800000);
        if (e == 255)
            return BitConverter.UInt32BitsToSingle((b & 0x7fffff) != 0 ? b | 0x400000 : sign != 0 ? 0xffc00000 : 0);
        if (sign != 0)
            return BitConverter.UInt32BitsToSingle(0xffc00000);
        var odd = (e + 1) & 1;
        var t = RsqrtTable[(odd << 12) | ((b >> 11) & 4095)];
        var exponent = (int)(t >> 23) - (((int)e - 127 - (int)odd) >> 1);
        return BitConverter.UInt32BitsToSingle(((uint)exponent << 23) | (t & 0x7fffff));
    }

    /// <summary>rcpps.</summary>
    public static Vector128<float> Rcp(Vector128<float> x) => !UseTable ? Sse.Reciprocal(x)
        : Vector128.Create(TableRcp(x[0]), TableRcp(x[1]), TableRcp(x[2]), TableRcp(x[3]));

    /// <summary>rsqrtps.</summary>
    public static Vector128<float> Rsqrt(Vector128<float> x) => !UseTable ? Sse.ReciprocalSqrt(x)
        : Vector128.Create(TableRsqrt(x[0]), TableRsqrt(x[1]), TableRsqrt(x[2]), TableRsqrt(x[3]));
}
