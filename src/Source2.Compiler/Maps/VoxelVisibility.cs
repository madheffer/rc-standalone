using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The <c>VXVS</c> payload of <c>world_visibility.vvis_c</c>: an octree over the
/// map, the cluster regions its leaves hold, and the PVS bit matrix that says
/// which clusters can see which.
///
/// <para>VXVS is six arrays laid end to end with no headers of its own. The sibling
/// DATA block is the index: a byte offset and an element count for each. Those
/// offsets are pure prefix sums of the counts, which
/// <see cref="Layout.For(VoxelVisibility)"/> reproduces and
/// <c>VoxelVisibilityTests</c> checks against Valve's numbers on every installed
/// map.</para>
/// </summary>
public sealed class VoxelVisibility
{
    /// <summary>Number of real clusters. Sky and sun may add rows beyond these.</summary>
    public uint BaseClusterCount { get; init; }

    /// <summary>Row stride of <see cref="VisBlocks"/>, in bytes.</summary>
    public uint PVSBytesPerCluster { get; init; }

    /// <summary>Bounds of the octree root node.</summary>
    public Vector3 MinBounds { get; init; }

    /// <inheritdoc cref="MinBounds"/>
    public Vector3 MaxBounds { get; init; }

    /// <summary>Edge length of the finest grid cell.</summary>
    public float GridSize { get; init; }

    /// <summary>Cluster row used for sky visibility.</summary>
    public uint SkyVisibilityCluster { get; init; }

    /// <summary>Cluster row used for sun visibility.</summary>
    public uint SunVisibilityCluster { get; init; }

    /// <summary>Octree nodes, child groups of eight addressed by octant.</summary>
    public VisNode[] Nodes { get; init; } = [];

    /// <summary>Cluster regions; a leaf owns a contiguous run of them.</summary>
    public VisRegion[] Regions { get; init; } = [];

    /// <summary>Ranges into <see cref="EnclosedClusters"/>.</summary>
    public VisClusterRange[] EnclosedClusterList { get; init; } = [];

    /// <summary>Cluster ids fully enclosed by a node, for the coarse query path.</summary>
    public ushort[] EnclosedClusters { get; init; } = [];

    /// <summary>4x4x4 occupancy masks, bit index <c>x + 4y + 16z</c>.</summary>
    public ulong[] Masks { get; init; } = [];

    /// <summary>The PVS matrix, one <see cref="PVSBytesPerCluster"/> row per cluster.</summary>
    public byte[] VisBlocks { get; init; } = [];

    /// <summary>
    /// Rows the PVS matrix should carry. Sky and sun get a row of their own only
    /// when numbered past the base clusters; otherwise they alias an existing one.
    /// </summary>
    public uint PVSRowCount
    {
        get
        {
            var rows = BaseClusterCount;
            if (SkyVisibilityCluster >= BaseClusterCount)
                rows++;
            if (SunVisibilityCluster >= BaseClusterCount && SunVisibilityCluster != SkyVisibilityCluster)
                rows++;
            return rows;
        }
    }

    /// <summary>
    /// The row stride Valve writes for a given cluster count: one bit per cluster,
    /// rounded up to whole bytes and then to a multiple of four. Measured over 112
    /// maps, where the surplus over <c>ceil(count/8)</c> is only ever 0, 2 or 3.
    /// </summary>
    public static uint BytesPerCluster(uint clusterCount) => (clusterCount + 31) / 32 * 4;

    /// <summary>
    /// Whether cluster <paramref name="from"/> can see <paramref name="to"/>.
    /// Rows are cluster-major and bits are least significant first, which is
    /// settled rather than assumed: on ze_aztecnoob_p that reading has 4095 of
    /// 4096 clusters seeing themselves, and the other order only 3136.
    /// </summary>
    public bool CanSee(uint from, uint to)
    {
        var bit = (int)(from * PVSBytesPerCluster * 8 + to);
        return (VisBlocks[bit >> 3] & (1 << (bit & 7))) != 0;
    }

    /// <summary>
    /// Whether a cluster's PVS row is entirely zero, meaning the build allocated
    /// the id but nothing occupies it. Such a cluster sees nothing, not even
    /// itself, so it is the one exception to the self-visibility invariant.
    /// </summary>
    public bool ClusterIsUnused(uint cluster)
    {
        var start = (int)(cluster * PVSBytesPerCluster);
        for (var i = 0; i < PVSBytesPerCluster; i++)
        {
            if (VisBlocks[start + i] != 0)
                return false;
        }
        return true;
    }

