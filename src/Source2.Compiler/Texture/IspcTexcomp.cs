using System.Runtime.InteropServices;

namespace Source2.Compiler;

/// <summary>
/// Intel's ISPC Texture Compressor as resourcecompiler loads it
/// (ispc_texcomp.dll from game/bin/win64, 180c72980: CompressBlocksBC1, BC3,
/// BC6H, BC7 and the GetProfile_* tables). resourcecompiler compresses one
/// row of 4 x 4 blocks per call (180c46c40 / 180c44880): a surface of the
/// image's width (at least 4) by 4 pixels, RGBA half floats for BC6H, RGBA8
/// for BC1/BC3.
/// <para>The library is loaded from a folder given at run time (the CS2
/// install), so this works where the game is installed (Windows); the
/// encoder is open source (ISPCTextureCompressor, MIT) for elsewhere.</para>
/// </summary>
public static unsafe class IspcTexcomp
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Surface
    {
        public byte* Ptr;
        public int Width, Height, Stride;
    }

    /// <summary>bc6h_enc_settings.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Bc6hSettings
    {
        [MarshalAs(UnmanagedType.U1)] public bool SlowMode;
        [MarshalAs(UnmanagedType.U1)] public bool FastMode;
        public int RefineIterations1p, RefineIterations2p, FastSkipThreshold;
    }

    private static nint _lib;
    private static delegate* unmanaged[Cdecl]<Surface*, byte*, Bc6hSettings*, void> _bc6h;
    private static delegate* unmanaged[Cdecl]<Surface*, byte*, void> _bc1;
    private static readonly delegate* unmanaged[Cdecl]<Bc6hSettings*, void>[] _profiles = new delegate* unmanaged[Cdecl]<Bc6hSettings*, void>[5];

    /// <summary>The BC6H profiles in resourcecompiler's table order.</summary>
    public static readonly string[] Bc6hProfiles = ["veryfast", "fast", "basic", "slow", "veryslow"];

    public static bool Load(string folder)
    {
        if (_lib != 0)
            return true;
        var path = Path.Combine(folder, "ispc_texcomp.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out _lib))
            return false;
        _bc6h = (delegate* unmanaged[Cdecl]<Surface*, byte*, Bc6hSettings*, void>)NativeLibrary.GetExport(_lib, "CompressBlocksBC6H");
        _bc1 = (delegate* unmanaged[Cdecl]<Surface*, byte*, void>)NativeLibrary.GetExport(_lib, "CompressBlocksBC1");
        for (var i = 0; i < 5; i++)
            _profiles[i] = (delegate* unmanaged[Cdecl]<Bc6hSettings*, void>)NativeLibrary.GetExport(_lib, "GetProfile_bc6h_" + Bc6hProfiles[i]);
        return true;
    }

    public static Bc6hSettings Bc6hProfile(int index)
    {
        var s = default(Bc6hSettings);
        _profiles[index](&s);
        return s;
    }

    /// <summary>BC6H (unsigned) blocks, row by row of blocks as resourcecompiler calls it: half RGBA texels, width x height.</summary>
    public static byte[] CompressBc6h(ReadOnlySpan<Half> rgba, int width, int height, Bc6hSettings settings)
    {
        var bw = (width + 3) / 4;
        var bh = (height + 3) / 4;
        var output = new byte[bw * bh * 16];
        var rowWidth = Math.Max(width, 4);
        var row = new Half[rowWidth * 4 * 4];
        fixed (byte* dst = output)
        fixed (Half* src = row)
            for (var by = 0; by < bh; by++)
            {
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < rowWidth; x++)
                    {
                        var sx = Math.Min(x, width - 1);
                        var sy = Math.Min(by * 4 + y, height - 1);
                        rgba.Slice((sy * width + sx) * 4, 4).CopyTo(row.AsSpan((y * rowWidth + x) * 4, 4));
                    }
                var surface = new Surface { Ptr = (byte*)src, Width = rowWidth, Height = 4, Stride = rowWidth * 8 };
                _bc6h(&surface, dst + by * bw * 16, &settings);
            }
        return output;
    }

    /// <summary>BC1 blocks from RGBA8 texels, row by row of blocks.</summary>
    public static byte[] CompressBc1(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var bw = (width + 3) / 4;
        var bh = (height + 3) / 4;
        var output = new byte[bw * bh * 8];
        var rowWidth = Math.Max(width, 4);
        var row = new byte[rowWidth * 4 * 4];
        fixed (byte* dst = output)
        fixed (byte* src = row)
            for (var by = 0; by < bh; by++)
            {
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < rowWidth; x++)
                    {
                        var sx = Math.Min(x, width - 1);
                        var sy = Math.Min(by * 4 + y, height - 1);
                        rgba.Slice((sy * width + sx) * 4, 4).CopyTo(row.AsSpan((y * rowWidth + x) * 4, 4));
                    }
                var surface = new Surface { Ptr = src, Width = rowWidth, Height = 4, Stride = rowWidth * 4 };
                _bc1(&surface, dst + by * bw * 8);
            }
        return output;
    }
}
