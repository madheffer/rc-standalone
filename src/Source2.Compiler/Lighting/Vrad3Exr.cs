using System.Buffers.Binary;
using System.Text;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The OpenEXR files vrad3 writes and reads (FloatBitMap_t::WriteToFile through
/// tinyexr_impl.cpp's WriteInMemoryEXRFast): scanline, uncompressed, one line
/// per chunk, half floats, the channels in name order (A, B, G, R). The header
/// carries exactly channels, compression (0), dataWindow, displayWindow,
/// lineOrder (0), pixelAspectRatio (1), screenWindowCenter (0, 0) and
/// screenWindowWidth (the width, as a float); then the line offset table.
/// irradiance.exr's size is exactly that layout (probe01: 100,729,145 bytes).
/// </summary>
public sealed class Vrad3Exr
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>The channel names, in file order (sorted: A, B, G, R).</summary>
    public IReadOnlyList<string> Channels { get; }

    /// <summary>Per channel, Width x Height halves, row major from the top.</summary>
    public Half[][] Planes { get; }

    public Vrad3Exr(int width, int height, IEnumerable<string> channels)
    {
        Width = width;
        Height = height;
        Channels = [.. channels.OrderBy(c => c, StringComparer.Ordinal)];
        Planes = [.. Channels.Select(_ => new Half[width * height])];
    }

    public Half[] Plane(string channel) => Planes[IndexOf(channel)];

    private int IndexOf(string channel)
    {
        for (var i = 0; i < Channels.Count; i++)
            if (Channels[i] == channel)
                return i;
        throw new KeyNotFoundException($"no channel {channel}");
    }

    public byte[] Write()
    {
        var header = new MemoryStream();
        var w = new BinaryWriter(header);
        w.Write(20000630);
        w.Write(2);
        void Name(string s)
        {
            w.Write(Encoding.ASCII.GetBytes(s));
            w.Write((byte)0);
        }
        void Attribute(string name, string type, byte[] value)
        {
            Name(name);
            Name(type);
            w.Write(value.Length);
            w.Write(value);
        }
        var chlist = new MemoryStream();
        var cw = new BinaryWriter(chlist);
        foreach (var c in Channels)
        {
            cw.Write(Encoding.ASCII.GetBytes(c));
            cw.Write((byte)0);
            cw.Write(1);        // HALF
            cw.Write(0);        // pLinear and reserved
            cw.Write(1);        // xSampling
            cw.Write(1);        // ySampling
        }
        cw.Write((byte)0);
        Attribute("channels", "chlist", chlist.ToArray());
        Attribute("compression", "compression", [0]);
        var box = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(8), Width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(12), Height - 1);
        Attribute("dataWindow", "box2i", box);
        Attribute("displayWindow", "box2i", box);
        Attribute("lineOrder", "lineOrder", [0]);
        Attribute("pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attribute("screenWindowCenter", "v2f", new byte[8]);
        Attribute("screenWindowWidth", "float", BitConverter.GetBytes((float)Width));
        w.Write((byte)0);
        var headerBytes = header.ToArray();

        var lineBytes = Width * Channels.Count * 2;
        var chunk = 8 + lineBytes;
        var bytes = new byte[headerBytes.Length + Height * 8 + (long)Height * chunk];
        headerBytes.CopyTo(bytes, 0);
        var first = headerBytes.Length + Height * 8L;
        for (var y = 0; y < Height; y++)
        {
            var at = first + (long)y * chunk;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(headerBytes.Length + y * 8), at);
            var line = bytes.AsSpan((int)at, chunk);
            BinaryPrimitives.WriteInt32LittleEndian(line, y);
            BinaryPrimitives.WriteInt32LittleEndian(line[4..], lineBytes);
            var o = 8;
            foreach (var plane in Planes)
                for (var x = 0; x < Width; x++, o += 2)
                    BinaryPrimitives.WriteHalfLittleEndian(line[o..], plane[y * Width + x]);
        }
        return bytes;
    }

    /// <summary>Read a file of this kind back (uncompressed, half, scanline).</summary>
    public static Vrad3Exr Read(ReadOnlySpan<byte> bytes)
    {
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != 20000630)
            throw new InvalidDataException("not an OpenEXR file");
        var o = 8;
        string Name(ReadOnlySpan<byte> b)
        {
            var end = b[o..].IndexOf((byte)0);
            var s = Encoding.ASCII.GetString(b.Slice(o, end));
            o += end + 1;
            return s;
        }
        var channels = new List<string>();
        int width = 0, height = 0;
        while (true)
        {
            var name = Name(bytes);
            if (name.Length == 0)
                break;
            var type = Name(bytes);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[o..]);
            o += 4;
            var value = bytes.Slice(o, size);
            o += size;
            if (name == "channels")
            {
                var q = 0;
                while (value[q] != 0)
                {
                    var end = value[q..].IndexOf((byte)0);
                    channels.Add(Encoding.ASCII.GetString(value.Slice(q, end)));
                    if (BinaryPrimitives.ReadInt32LittleEndian(value[(q + end + 1)..]) != 1)
                        throw new NotSupportedException("only half channels are read");
                    q += end + 1 + 16;
                }
            }
            else if (name == "compression" && value[0] != 0)
                throw new NotSupportedException("only uncompressed files are read");
            else if (name == "dataWindow")
            {
                width = BinaryPrimitives.ReadInt32LittleEndian(value[8..]) - BinaryPrimitives.ReadInt32LittleEndian(value) + 1;
                height = BinaryPrimitives.ReadInt32LittleEndian(value[12..]) - BinaryPrimitives.ReadInt32LittleEndian(value[4..]) + 1;
            }
        }
        var exr = new Vrad3Exr(width, height, channels);
        for (var y = 0; y < height; y++)
        {
            var at = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes[(o + y * 8)..]);
            var line = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
            var p = at + 8;
            foreach (var plane in exr.Planes)
                for (var x = 0; x < width; x++, p += 2)
                    plane[line * width + x] = BinaryPrimitives.ReadHalfLittleEndian(bytes[p..]);
        }
        return exr;
    }
}
