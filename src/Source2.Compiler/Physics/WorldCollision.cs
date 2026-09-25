using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// How the world's triangle meshes in <c>world_physics.vmdl_c</c> are put
/// together, before <see cref="RnMeshBuilder"/> builds each one.
///
/// <para>physicsbuilder's <c>CPhysicsBuilder::Build</c> walks the map's node
/// tree depth first, children in stored order. Each world mesh is split by
/// material, and every piece whose material is solid becomes one node of an
/// in-memory ModelDoc (<c>CPhysicsBuilderWorld_ModelDoc</c>). resourcecompiler
/// compiles that document. Each node's shape is appended to the physics part,
/// and the whole list is re-sorted by shape type with tier0's
/// <c>V_qsort</c> after every append (<see cref="PartOrder"/>). The part
/// builder then gathers the mesh shapes into one soup per collision attribute
/// (<see cref="Group"/>).</para>
/// </summary>
public static class WorldCollision
{
    /// <summary>The part's shape type for a triangle mesh (a hull is 2).</summary>
    public const int MeshType = 3;

    /// <summary>
    /// What physicsbuilder reads off a material (<see cref="MaterialCollision"/>).
    /// <c>Solid</c> false drops the piece. The group and the interact lists
    /// make the collision attribute, and <c>SurfaceProperty</c> is
    /// <c>PhysicsSurfaceProperties</c>.
    /// </summary>
    public sealed record MaterialPhysics(bool Solid, string CollisionGroup, string InteractAs, string SurfaceProperty)
    {
        public static readonly MaterialPhysics Default = new(true, "", "", "");

        public string InteractWith { get; init; } = "";
        public string InteractExclude { get; init; } = "";

        /// <summary>Whether the material stops its meshes' texcoord shift (<see cref="Maps.MapMeshCorners.KeepsTexcoords"/>).</summary>
        public bool KeepsTexcoords { get; init; }

        /// <summary>The painted layers, for a blend material (<see cref="ReadBlend"/>).</summary>
        public BlendLayers? Blend { get; init; }

        /// <summary>The collision attribute this material's pieces carry.</summary>
        public string AttributeKey => (CollisionGroup.Length == 0 ? "default" : CollisionGroup) + "|" + InteractAs
                                      + (InteractWith.Length + InteractExclude.Length == 0 ? "" : "|" + InteractWith + "|" + InteractExclude);
    }

    /// <summary>
    /// A blend material's painted layers as physicsbuilder reads them
    /// (0924: 1800156a0, 180015080). <c>Surfaces</c> are the layers' distinct
    /// surface properties, and <c>Remap</c> takes a layer to its surface's
    /// index. <c>PuddleChannel</c> is the blend channel whose paint puts a
    /// triangle on the <c>PuddleLayer</c> surface (-1 for none).
    /// <c>FirstHeightScale</c> below zero turns the first channel around.
    /// <c>Sampled</c> marks a material whose layers the builder may sample
    /// from its textures instead (not ported).
    /// </summary>
    public sealed record BlendLayers(int LayerCount, int PuddleChannel, int PuddleLayer, string[] Surfaces, int[] Remap, bool Swap, float FirstHeightScale, bool Sampled);

    /// <summary>
    /// The vertex paint flags a shader declares, as attributes of its
    /// programs, for a material's feature settings. Read from CS2's compiled
    /// shaders (1.41.8.4): only these four declare any of them, and none
    /// declares <c>VertexPaintHeightBlend</c>, <c>VertexPaintLayerCount</c>
    /// or the flag that swaps the first and third channels (key 0xad12291a).
    /// </summary>
    private static (int Layers, bool Puddles, bool Sampling) ShaderLayers(string shader, IReadOnlyDictionary<string, long> features)
    {
        bool On(string key) => features.TryGetValue(key, out var v) && v == 1;
        return Path.GetFileNameWithoutExtension(shader).ToLowerInvariant() switch
        {
            "csgo_simple_2way_blend" => (2, false, false),
            "csgo_environment_blend" => (On("F_ENABLE_LAYER_3") ? 3 : 2, On("F_WETNESS"), true),
            "csgo_environment" => (1, On("F_WETNESS"), false),
            "csgo_water_fancy" => (4, false, false),
            _ => (1, false, false),
        };
    }

