namespace Source2.Compiler.Compression;

/// <summary>
/// LZ4 blocks as KV3 stores them: <see cref="Compress"/> is <c>LZ4_compress_HC</c> at level 12 (see
/// <see cref="Lz4HcStream"/>), and the decoders check every length and offset against the input and
/// the output before using it, so a hostile block is an <see cref="InvalidDataException"/>.
/// </summary>
public static class Lz4
{
    /// <summary>One block of <paramref name="input"/>, LZ4 HC level 12, with a fresh state.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var output = new byte[Lz4HcStream.CompressBound(input.Length)];
        var n = Lz4HcStream.Compress(input, output);
        if (n <= 0)
            throw new InvalidOperationException("LZ4 HC compression failed");
        return output.AsSpan(0, n).ToArray();
    }

    /// <summary>Decode one block into <paramref name="output"/>; returns the bytes written.</summary>
    /// <exception cref="InvalidDataException">The block is malformed or does not fit.</exception>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output) => Decode(input, output, 0, output.Length);

    /// <summary>
    /// Decode one block into <paramref name="buffer"/> from <paramref name="at"/>, writing at most up to
    /// <paramref name="limit"/>; a match may reach back into <paramref name="buffer"/> before
    /// <paramref name="at"/> (the earlier blocks of a stream). Returns the bytes written.
    /// </summary>
    internal static int Decode(ReadOnlySpan<byte> input, Span<byte> buffer, int at, int limit)
    {
        int ip = 0, op = at;
        if (input.IsEmpty && limit == at)
            return 0;
        while (true)
        {
            if (ip >= input.Length) throw Bad("truncated sequence");
            int token = input[ip++];

            var literals = token >> 4;
            if (literals == 15)
            {
                int b;
                do
                {
                    if (ip >= input.Length) throw Bad("truncated literal length");
                    b = input[ip++];
                    literals += b;
                    if (literals > limit) throw Bad("literal length");
                } while (b == 255);
            }
            if (literals > input.Length - ip || literals > limit - op) throw Bad("literals past the end");
            input.Slice(ip, literals).CopyTo(buffer[op..]);
            ip += literals;
            op += literals;
            if (ip == input.Length)
                return op - at;   // the last sequence is literals only

            if (input.Length - ip < 2) throw Bad("truncated offset");
            var offset = input[ip] | input[ip + 1] << 8;
            ip += 2;
            if (offset == 0 || offset > op) throw Bad("offset before the start");

            var matchLength = token & 15;
            if (matchLength == 15)
            {
                int b;
                do
                {
                    if (ip >= input.Length) throw Bad("truncated match length");
                    b = input[ip++];
                    matchLength += b;
                    if (matchLength > limit) throw Bad("match length");
                } while (b == 255);
            }
            matchLength += 4;
            if (matchLength > limit - op) throw Bad("match past the end");
            var from = op - offset;
            if (offset >= matchLength)
            {
                buffer.Slice(from, matchLength).CopyTo(buffer[op..]);
                op += matchLength;
            }
            else
            {
                for (var i = 0; i < matchLength; i++)
                    buffer[op++] = buffer[from++];
            }
        }
    }

    private static InvalidDataException Bad(string what) => new($"LZ4: {what}");
}

/// <summary>
/// Decodes the frames of one LZ4 stream in order, each able to refer to the 64 KB decoded before it, as
/// KV3's blob frames are written (the counterpart of <c>LZ4_decompress_safe_continue</c>).
/// </summary>
public sealed class Lz4ChainDecoder(int maxFrameSize)
{
    private const int History = 0x10000;
    private readonly byte[] _buffer = new byte[History + 2 * Math.Max(maxFrameSize, 1)];
    private int _at;

    /// <summary>Decode one frame into <paramref name="output"/>; returns the bytes it decoded to.</summary>
    /// <exception cref="InvalidDataException">The frame is malformed, or larger than the frame size or the output.</exception>
    public int Decode(ReadOnlySpan<byte> frame, Span<byte> output)
    {
        if (_at + maxFrameSize > _buffer.Length)
        {
            // keep the last 64 KB as the dictionary of what follows
            _buffer.AsSpan(_at - History, History).CopyTo(_buffer);
            _at = History;
        }
        var n = Lz4.Decode(frame, _buffer, _at, _at + maxFrameSize);
        if (n > output.Length)
            throw new InvalidDataException("LZ4: frame larger than its destination");
        _buffer.AsSpan(_at, n).CopyTo(output);
        _at += n;
        return n;
    }
}
