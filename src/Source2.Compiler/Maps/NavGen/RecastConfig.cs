using System.Buffers.Binary;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The Recast configuration navsystem builds for the tiled Recast stage (navsystem 20261015,
/// 1800852a0, the tail after the chunky mesh). Every length is the generation setting in inches
/// times 0.0254: Recast runs in metres. The words are kept at the offsets the tile object copies
/// (cfg + 0xb8 .. 0x128 -> tile + 0x80 .. 0xf0), so a capture's config compares field by field.
/// </summary>
public sealed class RecastConfig
{
    public const float Inch = 0.0254f;

    // The generation settings, as the stage passes them (CNavGenParams; the third argument of
    // 180074330), and one hull entry (0x48 bytes at *(settings + 0x48), indexed by the stage's hull).
    public float TileSizeSetting, CellSize, CellHeight, DetailSampleDist, DetailSampleMaxError, MaxError;
    public int MinRegionSize, MergeRegionSize, MaxEdgeLen, VertsPerPoly;
    public bool OrientedPath;

    public float HullRadius, HullHeight, HullHeight2, HullHeight3, HullClimb, HullUnknown28;
    public int HullUnknown8, HullSlope;
    public bool HullHeight2On, HullHeight3On;

    /// <summary>The global added to the hull radius (*(DAT_180134b10) + 0x58; 0.75 in 20261015).</summary>
    public float RadiusPad = 0.75f;

    // Derived (1800852a0): metres and cells.
    public int TileSize, Width, Height, BorderSize, WalkableHeight, WalkableHeight2, WalkableHeight3, WalkableClimb,
        WalkableRadius, Unknown100, Unknown108, MaxEdgeLenCells, MinRegionArea, MergeRegionArea, Nvp;
    public byte Unknown104;
    public float Cs, Ch, WalkableSlope, MaxSimplificationError, SampleDist, SampleMaxError;

    // Metre values before the cell conversion (cfg + 0x64 .. 0xb4).
    public float RadiusM, HeightM, Height2M, Height3M, ClimbM, Unknown28M, EdgeLenM;

    /// <summary>Settings bytes (0x60 at the settings pointer) and the chosen hull entry (0x48 bytes).</summary>
    public static RecastConfig From(ReadOnlySpan<byte> settings, ReadOnlySpan<byte> hull, float radiusPad, byte flag)
    {
        float F(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
        int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
        var c = new RecastConfig
        {
            TileSizeSetting = F(settings, 4),
            CellSize = F(settings, 8),
            CellHeight = F(settings, 0xc),
            MinRegionSize = I(settings, 0x10),
            MergeRegionSize = I(settings, 0x14),
            DetailSampleDist = F(settings, 0x18),
            DetailSampleMaxError = F(settings, 0x1c),
            MaxEdgeLen = I(settings, 0x20),
            MaxError = F(settings, 0x24),
            VertsPerPoly = I(settings, 0x28),
            OrientedPath = settings[0x58] != 0,
            HullRadius = F(hull, 4),
            HullUnknown8 = I(hull, 8),
            HullHeight = F(hull, 0xc),
            HullHeight2On = hull[0x10] != 0,
            HullHeight2 = F(hull, 0x14),
            HullHeight3On = hull[0x18] != 0,
            HullHeight3 = F(hull, 0x1c),
            HullClimb = F(hull, 0x20),
            HullSlope = I(hull, 0x24),
            HullUnknown28 = F(hull, 0x28),
            RadiusPad = radiusPad,
        };
        c.Derive(flag);
        return c;
    }

    private void Derive(byte flag)
    {
        TileSize = (int)TileSizeSetting;
        Cs = CellSize * Inch;
        Ch = CellHeight * Inch;
        var minRegion = (int)(float)MinRegionSize;
        var mergeRegion = (int)(float)MergeRegionSize;
        SampleDist = DetailSampleDist * Inch;
        SampleMaxError = DetailSampleMaxError * Inch;
        EdgeLenM = (float)MaxEdgeLen * Inch;
        MaxSimplificationError = MaxError * Inch;
        var nvp = (int)(float)VertsPerPoly;
        RadiusM = (HullRadius + RadiusPad) * Inch;
        Unknown100 = HullUnknown8;
        Unknown104 = flag;
        HeightM = HullHeight * Inch;
        Height2M = HullHeight2 * Inch;
        Height3M = HullHeight3 * Inch;
        ClimbM = HullClimb * Inch;
        WalkableSlope = (float)HullSlope;
        Unknown28M = HullUnknown28 * Inch;

        WalkableHeight = (int)MathF.Ceiling(HeightM / Ch);
        WalkableHeight2 = HullHeight2On ? (int)MathF.Ceiling(Height2M / Ch) : int.MaxValue;
        WalkableHeight3 = HullHeight3On ? (int)MathF.Ceiling(Height3M / Ch) : int.MaxValue;
        WalkableClimb = (int)MathF.Floor(ClimbM / Ch);
        WalkableRadius = (int)MathF.Ceiling(RadiusM / Cs);
        Unknown108 = (int)MathF.Ceiling(Unknown28M / Ch);
        MaxEdgeLenCells = (int)(EdgeLenM / Cs);
        MinRegionArea = (int)((float)minRegion * (float)minRegion);
        MergeRegionArea = (int)((float)mergeRegion * (float)mergeRegion);
        Nvp = (int)(float)nvp;
        SampleDist = Cs <= SampleDist ? SampleDist : 0f;
        BorderSize = WalkableRadius + 3;
        Width = Height = TileSize + BorderSize * 2;
    }

    /// <summary>The tile words (tile + 0x80 .. 0xf0, bmin and bmax left zero) as 1800852a0 stores them.</summary>
    public byte[] TileWords()
    {
        var b = new byte[0x70];
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o - 0x80), v);
        void F(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o - 0x80), v);
        I(0x80, Width);
        I(0x84, Height);
        I(0x88, TileSize);
        I(0x8c, BorderSize);
        F(0x90, Cs);
        F(0x94, Ch);
        F(0xb0, WalkableSlope);
        I(0xb4, WalkableHeight);
        I(0xb8, WalkableHeight2);
        I(0xbc, WalkableHeight3);
        I(0xc0, WalkableClimb);
        I(0xc4, WalkableRadius);
        I(0xc8, Unknown100);
        b[0xcc - 0x80] = Unknown104;
        I(0xd0, Unknown108);
        I(0xd4, MaxEdgeLenCells);
        F(0xd8, MaxSimplificationError);
        I(0xdc, MinRegionArea);
        I(0xe0, MergeRegionArea);
        I(0xe4, Nvp);
        F(0xe8, SampleDist);
        F(0xec, SampleMaxError);
        return b;
    }
}
