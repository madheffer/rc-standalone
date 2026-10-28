using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Source2.Compiler.SteamAudio;

/// <summary>A probe's influence sphere (ipl::Serialized::Sphere: centre, radius; metres).</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Sphere(float X, float Y, float Z, float Radius);

/// <summary>An energy field: per channel, per band (3), <see cref="Bins"/> floats, flattened in that order.</summary>
public sealed class EnergyField
{
    public int Channels;
    public int Bins;
    public float[] Data = [];
}

/// <summary>ipl::Serialized::SoundPath.</summary>
public record struct SoundPath(short FirstProbe, short LastProbe, short ProbeAfterFirst, short ProbeBeforeLast,
                               bool Direct, float DistanceInternal, float DeviationInternal);

public sealed class BakedReflections
{
    /// <summary>One per probe (null where not baked); null when the layer has no convolution data.</summary>
    public List<EnergyField?>? EnergyFields;
    /// <summary>Reverb times, 3 per probe; null when the layer has no parametric data.</summary>
    public float[]? Reverbs;
    public byte[] NeedsUpdate = [];
}

public sealed class BakedPathing
{
    /// <summary>Per probe, the probes below it it can see (ProbeVisibilityGraph's edges with j &lt; i).</summary>
    public List<int[]> VisGraph = [];
    public List<SoundPath> UniquePaths = [];
    public int[] PathIndices = [];
    public int[] Paths = [];
}

/// <summary>ipl::Serialized::BakedDataLayer with its identifier.</summary>
public sealed class BakedDataLayer
{
    /// <summary>BakedDataVariation: 0 reverb, 1 static source, 2 static listener, 3 dynamic.</summary>
    public byte Variation;
    /// <summary>BakedDataType: 0 reflections, 1 pathing.</summary>
    public byte Type;
    public Sphere Influence;
    public BakedReflections? Reflections;
    public BakedPathing? Pathing;
}

/// <summary>
/// phonon's serialized probe batch (probe_batch.fbs, root ProbeBatch), the bytes a <c>.sareverb</c>'s and a
/// <c>.sapaths</c>' <c>m_pProbeBatch</c> hold. <see cref="Write"/> builds it in the order phonon's serializers do
/// (ProbeBatch::serialize: the probe spheres, then per layer its identifier and data, the layer vector, the root;
/// BakedReflectionsData::serialize: needs_update, each energy field's data and table, their vector, the reverbs;
/// BakedPathData::serialize: the visibility graph's lists, the sound paths, path_indices, paths), and the
/// generated Create functions' field order (largest scalars first, each size in reverse declaration order).
/// </summary>
public sealed class ProbeBatch
{
    public List<Sphere> Probes = [];
    public List<BakedDataLayer> Layers = [];

