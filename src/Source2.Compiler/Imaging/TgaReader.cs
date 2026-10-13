namespace Source2.Compiler.Imaging;

/// <summary>
/// Truevision TGA to 8-bit RGBA: uncompressed and run-length (types 2, 3, 10, 11), 8, 24 and 32
/// bits per pixel, either vertical origin. Valve's default material images are TGA, and the image
/// decoder the rest of the compiler uses does not read it. Colour-mapped files are refused.
/// </summary>
public static class TgaReader
{
    /// <summary>Whether the bytes look like a TGA this reader takes (no magic number: the header is checked).</summary>
    public static bool CanRead(ReadOnlySpan<byte> tga) =>
        tga.Length >= 18 && tga[1] == 0 && tga[2] is 2 or 3 or 10 or 11 && tga[16] is 8 or 24 or 32
        && BitConverter.ToUInt16(tga[12..14]) > 0 && BitConverter.ToUInt16(tga[14..16]) > 0;

    /// <summary>Decode to RGBA, top row first. 24-bit and grey images get alpha 255.</summary>
    public static (byte[] Rgba, int Width, int Height) Read(ReadOnlySpan<byte> tga)
    {
        if (!CanRead(tga))
            throw new InvalidDataException("Not a TGA this reader takes (true-colour or grey, 8/24/32 bits, no colour map).");
        int idLength = tga[0], type = tga[2], bpp = tga[16], descriptor = tga[17];
        int width = BitConverter.ToUInt16(tga[12..14]), height = BitConverter.ToUInt16(tga[14..16]);
        if ((long)width * height > SafeImageDecode.DefaultMaxPixels)
            throw new InvalidDataException($"TGA is {width}x{height}, over the decode limit.");
        int bytesPer = bpp / 8;
        bool grey = type is 3 or 11;
        if (grey != (bpp == 8))
            throw new InvalidDataException($"TGA type {type} with {bpp} bits per pixel is not read here.");

        int pos = 18 + idLength;
        int count = width * height;
        var pixels = new byte[count * bytesPer];
        if (type is 2 or 3)
        {
            if (pos + pixels.Length > tga.Length)
                throw new InvalidDataException("TGA pixel data is truncated.");
            tga.Slice(pos, pixels.Length).CopyTo(pixels);
        }
        else
        {
            int written = 0;
            while (written < count)
            {
                if (pos >= tga.Length)
                    throw new InvalidDataException("TGA run-length data is truncated.");
                int header = tga[pos++];
                int run = (header & 0x7F) + 1;
                if (written + run > count)
                    throw new InvalidDataException("TGA run-length data overruns the image.");
                if ((header & 0x80) != 0)
                {
                    if (pos + bytesPer > tga.Length)
                        throw new InvalidDataException("TGA run-length data is truncated.");
                    for (int i = 0; i < run; i++)
                        tga.Slice(pos, bytesPer).CopyTo(pixels.AsSpan((written + i) * bytesPer));
                    pos += bytesPer;
                }
                else
                {
                    if (pos + run * bytesPer > tga.Length)
                        throw new InvalidDataException("TGA run-length data is truncated.");
                    tga.Slice(pos, run * bytesPer).CopyTo(pixels.AsSpan(written * bytesPer));
                    pos += run * bytesPer;
                }
                written += run;
            }
        }

        bool topDown = (descriptor & 0x20) != 0, rightToLeft = (descriptor & 0x10) != 0;
        var rgba = new byte[count * 4];
        for (int y = 0; y < height; y++)
        {
            int sy = topDown ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int sx = rightToLeft ? width - 1 - x : x;
                int s = (sy * width + sx) * bytesPer, d = (y * width + x) * 4;
                if (grey)
                {
                    rgba[d] = rgba[d + 1] = rgba[d + 2] = pixels[s];
                    rgba[d + 3] = 255;
                }
                else
                {
                    rgba[d] = pixels[s + 2];
                    rgba[d + 1] = pixels[s + 1];
                    rgba[d + 2] = pixels[s];
                    rgba[d + 3] = bytesPer == 4 ? pixels[s + 3] : (byte)255;
                }
            }
        }
        return (rgba, width, height);
    }
}
