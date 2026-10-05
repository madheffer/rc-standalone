using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Source2.Compiler.Tests;

/// <summary>
/// The installed vphysics2.dll, loaded into the test process so the port can be
/// checked against Valve's own functions: same memory in, compare what comes out.
///
/// <para>Only the build the port was read from is used. Internal functions are
/// called by address, and an update moves every address, so a different
/// vphysics2.dll makes <see cref="Load"/> return null and the oracle tests
/// return without asserting. Loading the DLL runs nothing of the game; it is a
/// plain library in this process.</para>
/// </summary>
internal static unsafe class Vphysics2Oracle
{
    /// <summary>SHA-256 of vphysics2.dll as shipped on 2026-09-24.</summary>
    private const string Build = "0f896375fa9233196e3de769f23ce3c21907c1adab81a786418822da75518228";

    private const ulong ImageBase = 0x180000000;

    private static readonly object Gate = new();
    private static bool _tried;
    private static nint _module;

    /// <summary>The loaded module's base, or null when the build is not the one read.</summary>
    public static nint? Load()
    {
        lock (Gate)
        {
            if (_tried)
                return _module == 0 ? null : _module;
            _tried = true;
            // The installed DLL when it is the build read, else the archived
            // copy of that build staged beside the tier0 it imports.
            var dll = new[] { Path(), Staged() }.FirstOrDefault(d => d != null && IsBuild(d));
            if (dll == null)
                return null;
            // tier0.dll and the rest are beside it.
            if (NativeLibrary.TryLoad(dll, typeof(Vphysics2Oracle).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32,
                    out var handle))
                _module = handle;
            return _module == 0 ? null : _module;
        }
    }

    /// <summary>The address of a function Ghidra calls FUN_&lt;va&gt;.</summary>
    public static nint At(nint module, ulong va) => module + (nint)(va - ImageBase);

    /// <summary>A tier0 export, from the tier0.dll vphysics2 loaded.</summary>
    public static nint Tier0(string name)
        => NativeLibrary.GetExport(NativeLibrary.Load("tier0.dll"), name);

    private static bool IsBuild(string dll)
    {
        using var stream = File.OpenRead(dll);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == Build;
    }

    /// <summary>
    /// D:/tools/binaries' vphysics2_20260924.dll and tier0_20260923.dll (the
    /// installed tier0 still) copied to a folder of their own under the
    /// names vphysics2 loads them by, or null when the archive lacks them.
    /// </summary>
    private static string? Staged()
    {
        var archive = Environment.GetEnvironmentVariable("S2C_BINARIES") ?? @"D:\tools\binaries";
        var (vp, t0) = (System.IO.Path.Combine(archive, "vphysics2_20260924.dll"), System.IO.Path.Combine(archive, "tier0_20260923.dll"));
        if (!File.Exists(vp) || !File.Exists(t0))
            return null;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s2c_vphysics2_20260924");
        Directory.CreateDirectory(dir);
        var dll = System.IO.Path.Combine(dir, "vphysics2.dll");
        if (!File.Exists(dll))
            File.Copy(vp, dll);
        if (!File.Exists(System.IO.Path.Combine(dir, "tier0.dll")))
            File.Copy(t0, System.IO.Path.Combine(dir, "tier0.dll"));
        return dll;
    }

    private static string? Path()
    {
        var pak = CS2Fixtures.StockPak();
        if (pak is null)
            return null;
        var dll = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pak)!, "..")),
            "bin", "win64", "vphysics2.dll");
        return File.Exists(dll) ? dll : null;
    }
}
