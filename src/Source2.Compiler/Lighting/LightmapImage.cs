using System.Buffers.Binary;
using System.Text;

namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's CVrad3LightmapImage (global at DAT_180399e70): the w x h lightmap
/// the bake fills and the post-process works on (docs/VRAD3.md).
/// <list type="bullet">
/// <item>Up to 20 channels (Vrad3LightmapChannel_t), each a float bitmap of
/// four components with a mask of the ones it has; a channel is live when it
/// exists (its width is above zero).</item>
/// <item>Per luxel, a 12-byte record: the chart id (0xFFFFFFFF none), the
/// sample count in the low 24 bits of +4, flags at +8 (bit 0 valid, bit 1 the
/// chart's flags bit 29, bit 2 its bit 28) and padding, kept as written.</item>
/// <item>Per luxel, a 20-byte baked shadow record: four light indices (0xFF
/// none) and four floats.</item>
/// </list>
/// <see cref="Save"/> and <see cref="Load"/> are lightmap_image_save and _load
/// (180046d70 / 180047170): a folder holding lightmap_data.dat ("VRAD3LMI",
/// version 1, w, h, per channel its presence and mask, then the shadow and
/// luxel records) and channelNN.exr per live channel (RGB, plus A when the
/// mask has it, half floats; components outside the mask are 0).
/// </summary>
public sealed class LightmapImage
{
    public const int ChannelCount = 20;

    /// <summary>The channels the block compute fills (18002ff00 readback of Luxel_t).</summary>
    public static class Ch
    {
        /// <summary>Irradiance RGB (Luxel_t +0x00).</summary>
        public const int Irradiance = 0;
        /// <summary>SH red, green, blue (+0x10, +0x20, +0x30).</summary>
        public const int ShR = 1, ShG = 2, ShB = 3;
        /// <summary>Quadratic red, green, blue (+0x40..+0x60) and their final rgb (+0x70).</summary>
        public const int QuadraticR = 4, QuadraticG = 5, QuadraticB = 6, QuadraticFinal = 7;
        /// <summary>Ambient occlusion (+0xd0).</summary>
        public const int AmbientOcclusion = 8;
        /// <summary>The normalised normal (+0xc0) and sqrt((1 - |n|) / |n|) of its length.</summary>
        public const int Normal = 9, NormalSpread = 10;
        /// <summary>The tangent as accumulated (+0xb0) and the same spread of its length.</summary>
        public const int Tangent = 11, TangentSpread = 12;
    }

    public sealed class Channel(int pixels, uint mask)
    {
        /// <summary>Which of R, G, B, A the channel has (bits 0 to 3).</summary>
        public uint Mask { get; } = mask;
        /// <summary>Four floats per luxel, row major.</summary>
        public float[] Data { get; } = new float[pixels * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public Channel?[] Channels { get; } = new Channel?[ChannelCount];

    /// <summary>12 bytes per luxel: chart id, sample count (low 24 bits), flags.</summary>
    public byte[] LuxelRecords { get; }

    /// <summary>20 bytes per luxel: four light indices, four floats.</summary>
    public byte[] ShadowRecords { get; }

    public LightmapImage(int width, int height)
    {
        Width = width;
        Height = height;
        LuxelRecords = new byte[width * height * 12];
        ShadowRecords = new byte[width * height * 20];
    }

    public Channel AddChannel(int index, uint mask) => Channels[index] = new Channel(Width * Height, mask);

    public uint ChartId(int luxel) => BinaryPrimitives.ReadUInt32LittleEndian(LuxelRecords.AsSpan(luxel * 12));

    public uint SampleCount(int luxel) => BinaryPrimitives.ReadUInt32LittleEndian(LuxelRecords.AsSpan(luxel * 12 + 4)) & 0xffffff;

    public void SetSampleCount(int luxel, uint count)
    {
        var at = luxel * 12 + 4;
        var word = BinaryPrimitives.ReadUInt32LittleEndian(LuxelRecords.AsSpan(at));
        BinaryPrimitives.WriteUInt32LittleEndian(LuxelRecords.AsSpan(at), word ^ ((count ^ word) & 0xffffff));
    }

    public byte Flags(int luxel) => LuxelRecords[luxel * 12 + 8];

    public bool Valid(int luxel) => (LuxelRecords[luxel * 12 + 8] & 1) != 0;

    public void SetValid(int luxel) => LuxelRecords[luxel * 12 + 8] |= 1;

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var data = new MemoryStream();
        var w = new BinaryWriter(data);
        w.Write(Encoding.ASCII.GetBytes("VRAD3LMI"));
        w.Write(1);
        w.Write(Width);
        w.Write(Height);
        for (var c = 0; c < ChannelCount; c++)
        {
            var channel = Channels[c];
            w.Write(channel is null ? 0 : 1);
            if (channel is null)
                continue;
            w.Write(channel.Mask);
            File.WriteAllBytes(Path.Combine(folder, $"channel{c:00}.exr"), ChannelExr(channel).Write());
        }
        w.Write(ShadowRecords);
        w.Write(LuxelRecords);
        File.WriteAllBytes(Path.Combine(folder, "lightmap_data.dat"), data.ToArray());
    }

    // FloatBitMap (w, h, mask | 7): R, G, B always, A with mask bit 3.
    private Vrad3Exr ChannelExr(Channel channel)
    {
        var names = (channel.Mask & 8) != 0 ? new[] { "R", "G", "B", "A" } : ["R", "G", "B"];
        var exr = new Vrad3Exr(Width, Height, names);
        for (var k = 0; k < names.Length; k++)
        {
            var plane = exr.Plane(names[k]);
            var present = (channel.Mask >> k & 1) != 0;
            for (var i = 0; i < plane.Length; i++)
                plane[i] = present ? (Half)channel.Data[i * 4 + k] : Half.Zero;
        }
        return exr;
    }

    public static LightmapImage Load(string folder)
    {
        var data = File.ReadAllBytes(Path.Combine(folder, "lightmap_data.dat"));
        if (Encoding.ASCII.GetString(data, 0, 8) != "VRAD3LMI" || BitConverter.ToInt32(data, 8) != 1)
            throw new InvalidDataException("not a version 1 VRAD3LMI image");
        var image = new LightmapImage(BitConverter.ToInt32(data, 12), BitConverter.ToInt32(data, 16));
        var o = 20;
        for (var c = 0; c < ChannelCount; c++)
        {
            var present = BitConverter.ToInt32(data, o);
            o += 4;
            if (present == 0)
                continue;
            var channel = image.AddChannel(c, BitConverter.ToUInt32(data, o));
            o += 4;
            var exr = Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, $"channel{c:00}.exr")));
            var names = new[] { "R", "G", "B", "A" };
            for (var k = 0; k < 4; k++)
            {
                if (!exr.Channels.Contains(names[k]))
                    continue;
                var plane = exr.Plane(names[k]);
                for (var i = 0; i < plane.Length; i++)
                    channel.Data[i * 4 + k] = (float)plane[i];
            }
        }
        data.AsSpan(o, image.ShadowRecords.Length).CopyTo(image.ShadowRecords);
        o += image.ShadowRecords.Length;
        data.AsSpan(o, image.LuxelRecords.Length).CopyTo(image.LuxelRecords);
        return image;
    }
}
