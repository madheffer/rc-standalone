using Source2.Compiler.Kv3;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler;

/// <summary>
/// Lowers the KV3 limits for both readers a host may parse through: this package's
/// (<see cref="Kv3ReadLimit"/>) and VRF's (<see cref="BinaryKV3.UseReadLimits"/>), which also
/// bounds VRF's particle snapshot blocks.
/// </summary>
public static class VrfKv3ReadLimit
{
    /// <summary>Hold every KV3 read in this async flow to <paramref name="maxUncompressedBytes"/>
    /// and <paramref name="maxValues"/> until the result is disposed. Like each scope it opens, it
    /// only ever lowers a limit.</summary>
    public static IDisposable Use(long maxUncompressedBytes, long maxValues) =>
        new Both(Kv3ReadLimit.Use(maxUncompressedBytes, maxValues), BinaryKV3.UseReadLimits(maxUncompressedBytes, maxValues));

    /// <summary><see cref="Use(long, long)"/> with the value limit in force.</summary>
    public static IDisposable Use(long maxUncompressedBytes) => Use(maxUncompressedBytes, Kv3ReadLimit.MaxValues);

    private sealed class Both(IDisposable package, IDisposable vrf) : IDisposable
    {
        public void Dispose()
        {
            vrf.Dispose();
            package.Dispose();
        }
    }
}
