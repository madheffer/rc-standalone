using System.Numerics;
using Source2.Compiler.Meshopt;
using ValveResourceFormat;

namespace Source2.Compiler.Maps;

/// <summary>
/// A world node model (<c>worldnodes/*.vmdl_c</c>) from the meshes
/// <see cref="WorldNodeCompile"/> gives one render object or aggregate: its
/// draws, the vertex layout, the packed and meshopt-encoded buffers and the
/// description <see cref="WorldNodeModelTrees"/> writes.
/// <list type="bullet">
/// <item>An aggregate's meshes are its fragments in BuildAggregate's order
/// (180268e80: FUN_180296ed0 on the fragments, FUN_180296c80 on the
/// entries, one comparator): CMesh byte +0x58 ascending, then vertex count
/// descending, then the vertex data by memcmp. Each fragment is one draw;
/// the draws share one vertex buffer and one pooled index buffer, each
/// draw's indices offset by the vertices before it.</item>
/// <item>A render object's meshes are grouped by material in order of first
/// use, a group's meshes appended in order: one draw each.</item>
/// <item>Each draw goes through <see cref="NodeDraw"/> (weld, vertex cache,
/// overdraw, meshlets for an aggregate).</item>
/// <item>The layout lists the draw's streams in mesh order: position
/// (R32G32B32_FLOAT), texcoords (<see cref="NodeDraw.TexcoordFormat"/>),
/// normal with tangent as the packed tangent frame (R32_UINT), per-vertex
/// lighting as <c>color</c> 1 (R16G16B16A16_FLOAT), the rest R8G8B8A8_UNORM.
/// A stream the material's input signature reads takes its D3D semantic
/// and shader semantic; the others keep their mesh name and no semantic.</item>
/// </list>
/// </summary>
internal static class WorldNodeModels
{
    /// <summary>
    /// What a material tells the model: its vertex input signature (INSG
    /// <c>m_elems</c>) and the depth pass's (<c>m_depth_elems</c>). With depth
    /// elements the streams they name make vertex buffer 0, the depth buffer,
    /// and the rest buffer 1.
    /// </summary>
    public sealed record MaterialFacts(IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement> Signature,
                                       IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement> Depth);

    /// <summary>A built model: its description, buffer blocks in block order, referenced materials, and each draw's first mesh.</summary>
    public sealed record Built(WorldNodeModel Model, List<(BlockType Type, byte[] Bytes)> Buffers, List<string> References,
                               IReadOnlyList<WorldNodeCompile.Entry> DrawSources)
    {
        /// <summary>The vertex buffers before encoding, for comparison.</summary>
        public IReadOnlyList<byte[]> RawVertexBuffers { get; init; } = [];
    }

    // DXGI formats the node models use.
    private const uint R32G32B32Float = 6, R16G16B16A16Float = 10, R32G32Float = 16, R8G8B8A8Unorm = 28, R16G16Float = 34, R16G16Snorm = 37, R32Uint = 42;

    // A draw before packing: material, built geometry, stream layout and its source facts.
    private sealed record DrawInput(string Material, NodeDraw.Result Geometry, int Stride, IReadOnlyList<WrbMeshEntry.Stream> Streams, WrbMeshEntry Facts)
    {
        /// <summary>Each stream's first float ("name:index"), when the streams are not laid out in list order.</summary>
        public Dictionary<string, int>? Offsets { get; init; }
        /// <summary>
        /// A prop aggregate's draw: the welded vertices' normal and tangent are
        /// normalised once more as they are packed (CompressTangentFrame's inputs,
        /// capture_tangentframe.py on atixref), after the weld, which sees them raw.
        /// </summary>
        public bool RenormaliseFrame { get; init; }
    }

