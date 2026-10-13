namespace Source2.Compiler;

/// <summary>
/// The seam for a GPU BC7 encoder. The compiler ships none, since one needs a graphics API this
/// library does not depend on. A host that has one installs it with <see cref="Use"/>, and
/// <see cref="ResourceBuilder.BuildTexture(ResourceBuilder.TextureDef)"/> then routes BC7 to it
/// for every texture that asks for <see cref="ResourceBuilder.Bc7EncoderMode.Gpu"/>.
/// </summary>
public static class GpuBc7Encoder
{
    private sealed record Installed(Func<byte[], int, int, byte[]> Encode, string Tag);

    private static volatile Installed? s_installed;

    /// <summary>
    /// Install <paramref name="encoder"/>, or remove the installed one with null. It takes BGRA8888
    /// pixels and returns the BC7 block stream (the contract of <see cref="EncodeBgra"/>), and must
    /// be safe to call from several threads. <paramref name="cacheTag"/> names it in
    /// <see cref="ResourceBuilder.Bc7CacheTag"/>; change it whenever the encoder's output changes,
    /// so a host's cached textures from the old encoder are not served. 1 to 16 ASCII letters and digits.
    /// </summary>
    public static void Use(Func<byte[], int, int, byte[]>? encoder, string cacheTag = "bc7gpu")
    {
        ArgumentNullException.ThrowIfNull(cacheTag);
        if (encoder is not null && (cacheTag.Length is 0 or > 16 || !cacheTag.All(char.IsAsciiLetterOrDigit)))
            throw new ArgumentException("A cache tag is 1 to 16 ASCII letters and digits.", nameof(cacheTag));
        s_installed = encoder is null ? null : new Installed(encoder, cacheTag);
    }

    /// <summary>Whether a GPU encoder is installed.</summary>
    public static bool Available => s_installed is not null;

    /// <summary>The installed encoder's cache tag, or null when none is installed.</summary>
    public static string? CacheTag => s_installed?.Tag;

    /// <summary>
    /// Encode a BGRA8888 image to a BC7 block stream with the installed encoder. Same contract as
    /// <see cref="Bc7Native.EncodeBgra"/>: 16 bytes per 4x4 block, and the dimensions need not be
    /// multiples of 4 (the trailing edge is clamped to fill the last block row and column).
    /// </summary>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
        => (s_installed ?? throw new InvalidOperationException("No GPU BC7 encoder is installed; see GpuBc7Encoder.Use."))
            .Encode(bgra, width, height);
}
