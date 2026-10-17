namespace Source2.Compiler.Kv3;

/// <summary>
/// The limits every KV3 reader holds untrusted input to (binary blocks of every version and KV3
/// text), so a hostile file fails with <see cref="InvalidDataException"/> instead of exhausting
/// memory or the stack. Real CS2 content stays far inside all of them.
/// </summary>
internal static class Kv3Limits
{
    /// <summary>The deepest nesting of objects and arrays accepted. A reader recurses once per level,
    /// and a stack overflow ends the process where no catch can stop it.</summary>
    public const int MaxDepth = 512;

    /// <summary>
    /// Check sizes read from a block header before anything is allocated from them: none negative,
    /// what is stored no more than <paramref name="remaining"/> bytes, and what it decompresses to
    /// no more than <see cref="Kv3ReadLimit.MaxUncompressedBytes"/>.
    /// </summary>
    /// <param name="declared">Bytes the sections hold once decompressed.</param>
    /// <param name="stored">Bytes the sections take in the block.</param>
    /// <param name="remaining">Bytes of the block left to hold them.</param>
    /// <param name="sizes">Every size field read, each of which must be non-negative.</param>
    public static void CheckSizes(long declared, long stored, long remaining, params ReadOnlySpan<long> sizes)
    {
        foreach (var size in sizes)
            if (size < 0)
                throw Hostile($"a negative size ({size})");
        if (stored > remaining)
            throw Hostile($"{declared} bytes ({stored} stored) in a block that holds {remaining}");
        var limit = Kv3ReadLimit.MaxUncompressedBytes;
        if (declared > limit)
            throw Hostile($"{declared} bytes, over the {limit}-byte limit");
    }

    /// <summary>The most values a block of <paramref name="declared"/> decompressed bytes may hold.
    /// A typed array of a zero-width type (NULL, BOOLEAN_TRUE, INT64_ZERO) states any length in four
    /// bytes, so the count is bounded by the block's size rather than by its data.</summary>
    public static long NodeBudget(long declared) => (1 << 20) + 16L * declared;

    /// <summary>A collection capacity for a count read from a file: only a hint, never an allocation
    /// the file controls.</summary>
    public static int Capacity(long declared) => (int)Math.Clamp(declared, 0, 4096);

    /// <summary>Counts nesting and values while a reader walks a tree.</summary>
    public sealed class Budget(long nodes)
    {
        private long _nodes = nodes;
        private int _depth;

        /// <summary>Enter a value: one more node, one level deeper. Pair with <see cref="Leave"/>.</summary>
        public void Enter()
        {
            if (--_nodes < 0)
                throw Hostile("more values than its size allows");
            if (++_depth > MaxDepth)
            {
                _depth--;
                throw Hostile($"nesting deeper than {MaxDepth} levels");
            }
        }

        /// <summary>Leave the value <see cref="Enter"/> entered.</summary>
        public void Leave() => _depth--;
    }

    /// <summary>The exception for a block a reader refuses.</summary>
    public static InvalidDataException Hostile(string what) =>
        new($"KV3 block declares {what}; refusing to parse (malformed or hostile resource).");
}
