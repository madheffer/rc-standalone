using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The overlay descriptors a world node's BuildNode projects
/// (WRBNode_GenerateOverlayMeshes): one per material of each visible
/// <c>CMapStaticOverlay</c>, in walk order, as
/// CMapStaticOverlay_GetOverlayDescs (181194f40) fills them. Each face is the
/// overlay's polygon in world space with its texcoords, corners from the
/// face's first half-edge.
/// </summary>
internal static class NodeOverlays
{
    public sealed record Face(Vector3[] Positions, Vector2[] Texcoords);

    public sealed record Descriptor(int NodeId, string Material, int Mode, float Far, int RenderOrder, bool BackFaces, float BackFacingAngle,
                                    uint Tint, int[] Targets, Face[] Faces)
    {
        /// <summary>
        /// Descriptor +0x60, the mesh's VertexGenericIntegerData: the
        /// overlay's MaterialAdjustmentParamsStruct packed as
        /// CreateOverlayDescs does, (int)(x * 15) a nibble from the bottom:
        /// ColorBrightness, ColorContrast, RoughnessMetalnessOverride and
        /// NormalBlendOverride (bits 8 and 9), ColorAlpha, RoughnessBrightness,
        /// RoughnessContrast, ShadingAlpha, NormalIntensity (from bit 28,
        /// unmasked). Matches every atixref descriptor.
        /// </summary>
        public int IntegerData { get; init; }

        /// <summary>The overlay's tintColor (r, g, b, a bytes; white when it has none).</summary>
        public byte[] TintColor { get; init; } = [255, 255, 255, 255];
    }

