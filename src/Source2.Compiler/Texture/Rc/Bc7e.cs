using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.RcTexture;

/// <summary>Whose <c>rcpps</c> the BC7 encoder reproduces (docs/TEXTURES.md, "BC7 without the CPU").</summary>
public enum Bc7Vendor
{
    /// <summary>AMD's: what resourcecompiler writes on an AMD CPU, and our references.</summary>
    Amd,
    /// <summary>Intel's: what resourcecompiler writes on an Intel CPU.</summary>
    Intel,
}

/// <summary>
/// Valve's BC7 encoder (bc7e, level 6) as a native library built from <c>Native/bc7e</c> with
/// one vendor's reciprocal table, so its blocks are the same on every machine that can run it
/// (x64 with AVX2 and FMA, the target Valve's own DLL takes on such a CPU).
/// </summary>
public static unsafe class Bc7e
{
    private static readonly Lazy<nint>[] Encoders =
        [new(() => Load(Bc7Vendor.Amd)), new(() => Load(Bc7Vendor.Intel))];

    /// <summary>Why BC7 cannot be encoded here, or null when it can.</summary>
    public static string? Unavailable(Bc7Vendor vendor)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return $"BC7 needs an x64 process; this one is {RuntimeInformation.ProcessArchitecture}.";
        if (!Avx2.IsSupported || !Fma.IsSupported)
            return "BC7 needs a CPU with AVX2 and FMA.";
        return Encoders[(int)vendor].Value == 0 ? $"The native bc7e library ({FileName(vendor)}) was not found." : null;
    }

    /// <summary>Encode blocks of 16 RGBA8 pixels (R, G, B, A in memory) to 16-byte BC7 blocks.</summary>
    public static byte[] EncodeBlocks(ReadOnlySpan<byte> rgbaBlocks, bool perceptual, Bc7Vendor vendor)
    {
        if (rgbaBlocks.Length % 64 != 0)
            throw new ArgumentException("Blocks are 64 bytes each.", nameof(rgbaBlocks));
        if (Unavailable(vendor) is { } why)
            throw new InvalidOperationException(why);
        var fn = (delegate* unmanaged<uint*, ulong*, uint, int, void>)Encoders[(int)vendor].Value;
        int blocks = rgbaBlocks.Length / 64;
        var output = new byte[blocks * 16];
        fixed (byte* src = rgbaBlocks)
        fixed (byte* dst = output)
            fn((uint*)src, (ulong*)dst, (uint)blocks, perceptual ? 1 : 0);
        return output;
    }

    private static string FileName(Bc7Vendor vendor)
    {
        var v = vendor == Bc7Vendor.Intel ? "intel" : "amd";
        return OperatingSystem.IsWindows() ? $"bc7e_s2c_{v}.dll" : $"libbc7e_s2c_{v}.so";
    }

    private static nint Load(Bc7Vendor vendor)
    {
        var file = FileName(vendor);
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "runtimes", rid, "native", file), Path.Combine(baseDir, file) })
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var lib)
                && NativeLibrary.TryGetExport(lib, "s2c_bc7e_encode", out var fn))
                return fn;
        return 0;
    }
}
