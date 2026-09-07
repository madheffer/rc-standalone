using SkiaSharp;

namespace Source2.Compiler;

/// <summary>
/// Packs the frame images of an <c>.mks</c> script into one atlas bitmap and
/// produces the matching <see cref="SpriteSheet"/> sequences.
///
/// <para><b>The packing is ours to choose.</b> A SHEET block stores an explicit
/// UV rect per frame, so nothing downstream cares how the atlas is laid out -
/// there is no grid to line up with and no need to imitate Valve's packer. This
/// uses a shelf packer (rows of equal-height frames, tallest first), which is
/// simple, deterministic, and tight enough for sprite frames, which are usually
/// all the same size.</para>
///
/// <para><b>Half-texel inset.</b> UVs address texel centres, not texel edges:
/// a frame at pixel column <c>x</c> spanning <c>w</c> pixels gets
/// <c>u = (x + 0.5) / atlasWidth</c> to <c>u = (x + w - 0.5) / atlasWidth</c>.
/// Measured on Valve's own <c>explosion_blast_01_flame</c>: its first frame
/// starts at <c>0.00012207031</c> on a 4096-wide atlas, which is exactly half a
/// texel. Without the inset, bilinear sampling at a frame edge bleeds in the
/// neighbouring frame.</para>
/// </summary>
public static class SheetAtlas
{
    /// <summary>The packed result.</summary>
    /// <param name="Atlas">The atlas bitmap, power-of-two on both axes. Caller disposes.</param>
    /// <param name="Sequences">Sequences with UVs resolved against <paramref name="Atlas"/>.</param>
    public sealed record Packed(SKBitmap Atlas, IReadOnlyList<SpriteSheet.Sequence> Sequences) : IDisposable
    {
        /// <summary>Disposes the atlas bitmap.</summary>
        public void Dispose() => Atlas.Dispose();
    }

    /// <summary>
    /// Pack <paramref name="script"/>, loading each frame image through
    /// <paramref name="loadImage"/> (given the path exactly as the script wrote
    /// it). Every distinct path is loaded once, so a frame reused across
    /// sequences is packed once and shares its rect, matching how Valve's own
    /// sheets reuse a frame.
    /// </summary>
    /// <param name="maxAtlasSize">Refuse to produce an atlas larger than this on either axis.</param>
    public static Packed Pack(MksSource.Script script, Func<string, byte[]> loadImage, int maxAtlasSize = 8192)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(loadImage);

        // Load each distinct image once.
        var images = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in script.ImagePaths)
            {
                var bytes = loadImage(path);
                if (bytes is not { Length: > 0 })
                    throw new InvalidOperationException($"Frame image '{path}' is empty or could not be read.");
                images[path] = Imaging.SafeImageDecode.Decode(bytes);
            }

            var placements = ShelfPack(images, maxAtlasSize, out var atlasW, out var atlasH);

            var atlas = new SKBitmap(new SKImageInfo(atlasW, atlasH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(atlas))
            {
                canvas.Clear(SKColors.Transparent);
                foreach (var (path, rect) in placements)
                    canvas.DrawBitmap(images[path], SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height));
            }

            var sequences = script.Sequences.Select(seq => new SpriteSheet.Sequence(
                Frames: seq.Frames.Select(f =>
                {
                    var r = placements[f.ImagePath];
                    return new SpriteSheet.Frame(
                        f.DisplayTime,
                        Min: ((r.Left + 0.5f) / atlasW, (r.Top + 0.5f) / atlasH),
                        Max: ((r.Right - 0.5f) / atlasW, (r.Bottom - 0.5f) / atlasH));
                }).ToList(),
                Clamp: seq.Clamp,
                NoColor: seq.NoColor,
                NoAlpha: seq.NoAlpha)).ToList();

            return new Packed(atlas, sequences);
        }
        finally
        {
            foreach (var b in images.Values) b.Dispose();
        }
    }

    /// <summary>
    /// Shelf packing: sort by descending height, lay frames left to right on a
    /// row, start a new row when the current one is full. Atlas width is the
    /// smallest power of two that fits the widest frame and keeps the result
    /// roughly square; height is whatever the rows need, rounded up to a power
    /// of two (the texture builder wants power-of-two dimensions anyway).
    /// </summary>
    private static Dictionary<string, SKRectI> ShelfPack(
        Dictionary<string, SKBitmap> images, int maxAtlasSize, out int atlasW, out int atlasH)
    {
        var ordered = images
            .OrderByDescending(kv => kv.Value.Height)
            .ThenByDescending(kv => kv.Value.Width)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)     // deterministic ties
            .ToList();

        var widest = ordered.Max(kv => kv.Value.Width);
        var totalArea = ordered.Sum(kv => (long)kv.Value.Width * kv.Value.Height);

        // Start from a width that fits the widest frame and gives a roughly
        // square atlas for the total area, then widen until everything fits.
        var width = Math.Max(NextPow2(widest), NextPow2((int)Math.Ceiling(Math.Sqrt(totalArea))));

        while (true)
        {
            if (width > maxAtlasSize)
                throw new InvalidOperationException(
                    $"The frames do not fit an atlas of {maxAtlasSize}x{maxAtlasSize}. " +
                    "Use fewer or smaller frames.");

            var placed = TryPack(ordered, width, out var usedHeight);
            if (placed is not null)
            {
                var height = NextPow2(usedHeight);
                if (height <= maxAtlasSize)
                {
                    atlasW = width;
                    atlasH = height;
                    return placed;
                }
            }
            width *= 2;
        }
    }

    private static Dictionary<string, SKRectI>? TryPack(
        List<KeyValuePair<string, SKBitmap>> ordered, int width, out int usedHeight)
    {
        var result = new Dictionary<string, SKRectI>(StringComparer.OrdinalIgnoreCase);
        int x = 0, y = 0, rowHeight = 0;
        usedHeight = 0;

        foreach (var (path, bmp) in ordered)
        {
            if (bmp.Width > width) return null;
            if (x + bmp.Width > width)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }
            result[path] = SKRectI.Create(x, y, bmp.Width, bmp.Height);
            x += bmp.Width;
            rowHeight = Math.Max(rowHeight, bmp.Height);
        }

        usedHeight = y + rowHeight;
        return result;
    }

    private static int NextPow2(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }
}
