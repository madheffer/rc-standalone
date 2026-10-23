using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Source2.Compiler.Tests;

/// <summary>
/// physicsbuilder.dll as read (20260924), loaded into the test process so the
/// port can be checked against Valve's own functions, as <see cref="Vphysics2Oracle"/>
/// does for vphysics2. The installed build has moved on, so the archived copy
/// in tools/re/binaries is staged beside the tier0 it was read with; without
/// it <see cref="Load"/> returns null and the oracle tests return without
/// asserting. physicsbuilder imports only tier0, kernel32 and user32.
/// </summary>
internal static class PhysicsBuilderOracle
{
    /// <summary>SHA-256 of physicsbuilder.dll as shipped on 2026-09-24.</summary>
    private const string Build = "1c324870ffbf4ece889329238a340cecb2fcbedd44664d06c4eedce1729c391b";

    private const ulong ImageBase = 0x180000000;

    private static readonly object Gate = new();
    private static bool _tried;
    private static nint _module;

    /// <summary>The loaded module's base, or null when the archive lacks the build.</summary>
    public static nint? Load()
    {
        lock (Gate)
        {
            if (_tried)
                return _module == 0 ? null : _module;
            _tried = true;
            var dll = Staged();
            if (dll == null)
                return null;
            using (var stream = File.OpenRead(dll))
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) != Build)
                    return null;
            if (NativeLibrary.TryLoad(dll, typeof(PhysicsBuilderOracle).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32,
                    out var handle))
                _module = handle;
            return _module == 0 ? null : _module;
        }
    }

    /// <summary>The address of a function Ghidra calls FUN_&lt;va&gt;.</summary>
    public static nint At(nint module, ulong va) => module + (nint)(va - ImageBase);

    private static string? Staged()
    {
        var archive = Environment.GetEnvironmentVariable("S2C_BINARIES") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "re", "binaries");
        var (pb, t0) = (Path.Combine(archive, "physicsbuilder_20260924.dll"), Path.Combine(archive, "tier0_20260923.dll"));
        if (!File.Exists(pb) || !File.Exists(t0))
            return null;
        var dir = Path.Combine(Path.GetTempPath(), "s2c_physicsbuilder_20260924");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, "physicsbuilder.dll");
        if (!File.Exists(dll))
            File.Copy(pb, dll);
        if (!File.Exists(Path.Combine(dir, "tier0.dll")))
            File.Copy(t0, Path.Combine(dir, "tier0.dll"));
        return dll;
    }
}
