using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Source2.Compiler.Tests;

/// <summary>
/// The installed resourcecompiler.dll, loaded into the test process so pure
/// maths inside it can be called by address and compared with the port. Like
/// <see cref="Vphysics2Oracle"/>, only the build the port was read from is
/// used; another build makes <see cref="Load"/> return null and the oracle
/// tests return without asserting.
/// </summary>
internal static unsafe class ResourceCompilerOracle
{
    /// <summary>SHA-256 of resourcecompiler.dll as shipped on 2026-09-24.</summary>
    private const string Build = "5163550a4d45ef6b1797ca042b2dd7deb2d7a22a9c7b88f8cc0ddfee1d18fc09";

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
            // The installed DLL when it is the build read, else the archived copy
            // of that build staged beside the tier0 it imports.
            var dll = new[] { Path(), Staged() }.FirstOrDefault(d => d != null && IsBuild(d));
            if (dll == null)
                return null;
            if (NativeLibrary.TryLoad(dll, typeof(ResourceCompilerOracle).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32,
                    out var handle))
                _module = handle;
            return _module == 0 ? null : _module;
        }
    }

    /// <summary>The address of a function Ghidra calls FUN_&lt;va&gt;.</summary>
    public static nint At(nint module, ulong va) => module + (nint)(va - ImageBase);

    /// <summary>A tier0 export, from the tier0.dll resourcecompiler loaded.</summary>
    public static nint Tier0(string name)
        => NativeLibrary.GetExport(NativeLibrary.Load("tier0.dll"), name);

    private static bool IsBuild(string dll)
    {
        using var stream = File.OpenRead(dll);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == Build;
    }

    /// <summary>
    /// tools/re/binaries' resourcecompiler_20260924.dll and tier0_20260923.dll
    /// copied to a folder of their own under the names the DLL loads, with the
    /// third-party DLLs it imports (fbx, embree, steam_api, v8) taken from the
    /// game's bin folder; null when the archive or the game lacks them.
    /// </summary>
    private static string? Staged()
    {
        var archive = Environment.GetEnvironmentVariable("S2C_BINARIES") ?? System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "re", "binaries");
        var (rc, t0) = (System.IO.Path.Combine(archive, "resourcecompiler_20260924.dll"), System.IO.Path.Combine(archive, "tier0_20260923.dll"));
        if (!File.Exists(rc) || !File.Exists(t0) || CS2Fixtures.StockPak() is not { } pak)
            return null;
        var bin = System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pak)!, "..")), "bin", "win64");
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s2c_resourcecompiler_20260924");
        Directory.CreateDirectory(dir);
        void Stage(string from, string name)
        {
            var to = System.IO.Path.Combine(dir, name);
            if (!File.Exists(to) && File.Exists(from))
                File.Copy(from, to);
        }
        Stage(rc, "resourcecompiler.dll");
        Stage(t0, "tier0.dll");
        foreach (var name in new[] { "libfbxsdk_2020_3_1.dll", "embree3.dll", "steam_api64.dll", "v8.dll", "v8_libbase.dll", "v8_icui18n.dll", "v8_icuuc.dll", "v8_zlib.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            Stage(System.IO.Path.Combine(bin, name), name);
        return System.IO.Path.Combine(dir, "resourcecompiler.dll");
    }

    private static string? Path()
    {
        var pak = CS2Fixtures.StockPak();
        if (pak is null)
            return null;
        var dll = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pak)!, "..")),
            "bin", "win64", "resourcecompiler.dll");
        return File.Exists(dll) ? dll : null;
    }
}
