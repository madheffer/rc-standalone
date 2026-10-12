using System.Buffers.Binary;

namespace Source2.Compiler;

/// <summary>
/// The DATA block of a pre-CTRL <c>.vsnd_c</c> (resource versions 1 to 4): what kind of audio it holds
/// and where the streamed samples are. Version 5 keeps all of this in a CTRL tree instead.
///
/// <para>Layout, as VRF reads it (MIT, <c>Resource/ResourceTypes/Sound.cs</c>):
/// version 4 starts <c>u16 rate; u8 format (0 PCM16, 1 PCM8, 2 MP3, 3 ADPCM); u8 channels</c>, older
/// versions pack type / bits / channels / wave format / rate into one u32; then
/// <c>i32 loopStart; u32 sampleCount; f32 duration; u32 sentence; u32 headerOffset; i32 headerSize;
/// u32 streamingDataSize</c>, 8 more bytes from version 1, 4 from version 2, and <c>i32 loopEnd</c> from
/// version 4.</para>
/// </summary>
public readonly record struct LegacySoundHeader(
    bool IsPcm, int Bits, uint Channels, uint SampleRate, int LoopStart, uint SampleCount,
    float Duration, uint StreamingDataSize, int LoopEnd)
{
    /// <summary>Parse <paramref name="data"/>, the DATA block of a resource of <paramref name="resourceVersion"/>.</summary>
    /// <exception cref="InvalidDataException">The block is too short or names an unknown format.</exception>
    public static LegacySoundHeader Read(ReadOnlySpan<byte> data, ushort resourceVersion)
    {
        var need = 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + (resourceVersion >= 1 ? 8 : 0) + (resourceVersion >= 2 ? 4 : 0) + (resourceVersion >= 4 ? 4 : 0);
        if (resourceVersion > 4 || data.Length < need)
            throw new InvalidDataException($"Not a version {resourceVersion} sound header ({data.Length} bytes).");

        bool pcm;
        int bits;
        uint channels, rate;
        if (resourceVersion >= 4)
        {
            rate = BinaryPrimitives.ReadUInt16LittleEndian(data);
            var format = data[2];
            channels = data[3];
            (pcm, bits) = format switch
            {
                0 => (true, 16),
                1 => (true, 8),
                2 or 3 => (false, 16),
                _ => throw new InvalidDataException($"Unknown sound format {format}."),
            };
        }
        else
        {
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint Sub(int offset, int width) => (packed >> offset) & ((1u << width) - 1);
            var type = Sub(0, 2);     // 0 AAC (played as PCM16), 1 WAV, 2 MP3
            var packedBits = Sub(2, 5);
            var waveFormat = Sub(12, 2); // 1 PCM, 2 ADPCM
            pcm = type == 0 || (type == 1 && waveFormat != 2);
            bits = type == 1 && packedBits == 8 ? 8 : 16;
            channels = Sub(7, 2);
            rate = Sub(14, 16);
        }

        var loopStart = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var duration = BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
        var streaming = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        var loopEnd = resourceVersion >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data[(need - 4)..]) : 0;
        return new LegacySoundHeader(pcm, bits, channels, rate, loopStart, sampleCount, duration, streaming, loopEnd);
    }
}
