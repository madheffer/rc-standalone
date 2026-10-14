namespace Source2.Compiler;

/// <summary>
/// The check for a mip chain a caller hands in (<see cref="ResourceBuilder.TextureDef.MipChainBgra"/>,
/// <see cref="RcTexture.VtexCompileSettings.MipChainBgra"/>): finished 8-bit BGRA levels, level 0
/// first, each half the size of the one before it (a side never below 1). The levels are used as
/// given: no resampling, no colour-space conversion, no premultiplication.
/// </summary>
internal static class MipChainInput
{
    /// <summary>The size of level <paramref name="level"/> of a chain whose top is
    /// <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static (int Width, int Height) LevelSize(int width, int height, int level) =>
        (Math.Max(1, width >> level), Math.Max(1, height >> level));

    /// <summary>Throws unless <paramref name="chain"/> is a chain whose top is <paramref name="width"/>
    /// by <paramref name="height"/>.</summary>
    /// <exception cref="ArgumentException">The chain is empty, or a level has the wrong byte count.</exception>
    public static void Check(IReadOnlyList<byte[]> chain, int width, int height, string paramName)
    {
        if (chain.Count == 0)
            throw new ArgumentException("A mip chain needs at least level 0.", paramName);
        for (var i = 0; i < chain.Count; i++)
        {
            var (w, h) = LevelSize(width, height, i);
            if (chain[i] is null || chain[i].LongLength != (long)w * h * 4)
                throw new ArgumentException(
                    $"Mip level {i} must be {w}x{h} BGRA ({(long)w * h * 4} bytes), not {chain[i]?.LongLength ?? 0} bytes.", paramName);
            if (w == 1 && h == 1 && i < chain.Count - 1)
                throw new ArgumentException($"The mip chain has {chain.Count} levels; a {width}x{height} chain ends at level {i}.", paramName);
        }
    }
}
