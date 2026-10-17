namespace Source2.Compiler.Kv3;

/// <summary>
/// The most a binary KV3 block may make a reader build: the bytes it declares it decompresses to
/// (buffers and blobs together) and the values it holds. Every KV3 reader in this package refuses
/// a block over either, before allocating from it. A host that parses untrusted files lowers them
/// around that work; one that also parses through VRF wants
/// <c>Source2.Compiler.VrfKv3ReadLimit.Use</c>, which scopes both readers.
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

    /// <summary>16M (16,777,216). The most values counted in a real block is 5,351,200 (de_boulder's world_physics PHYS), every node and typed-array element included; a value costs about 85 bytes of memory.</summary>
    public const long DefaultMaxValues = 16L << 20;

    private static readonly AsyncLocal<(long Bytes, long Values)?> s_current = new();

    /// <summary>The byte limit in force here: the innermost <see cref="Use(long, long)"/>, else the default.</summary>
    public static long MaxUncompressedBytes => s_current.Value?.Bytes ?? DefaultMaxUncompressedBytes;

    /// <summary>The value limit in force here: the innermost <see cref="Use(long, long)"/>, else the default.</summary>
    public static long MaxValues => s_current.Value?.Values ?? DefaultMaxValues;

    /// <summary>Hold KV3 reads in this async flow to <paramref name="maxUncompressedBytes"/> and
    /// <paramref name="maxValues"/> until the result is disposed. A scope can only lower a limit
    /// in force, never raise it.</summary>
    public static IDisposable Use(long maxUncompressedBytes, long maxValues)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxUncompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValues);
        var outer = s_current.Value;
        s_current.Value = (Math.Min(maxUncompressedBytes, MaxUncompressedBytes), Math.Min(maxValues, MaxValues));
        return new Scope(outer);
    }

    /// <summary><see cref="Use(long, long)"/> with the value limit in force.</summary>
    public static IDisposable Use(long maxUncompressedBytes) => Use(maxUncompressedBytes, MaxValues);

    private sealed class Scope((long Bytes, long Values)? outer) : IDisposable
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