    public static List<Descriptor> FromWorld(DmxBinary.Document doc)
    {
        var found = new List<Descriptor>();
        foreach (var overlay in MapMeshes.ReadOverlays(doc))
        {
            if (overlay.Hidden || overlay.Element is not { } e || e.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            var positions = Stream(data, "vertexData", "position");
            var texcoords = Stream(data, "faceVertexData", "texcoord");
            var materialIndex = Stream(data, "faceData", "materialindex");
            var materials = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
            var next = Ints(data, "edgeNextIndices");
            var to = Ints(data, "edgeVertexIndices");
            var vertexData = Ints(data, "vertexDataIndices");
            var cornerData = Ints(data, "edgeVertexDataIndices");
            var first = Ints(data, "faceEdgeIndices");
            var faceData = Ints(data, "faceDataIndices");
            var scales = e.GetValue<Vector3>("scales") ?? Vector3.One;
            var byMaterial = new SortedDictionary<int, List<Face>>();
            for (var f = 0; f < first.Length; f++)
            {
                var pts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var h = first[f];
                do
                {
                    pts.Add(Snap(MapMeshes.Transform(overlay.World, (Vector3)positions[vertexData[to[h]]]! * scales)));
                    uvs.Add(texcoords.Length > 0 ? (Vector2)texcoords[cornerData[h]]! : Vector2.Zero);
                    h = next[h];
                } while (h != first[f] && pts.Count <= next.Length);
                var m = materialIndex.Length > 0 && materialIndex[faceData[f]] is int mi ? mi : 0;
                if (!byMaterial.TryGetValue(m, out var faces))
                    byMaterial[m] = faces = [];
                faces.Add(new Face([.. pts], Recentre(uvs)));
            }
            var targets = (e.Get<object?[]>("projectionTargets") ?? []).OfType<int>().ToArray();
            var integer = Pack(e.Get<DmxBinary.Element>("MaterialAdjustmentParamsStruct"));
            foreach (var (m, faces) in byMaterial)
                found.Add(new Descriptor(overlay.NodeId, m < materials.Length ? materials[m] : "", e.GetValue<int>("projectionMode") ?? 0,
                                         e.GetValue<float>("projectionFar") ?? 0f, e.GetValue<int>("renderOrder") ?? 0, e.GetValue<bool>("projectOnBackFaces") ?? false,
                                         e.GetValue<float>("backFacingAngle") ?? 90f, 0, targets, [.. faces])
                {
                    IntegerData = integer,
                    TintColor = e.Attributes.GetValueOrDefault("tintColor") is byte[] { Length: 4 } c ? c : [255, 255, 255, 255],
                });
        }
        return found;
    }

    /// <summary>
    /// A face's texcoords whose box leaves [0, 1] lose the box centre
    /// truncated toward zero (measured on all 297 atixref overlays: 1.5 and
    /// 2.5 lose 1 and 2, -1.6 loses -1; the code doing it is not read).
    /// </summary>
    private static Vector2[] Recentre(List<Vector2> uvs)
    {
        float minU = float.MaxValue, minV = float.MaxValue, maxU = -float.MaxValue, maxV = -float.MaxValue;
        foreach (var uv in uvs)
        {
            minU = MathF.Min(minU, uv.X);
            maxU = MathF.Max(maxU, uv.X);
            minV = MathF.Min(minV, uv.Y);
            maxV = MathF.Max(maxV, uv.Y);
        }
        if (!(minU < 0f || minV < 0f || 1f < maxU || 1f < maxV))
            return [.. uvs];
        var su = (float)(int)((maxU + minU) * 0.5f);
        var sv = (float)(int)((maxV + minV) * 0.5f);
        return [.. uvs.Select(uv => new Vector2(uv.X - su, uv.Y - sv))];
    }

    private static int Pack(DmxBinary.Element? p)
    {
        int N(string key, float fallback) => (int)((p?.GetValue<float>(key) ?? fallback) * 15f);
        int B(string key) => (p?.GetValue<bool>(key) ?? false) ? 1 : 0;
        var v = N("NormalIntensity", 0.75f);
        v = (v << 4) | (N("ShadingAlpha", 1f) & 0xf);
        v = (v << 4) | (N("RoughnessContrast", 0.5f) & 0xf);
        v = (v << 4) | (N("RoughnessBrightness", 0.5f) & 0xf);
        v = (v << 4) | (N("ColorAlpha", 1f) & 0xf);
        v = (v << 3) | B("NormalBlendOverride");
        v = (v * 2) | B("RoughnessMetalnessOverride");
        v = (v << 4) | (N("ColorContrast", 0.5f) & 0xf);
        v = (v << 4) | (N("ColorBrightness", 0.5f) & 0xf);
        return v;
    }

    /// <summary>A node entry as the overlay pass sees it (WRBNode_CollectOverlayTargets).</summary>
    public sealed record Target(float[] Vertices, int Stride, IReadOnlyList<Physics.MeshWeld.Stream> Streams, int[] Indices, float[] Matrix,
                                int Kind, int NodeId, ulong Attributes);

    /// <summary>One projected mesh: the overlay and target it came from.</summary>
    public sealed record Projection(int Overlay, int Target, OverlayProjector.Mesh Mesh);

    /// <summary>
    /// WRBNode_GenerateOverlayMeshes (18025ece0) over <paramref name="targets"/>
    /// (the node's entries in order): for each overlay, each target whose
    /// attributes miss 0x1209, that has more than two vertices and indices,
    /// whose world box meets the projector's and that the mode accepts (1 and
    /// 2: the target's kind; 3: the listed node ids) is projected
    /// (COverlayProjector_ProjectOntoTarget); a result of fewer than three
    /// points makes no mesh. The node's name table (node +0x218) is not read.
    /// </summary>
    public static List<Projection> Project(IReadOnlyList<Descriptor> overlays, IReadOnlyList<Target> targets)
    {
        var boxes = targets.Select(TargetBox).ToList();
        var found = new List<Projection>();
        for (var o = 0; o < overlays.Count; o++)
        {
            var d = overlays[o];
            var faces = d.Faces.Select(f => f.Positions).ToList();
            var (n, w) = OverlayProjector.Plane(faces);
            var (lo, hi) = ProjectorBox(d);
            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                if ((target.Attributes & 0x1209) != 0 || target.Vertices.Length / target.Stride <= 2 || target.Indices.Length <= 2)
                    continue;
                var (tlo, thi) = boxes[t];
                if (!(tlo.X <= hi.X && lo.X <= thi.X && tlo.Y <= hi.Y && lo.Y <= thi.Y && tlo.Z <= hi.Z && lo.Z <= thi.Z))
                    continue;
                if (d.Mode is 1 or 2 ? target.Kind != d.Mode : d.Mode == 3 && !d.Targets.Contains(target.NodeId))
                    continue;
                var tris = target.Indices.Select(i => MapMeshes.Transform(target.Matrix, new Vector3(target.Vertices[i * target.Stride], target.Vertices[(i * target.Stride) + 1], target.Vertices[(i * target.Stride) + 2]))).ToList();
                var points = new List<Vector3>();
                var tags = new List<Vector4>();
                var uvs = new List<Vector2>();
                foreach (var face in d.Faces)
                {
                    var r = OverlayProjector.Project(face.Positions, -n, d.Far, !d.BackFaces, d.BackFacingAngle, tris, 0);
                    points.AddRange(r.Points);
                    tags.AddRange(r.Tags);
                    uvs.AddRange(r.Points.Select(p => OverlayProjector.Texcoord(face.Positions, face.Texcoords, OverlayProjector.Drop(p, n, w))));
                }
                if (points.Count < 3)
                    continue;
                var mesh = OverlayProjector.Build(new OverlayProjector.Result(points, tags), uvs, target.Vertices, target.Stride, target.Streams, target.Indices,
                                                  target.Matrix, d.IntegerData, n);
                found.Add(new Projection(o, t, mesh));
            }
        }
        return found;
    }

