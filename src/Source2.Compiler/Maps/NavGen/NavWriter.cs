using System.Buffers.Binary;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The .nav of a generated mesh. After the stage list, navsystem (1800250e0) appends each hull's
/// polygons into the processor's lists (18006bee0), runs JoinNonZup (180072b50, convar
/// nav_gen_join_nonzup, on) and compacts the vertex pool (180073ef0); 180022200 then hands the
/// polygons to the mesh's vfn 0x98 (CNavMesh build, rc 0923 1818d63c0; CS2's mesh is server.dll's
/// CSNavMesh through gameinfo's CustomNavBuild, which shares that code), and resourcecompiler writes
/// the file with SaveNavMeshToBuffer (rc 0923 1818d1320). The last block, the CS custom data
/// (CSNavMesh vfn 0x78), is the caller's.
/// </summary>
public static class NavWriter
{
    /// <summary>A mesh area as the build makes it (1818ea6f0) and CNavArea's save writes it (1818cfe80).</summary>
    public sealed class Area
    {
        public uint Id;
        public ulong Attributes;
        public byte Byte58;
        public int Polygon;

        /// <summary>Per corner edge: (area id, edge on that area) of each walk connection.</summary>
        public List<(uint Area, int Edge)>[] Connections = [];

        /// <summary>The area's two ladder lists (1818ed310 0 and 1), as ladder ids: 0 holds ladders it is a bottom area of, 1 a top area.</summary>
        public List<uint>[] Ladders = [[], []];
    }

    /// <summary>A CNavLadder (0x78 bytes, 1818e6970) as the mesh build leaves it and 1818e71b0 saves it.</summary>
    public sealed class Ladder
    {
        public uint Id;
        public System.Numerics.Vector3 Top, Bottom, Normal;
        public float Width, Length;
        public int Direction;

        /// <summary>The area slots +0x20, +0x28, +0x30, +0x38 (top) and +0x40, +0x48, +0x50 (bottom).</summary>
        public Area?[] Areas = new Area?[7];
    }

    /// <summary>The mesh the file is written from: the compacted corners, the polygons and the areas.</summary>
    public sealed class Mesh
    {
        public List<(float X, float Y, float Z)> Corners = [];
        public List<int[]> Polygons = [];
        public List<Area> Areas = [];

        /// <summary>The movable meshes (navsystem's list of 0x70-byte entries): none of their polygons are ported.</summary>
        public List<MovableMesh> Movable = [];

        /// <summary>The ladders (the mesh's +0x108 list), from <see cref="NavMeshLadders.Build"/>.</summary>
        public List<Ladder> Ladders = [];
    }

    /// <summary>
    /// A movable mesh as 1818e9510 writes it: its id path through 180106ee0 ("%d", then "%c%d" with ':'
    /// for each further id) and the 3x4 matrix; 1818d1a70 lists the gravity bit as
    /// movable_mesh_gravity_follows_rotation.
    /// </summary>
    public sealed record MovableMesh(string Name, float[] Matrix, bool GravityFollowsRotation)
    {
        /// <summary>
        /// navsystem's 0x70-byte entry (the list 180022200 hands the mesh), as 1818ef5e0 copies it: the
        /// id path (+0 count, ids from +4), the matrix at +0x38 .. +0x64, the gravity bit (+0x68 bit 0).
        /// Paths of more than five ids keep the rest out of line (+0x20 count, +0x28 pointer): not ported.
        /// </summary>
        public static MovableMesh FromNavGenEntry(ReadOnlySpan<byte> e)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(e);
            if (BinaryPrimitives.ReadInt32LittleEndian(e[0x20..]) != 0 || count > 5)
                throw new NotSupportedException("a movable mesh id path past five ids is not ported");
            var ids = new string[count];
            for (var k = 0; k < count; k++)
                ids[k] = BinaryPrimitives.ReadInt32LittleEndian(e[(4 + 4 * k)..]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var matrix = new float[12];
            for (var k = 0; k < 12; k++)
                matrix[k] = BinaryPrimitives.ReadSingleLittleEndian(e[(0x38 + 4 * k)..]);
            return new MovableMesh(string.Join(':', ids), matrix, (e[0x68] & 1) != 0);
        }
    }