    public byte[] Write()
    {
        var fbb = new FlatBufferBuilder();
        var probes = fbb.CreateVector(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(Probes)), 16, 4);
        var layers = new List<int>(Layers.Count);
        foreach (var layer in Layers)
        {
            // CreateBakedDataIdentifier(variation, type, influence): the struct, then type, then variation.
            fbb.StartTable();
            fbb.AddStruct(2, MemoryMarshal.AsBytes(new ReadOnlySpan<Sphere>(in layer.Influence)), 4);
            fbb.AddByte(1, layer.Type);
            fbb.AddByte(0, layer.Variation);
            var identifier = fbb.EndTable();
            var reflections = layer.Reflections is { } r ? WriteReflections(fbb, r) : 0;
            var pathing = layer.Pathing is { } p ? WritePathing(fbb, p) : 0;
            fbb.StartTable();
            fbb.AddOffset(2, pathing);
            fbb.AddOffset(1, reflections);
            fbb.AddOffset(0, identifier);
            layers.Add(fbb.EndTable());
        }
        var layerVector = fbb.CreateOffsetVector(layers);
        fbb.StartTable();
        fbb.AddOffset(1, layerVector);
        fbb.AddOffset(0, probes);
        return fbb.Finish(fbb.EndTable());
    }

    private static int WriteReflections(FlatBufferBuilder fbb, BakedReflections r)
    {
        var needsUpdate = fbb.CreateVector<byte>(r.NeedsUpdate);
        var fields = 0;
        if (r.EnergyFields is { } list)
        {
            var offsets = new int[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                if (r.NeedsUpdate[i] != 0 || list[i] is not { } e)
                    continue;
                var data = fbb.CreateVector<float>(e.Data);
                fbb.StartTable();
                fbb.AddOffset(2, data);
                fbb.AddInt(1, e.Bins);
                fbb.AddInt(0, e.Channels);
                offsets[i] = fbb.EndTable();
            }
            fields = fbb.CreateOffsetVector(offsets);
        }
        var reverbs = r.Reverbs is { } rv ? fbb.CreateVector(MemoryMarshal.AsBytes<float>(rv), 12, 4) : 0;
        fbb.StartTable();
        fbb.AddOffset(2, needsUpdate);
        fbb.AddOffset(1, reverbs);
        fbb.AddOffset(0, fields);
        return fbb.EndTable();
    }

    private static int WritePathing(FlatBufferBuilder fbb, BakedPathing p)
    {
        var lists = new int[p.VisGraph.Count];
        for (var i = 0; i < lists.Length; i++)
        {
            var edges = fbb.CreateVector<int>(p.VisGraph[i]);
            fbb.StartTable();
            fbb.AddOffset(0, edges);
            lists[i] = fbb.EndTable();
        }
        var nodes = fbb.CreateOffsetVector(lists);
        fbb.StartTable();
        fbb.AddOffset(0, nodes);
        var graph = fbb.EndTable();
        var paths = new int[p.UniquePaths.Count];
        for (var i = 0; i < paths.Length; i++)
        {
            var s = p.UniquePaths[i];
            // CreateSoundPath: the two floats, the four shorts, then direct (each size in reverse order).
            fbb.StartTable();
            fbb.AddFloat(6, s.DeviationInternal);
            fbb.AddFloat(5, s.DistanceInternal);
            fbb.AddShort(3, s.ProbeBeforeLast);
            fbb.AddShort(2, s.ProbeAfterFirst);
            fbb.AddShort(1, s.LastProbe);
            fbb.AddShort(0, s.FirstProbe);
            fbb.AddByte(4, s.Direct ? (byte)1 : (byte)0);
            paths[i] = fbb.EndTable();
        }
        var unique = fbb.CreateOffsetVector(paths);
        var indices = fbb.CreateVector<int>(p.PathIndices);
        var refs = fbb.CreateVector<int>(p.Paths);
        fbb.StartTable();
        fbb.AddOffset(3, refs);
        fbb.AddOffset(2, indices);
        fbb.AddOffset(1, unique);
        fbb.AddOffset(0, graph);
        return fbb.EndTable();
    }

    // ---- reading -------------------------------------------------------------------------------------------

    private readonly ref struct Table(ReadOnlySpan<byte> buf, int pos)
    {
        private readonly ReadOnlySpan<byte> _buf = buf;
        public readonly int Pos = pos;

        private int Field(int index)
        {
            var vt = Pos - BinaryPrimitives.ReadInt32LittleEndian(_buf[Pos..]);
            var vlen = BinaryPrimitives.ReadUInt16LittleEndian(_buf[vt..]);
            var at = 4 + 2 * index;
            return at < vlen ? BinaryPrimitives.ReadUInt16LittleEndian(_buf[(vt + at)..]) : 0;
        }

        public int Int(int index, int def = 0) => Field(index) is var o and > 0 ? BinaryPrimitives.ReadInt32LittleEndian(_buf[(Pos + o)..]) : def;
        public float Float(int index) => Field(index) is var o and > 0 ? BinaryPrimitives.ReadSingleLittleEndian(_buf[(Pos + o)..]) : 0;
        public short Short(int index) => Field(index) is var o and > 0 ? BinaryPrimitives.ReadInt16LittleEndian(_buf[(Pos + o)..]) : (short)0;
        public byte Byte(int index) => Field(index) is var o and > 0 ? _buf[Pos + o] : (byte)0;
        public bool Has(int index) => Field(index) > 0;
        public int At(int index) => Pos + Field(index);

        /// <summary>The object an offset field points at (0 when absent).</summary>
        public int Ref(int index)
        {
            var o = Field(index);
            if (o == 0)
                return 0;
            var at = Pos + o;
            return at + (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf[at..]);
        }
    }

    private static int Count(ReadOnlySpan<byte> b, int vec) => (int)BinaryPrimitives.ReadUInt32LittleEndian(b[vec..]);

    private static int Element(ReadOnlySpan<byte> b, int vec, int i)
    {
        var at = vec + 4 + 4 * i;
        return at + (int)BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    }

    private static T[] Array<T>(ReadOnlySpan<byte> b, int vec, int elemSize) where T : unmanaged
        => vec == 0 ? [] : MemoryMarshal.Cast<byte, T>(b.Slice(vec + 4, Count(b, vec) * elemSize)).ToArray();

    public static ProbeBatch Read(ReadOnlySpan<byte> b)
    {
        var root = new Table(b, (int)BinaryPrimitives.ReadUInt32LittleEndian(b));
        var batch = new ProbeBatch { Probes = [.. Array<Sphere>(b, root.Ref(0), 16)] };
        var layers = root.Ref(1);
        for (var i = 0; layers != 0 && i < Count(b, layers); i++)
        {
            var lt = new Table(b, Element(b, layers, i));
            var layer = new BakedDataLayer();
            if (lt.Ref(0) is var id and > 0)
            {
                var it = new Table(b, id);
                layer.Variation = it.Byte(0);
                layer.Type = it.Byte(1);
                if (it.Has(2))
                    layer.Influence = MemoryMarshal.Read<Sphere>(b[it.At(2)..]);
            }
            if (lt.Ref(1) is var rpos and > 0)
            {
                var rt = new Table(b, rpos);
                var r = new BakedReflections { NeedsUpdate = Array<byte>(b, rt.Ref(2), 1) };
                if (rt.Ref(0) is var ev and > 0)
                {
                    r.EnergyFields = [];
                    for (var j = 0; j < Count(b, ev); j++)
                    {
                        var et = new Table(b, Element(b, ev, j));
                        r.EnergyFields.Add(new EnergyField { Channels = et.Int(0), Bins = et.Int(1), Data = Array<float>(b, et.Ref(2), 4) });
                    }
                }
                if (rt.Ref(1) is var rv and > 0)
                    r.Reverbs = MemoryMarshal.Cast<byte, float>(b.Slice(rv + 4, Count(b, rv) * 12)).ToArray();
                layer.Reflections = r;
            }
            if (lt.Ref(2) is var ppos and > 0)
            {
                var pt = new Table(b, ppos);
                var p = new BakedPathing();
                if (pt.Ref(0) is var g and > 0 && new Table(b, g).Ref(0) is var nodes and > 0)
                    for (var j = 0; j < Count(b, nodes); j++)
                        p.VisGraph.Add(Array<int>(b, new Table(b, Element(b, nodes, j)).Ref(0), 4));
                if (pt.Ref(1) is var up and > 0)
                    for (var j = 0; j < Count(b, up); j++)
                    {
                        var st = new Table(b, Element(b, up, j));
                        p.UniquePaths.Add(new SoundPath(st.Short(0), st.Short(1), st.Short(2), st.Short(3), st.Byte(4) != 0, st.Float(5), st.Float(6)));
                    }
                p.PathIndices = Array<int>(b, pt.Ref(2), 4);
                p.Paths = Array<int>(b, pt.Ref(3), 4);
                layer.Pathing = p;
            }
            batch.Layers.Add(layer);
        }
        return batch;
    }
}