    /// <param name="mapMesh">embedded_map_mesh: the model compiler splits the depth stream (FUN_1802f9cc0: with SplitDepthStream, not pooled) and compresses texcoords (ModelCompile_BuildDrawSets) only for a map mesh; a brush entity's model keeps one buffer and float texcoords.</param>
    /// <param name="layout">The signature the layout follows, when not the first draw's material's.</param>
    public static Built Build(string name, IReadOnlyList<WorldNodeCompile.Entry> meshes, bool aggregate, Func<string, MaterialFacts> material, bool mapMesh = true,
                              MaterialFacts? layout = null)
    {
        var groups = aggregate ? FragmentOrder(meshes).Select(e => new List<WorldNodeCompile.Entry> { e }).ToList() : ByMaterial(meshes);
        var draws = new List<DrawInput>();
        foreach (var g in groups)
        {
            var (vertices, indices) = Append(g);
            var first = g[0];
            var streams = WeldStreams(first.Facts.Streams);
            var position = streams.First(s => s.Name == "position").First;
            var cone = NodeDraw.ConeWeight((first.Facts.Attributes & 0x100000000) != 0);
            draws.Add(new DrawInput(first.Material, NodeDraw.Build(vertices, first.Stride, position, indices, streams, aggregate, cone),
                                    first.Stride, first.Facts.Streams, first.Facts));
        }

        return Assemble(name, draws, aggregate, layout ?? material(draws[0].Material), mapMesh, null, Warnings(draws, material)) with { DrawSources = [.. groups.Select(g => g[0])] };
    }

    /// <summary>
    /// A static prop aggregate (<c>agg_prop</c>, <see cref="NodePropAggregates"/>):
    /// its draws as built, in model space, the streams laid out position,
    /// texcoord, texcoord 1, normal, colour (atixref's 171 agg_prop layouts),
    /// texcoords in the run's formats.
    /// </summary>
    public static Built BuildPropAggregate(string name, NodePropAggregates.Aggregate aggregate, Func<string, MaterialFacts> material)
    {
        var draws = aggregate.Draws.Select(d =>
        {
            var source = NodePropEntries.Streams(d.Color);
            // PrepareForMerge's filter by the material's inputs, then the layout order.
            var kept = MaterialStreams.Kept(source, MaterialStreams.Inputs(material(d.Material).Signature.Select(x => x.Semantic)), false).ToHashSet();
            var order = new[] { 0, 3, 5, 1, 2, 4 }.Where(kept.Contains).Select(i => source[i]).ToList();
            var streams = order.Select(x => new WrbMeshEntry.Stream(x.Name, x.Name == "texcoord" && x.First == 16 ? 1 : 0, x.Count, 0, x.Type)).ToList();
            var offsets = order.Zip(streams).ToDictionary(z => $"{z.Second.Name}:{z.Second.Index}", z => z.First.First, StringComparer.OrdinalIgnoreCase);
            return new DrawInput(d.Material, d.Geometry, NodePropEntries.Stride, streams,
                                 new WrbMeshEntry { Material = d.Material, Attributes = d.Attributes, Field28 = Vector4.One, Streams = streams }) { Offsets = offsets, RenormaliseFrame = true };
        }).ToList();
        uint Format(string f) => f switch { "R32G32_FLOAT" => R32G32Float, "R16G16_SNORM" => R16G16Snorm, _ => R16G16Float };
        return Assemble(name, draws, true, material(draws[0].Material), true, index => Format(aggregate.TexcoordFormats[index]), Warnings(draws, material));
    }

    // A draw whose material the game lacks is a compile warning (cardtest's teleport02 models: 1).
    private static int Warnings(List<DrawInput> draws, Func<string, MaterialFacts> material)
        => draws.Select(d => d.Material).Distinct(StringComparer.OrdinalIgnoreCase).Count(m => material(m).Signature.Count == 0);