    // The generic KV3 format (7c161274-e906-4698-aff2-e63eb59037e7) and CS's navmeshcustomdata1.
    internal static readonly Guid GenericFormat = new([0x7c, 0x16, 0x12, 0x74, 0xe9, 0x06, 0x98, 0x46, 0xaf, 0xf2, 0xe6, 0x3e, 0xb5, 0x90, 0x37, 0xe7]);
    internal static readonly Guid CustomDataFormat = new([0x29, 0xf0, 0x00, 0x73, 0x00, 0x8e, 0xf9, 0x45, 0xb9, 0xc3, 0x32, 0xe1, 0x40, 0x3c, 0x96, 0x20]);

    /// <summary>
    /// The mesh from the polygons after the last stage (IslandRemoval, 029). 180073ef0 renumbers the
    /// pool in first-use order over the polygons' corners (vertices no polygon uses are dropped);
    /// the build makes area i (id i + 1) from polygon i (1818ea6f0): per edge, the edge's connections
    /// of kind 0 whose other polygon is another area, as (that area's id, +0xc of the connection);
    /// attributes 0x8000 for flag 8, 0x20000 for flag 0x10, 0x10000 for kind 1, 0x40000 for kind 2;
    /// +0x58 from the polygon's +0x62.
    /// </summary>
    public static Mesh Build(NavGenPolys g)
    {
        // JoinNonZup only acts on polygons with flag 8 (the oriented-mesh path).
        if (g.Polys.Any(p => (p.Flags & 8) != 0))
            throw new NotSupportedException("nav polygons with flag 8 (JoinNonZup, 180072b50) are not ported");
        var mesh = new Mesh();
        var remap = new int[g.Pool.Count];
        Array.Fill(remap, -1);
        foreach (var p in g.Polys)
        {
            var poly = new int[p.Indices.Count];
            for (var k = 0; k < poly.Length; k++)
            {
                var v = p.Indices[k];
                if (remap[v] == -1)
                {
                    remap[v] = mesh.Corners.Count;
                    var (x, y, z) = g.Pool[v];
                    mesh.Corners.Add(((float)x, (float)y, (float)z));
                }
                poly[k] = remap[v];
            }
            mesh.Polygons.Add(poly);
        }
        for (var i = 0; i < g.Polys.Count; i++)
        {
            var p = g.Polys[i];
            var area = new Area { Id = (uint)i + 1, Polygon = i, Byte58 = p.Field62, Connections = new List<(uint, int)>[p.Indices.Count] };
            for (var e = 0; e < area.Connections.Length; e++)
            {
                area.Connections[e] = [];
                if (p.Links is not { } links)
                    continue;
                foreach (var c in links.PerEdge[e])
                {
                    var link = links.Connections[c];
                    var target = (uint)link.PolyB + 1;
                    if (link.Kind == 0 && target != area.Id)
                        area.Connections[e].Add((target, (sbyte)(byte)link.EdgeB));
                }
            }
            ulong attributes = 0;
            if ((p.Flags & 8) != 0)
                attributes = 0x8000;
            if ((p.Flags & 0x10) != 0)
                attributes |= 0x20000;
            if (p.Kind == 1)
                attributes |= 0x10000;
            if (p.Kind == 2)
                attributes |= 0x40000;
            area.Attributes = attributes;
            mesh.Areas.Add(area);
        }
        // CSNavMesh::Analyze's shared step (1814cfaa0): one-way connections only go on the target's incoming
        // lists (+0x90, 1814c67b0), which the save does not write; then the overlapping connections go.
        NavMeshOverlaps.Remove(mesh);
        return mesh;
    }

