using Source2.Compiler.Lighting;
using Source2.Compiler.RcTexture;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// What the game does with a cubemap's six rendered faces before resourcecompiler sees them (client.dll
/// 10-15, 18020e130 and 1802091d0; docs/CUBEMAPS.md): each face's RGBA16F readback (render size square,
/// as read back) becomes a float bitmap (1813db210), is halved with FloatBitMap's QuarterSize
/// (1813ddd00, the same code as resourcecompiler's 180c43450, so <see cref="RcMipChain.QuarterSize"/>)
/// down to the face size, flipped vertically (1813dc290: rows y and h - 1 - y swapped), and copied into
/// a strip of six faces left to right (181756620 at x = face * size; the strip starts as (0, 0, 0, 1)).
/// The strip is written as an uncompressed half EXR through tinyexr's fast writer (1813dede0 with
/// compression 0 and half pixels: the code vrad3's FloatBitMap_t::WriteToFile runs, so
/// <see cref="Vrad3Exr"/> and its ties-away rounding), channels R, G and B (the readback frame has no alpha: the
/// game's EXRs list B, G, R, 2026-10-21 capture).
/// </summary>
public static class CubemapFaceExr
{
    /// <summary>The EXR for one cubemap from its six faces' readbacks (each renderSize^2 RGBA halves, row 0 first).</summary>
    public static byte[] Build(IReadOnlyList<Half[]> faces, int renderSize, int faceSize)
    {
        if (faces.Count != 6)
            throw new ArgumentException("six faces");
        var strip = new float[6 * faceSize * faceSize * 4];
        for (var i = 3; i < strip.Length; i += 4)
            strip[i] = 1f;
        for (var f = 0; f < 6; f++)
        {
            var rgba = new float[renderSize * renderSize * 4];
            for (var i = 0; i < rgba.Length; i++)
                rgba[i] = (float)faces[f][i];
            var level = new RcMipChain.Level(renderSize, renderSize, rgba);
            while (level.Width > faceSize)
                level = RcMipChain.QuarterSize(level);
            if (level.Width != faceSize)
                throw new NotSupportedException($"render size {renderSize} does not halve to face size {faceSize}");
            for (var y = 0; y < faceSize; y++)
            {
                // Flipped: the strip's row y is the face's row size - 1 - y.
                var from = (faceSize - 1 - y) * faceSize * 4;
                Array.Copy(level.Rgba, from, strip, (y * 6 * faceSize + f * faceSize) * 4, faceSize * 4);
            }
        }
        var exr = new Vrad3Exr(6 * faceSize, faceSize, ["R", "G", "B"]);
        for (var c = 0; c < 3; c++)
        {
            var plane = exr.Plane("RGB"[c].ToString());
            for (var i = 0; i < plane.Length; i++)
                plane[i] = LightmapImage.ToHalfAway(strip[i * 4 + c]);
        }
        return exr.Write();
    }
}
