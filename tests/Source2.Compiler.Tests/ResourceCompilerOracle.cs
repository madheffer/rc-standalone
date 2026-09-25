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
            if (Path() is not { } dll)
                return null;
            using (var stream = File.OpenRead(dll))
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) != Build)
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