    /// <summary>
    /// SaveNavMeshToBuffer (1818d1320), version 36, for a mesh with no movable meshes and no area
    /// names: the header, the place-name table (1818ee8d0), the movable meshes' KV3 (vfn 0xd8,
    /// null without any), the polygon pool (1818e9510), the second KV3 (vfn 0xe8, null), the areas
    /// (vfn 0x40, 1818cfe80), ladders, transformed bounds, the generation parameters (1818d0690), the
    /// NavGenParams KV3 (vfn 0xf8, 1818d18f0) and the custom data (CSNavMesh vfn 0x78). Each KV3
    /// starts on an 8-byte boundary (CUtlBuffer::AlignBuffer, zero filled) and is binary_auto.
    /// </summary>
    public static byte[] Write(Mesh mesh, NavGenSettings settings, KVObject customData)
    {
        var w = new Writer();
        w.U32(0xFEEDFACE);
        w.U32(36);
        // CSNavMesh vfn 0x70: the custom data's version.
        w.U32(1);
        // +0x101: analysed (CSNavMesh's analysis sets it; 1818d1e70 warns when clear).
        w.U8(1);
        // 1818ee8d0: the place names (none), then whether any area uses name 0.
        w.U16(0);
        w.U8((byte)(mesh.Areas.Count > 0 ? 1 : 0));
        w.Kv3(MovableKv3(mesh.Movable), GenericFormat);

        // 1818e9510: corners, polygons (corner count, corners, the area's movable mesh, -1), the
        // vertex attribute flags (0: none), the movable meshes (none).
        w.U32((uint)mesh.Corners.Count);
        foreach (var (x, y, z) in mesh.Corners)
        {
            w.F32(x);
            w.F32(y);
            w.F32(z);
        }
        w.U32((uint)mesh.Polygons.Count);
        foreach (var poly in mesh.Polygons)
        {
            w.U8((byte)poly.Length);
            foreach (var k in poly)
                w.U32((uint)k);
            w.U32(0xFFFFFFFF);
        }
        w.U32(0);
        w.U32((uint)mesh.Movable.Count);
        foreach (var m in mesh.Movable)
        {
            w.Str(m.Name);
            foreach (var f in m.Matrix)
                w.F32(f);
        }
        w.Kv3(KVObject.Null(), GenericFormat);

        w.U32((uint)mesh.Areas.Count);
        foreach (var a in mesh.Areas)
        {
            w.U32(a.Id);
            w.U64(a.Attributes & 0x7f002);
            w.U8(a.Byte58);
            w.U32((uint)a.Polygon);
            w.F32(0f);
            foreach (var edge in a.Connections)
            {
                w.U32((uint)edge.Count);
                foreach (var (id, e) in edge)
                {
                    w.U32(id);
                    w.U32((uint)e);
                }
            }
            // Hiding spots (byte) and encounter spots: none. Then ladder lists 0 and 1 (count, ids +0x68).
            w.U8(0);
            w.U32(0);
            foreach (var list in a.Ladders)
            {
                w.U32((uint)list.Count);
                foreach (var id in list)
                    w.U32(id);
            }
        }
        // 1818e71b0 per ladder: id, width, top, bottom, length, direction, the seven areas' ids (0 for none).
        w.U32((uint)mesh.Ladders.Count);
        foreach (var l in mesh.Ladders)
        {
            w.U32(l.Id);
            w.F32(l.Width);
            w.F32(l.Top.X);
            w.F32(l.Top.Y);
            w.F32(l.Top.Z);
            w.F32(l.Bottom.X);
            w.F32(l.Bottom.Y);
            w.F32(l.Bottom.Z);
            w.F32(l.Length);
            w.U32((uint)l.Direction);
            foreach (var a in l.Areas)
                w.U32(a?.Id ?? 0);
        }
        // Transformed bounds (movable meshes not marked +0x4c): none.
        w.U32(0);
        GenerationParameters(w, settings);
        w.Kv3(NavGenParams(settings), GenericFormat);
        w.Kv3(customData, CustomDataFormat);
        return w.ToArray();
    }

