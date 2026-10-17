namespace Source2.Compiler.Kv3;

/// <summary>
/// The most bytes a binary KV3 block may declare it decompresses to (its buffers and blobs
/// together). Every KV3 reader in this package refuses a block over it, from the header, before
/// anything is allocated. A host that parses untrusted files lowers it around that work; one that
/// also parses through VRF wants <c>Source2.Compiler.VrfKv3ReadLimit.Use</c>, which scopes both.
/// <para>The scope is an <see cref="AsyncLocal{T}"/>: it flows into awaits and tasks started
/// inside it, but not back out of an async method that opened it, and not into work queued with
/// <c>UnsafeQueueUserWorkItem</c> or under <see cref="ExecutionContext.SuppressFlow"/>. Open it
/// in the method that does the parsing.</para>
/// </summary>
public static class Kv3ReadLimit
{
    /// <summary>512 MB. The largest real block measured is 303,244,222 bytes (de_boulder's
    /// world_physics.vmdl_c PHYS), across 435,536 KV3 blocks in pak01, every stock map and
    /// community packs; p99.99 is 2.7 MB.</summary>
    public const long DefaultMaxUncompressedBytes = 512L << 20;

    private static readonly AsyncLocal<long?> s_current = new();

    /// <summary>The limit in force here: the innermost <see cref="Use"/>, else the default.</summary>
    public static long MaxUncompressedBytes => s_current.Value ?? DefaultMaxUncompressedBytes;

    /// <summary>Hold KV3 reads in this async flow to <paramref name="maxUncompressedBytes"/> until
    /// the result is disposed. A scope can only lower the limit in force, never raise it.</summary>
    public static IDisposable Use(long maxUncompressedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxUncompressedBytes);
        var outer = s_current.Value;
        s_current.Value = Math.Min(maxUncompressedBytes, outer ?? DefaultMaxUncompressedBytes);
        return new Scope(outer);
    }

    private sealed class Scope(long? outer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            s_current.Value = outer;
        }
    }
}
