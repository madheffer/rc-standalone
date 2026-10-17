namespace Source2.Compiler.Lighting;

/// <summary>
/// chart_repacking.dat, which vrad3 writes at lightmapVRadQuality 1 and 2, and
/// BakeLighting's use of it after the bake (180217740, enabled by 182a88550 =
/// {0, 1, 1} per quality unless noRepack, gameinfo BakedLighting/NoRepack, or
/// disableLightingCalculations is set).
/// <list type="bullet">
/// <item>StaticLighting_LoadChartRepacking (18020e230): u32 magic 0x0531771e, u32
/// chart count (must equal the packing's), int width and height, a width x height
/// grid of each texel's original chart (u32, -1 for none), then per chart
/// {x, y, flags, size} as ints.</item>
/// <item>18020e4e0, for every lightmap uv: the texel under it is
/// (int)(u * width), clamped to 0..width-1 (likewise v). A chart with a size
/// above 0 moves: with flags bit 1, the uv goes to the centre of its size x size
/// square at (x, y); otherwise the uv keeps its offset from the chart's old corner,
/// swapped when bit 0 is set, from the new corner (x, y).</item>
/// </list>
/// The query data and the meshes' lightmap uvs see the moved charts; the packing
/// geometry file vrad3 read does not.
/// </summary>
public sealed class LightmapChartRepacking
{
    private const uint Magic = 0x0531771e;

    public readonly record struct Move(int X, int Y, int Flags, int Size);

    public int Width { get; private init; }
    public int Height { get; private init; }
    public uint[] Charts { get; private init; } = [];
    public Move[] Moves { get; private init; } = [];

    /// <summary>182a88550: whether BakeLighting repacks at each lightmapVRadQuality.</summary>
    public static bool RepacksAt(int quality) => quality is 1 or 2;

    /// <summary>The file, or null where Valve warns and does not repack (wrong code or chart count, short data).</summary>
    public static LightmapChartRepacking? Read(byte[] bytes, int chartCount)
    {
        if (bytes.Length < 16)
            return null;
        var r = new BinaryReader(new MemoryStream(bytes));
        if (r.ReadUInt32() != Magic)
            return null;
        var count = r.ReadUInt32();
        if (count != (uint)chartCount)
            return null;
        var width = r.ReadInt32();
        var height = r.ReadInt32();
        if (width <= 0 || height <= 0 || bytes.Length < 16 + (long)width * height * 4 + count * 16L)
            return null;
        var charts = new uint[width * height];
        for (var i = 0; i < charts.Length; i++)
            charts[i] = r.ReadUInt32();
        var moves = new Move[count];
        for (var i = 0; i < moves.Length; i++)
            moves[i] = new Move(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        return new LightmapChartRepacking { Width = width, Height = height, Charts = charts, Moves = moves };
    }

    /// <summary>The packing geometry's atlas uvs after the repack (18020e4e0 on each).</summary>
    public float[] Apply(PackingGeometry g)
    {
        var uv = (float[])g.AtlasUv.Clone();
        float w = Width, h = Height;
        var invW = 1f / w;
        var invH = 1f / h;
        for (var i = 0; i + 1 < uv.Length; i += 2)
        {
            var x = Math.Min(Math.Max(Cvttss2si(uv[i] * w), 0), Width - 1);
            var y = Math.Min(Math.Max(Cvttss2si(uv[i + 1] * h), 0), Height - 1);
            var id = Charts[Width * y + x];
            if (id == uint.MaxValue)
                continue;
            var move = Moves[id];
            if (move.Size <= 0)
                continue;
            if ((move.Flags >> 1 & 1) != 0)
            {
                var half = move.Size * 0.5f;
                uv[i] = (move.X + half) * invW;
                uv[i + 1] = (move.Y + half) * invH;
                continue;
            }
            var chart = g.Charts[id];
            var du = uv[i] - chart.X * invW;
            var dv = uv[i + 1] - chart.Y * invH;
            if ((move.Flags & 1) != 0)
                (du, dv) = (dv, du);
            uv[i] = move.X * invW + du;
            uv[i + 1] = move.Y * invH + dv;
        }
        return uv;
    }

    // cvttss2si: truncation, 0x80000000 for NaN or out of range.
    private static int Cvttss2si(float f) => float.IsNaN(f) || f >= 2147483648f || f < -2147483648f ? int.MinValue : (int)f;
}