    /// <summary>COverlayProjector_Init's box: each face corner and the corner pushed back by far along the face normal, grown by 1.</summary>
    private static (Vector3 Lo, Vector3 Hi) ProjectorBox(Descriptor d)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(-float.MaxValue);
        foreach (var face in d.Faces)
        {
            var n = PolygonTriangulator.Newell(face.Positions);
            var off = d.Far <= 0f ? n * float.MaxValue : n * d.Far;
            foreach (var p in face.Positions)
            {
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
                var q = p - off;
                lo = Vector3.Min(lo, q);
                hi = Vector3.Max(hi, q);
            }
        }
        return (lo - Vector3.One, hi + Vector3.One);
    }

    /// <summary>The mesh's bounds through Matrix3x4_TransformAABB (18125cd10): centre and extents.</summary>
    private static (Vector3 Lo, Vector3 Hi) TargetBox(Target t)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(-float.MaxValue);
        for (var k = 0; k < t.Vertices.Length / t.Stride; k++)
        {
            var p = new Vector3(t.Vertices[k * t.Stride], t.Vertices[(k * t.Stride) + 1], t.Vertices[(k * t.Stride) + 2]);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        var c = new Vector3((hi.X + lo.X) * 0.5f, (hi.Y + lo.Y) * 0.5f, (hi.Z + lo.Z) * 0.5f);
        float ex = hi.X - c.X, ey = hi.Y - c.Y, ez = hi.Z - c.Z;
        var m = t.Matrix;
        var cw = MapMeshes.Transform(m, c);
        var wx = MathF.Abs(ey * m[1]) + MathF.Abs(ex * m[0]) + MathF.Abs(ez * m[2]);
        var wy = MathF.Abs(ey * m[5]) + MathF.Abs(ex * m[4]) + MathF.Abs(ez * m[6]);
        var wz = MathF.Abs(ey * m[9]) + MathF.Abs(ex * m[8]) + MathF.Abs(ez * m[10]);
        return (new Vector3(cw.X - wx, cw.Y - wy, cw.Z - wz), new Vector3(wx + cw.X, wy + cw.Y, wz + cw.Z));
    }

    /// <summary>HammerMesh_SnapVertices (1810db6a0) at 1/8: floor(p / g + 0.5) * g per axis.</summary>
    internal static Vector3 Snap(Vector3 p)
    {
        const float g = 0.125f;
        return new(MathF.Floor((p.X / g) + 0.5f) * g, MathF.Floor((p.Y / g) + 0.5f) * g, MathF.Floor((p.Z / g) + 0.5f) * g);
    }

    private static object?[] Stream(DmxBinary.Element data, string holder, string name)
        => data.Get<DmxBinary.Element>(holder)?.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];

    private static int[] Ints(DmxBinary.Element e, string name)
        => (e.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
}
