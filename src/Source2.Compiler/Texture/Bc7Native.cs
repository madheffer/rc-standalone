using System.Runtime.InteropServices;

namespace Source2.Compiler;

/// <summary>
/// P/Invoke into the vendored native BC7 encoder (<c>Native/bc7enc</c>) — the
/// fast replacement for <c>BCnEncoder.Net</c>'s BC7 path, which is the skin
/// pipeline's single biggest cost (~48 s on a 4096² texture vs. well under a
/// second here).
///
/// <para>The native library is optional. When <c>bc7enc_native</c> is not
/// present (e.g. a dev box with no C compiler), <see cref="Available"/> is
/// false and <see cref="ResourceBuilder.BuildTexture"/> transparently falls
/// back to BCnEncoder.Net. The Linux/production image always builds it (see
/// the Dockerfile); for Windows dev see <c>Native/bc7enc/CMakeLists.txt</c>.</para>
/// </summary>
internal static partial class Bc7Native
{
    private const string Lib = "bc7enc_native";

    [LibraryImport(Lib, EntryPoint = "bc7enc_image_init")]
    private static partial void NativeInit();

    [LibraryImport(Lib, EntryPoint = "bc7enc_encode_bgra")]
    private static partial void NativeEncodeBgra(
        nint src, int width, int height,
        int uberLevel, int maxPartitions, int perceptual, nint dst);

    /// <summary>True when the native encoder loaded and initialised.</summary>
    public static bool Available { get; }

    static Bc7Native()
    {
        try
        {
            NativeInit();   // forces the load + builds bc7enc's tables
            Available = true;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Available = false;
        }
    }

    // Fast bc7enc settings — uber level 1, 16-partition search. A measured A/B
    // (encode → decode → PSNR on real 2048²/4096² skin textures) put uber-1/16
    // within ~0.1–0.2 dB PSNR of the old max-quality uber-4/64 — visually
    // identical at ~52–54 dB — for ~25–30% less encode time.
    // Linear (non-perceptual) error: the pipeline's BC7 textures include
    // packed data maps (g_tMetalness = roughness/metalness/wear/pearlescent),
    // where YCbCr weighting would distort the non-colour channels.
    private const int UberLevel = 1;
    private const int MaxPartitions = 16;
    private const int Perceptual = 0;

    /// <summary>
    /// Encode a BGRA8888 image to a BC7 block stream. <paramref name="width"/>
    /// and <paramref name="height"/> need not be multiples of 4 — the encoder
    /// clamps the trailing edge to fill the last block row/column. The image is
    /// sliced into horizontal strips encoded in parallel (BC7 blocks are
    /// independent, so a 4-pixel-row boundary is a clean cut).
    /// </summary>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
    {
        // Guard the managed→native boundary: the native side reads
        // width*height*4 bytes from this buffer, so a short buffer would be an
        // out-of-bounds read rather than a catchable exception.
        if ((long)bgra.Length < (long)width * height * 4)
            throw new ArgumentException(
                $"BGRA buffer is {bgra.Length} B, too small for {width}x{height}.", nameof(bgra));

        int blockCols = (width + 3) / 4;
        int blockRows = (height + 3) / 4;
        int rowBytes = blockCols * 16;                  // BC7 = 16 B per 4×4 block
        var outBuf = new byte[blockRows * rowBytes];

        // Max(1, …) not Clamp(…, 1, blockRows): Clamp throws when blockRows is 0.
        int strips = Math.Max(1, Math.Min(Environment.ProcessorCount, blockRows));
        int blockRowsPerStrip = (blockRows + strips - 1) / strips;

        var srcHandle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        var dstHandle = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            nint srcBase = srcHandle.AddrOfPinnedObject();
            nint dstBase = dstHandle.AddrOfPinnedObject();
            Parallel.For(0, strips, i =>
            {
                int firstBlockRow = i * blockRowsPerStrip;
                if (firstBlockRow >= blockRows) return;
                int stripBlockRows = Math.Min(blockRowsPerStrip, blockRows - firstBlockRow);

                int srcRow0 = firstBlockRow * 4;
                int stripHeight = Math.Min(stripBlockRows * 4, height - srcRow0);
                nint src = srcBase + (nint)srcRow0 * width * 4;
                nint dst = dstBase + (nint)firstBlockRow * rowBytes;

                NativeEncodeBgra(src, width, stripHeight,
                    UberLevel, MaxPartitions, Perceptual, dst);
            });
        }
        finally
        {
            srcHandle.Free();
            dstHandle.Free();
        }
        return outBuf;
    }
}