    private static Built Assemble(string name, List<DrawInput> draws, bool aggregate, MaterialFacts facts, bool mapMesh, Func<int, uint>? texcoordFormat,
                                  int warnings = 0)
    {
        // Draw sets: draws whose input layout matches share vertex buffers and an
        // index buffer (InputLayout_MatchSignature, 181363de0), in first-draw order;
        // probe01's no-vis cb_mesh holds two (with and without PerVertexLighting).
        var sets = new List<(List<Slot> Slots, List<DrawInput> Draws)>();
        foreach (var d in draws)
        {
            var slots = Layout([d], mapMesh ? facts : facts with { Depth = [] }, aggregate, mapMesh, texcoordFormat);
            var set = sets.FindIndex(x => x.Slots.Select(l => (l.Field, l.Buffer)).SequenceEqual(slots.Select(l => (l.Field, l.Buffer))));
            if (set < 0)
                sets.Add((slots, [d]));
            else
                sets[set].Draws.Add(d);
        }
        var vertexBuffers = new List<WorldNodeModel.VertexBuffer>();
        var indexBuffers = new List<WorldNodeModel.IndexBuffer>();
        var buffers = new List<(BlockType Type, byte[] Bytes)>();
        var raw = new List<byte[]>();
        var described = new Dictionary<DrawInput, WorldNodeModel.Draw>(ReferenceEqualityComparer.Instance);
        var drawBounds = new Dictionary<DrawInput, (Vector3, Vector3)>(ReferenceEqualityComparer.Instance);
        var meshlets = new List<WorldNodeModel.Meshlet>();
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var (slots, setDraws) in sets)
        {
            // The depth pass reads buffer 0 whenever the material has a depth list, split
            // or not (atixref's inferno_fountain: one buffer, index 0); without one, 255.
            var bufferCount = slots.Max(l => l.Buffer) + 1;
            var firstBuffer = vertexBuffers.Count;
            int[] handles = [.. Enumerable.Range(firstBuffer, bufferCount)];
            var strides = Enumerable.Range(0, bufferCount).Select(b => slots.Where(l => l.Buffer == b).Sum(f => FormatSize(f.Field.Format))).ToArray();
            var totalVertices = setDraws.Sum(d => d.Geometry.VertexCount);
            var vbs = strides.Select(st => new byte[totalVertices * st]).ToArray();
            var ib = new List<uint>();
            var vertexBase = 0;
            foreach (var d in setDraws)
            {
                var g = d.Geometry;
                var offsets = d.Offsets ?? StreamOffsets(d.Streams);
                for (var b = 0; b < bufferCount; b++)
                    for (var v = 0; v < g.VertexCount; v++)
                        Pack(vbs[b].AsSpan((vertexBase + v) * strides[b], strides[b]), g.Vertices.AsSpan(v * d.Stride, d.Stride), offsets, slots.Where(l => l.Buffer == b), d.RenormaliseFrame);
                var positions = Positions(g, d.Stride, offsets["position:0"]);
                var (dmin, dmax) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
                foreach (var i in g.Indices)
                    (dmin, dmax) = (Vector3.Min(dmin, positions[i]), Vector3.Max(dmax, positions[i]));
                (min, max) = (Vector3.Min(min, dmin), Vector3.Max(max, dmax));
                drawBounds[d] = (dmin, dmax);
                var startIndex = ib.Count;
                var firstMeshlet = meshlets.Count;
                if (aggregate)
                    foreach (var m in g.Meshlets)
                    {
                        var idx = g.Indices.AsSpan(m.TriangleOffset * 3, m.TriangleCount * 3);
                        var (mmin, mmax) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
                        foreach (var i in idx)
                            (mmin, mmax) = (Vector3.Min(mmin, positions[i]), Vector3.Max(mmax, positions[i]));
                        var (pmin, pmax) = MeshletBounds.Pack(dmin, dmax, mmin, mmax);
                        var c = (d.Facts.Attributes & 0x100000000) != 0 ? MeshletBounds.Cone(positions, idx) : (0, 0, 0, 0);
                        // The count leaves out the padding triangle (last, last, last).
                        var padded = idx.Length >= 6 && idx[^1] == idx[^2] && idx[^2] == idx[^3];
                        meshlets.Add(new WorldNodeModel.Meshlet(pmin, pmax, c.Item1, c.Item2, c.Item3, c.Item4, vertexBase,
                                                                (startIndex / 3) + m.TriangleOffset, m.VertexCount, m.TriangleCount - (padded ? 1 : 0)));
                    }
                ib.AddRange(g.Indices.Select(i => (uint)(i + vertexBase)));
                var uv = TexcoordValues(g, d.Stride, offsets);
                var density = uv == null ? 0f : UvDensity.Compute(positions, uv, g.Indices);
                // An aggregate's draws are white (all 311 of atixref's list aggregate draws);
                // the mesh's tint rides on its fragment in the .vwnod.
                var tint = aggregate ? Vector4.One : d.Facts.Field28;
                described[d] = new WorldNodeModel.Draw(d.Material, new Vector3(tint.X, tint.Y, tint.Z), density, firstMeshlet, meshlets.Count - firstMeshlet,
                    vertexBase, bufferCount > 1 || (mapMesh && facts.Depth.Count > 0) ? 0 : 255, vertexBase + g.VertexCount, startIndex, g.Indices.Length, indexBuffers.Count, handles,
                    d.Streams.Any(s => s.Name.Equals("PerVertexLighting", StringComparison.OrdinalIgnoreCase)),
                    // A field the material has no input for (atixref's milwall001 object keeps
                    // VertexPaintTintColor and VertexPaintBlendParams) marks the draw unmatched.
                    slots.Any(l => l.Field.ShaderSemantic.Length == 0));
                vertexBase += g.VertexCount;
            }
            var elementSize = totalVertices > 0xffff ? 4 : 2;
            // The index codec's output is kept only when smaller than the raw indices.
            var ibBytes = MeshoptEncoder.EncodeIndexBuffer(ib.ToArray());
            var ibMeshopt = ibBytes.Length < ib.Count * elementSize;
            if (!ibMeshopt)
                ibBytes = elementSize == 2 ? [.. ib.SelectMany(i => BitConverter.GetBytes((ushort)i))] : [.. ib.SelectMany(i => BitConverter.GetBytes(i))];
            for (var b = 0; b < bufferCount; b++)
            {
                vertexBuffers.Add(new WorldNodeModel.VertexBuffer(totalVertices, strides[b], true, [.. slots.Where(l => l.Buffer == b).Select(l => l.Field)]));
                buffers.Add((BlockType.MVTX, MeshoptEncoder.EncodeVertexBuffer(vbs[b], totalVertices, strides[b])));
                raw.Add(vbs[b]);
            }
            indexBuffers.Add(new WorldNodeModel.IndexBuffer(ib.Count, elementSize, ibMeshopt, aggregate));
            buffers.Add((BlockType.MIDX, ibBytes));
        }
        var model = new WorldNodeModel(name, vertexBuffers, indexBuffers,
            [new WorldNodeModel.SceneObject(min, max, [.. draws.Select(d => described[d])], aggregate ? [.. draws.Select(d => drawBounds[d])] : [], meshlets)],
            aggregate, warnings);
        return new Built(model, buffers, [.. draws.Select(d => d.Material).Distinct(StringComparer.OrdinalIgnoreCase)], []) { RawVertexBuffers = [.. raw] };
    }

    // Step_SplittingMeshWith (180260710) welds each fragment's CMesh (CMesh_Weld with 0), then
    // BuildAggregate sorts: +0x58 (0 for every map mesh), welded vertex count descending, then memcmp of the vertices.
    private static List<WorldNodeCompile.Entry> FragmentOrder(IReadOnlyList<WorldNodeCompile.Entry> meshes)
    {
        var welded = meshes.Select(e =>
        {
            var (v, i) = Physics.MeshWeld.Weld(e.Vertices, e.Stride, e.Indices, WeldStreams(e.Facts.Streams), 0f, true);
            return e with { Vertices = v, Indices = i };
        }).ToArray();
        MsvcSort.Sort(welded, (a, b) =>
        {
            var (na, nb) = (a.Vertices.Length / a.Stride, b.Vertices.Length / b.Stride);
            if (na != nb)
                return nb <= na;
            var sa = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.Vertices.AsSpan());
            var sb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Vertices.AsSpan());
            return sa[..Math.Min(sa.Length, sb.Length)].SequenceCompareTo(sb[..Math.Min(sa.Length, sb.Length)]) < 0;
        });
        return [.. welded];
    }

    private static List<List<WorldNodeCompile.Entry>> ByMaterial(IReadOnlyList<WorldNodeCompile.Entry> meshes)
    {
        var groups = new List<List<WorldNodeCompile.Entry>>();
        foreach (var e in meshes)
        {
            // One draw per material and vertex layout: entries with and without a stream
            // (PerVertexLighting) are separate draws (probe01's no-vis cb_mesh).
            var g = groups.FirstOrDefault(x => string.Equals(x[0].Material, e.Material, StringComparison.OrdinalIgnoreCase)
                                               && x[0].Stride == e.Stride && SameLayout(x[0].Facts.Streams, e.Facts.Streams));
            if (g == null)
                groups.Add(g = []);
            g.Add(e);
        }
        return groups;
    }

    private static bool SameLayout(IReadOnlyList<WrbMeshEntry.Stream> a, IReadOnlyList<WrbMeshEntry.Stream> b)
        => a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    private static (float[] Vertices, int[] Indices) Append(List<WorldNodeCompile.Entry> group)
    {
        if (group.Count == 1)
            return (group[0].Vertices, group[0].Indices);
        var vertices = new List<float>();
        var indices = new List<int>();
        foreach (var e in group)
        {
            var at = vertices.Count / e.Stride;
            vertices.AddRange(e.Vertices);
            indices.AddRange(e.Indices.Select(i => i + at));
        }
        return ([.. vertices], [.. indices]);
    }

    private static List<Physics.MeshWeld.Stream> WeldStreams(IReadOnlyList<WrbMeshEntry.Stream> streams)
    {
        var list = new List<Physics.MeshWeld.Stream>();
        var at = 0;
        foreach (var s in streams)
        {
            list.Add(new Physics.MeshWeld.Stream(s.Name, at, s.Count, false, s.Type));
            at += s.Count;
        }
        return list;
    }

    private static Dictionary<string, int> StreamOffsets(IReadOnlyList<WrbMeshEntry.Stream> streams)
    {
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var at = 0;
        foreach (var s in streams)
        {
            offsets[$"{s.Name}:{s.Index}"] = at;
            at += s.Count;
        }
        return offsets;
    }

    private static Vector3[] Positions(NodeDraw.Result g, int stride, int at)
    {
        var p = new Vector3[g.VertexCount];
        for (var v = 0; v < p.Length; v++)
            p[v] = new Vector3(g.Vertices[(v * stride) + at], g.Vertices[(v * stride) + at + 1], g.Vertices[(v * stride) + at + 2]);
        return p;
    }

    private static Vector2[]? TexcoordValues(NodeDraw.Result g, int stride, Dictionary<string, int> offsets)
    {
        if (!offsets.TryGetValue("texcoord:0", out var at))
            return null;
        var uv = new Vector2[g.VertexCount];
        for (var v = 0; v < uv.Length; v++)
            uv[v] = new Vector2(g.Vertices[(v * stride) + at], g.Vertices[(v * stride) + at + 1]);
        return uv;
    }

    // A layout field, its vertex buffer and the mesh stream it packs ("name:index").
    private sealed record Slot(WorldNodeModel.LayoutField Field, int Buffer, string Stream, int Count);

    // The per-vertex lighting stream is the semantic table's color 1.
    private static (string Name, int Index) Key(WrbMeshEntry.Stream s)
        => s.Name.Equals("PerVertexLighting", StringComparison.OrdinalIgnoreCase) ? ("color", 1) : (s.Name, s.Index);

    private static bool Reads(IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement> elements, WrbMeshEntry.Stream s)
        => elements.Any(x => MaterialStreams.Inputs([x.Semantic]) is [var m] && Io.Tier0Strings.EqualsIgnoreCase(m.Name, Key(s).Name) && m.Index == Key(s).Index);

    // Field order: position, texcoords, normal, tangent, then the rest as the mesh has them
    // (ze_hold_em_p's baked fluorescent props, streamed P N Tg T0, lay out P T0 N).
    private static int Rank(WrbMeshEntry.Stream s) => s.Name.ToLowerInvariant() switch
    {
        "position" => 0, "texcoord" => 1, "normal" => 2, "tangent" => 3, _ => 4,
    };

    private static List<Slot> Layout(List<DrawInput> draws, MaterialFacts facts, bool aggregate, bool mapMesh, Func<int, uint>? texcoordFormat = null)
    {
        var signature = facts.Signature;
        // FUN_1802f5680 builds the depth and remainder layouts when the material has a depth
        // list; AddDrawDescriptors (1802ed7f0) splits when both have fields, so a depth list
        // that reads every stream keeps one buffer.
        var splits = facts.Depth.Count > 0
                     && draws[0].Streams.Any(x => !x.Name.Equals("tangent", StringComparison.OrdinalIgnoreCase) && !Reads(facts.Depth, x));
        var slots = new List<Slot>();
        var offsets = new int[2];
        foreach (var s in draws[0].Streams.OrderBy(Rank))
        {
            if (s.Name.Equals("tangent", StringComparison.OrdinalIgnoreCase))
                continue;
            var (stream, index) = s.Name.Equals("PerVertexLighting", StringComparison.OrdinalIgnoreCase) ? ("color", 1) : (s.Name, s.Index);
            var input = signature.FirstOrDefault(x => MaterialStreams.Inputs([x.Semantic]) is [var m] && Io.Tier0Strings.EqualsIgnoreCase(m.Name, s.Name) && m.Index == s.Index);
            uint format = s.Name.ToLowerInvariant() switch
            {
                "position" => R32G32B32Float,
                "texcoord" => texcoordFormat?.Invoke(s.Index) ?? TexcoordFormat(draws, s, aggregate),
                "normal" or "vertexgenericintegerdata" => R32Uint,
                "pervertexlighting" => R16G16B16A16Float,
                _ => R8G8B8A8Unorm,
            };
            var buffer = splits && !Reads(facts.Depth, s) ? 1 : 0;
            var offset = offsets[buffer];
            var field = input != null
                ? new WorldNodeModel.LayoutField(input.D3DSemanticName, input.D3DSemanticIndex, format, offset, Truncate(input.Semantic))
                : new WorldNodeModel.LayoutField(stream, index, format, offset, "");
            slots.Add(new Slot(field, buffer, $"{s.Name}:{s.Index}", s.Count));
            offsets[buffer] += FormatSize(format);
        }
        return slots;
    }

    // The shader semantic is kept in a 32-byte buffer.
    private static string Truncate(string s) => s.Length > 31 ? s[..31] : s;

    private static uint TexcoordFormat(List<DrawInput> draws, WrbMeshEntry.Stream s, bool aggregate)
    {
        var maxima = new List<(bool Precise, float Max)>();
        foreach (var d in draws)
        {
            var offsets = StreamOffsets(d.Streams);
            var at = offsets[$"{s.Name}:{s.Index}"];
            var precise = d.Streams.First(x => x.Name == s.Name && x.Index == s.Index).Precise != 0;
            var m = 0f;
            for (var v = 0; v < d.Geometry.VertexCount; v++)
                for (var k = 0; k < 2; k++)
                    m = MathF.Max(m, MathF.Abs(d.Geometry.Vertices[(v * d.Stride) + at + k]));
            maxima.Add((precise, m));
        }
        var promoted = aggregate && NodeDraw.RunPrecise(maxima);
        return NodeDraw.TexcoordFormat(promoted || maxima.Any(x => x.Precise), maxima.Where(x => !x.Precise).Select(x => x.Max)) switch
        {
            "R32G32_FLOAT" => R32G32Float,
            "R16G16_SNORM" => R16G16Snorm,
            _ => R16G16Float,
        };
    }

    private static int FormatSize(uint format) => format switch
    {
        R32G32B32Float => 12,
        R16G16B16A16Float => 8,
        R32G32Float => 8,
        _ => 4,
    };

    private static void Pack(Span<byte> to, ReadOnlySpan<float> v, Dictionary<string, int> offsets, IEnumerable<Slot> layout, bool renormalise = false)
    {
        foreach (var slot in layout)
        {
            var at = offsets[slot.Stream];
            var o = to[slot.Field.Offset..];
            switch (slot.Field.Format)
            {
                case R32G32B32Float:
                    for (var k = 0; k < 3; k++)
                        BitConverter.TryWriteBytes(o[(k * 4)..], v[at + k]);
                    break;
                case R32G32Float:
                    for (var k = 0; k < 2; k++)
                        BitConverter.TryWriteBytes(o[(k * 4)..], v[at + k]);
                    break;
                case R16G16Float:
                    for (var k = 0; k < 2; k++)
                        BitConverter.TryWriteBytes(o[(k * 2)..], ToHalf(v[at + k]));
                    break;
                case R16G16Snorm:
                    for (var k = 0; k < 2; k++)
                        BitConverter.TryWriteBytes(o[(k * 2)..], Snorm16(v[at + k]));
                    break;
                case R16G16B16A16Float:
                    for (var k = 0; k < 4; k++)
                        BitConverter.TryWriteBytes(o[(k * 2)..], ToHalf(k < slot.Count ? v[at + k] : 1f));
                    break;
                case R32Uint when slot.Stream.StartsWith("normal:", StringComparison.OrdinalIgnoreCase):
                    var n = new Vector3(v[at], v[at + 1], v[at + 2]);
                    var t = offsets.TryGetValue("tangent:0", out var ta) ? new Vector4(v[ta], v[ta + 1], v[ta + 2], v[ta + 3]) : Vector4.Zero;
                    if (renormalise)
                    {
                        n = TJunctionFix.Normalise(n);
                        var tn = TJunctionFix.Normalise(new Vector3(t.X, t.Y, t.Z));
                        t = new Vector4(tn, t.W);
                    }
                    BitConverter.TryWriteBytes(o, TangentFrame.Compress(n, t));
                    break;
                case R32Uint:
                    BitConverter.TryWriteBytes(o, BitConverter.SingleToUInt32Bits(v[at]));
                    break;
                default:
                    for (var k = 0; k < 4; k++)
                        // A colour short of four components reads (0, 0, 0, 1) for the rest (181365ce0).
                        o[k] = k < slot.Count ? Unorm8(v[at + k]) : k == 3 ? (byte)255 : (byte)0;
                    break;
            }
        }
    }

    private static short Snorm16(float v)
    {
        v = v < -1f ? -1f : v > 1f ? 1f : v;
        // CMesh_PackField truncates toward zero (atixref entity models: 6863, not 6864).
        return (short)(v * 32767f);
    }

    // ConvertFloatToChannel (181363b90): clamped, scaled and truncated.
    private static byte Unorm8(float v)
    {
        v = 0f <= v ? (v <= 1f ? v : 1f) : 0f;
        return (byte)(int)(v * 255f);
    }

    /// <summary>
    /// FUN_1802ea750, the packer's float to half: clamped to +-65504, the
    /// mantissa truncated, too small values flushed to signed zero below
    /// 2^-24, an infinity or NaN's sign kept with a zero or the largest value.
    /// </summary>
    internal static ushort ToHalf(float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        var sign = (ushort)((bits >> 16) & 0x8000);
        var low = (ushort)bits;
        if ((byte)(bits >> 23) == 0)
            return sign;
        if (((bits >> 23) & 0xff) == 0xff)
            return (bits & 0x7fffff) == 0 ? (ushort)(sign | (low & 0x7bff) | 0x7bff) : sign;
        var f = value > 65504f ? 65504f : value < -65504f ? -65504f : value;
        var fb = BitConverter.SingleToUInt32Bits(f);
        var biased = (int)((fb >> 23) & 0xff);
        var e = biased - 0x7f;
        var h = sign;
        if (-0x19 < e)
            h = (ushort)(sign | (low & 0x7fff));
        if (e < -14)
        {
            var shift = -biased;
            h = (ushort)(h & 0x83ff);
            if ((uint)(shift + 0x70) < 10)
            {
                var m = (fb & 0x7fffff) >> ((shift + 0x7e) & 0x1f);
                return (ushort)((((ushort)m + (1 << ((10 - (shift + 0x71)) & 0x1f))) ^ h) & 0x3ff ^ h);
            }
            return h;
        }
        if (e > 15)
            return (ushort)((h & 0xfbff) | 0x7bff);
        return (ushort)(((fb >> 13) & 0x3ff) | ((uint)((e + 15) * 0x400) & 0x7c00) | (uint)(h & 0x8000));
    }
}