    /// <summary>Byte offsets and element counts of the six arrays inside VXVS.</summary>
    /// <param name="Nodes">Offset and count of the node array.</param>
    /// <param name="Regions">Offset and count of the region array.</param>
    /// <param name="EnclosedClusterList">Offset and count of the enclosed-range array.</param>
    /// <param name="EnclosedClusters">Offset and count of the enclosed-cluster array.</param>
    /// <param name="Masks">Offset and count of the mask array.</param>
    /// <param name="VisBlocks">Offset and byte length of the PVS matrix.</param>
    public readonly record struct Layout(
        VisBlockRef Nodes,
        VisBlockRef Regions,
        VisBlockRef EnclosedClusterList,
        VisBlockRef EnclosedClusters,
        VisBlockRef Masks,
        VisBlockRef VisBlocks)
    {
        /// <summary>Total VXVS size implied by these six ranges.</summary>
        public int TotalBytes => VisBlocks.Offset + VisBlocks.Count * VisBlocks.Stride;

        /// <summary>The layout Valve writes for this data: offsets are prefix sums.</summary>
        public static Layout For(VoxelVisibility vis)
        {
            var at = 0;
            VisBlockRef Next(int count, int stride)
            {
                var block = new VisBlockRef(at, count, stride);
                at += count * stride;
                return block;
            }
            return new Layout(
                Next(vis.Nodes.Length, 8),
                Next(vis.Regions.Length, 8),
                Next(vis.EnclosedClusterList.Length, 8),
                Next(vis.EnclosedClusters.Length, 2),
                Next(vis.Masks.Length, 8),
                Next(vis.VisBlocks.Length, 1));
        }
    }

    /// <summary>
    /// Decode a VXVS payload using the offsets and counts the DATA index gives.
    /// </summary>
    public static VoxelVisibility ReadVxvs(ReadOnlySpan<byte> vxvs, Layout layout, VoxelVisibility scalars)
    {
        var nodes = new VisNode[layout.Nodes.Count];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = VisNode.Decode(ReadU32(vxvs, layout.Nodes.Offset + i * 8),
                                      ReadU32(vxvs, layout.Nodes.Offset + i * 8 + 4));

        var regions = new VisRegion[layout.Regions.Count];
        for (var i = 0; i < regions.Length; i++)
            regions[i] = VisRegion.Decode(ReadU64(vxvs, layout.Regions.Offset + i * 8));

        var ranges = new VisClusterRange[layout.EnclosedClusterList.Count];
        for (var i = 0; i < ranges.Length; i++)
            ranges[i] = new VisClusterRange(
                (int)ReadU32(vxvs, layout.EnclosedClusterList.Offset + i * 8),
                (int)ReadU32(vxvs, layout.EnclosedClusterList.Offset + i * 8 + 4));

        var enclosed = new ushort[layout.EnclosedClusters.Count];
        for (var i = 0; i < enclosed.Length; i++)
            enclosed[i] = (ushort)(vxvs[layout.EnclosedClusters.Offset + i * 2]
                                 | vxvs[layout.EnclosedClusters.Offset + i * 2 + 1] << 8);

        var masks = new ulong[layout.Masks.Count];
        for (var i = 0; i < masks.Length; i++)
            masks[i] = ReadU64(vxvs, layout.Masks.Offset + i * 8);

        return new VoxelVisibility
        {
            BaseClusterCount = scalars.BaseClusterCount,
            PVSBytesPerCluster = scalars.PVSBytesPerCluster,
            MinBounds = scalars.MinBounds,
            MaxBounds = scalars.MaxBounds,
            GridSize = scalars.GridSize,
            SkyVisibilityCluster = scalars.SkyVisibilityCluster,
            SunVisibilityCluster = scalars.SunVisibilityCluster,
            Nodes = nodes,
            Regions = regions,
            EnclosedClusterList = ranges,
            EnclosedClusters = enclosed,
            Masks = masks,
            VisBlocks = vxvs.Slice(layout.VisBlocks.Offset, layout.VisBlocks.Count).ToArray(),
        };
    }