    // 1818d1a70: null without movable meshes, else one int16 per mesh (typed array 0xb).
    private static KVObject MovableKv3(List<MovableMesh> movable)
    {
        if (movable.Count == 0)
            return KVObject.Null();
        var flags = KVObject.Array();
        foreach (var m in movable)
            flags.Add(new KVObject((short)(m.GravityFollowsRotation ? 1 : 0)));
        var root = KVObject.Collection();
        root.Add("movable_mesh_gravity_follows_rotation", flags);
        return root;
    }

    // 1818d0690: version 13, the block's numbers, the two strings, the hulls, +0x59.
    private static void GenerationParameters(Writer w, NavGenSettings s)
    {
        w.U32(13);
        w.U32(s.UseProjectDefaults ? 1u : 0u);
        w.F32(s.TileSize);
        w.F32(s.CellSize);
        w.F32(s.CellHeight);
        w.U32((uint)s.RegionMinSize);
        w.U32((uint)s.RegionMergeSize);
        w.F32(s.DetailSampleDist);
        w.F32(s.DetailSampleMaxError);
        w.U32((uint)s.EdgeMaxLen);
        w.F32(s.EdgeMaxError);
        w.U32((uint)s.VertsPerPoly);
        w.F32(s.SmallAreaOnEdgeRemovalSize);
        w.Str(s.HullPreset);
        w.Str(s.HullsVDataOverride);
        w.U32((uint)s.Hulls.Count);
        foreach (var h in s.Hulls)
        {
            w.U8((byte)(h.Enabled ? 1 : 0));
            w.F32(h.Radius);
            w.F32(h.Height);
            w.U8((byte)(h.ShortHeightEnabled ? 1 : 0));
            w.F32(h.ShortHeight);
            w.U8((byte)(h.CrawlEnabled ? 1 : 0));
            w.F32(h.CrawlHeight);
            w.F32(h.MaxClimb);
            w.U32((uint)h.MaxSlope);
            w.F32(h.MaxJumpDownDist);
            w.F32(h.MaxJumpHorizDistBase);
            w.F32(h.MaxJumpUpDist);
            w.U32((uint)h.BorderErosion);
        }
        w.U8((byte)(s.Byte59 != 0 ? 1 : 0));
    }

    // 1818d18f0: NavGenParams { HullParams [ { Name, FlowMap_Enabled, FlowMap_NodeMaxRadius } ] }.
    private static KVObject NavGenParams(NavGenSettings s)
    {
        var hulls = KVObject.Array();
        foreach (var h in s.Hulls)
        {
            var hull = KVObject.Collection();
            hull.Add("Name", new KVObject(h.Name));
            hull.Add("FlowMap_Enabled", new KVObject(h.FlowMapGenerationEnabled));
            hull.Add("FlowMap_NodeMaxRadius", new KVObject((double)h.FlowMapNodeMaxRadius));
            hulls.Add(hull);
        }
        var generation = KVObject.Collection();
        generation.Add("HullParams", hulls);
        var root = KVObject.Collection();
        root.Add("NavGenParams", generation);
        return root;
    }

    private sealed class Writer
    {
        private readonly MemoryStream _m = new();
        private readonly byte[] _b = new byte[8];

        public void U8(byte v) => _m.WriteByte(v);

        public void U16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_b, v);
            _m.Write(_b, 0, 2);
        }

        public void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_b, v);
            _m.Write(_b, 0, 4);
        }

        public void U64(ulong v)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_b, v);
            _m.Write(_b, 0, 8);
        }

        public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));

        // CUtlBuffer::PutString: the bytes and a terminating zero.
        public void Str(string s)
        {
            _m.Write(System.Text.Encoding.UTF8.GetBytes(s));
            _m.WriteByte(0);
        }

        public void Kv3(KVObject root, Guid format)
        {
            while (_m.Length % 8 != 0)
                _m.WriteByte(0);
            _m.Write(Kv3V5Writer.Write(root, format));
        }

        public byte[] ToArray() => _m.ToArray();
    }
}
