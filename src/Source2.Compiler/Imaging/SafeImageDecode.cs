using SkiaSharp;

namespace Source2.Compiler.Imaging;

/// <summary>
/// Decode an uploaded/encoded image with a hard pixel-count ceiling read from
/// the image HEADER before any pixel buffer is allocated.
///
/// <para><see cref="SKBitmap.Decode(byte[])"/> allocates Width*Height*4 bytes up
/// front, so a tiny compressed file that merely *declares* huge dimensions
/// (a 1 KB PNG claiming 30000x30000) forces a ~3.6 GB allocation and OOM-kills
/// the process, which no try/catch can stop. Reading the dimensions through
/// <see cref="SKCodec"/> first turns that into a catchable rejection, so every
/// decode of untrusted image bytes goes through here.</para>
/// </summary>
public static class SafeImageDecode
{
    /// <summary>Default ceiling: 24&#160;megapixels. Comfortably above any legitimate
    /// skin / hitmarker / agent texture (the VPK-texture path already caps at
    /// 4096*4096 = 16.7&#160;MP) yet far below an OOM-inducing decode. Pass a tighter
    /// value where the use-case warrants it.</summary>
    public const long DefaultMaxPixels = 24L * 1024 * 1024;

    /// <summary>Decode <paramref name="bytes"/> to an <see cref="SKBitmap"/> (owned
    /// by the caller) after verifying the declared dimensions are sane and within
    /// <paramref name="maxPixels"/>. Throws <see cref="InvalidOperationException"/>
    /// (catchable) on an empty, corrupt, or oversized image.</summary>
    public static SKBitmap Decode(byte[] bytes, long maxPixels = DefaultMaxPixels)
    {
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException("Empty image.");

        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data))
        {
            if (codec is null)
                throw new InvalidOperationException("Unrecognized or corrupt image.");
            var info = codec.Info;
            var pixels = (long)info.Width * info.Height;
            if (info.Width <= 0 || info.Height <= 0 || pixels > maxPixels)
                throw new InvalidOperationException(
                    $"Image is {info.Width}x{info.Height}px, which exceeds the {maxPixels / (1024 * 1024)}MP decode limit.");
        }

        return SKBitmap.Decode(bytes)
            ?? throw new InvalidOperationException("SkiaSharp could not decode the image.");
    }
}
