using Source2.Compiler.Kv3;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler;

/// <summary>
/// Lowers the KV3 decompression ceiling for both readers a host may parse through: this package's
/// (<see cref="Kv3ReadLimit"/>) and VRF's (<see cref="BinaryKV3.UseMaxUncompressedBytes"/>).
/// </summary>
public static class VrfKv3ReadLimit
{
    /// <summary>Hold every KV3 read in this async flow to <paramref name="maxUncompressedBytes"/>
    /// until the result is disposed. Like each scope it opens, it only ever lowers the limit.</summary>
    public static IDisposable Use(long maxUncompressedBytes) =>
        new Both(Kv3ReadLimit.Use(maxUncompressedBytes), BinaryKV3.UseMaxUncompressedBytes(maxUncompressedBytes));

    private sealed class Both(IDisposable package, IDisposable vrf) : IDisposable
    {
        public void Dispose()
        {
            vrf.Dispose();
            package.Dispose();
        }
    }
}