    /// <summary>
    /// A material's painted layers (0924: 1800156a0). Layer <c>i</c> (from 1)
    /// takes <c>PhysicsSurfaceProperties&lt;i&gt;</c>, else the material's
    /// <c>PhysicsSurfaceProperties</c>, else "default". A layer whose surface
    /// is already listed shares its index. A <c>PhysicsSurfacePropertiesWet</c>
    /// is listed last as the puddle surface, puddle channel or not.
    /// </summary>
    public static BlendLayers ReadBlend(string shader, IReadOnlyDictionary<string, long> features, IReadOnlyDictionary<string, float> floats, IReadOnlyDictionary<string, string> strings)
    {
        string? Text(string key) => strings.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        var (layers, puddles, sampling) = ShaderLayers(shader, features);
        var baseSurface = Text("PhysicsSurfaceProperties") ?? "default";
        var surfaces = new List<string>();
        var remap = new int[layers];
        var firstScale = 1f;
        for (var i = 1; i <= layers; i++)
        {
            var name = Text($"PhysicsSurfaceProperties{i}") ?? baseSurface;
            var at = surfaces.IndexOf(name);
            if (at < 0)
            {
                surfaces.Add(name);
                at = surfaces.Count - 1;
            }
            if (i == 1)
                firstScale = floats.FirstOrDefault(kv => string.Equals(kv.Key, "g_flHeightMapScale1", StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : 1f;
            remap[i - 1] = at;
        }
        var puddleLayer = -1;
        if (Text("PhysicsSurfacePropertiesWet") is { Length: > 0 } wet)
        {
            puddleLayer = surfaces.IndexOf(wet);
            if (puddleLayer < 0)
            {
                surfaces.Add(wet);
                puddleLayer = surfaces.Count - 1;
            }
        }
        return new BlendLayers(layers, puddles ? 2 : -1, puddleLayer, [.. surfaces], remap, false, firstScale, surfaces.Count >= 2 && sampling);
    }

    /// <summary>
    /// Whether physicsbuilder splits a piece of this material by layer
    /// (0924: 180016230): more than one surface, or a puddle channel with a
    /// puddle surface. With one surface the piece stays whole and takes it.
    /// </summary>
    private static bool Splits(BlendLayers b)
        => b.Surfaces.Length > 1 || (b.PuddleChannel >= 0 && b.PuddleLayer >= 0 && b.PuddleLayer < b.Surfaces.Length);

    /// <summary>
    /// A piece cut into one mesh per layer surface (0924: 180015930). Each
    /// triangle averages its corners' blend paint, channel by channel as
    /// (second + first + third) / 3. Paint above 0.5 in the puddle channel
    /// sends it to the puddle surface; otherwise, with more than one layer,
    /// the first channel at 0.5 or more picks layer 1, else layer 0. The
    /// triangle's three positions go to that surface's mesh unshared, and
    /// each mesh is welded at 1/32 (CMesh_Weld). Meshes left empty are kept.
    /// </summary>
    private static List<(Vector3[] Points, int[] Indices)> SplitLayers(BlendLayers b, Vector3[] points, int[] indices, Vector4[] paint)
    {
        var corners = new List<Vector3>[b.Surfaces.Length];
        for (var s = 0; s < corners.Length; s++)
            corners[s] = [];
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            Vector4 p0 = paint[indices[t]], p1 = paint[indices[t + 1]], p2 = paint[indices[t + 2]];
            var avg = new[] { (p1.X + p0.X + p2.X) / 3f, (p1.Y + p0.Y + p2.Y) / 3f, (p1.Z + p0.Z + p2.Z) / 3f, (p1.W + p0.W + p2.W) / 3f };
            if (b.Swap)
                (avg[0], avg[2]) = (avg[2], avg[0]);
            int into;
            if (b.PuddleChannel >= 0 && b.PuddleLayer >= 0 && b.PuddleLayer < b.Surfaces.Length && avg[b.PuddleChannel] > 0.5f)
                into = b.PuddleLayer;
            else
            {
                var layer = 0;
                if (b.LayerCount > 1)
                {
                    var x = b.FirstHeightScale < 0f ? 1f - avg[0] : avg[0];
                    layer = 0.5f <= x ? 1 : 0;
                }
                if (layer >= b.Remap.Length)
                    into = 0;
                else if ((into = b.Remap[layer]) < 0)
                    continue;
            }
            if (into >= corners.Length)
                continue;
            corners[into].Add(points[indices[t]]);
            corners[into].Add(points[indices[t + 1]]);
            corners[into].Add(points[indices[t + 2]]);
        }
        var result = new List<(Vector3[], int[])>();
        foreach (var list in corners)
        {
            if (list.Count == 0)
            {
                result.Add(([], []));
                continue;
            }
            var flat = new float[list.Count * 3];
            for (var i = 0; i < list.Count; i++)
                (flat[i * 3], flat[(i * 3) + 1], flat[(i * 3) + 2]) = (list[i].X, list[i].Y, list[i].Z);
            var (v, welded) = MeshWeld.Weld(flat, 3, [.. Enumerable.Range(0, list.Count)], [new MeshWeld.Stream("position", 0, 3, false, 42)], 1f / 32f);
            var outPoints = new Vector3[v.Length / 3];
            for (var i = 0; i < outPoints.Length; i++)
                outPoints[i] = new Vector3(v[i * 3], v[(i * 3) + 1], v[(i * 3) + 2]);
            result.Add((outPoints, welded));
        }
        return result;
    }

    /// <summary>
    /// A material's physics (<see cref="MaterialCollision.Read"/>), its
    /// surface property, and the painted layers of a material whose shader
    /// declares more than one layer or a puddle channel. physicsbuilder reads
    /// the layers of any mesh with a VertexPaintBlendParams stream; for other
    /// shaders that only matters with a <c>PhysicsSurfaceProperties1</c> or
    /// <c>PhysicsSurfacePropertiesWet</c> set, which is left out.
    /// </summary>
    public static MaterialPhysics ReadMaterial(Maps.SettleWorld.MaterialInfo? info, Func<string, Maps.SettleWorld.CollisionProperty?> collisionProperty)
    {
        if (info == null)
            return MaterialPhysics.Default;
        var c = MaterialCollision.Read(info, collisionProperty);
        var surface = info.Strings.TryGetValue("PhysicsSurfaceProperties", out var s) ? s : "";
        var physics = new MaterialPhysics(c.Solid, c.Group, c.InteractAs, surface) { InteractWith = c.InteractWith, InteractExclude = c.InteractExclude, KeepsTexcoords = Maps.MapMeshCorners.KeepsTexcoords(info) };
        var (layers, puddles, _) = ShaderLayers(info.Shader, info.Params);
        return layers > 1 || puddles ? physics with { Blend = ReadBlend(info.Shader, info.Params, info.Floats, info.Strings) } : physics;
    }

    /// <summary>One world piece: a mesh's triangles in one solid material.</summary>
    public sealed record Piece(int NodeId, int Material, string MaterialName, MaterialPhysics Physics, Vector3[] Points, int[] Indices);

    /// <summary>
    /// The world's solid pieces in the order physicsbuilder hands them to the
    /// ModelDoc: meshes in node-walk order, each split by material in
    /// material order (<see cref="BrushHulls.Pieces"/>), triangulated the way
    /// the shape reads its mesh (<see cref="BrushHulls.TriangleMesh"/>).
    /// </summary>
    public static List<Piece> Pieces(DmxBinary.Document doc, Func<string, MaterialPhysics> materials, List<string>? notes = null)
    {
        var world = doc.OfType("CMapWorld").First();
        var result = new List<Piece>();
        foreach (var mesh in Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup"))
        {
            // A mesh set to no physics (physicsType "none") gives no pieces;
            // atixref's railing kit and fluorescent lights are such meshes.
            if (string.Equals(mesh.Element!.Get<string>("physicsType"), "none", StringComparison.OrdinalIgnoreCase))
                continue;
            var names = mesh.Element!.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [];
            var paint = PaintStream(mesh.Element!);
            if (Subdivided(mesh.Element!))
            {
                // The baked mesh's paint comes from the tessellation, which
                // lerps it over each patch as the bake does; a mesh with faces
                // stitched to finer neighbours has no ported paint there.
                var tessellated = TessellatedPieces(mesh.Element!, world, mesh.Instances.Length > 0 ? mesh.Path : null, out var covered);
                if (!covered)
                    notes?.Add($"node {mesh.NodeId}: subdivided faces stitched to finer neighbours, cut order not ported");
                foreach (var (material, points, indices, piecePaint) in tessellated)
                {
                    var name = material < names.Length ? (names[material] as string ?? "") : "";
                    var physics = materials(name);
                    if (!physics.Solid)
                        continue;
                    if (physics.Blend != null && paint != null && !covered && Splits(physics.Blend))
                    {
                        notes?.Add($"node {mesh.NodeId} material {material}: stitched subdivided mesh, not split");
                        result.Add(new Piece(mesh.NodeId, material, name, physics, points, indices));
                        continue;
                    }
                    if (physics.Blend is { Sampled: true } && paint != null && Splits(physics.Blend))
                        notes?.Add($"node {mesh.NodeId} material {material}: layers may come from the material sampler");
                    AddPieces(result, mesh.NodeId, material, name, physics, physics.Blend != null ? piecePaint : null, points, indices);
                }
                continue;
            }
            foreach (var (material, positions, faces, local, corners, cornerData) in BrushHulls.PiecesWithCorners(mesh.Element!, world, mesh.Instances.Length > 0 ? mesh.Path : null,
                         !names.Any(n => n is string m && materials(m).KeepsTexcoords)))
            {
                var name = material < names.Length ? (names[material] as string ?? "") : "";
                var physics = materials(name);
                if (!physics.Solid)
                    continue;
                var made = new List<(int Face, int Corner)>();
                var (points, triangles) = BrushHulls.TriangleMesh(positions, faces, local, corners, made);
                var indices = new int[triangles.Count * 3];
                for (var t = 0; t < triangles.Count; t++)
                    (indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2]) = triangles[t];
                // The shape's mesh keeps one vertex per .vmap vertex; its paint
                // is taken from the corner that made it (the .vmap stores
                // paint per corner; which corner the builder keeps is not
                // measured; notes lists pieces where the corners disagree).
                Vector4[]? vertexPaint = null;
                if (physics.Blend != null && paint != null)
                {
                    vertexPaint = [.. made.Select(m => paint[cornerData[m.Face][m.Corner]])];
                    // The sampler (18064c460) needs a render device, scene and
                    // mod-tools system connected; whether a compile has them is
                    // not measured, so such pieces are listed.
                    if (physics.Blend.Sampled)
                        notes?.Add($"node {mesh.NodeId} material {material}: layers may come from the material sampler");
                    if (notes != null && Disagreements(corners, cornerData, paint) is > 0 and var n)
                        notes.Add($"node {mesh.NodeId} material {material}: {n} vertices painted differently at their corners");
                }
                AddPieces(result, mesh.NodeId, material, name, physics, vertexPaint, [.. points], indices);
            }
        }
        return result;
    }

    // A piece as physicsbuilder's shape step hands it on (0924: 180016230):
    // whole, or cut by painted layer when its material has a blend and its
    // mesh a paint stream.
    private static void AddPieces(List<Piece> result, int node, int material, string name, MaterialPhysics physics, Vector4[]? paint, Vector3[] points, int[] indices)
    {
        if (physics.Blend is not { } blend || paint == null)
            result.Add(new Piece(node, material, name, physics, points, indices));
        else if (!Splits(blend))
            result.Add(new Piece(node, material, name, physics with { SurfaceProperty = blend.Surfaces[0] }, points, indices));
        else
        {
            var layers = SplitLayers(blend, points, indices, paint);
            for (var s = 0; s < layers.Count; s++)
                result.Add(new Piece(node, material, name, physics with { SurfaceProperty = blend.Surfaces[s] }, layers[s].Points, layers[s].Indices));
        }
    }

    // The mesh's vertex paint, per corner (faceVertexData "VertexPaintBlendParams").
    private static Vector4[]? PaintStream(DmxBinary.Element mesh)
        => mesh.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams")
               .FirstOrDefault(st => st.Name.Split(':')[0] == "VertexPaintBlendParams")?.Get<object?[]>("data")
               ?.Select(x => x is Vector4 v ? v : Vector4.Zero).ToArray();

    // Vertices whose corners carry different paint.
    private static int Disagreements(int[][] corners, int[][] cornerData, Vector4[] paint)
    {
        var seen = new Dictionary<int, Vector4>();
        var differ = new HashSet<int>();
        for (var f = 0; f < corners.Length; f++)
        {
            for (var j = 0; j < corners[f].Length; j++)
            {
                var p = paint[cornerData[f][j]];
                if (!seen.TryAdd(corners[f][j], p) && seen[corners[f][j]] != p)
                    differ.Add(corners[f][j]);
            }
        }
        return differ.Count;
    }

    private static bool Subdivided(DmxBinary.Element mesh)
        => mesh.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") is { } levels
           && levels.Any(x => x is int i && i > 0);

    /// <summary>
    /// A mesh with subdivided faces, cut as the mesh library cuts it for
    /// physics (<see cref="Maps.MeshTessellation"/>: patches for subdivided
    /// faces, neighbours stitched to them, equal positions welded). Each
    /// material's triangles go in face order, a vertex numbered where the
    /// material first meets it; positions are scaled and moved like the
    /// other pieces'.
    /// </summary>
    private static List<(int Material, Vector3[] Points, int[] Indices, Vector4[]? Paint)> TessellatedPieces(DmxBinary.Element mesh, DmxBinary.Element world, float[]? path, out bool covered)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData")!;
        var faceData = (data.Get<object?[]>("faceDataIndices") ?? []).Select(x => x is int i ? i : 0).ToArray();
        var faceMaterials = (data.Get<DmxBinary.Element>("faceData")?.GetElements("streams")
            .FirstOrDefault(st => st.Name.StartsWith("materialindex", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [])
            .Select(x => x is int i ? i : 0).ToArray();
        var faceBiases = (data.Get<DmxBinary.Element>("faceData")?.GetElements("streams")
            .FirstOrDefault(st => st.Name.StartsWith("lightmapScaleBias", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [])
            .Select(x => x is int i ? i : 0).ToArray();
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var toWorld = Maps.CTransform.FromNode(mesh).Matrix();
        var toEntity = Maps.CTransform.FromNode(world).Inverse().Matrix();
        (var cut, covered) = Maps.MeshTessellation.TriangulateBuilder(data);
        // One piece per (lightmap scale bias, material), as the exported mesh's face sets.
        var byMaterial = new SortedDictionary<(int Bias, int Material), (List<Vector3> Points, List<int> Indices, Dictionary<int, int> Of, List<Vector4> Paint)>();
        for (var t = 0; t < cut.Faces.Count; t++)
        {
            var f = cut.Faces[t];
            var material = faceMaterials.Length == 0 ? 0 : faceMaterials[faceData[f]];
            var bias = faceBiases.Length == 0 ? 0 : faceBiases[faceData[f]];
            if (!byMaterial.TryGetValue((bias, material), out var piece))
                byMaterial[(bias, material)] = piece = ([], [], [], []);
            for (var k = 0; k < 3; k++)
            {
                var v = cut.Indices[(t * 3) + k];
                if (!piece.Of.TryGetValue(v, out var at))
                {
                    at = piece.Points.Count;
                    piece.Of[v] = at;
                    var p = Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, cut.Positions[v] * scales));
                    piece.Points.Add(path == null ? p : Maps.MapMeshes.Transform(path, p));
                    if (cut.Paint != null)
                        piece.Paint.Add(cut.Paint[v]);
                }
                piece.Indices.Add(at);
            }
        }
        return [.. byMaterial.Select(kv => (kv.Key.Material, kv.Value.Points.ToArray(), kv.Value.Indices.ToArray(), cut.Paint == null ? null : kv.Value.Paint.ToArray()))];
    }

    /// <summary>
    /// The order shapes end up in the part (resourcecompiler 0923:
    /// 180c28150). Each one is appended and the whole list sorted by type
    /// with the CRT qsort. Equal types are not kept in order: the first eight
    /// of a run come out shuffled.
    /// </summary>
    public static List<T> PartOrder<T>(IEnumerable<T> shapes, Func<T, int> type)
    {
        var list = new List<T>();
        foreach (var shape in shapes)
        {
            list.Add(shape);
            Maps.CrtQsort.Sort(list, (a, b) => type(a) - type(b));
        }
        return list;
    }

    /// <summary>One triangle soup handed to RnMeshCreate.</summary>
    public sealed class Bucket
    {
        public int Attribute { get; internal set; }
        public int SurfaceProperty { get; internal set; }
        public List<Vector3> Vertices { get; } = [];
        public List<int> Indices { get; } = [];

        /// <summary>A surface property per triangle, once the soup mixes them.</summary>
        public List<byte>? Materials { get; internal set; }

        // +0x80: whether the soup's one shape so far shares the default object
        // at shape+0x100; null once a second shape has joined.
        internal bool? SoleShared { get; set; }
    }

    // The count limit the gatherer puts on a soup and a shape joining it.
    private const int CountLimit = 0xc000000;

    /// <summary>
    /// The part builder's mesh gathering (resourcecompiler 0923: 180c29500,
    /// joining in 180c27e30). The shapes go in part order. A shape joins the
    /// newest soup of its collision attribute when that soup is empty, or when
    /// all of these hold:
    /// <list type="bullet">
    /// <item>the soup's one shape so far, if it has only one, and the shape
    /// both hold the shared default at shape+0x100 (tag 0x32de3ab1);</item>
    /// <item>the soup's and the shape's index and vertex counts are at most
    /// 0xc000000;</item>
    /// <item>the surface properties allow it: equal, or both below 256, or
    /// the soup already per-triangle and the shape's below 256.</item>
    /// </list>
    /// Otherwise it starts a new soup, which becomes the attribute's newest.
    /// Soups left empty are removed, the last moving into the gap.
    /// </summary>
    /// <remarks>
    /// Every shape of every part in the atixref and ze_hold_em_p captures,
    /// world pieces, props and brush entities alike, points at one object at
    /// +0x100, the shared default; <c>Shared</c> is for a shape that would not.
    /// </remarks>
    public static List<Bucket> Group(IEnumerable<(int Attribute, int SurfaceProperty, Vector3[] Points, int[] Indices)> shapes)
        => Group(shapes.Select(x => (x.Attribute, x.SurfaceProperty, x.Points, x.Indices, true)));

    public static List<Bucket> Group(IEnumerable<(int Attribute, int SurfaceProperty, Vector3[] Points, int[] Indices, bool Shared)> shapes)
    {
        var buckets = new List<Bucket>();
        var newest = new Dictionary<int, int>();
        foreach (var (attribute, surface, points, indices, shared) in shapes)
        {
            Bucket? into = null;
            if (newest.TryGetValue(attribute, out var at))
            {
                var b = buckets[at];
                if (b.Indices.Count == 0)
                    into = b;
                else if (b.SoleShared != false && shared && b.Indices.Count <= CountLimit && indices.Length <= CountLimit
                         && b.Vertices.Count <= CountLimit && points.Length <= CountLimit)
                {
                    if (b.Materials == null)
                    {
                        if (surface == b.SurfaceProperty || (surface <= 0xff && b.SurfaceProperty < 0x100))
                            into = b;
                    }
                    else if (surface < 0x100)
                        into = b;
                }
            }
            if (into == null)
            {
                into = new Bucket();
                buckets.Add(into);
                newest[attribute] = buckets.Count - 1;
            }
            if (indices.Length >= 3)
                into.SoleShared = into.Indices.Count == 0 ? shared : null;
            Join(into, attribute, surface, points, indices);
        }
        for (var i = 0; i < buckets.Count;)
        {
            if (buckets[i].Indices.Count != 0)
            {
                i++;
                continue;
            }
            var last = buckets.Count - 1;
            if (i != last)
                buckets[i] = buckets[last];
            buckets.RemoveAt(last);
        }
        return buckets;
    }

    private static void Join(Bucket b, int attribute, int surface, Vector3[] points, int[] indices)
    {
        var triangles = indices.Length / 3;
        if (triangles == 0)
            return;
        if (b.Indices.Count == 0)
        {
            b.Attribute = attribute;
            b.SurfaceProperty = surface;
        }
        else if (b.Materials == null)
        {
            if (b.SurfaceProperty != surface)
            {
                b.Materials = [.. Enumerable.Repeat((byte)b.SurfaceProperty, b.Indices.Count / 3)];
                b.Materials.AddRange(Enumerable.Repeat((byte)surface, triangles));
            }
        }
        else
            b.Materials.AddRange(Enumerable.Repeat((byte)surface, triangles));
        var baseVertex = b.Vertices.Count;
        // The join (180c27e30 through 180c25a80) moves each vertex by the
        // shape's transform, rotate then scale then translate, with no
        // shortcut for the identity. With identity values that adds zeros,
        // which leaves every value alone but turns -0 into +0.
        foreach (var p in points)
            b.Vertices.Add(new Vector3(p.X + 0f, p.Y + 0f, p.Z + 0f));
        foreach (var i in indices)
            b.Indices.Add(i + baseVertex);
    }
}
