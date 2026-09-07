namespace Source2.Compiler.Imaging;

/// <summary>
/// F19: minimal "are these bytes actually the image type the extension claims"
/// check for user-uploaded thumbnails. The quarantine scan only rejects
/// executable/script magic; it does NOT catch a content-vs-extension mismatch
/// (e.g. a .gif whose body is HTML or SVG), which would then be stored and
/// re-served under an image content type. This rejects that mismatch at upload.
/// </summary>
public static class ImageMagic
{
    /// <summary>True when <paramref name="head"/> begins with the real magic for the
    /// claimed image <paramref name="ext"/> (leading-dot, any case). Unknown ext → false.</summary>
    public static bool Matches(string? ext, ReadOnlySpan<byte> head)
    {
        return (ext ?? "").ToLowerInvariant() switch
        {
            ".png"            => StartsWith(head, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => StartsWith(head, new byte[] { 0xFF, 0xD8, 0xFF }),
            ".gif"            => StartsWith(head, "GIF8"u8),
            ".bmp"            => StartsWith(head, "BM"u8),
            // RIFF....WEBP
            ".webp"           => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8),
            _                 => false,
        };
    }

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> sig)
        => head.Length >= sig.Length && head[..sig.Length].SequenceEqual(sig);
}