    /// <summary>Encode this data as a VXVS payload.</summary>
    public byte[] WriteVxvs()
    {
        var layout = Layout.For(this);
        var buffer = new byte[layout.TotalBytes];

        for (var i = 0; i < Nodes.Length; i++)
        {
            var (first, second) = Nodes[i].Encode();
            WriteU32(buffer, layout.Nodes.Offset + i * 8, first);
            WriteU32(buffer, layout.Nodes.Offset + i * 8 + 4, second);
        }

        for (var i = 0; i < Regions.Length; i++)
            WriteU64(buffer, layout.Regions.Offset + i * 8, Regions[i].Encode());

        for (var i = 0; i < EnclosedClusterList.Length; i++)
        {
            WriteU32(buffer, layout.EnclosedClusterList.Offset + i * 8, (uint)EnclosedClusterList[i].Offset);
            WriteU32(buffer, layout.EnclosedClusterList.Offset + i * 8 + 4, (uint)EnclosedClusterList[i].Count);
        }

        for (var i = 0; i < EnclosedClusters.Length; i++)
        {
            buffer[layout.EnclosedClusters.Offset + i * 2] = (byte)EnclosedClusters[i];
            buffer[layout.EnclosedClusters.Offset + i * 2 + 1] = (byte)(EnclosedClusters[i] >> 8);
        }

        for (var i = 0; i < Masks.Length; i++)
            WriteU64(buffer, layout.Masks.Offset + i * 8, Masks[i]);

        VisBlocks.CopyTo(buffer, layout.VisBlocks.Offset);
        return buffer;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int at)
        => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    private static ulong ReadU64(ReadOnlySpan<byte> b, int at)
        => ReadU32(b, at) | (ulong)ReadU32(b, at + 4) << 32;

    private static void WriteU32(byte[] b, int at, uint v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }

    private static void WriteU64(byte[] b, int at, ulong v)
    {
        WriteU32(b, at, (uint)v);
        WriteU32(b, at + 4, (uint)(v >> 32));
    }
}

/// <summary>One of the six arrays inside VXVS.</summary>
/// <param name="Offset">Byte offset from the start of the VXVS block.</param>
/// <param name="Count">Element count.</param>
/// <param name="Stride">Bytes per element.</param>
public readonly record struct VisBlockRef(int Offset, int Count, int Stride);

/// <summary>A range into <see cref="VoxelVisibility.EnclosedClusters"/>.</summary>
/// <param name="Offset">First index.</param>
/// <param name="Count">How many.</param>
public readonly record struct VisClusterRange(int Offset, int Count);

/// <summary>
/// An octree node, two packed 32-bit words.
///
/// <code>
/// word 0   bit  0      isLeaf
///          bits 1..31  offset: first child node for a branch (a group of 8,
///                      indexed by octant), first region for a leaf
/// word 1   bits 0..7   regionCount
///          bits 8..31  enclosedListIndex, 0xFFFFFF when the node has none
/// </code>
/// </summary>
/// <param name="IsLeaf">Whether the node holds regions rather than children.</param>
/// <param name="Offset">Child base or region base, per <see cref="IsLeaf"/>.</param>
/// <param name="RegionCount">Regions owned by a leaf.</param>
/// <param name="EnclosedListIndex">Index into the enclosed ranges, or <see cref="NoEnclosedList"/>.</param>
public readonly record struct VisNode(bool IsLeaf, uint Offset, byte RegionCount, uint EnclosedListIndex)
{
    /// <summary>The sentinel meaning "this node has no enclosed cluster list".</summary>
    public const uint NoEnclosedList = 0xFFFFFF;

    /// <summary>Whether the node carries a precomputed enclosed cluster list.</summary>
    public bool HasEnclosedList => EnclosedListIndex != NoEnclosedList;

    /// <summary>Unpack the two stored words.</summary>
    public static VisNode Decode(uint first, uint second)
        => new((first & 1) != 0, first >> 1, (byte)second, second >> 8);

    /// <summary>Pack back to the two stored words.</summary>
    public (uint First, uint Second) Encode()
        => (Offset << 1 | (IsLeaf ? 1u : 0u), RegionCount | EnclosedListIndex << 8);
}

/// <summary>
/// A cluster region owned by a leaf, one packed 64-bit word.
///
/// <code>
/// bits  0..14  clusterId
/// bit  15      intersectsGeometry
/// bits 16..39  leafIndex   (written by the compiler, unused by the game)
/// bits 40..63  maskIndex
/// </code>
/// </summary>
/// <param name="ClusterId">The cluster this region belongs to.</param>
/// <param name="IntersectsGeometry">Whether the region straddles solid geometry.</param>
/// <param name="LeafIndex">Back-reference to the owning leaf.</param>
/// <param name="MaskIndex">Index into <see cref="VoxelVisibility.Masks"/>.</param>
public readonly record struct VisRegion(ushort ClusterId, bool IntersectsGeometry, uint LeafIndex, uint MaskIndex)
{
    /// <summary>Unpack the stored word.</summary>
    public static VisRegion Decode(ulong value)
        => new((ushort)(value & 0x7FFF), (value >> 15 & 1) != 0,
               (uint)(value >> 16 & 0xFFFFFF), (uint)(value >> 40));

    /// <summary>Pack back to the stored word.</summary>
    public ulong Encode()
        => ClusterId | (IntersectsGeometry ? 1UL << 15 : 0) | (ulong)LeafIndex << 16 | (ulong)MaskIndex << 40;
}
