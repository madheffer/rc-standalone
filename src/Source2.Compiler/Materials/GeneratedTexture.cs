using System.Text;

namespace Source2.Compiler.Materials;

/// <summary>One input image of a generated texture.</summary>
/// <param name="FileName">Content path of the image.</param>
/// <param name="Srgb">Its colour space: sRGB, else linear.</param>
/// <param name="FileExt">The shader's file ending for the input (<c>color</c>, <c>rough</c>).</param>
/// <param name="MinBitsPerChannel">The shader's minimum precision.</param>
/// <param name="ImageProcessor">The input processing command (<c>NormalizeNormals</c>), or empty.</param>
public sealed record GeneratedTextureInput(string FileName, bool Srgb, string FileExt, int MinBitsPerChannel, string ImageProcessor);

/// <summary>One output channel group of a generated texture.</summary>
/// <param name="SrcChannels">Source channels, as letters (<c>rgb</c>).</param>
/// <param name="DstChannels">Destination channels, as letters (<c>a</c>).</param>
/// <param name="MipAlgorithm">The mip processing command (<c>ArtistDefined</c>).</param>
/// <param name="Srgb">Output colour space: sRGB, else linear.</param>
/// <param name="Inputs">Indices into <see cref="GeneratedTexture.Inputs"/>.</param>
public sealed record GeneratedTextureChannel(string SrcChannels, string DstChannels, string MipAlgorithm, bool Srgb, int[] Inputs);

/// <summary>
/// The <c>.vtex</c> resourcecompiler generates for a material texture (docs/MATERIALS.md): its
/// inputs, format and channels, the logical-CRC text RC hashes, and the child path that hash
/// names.
/// </summary>
/// <param name="Inputs">Input images, one per channel input, repeats kept.</param>
/// <param name="OutputFormat">Image format name (<c>DXT1</c>, <c>BC7</c>).</param>
/// <param name="Channels">Output channel groups.</param>
public sealed record GeneratedTexture(IReadOnlyList<GeneratedTextureInput> Inputs, string OutputFormat, IReadOnlyList<GeneratedTextureChannel> Channels)
{
    /// <summary>The seed of the logical CRC's MurmurHash2 (181e91070).</summary>
    public const uint LogicalCrcSeed = 0x24871466;

    /// <summary>
    /// The text RC prints for its logical CRC (181e8fcf0): every string lowercased, a string
    /// written only when not empty and an integer only when not its default. The
    /// <c>m_textureOutputChannelArray[%d]:</c> line counts inputs, as RC's does.
    /// </summary>
    public string LogicalText()
    {
        var b = new StringBuilder();
        b.Append("VTEX_LOGICAL_CRC_VERSION = 1\n");
        b.Append($"m_inputTextureArray[{Inputs.Count}]:\n");
        for (int i = 0; i < Inputs.Count; i++)
        {
            var input = Inputs[i];
            b.Append($"m_inputTextureArray[{i}] =\n{{\n");
            Int(b, "m_texture_index", i, -1);
            Str(b, "m_fileName", input.FileName);
            Str(b, "m_colorSpace", input.Srgb ? "srgb" : "linear");
            Str(b, "m_fileExt", input.FileExt);
            Int(b, "m_nMinBitsPerChannel", input.MinBitsPerChannel, -1);
            Str(b, "m_typeString", "2D");
            // One image processor per input, empty when the shader names none.
            b.Append("m_imageProcessorArray[1]:\nm_imageProcessorArray[0] =\n{\n");
            Str(b, "m_algorithm", input.ImageProcessor);
            b.Append("}\n}\n");
        }
        Str(b, "m_outputTypeString", "2D");
        Str(b, "m_outputFormat", OutputFormat);
        b.Append($"m_textureOutputChannelArray[{Inputs.Count}]:\n");
        for (int c = 0; c < Channels.Count; c++)
        {
            var ch = Channels[c];
            b.Append($"m_textureOutputChannelArray[{c}] =\n{{\n");
            Str(b, "m_srcChannels", ch.SrcChannels);
            Str(b, "m_dstChannels", ch.DstChannels);
            b.Append("m_mipAlgorithm =\n{\n");
            Str(b, "m_algorithm", ch.MipAlgorithm);
            b.Append("}\n");
            Str(b, "m_outputColorSpace", ch.Srgb ? "srgb" : "linear");
            b.Append($"m_inputTextureArray[{ch.Inputs.Length}]:\n");
            for (int i = 0; i < ch.Inputs.Length; i++)
                b.Append($"m_inputTextureArray[{i}] = {ch.Inputs[i]}\n");
            b.Append("}\n");
        }
        return b.ToString();
    }

    /// <summary>MurmurHash2 of <see cref="LogicalText"/>: the child's name and its
    /// <c>___OverrideInputData___</c> fingerprint.</summary>
    public uint LogicalCrc => MurmurHash2(Encoding.UTF8.GetBytes(LogicalText()), LogicalCrcSeed);

    /// <summary>The child resource path: the first input with its <c>.</c> as <c>_</c>, the CRC
    /// in lowercase hex, then <c>.vtex</c> (<c>'%s_%x.%s'</c>).</summary>
    public string ChildPath
    {
        get
        {
            var first = Inputs[0].FileName;
            int dot = first.LastIndexOf('.');
            var stem = dot > first.LastIndexOf('/') ? first[..dot] + "_" + first[(dot + 1)..] : first;
            return $"{stem}_{LogicalCrc:x}.vtex";
        }
    }

    private static void Str(StringBuilder b, string name, string value)
    {
        if (value.Length > 0)
            b.Append(name).Append(" = \"").Append(AsciiLower(value)).Append("\"\n");
    }

    // CUtlString::ToLowerFast: A-Z only.
    private static string AsciiLower(string s) => string.Create(s.Length, s, (dst, src) =>
    {
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
    });

    private static void Int(StringBuilder b, string name, int value, int defaultValue)
    {
        if (value != defaultValue)
            b.Append(name).Append(" = ").Append(value).Append('\n');
    }

    /// <summary>MurmurHash2 (32-bit), as tier0 and RC compute it.</summary>
    public static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed)
    {
        const uint m = 0x5bd1e995;
        uint h = seed ^ (uint)data.Length;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            uint k = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            k *= m;
            k ^= k >> 24;
            k *= m;
            h *= m;
            h ^= k;
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }
        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }
}
