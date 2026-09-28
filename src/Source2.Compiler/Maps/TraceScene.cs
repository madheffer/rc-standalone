using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The ray trace scene visibility reads, built from the .vmap: the triangles
/// the collector takes (<see cref="MapGeometry.RteTriangles"/>), each with the
/// flag word the collector gives it (WRB_CollectRteMeshes, WRB_EmitRteTriangles):
/// the mesh entry's own bits, then the material's (<see cref="MaterialFlags"/>).
/// </summary>
public static class TraceScene
{
    /// <summary>One triangle and its flag word.</summary>
    public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, ushort Flags)
    {
        /// <summary>The face's material, for diagnostics.</summary>
        public string Material { get; init; } = "";

        /// <summary>The mesh node, for diagnostics.</summary>
        public int Node { get; init; }
    }

    /// <summary>
    /// The material's trace flags (Material_VisFlags), each an int attribute
    /// of the loaded material asked for by its token hash:
    /// 1 alphatest, translucent or bShaderBlended; 2 renderbackfaces;
    /// 8 NeedsDynamicShadows; 0x10 mapbuilder.visblocker; 0x20 mapbuilder.nodraw;
    /// 0x40 mapbuilder.blocklight; 0x100 tools.toolsmaterial;
    /// 0x200 tools.reference_notrace; 0x800 mapbuilder.detail; 0x1000 mapbuilder.sky.
    /// The loaded material answers its vmat_c int attributes and its shader's
    /// own attributes (<see cref="Physics.ShaderAttributes"/>: alphatest and
    /// translucent; bShaderBlended, renderbackfaces and NeedsDynamicShadows
    /// are not declared by any pixel shader probed and come from elsewhere,
    /// not read).
    /// </summary>
    public static ushort MaterialFlags(SettleWorld.MaterialInfo? info)
    {
        if (info is null)
            return 0;
        bool On(string key) => info.Ints.TryGetValue(key, out var v) && v != 0;
        ushort flags = 0;
        if (On("alphatest") || On("translucent") || On("bShaderBlended")
            || Physics.ShaderAttributes.AlphaTest(info.Shader, info.Params)
            || Physics.ShaderAttributes.Translucent(info.Shader, info.Params))
            flags |= 1;
        if (On("renderbackfaces"))
            flags |= 2;
        if (On("NeedsDynamicShadows"))
            flags |= 8;
        if (On("mapbuilder.detail"))
            flags |= 0x800;
        if (On("mapbuilder.sky"))
            flags |= 0x1000;
        if (On("mapbuilder.visblocker"))
            flags |= 0x10;
        if (On("mapbuilder.nodraw"))
            flags |= 0x20;
        if (On("mapbuilder.blocklight"))
            flags |= 0x40;
        if (On("tools.toolsmaterial"))
            flags |= 0x100;
        if (On("tools.reference_notrace"))
            flags |= 0x200;
        return flags;
    }

    /// <summary>
    /// The mesh entry's bits the collector puts under the material's. A mesh
    /// marked visexclude is not traced (0x800), measured on every excluded
    /// triangle of cardtest and probe01; the entry's other bits (0x80, 0x2000,
    /// 0xa000) are not ported and no specimen map's world meshes carry them.
    /// </summary>
    public static ushort EntryFlags(DmxBinary.Element? mesh)
        => mesh?.GetValue<bool>("visexclude") == true ? (ushort)0x800 : (ushort)0;

    /// <summary>The scene's triangles, in the collector's order.</summary>
    public static List<Triangle> Triangles(IReadOnlyList<MapMeshes.Mesh> meshes,
                                           Func<string, SettleWorld.MaterialInfo?> material,
                                           Func<string, MaterialVisFlags> visFlags, Func<string, bool> rendersAsWorld)
    {
        var found = new List<Triangle>();
        foreach (var mesh in meshes)
        {
            if (mesh.Hidden)
                continue;
            var entry = EntryFlags(mesh.Element);
            if (Subdivided(mesh) is { } baked)
            {
                if (mesh.ParentType == "CMapEntity" && !(mesh.ParentClass is { } c && rendersAsWorld(c)))
                    continue;
                foreach (var t in baked)
                    if (!visFlags(t.Material).LeftOutOfTrace)
                        found.Add(new Triangle(t.A, t.B, t.C, (ushort)(entry | MaterialFlags(material(t.Material)))) { Material = t.Material, Node = mesh.NodeId });
                continue;
            }
            foreach (var t in MapGeometry.RteTriangles([mesh], visFlags, rendersAsWorld))
                found.Add(new Triangle(t.A, t.B, t.C, (ushort)(entry | MaterialFlags(material(t.Material)))) { Material = t.Material, Node = mesh.NodeId });
        }
        return found;
    }

    /// <summary>
    /// A subdivided mesh's triangles as the builder's bake leaves them
    /// (<see cref="SubdivisionBake"/>, exact for world physics), each corner
    /// placed by the mesh's world matrix; null for a mesh with no subdivision.
    /// </summary>
    private static List<MapGeometry.Triangle>? Subdivided(MapMeshes.Mesh mesh)
    {
        var data = mesh.Element?.Get<DmxBinary.Element>("meshData");
        var levels = data?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels");
        if (data is null || levels is null || !levels.Any(x => x is int i && i > 0))
            return null;
        var faceData = (data.Get<object?[]>("faceDataIndices") ?? []).Select(x => x is int i ? i : 0).ToArray();
        var materials = (data.Get<DmxBinary.Element>("faceData")?.GetElements("streams")
            .FirstOrDefault(st => st.Name.StartsWith("materialindex", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [])
            .Select(x => x is int i ? i : 0).ToArray();
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var world = mesh.World;
        var scales = mesh.Scales;
        Vector3 Place(Vector3 local) => MapMeshes.Transform(world, local * scales);
        var cut = SubdivisionBake.Bake(data, Place);
        var triangles = new List<MapGeometry.Triangle>(cut.Faces.Count);
        for (var t = 0; t < cut.Faces.Count; t++)
        {
            var f = cut.Faces[t];
            var m = materials.Length == 0 ? 0 : materials[faceData[f]];
            var name = m >= 0 && m < names.Length ? names[m] : "";
            var (a, b, c) = (Place(cut.Positions[cut.Indices[t * 3]]), Place(cut.Positions[cut.Indices[(t * 3) + 1]]), Place(cut.Positions[cut.Indices[(t * 3) + 2]]));
            if (MapGeometry.Emitted(a, b, c))
                triangles.Add(new MapGeometry.Triangle(a, b, c, name));
        }
        return triangles;
    }

    /// <summary>The scene as visibility reads it.</summary>
    public static RayTraceEnvironment Environment(IReadOnlyList<Triangle> triangles)
        => RayTraceEnvironment.FromTriangles([.. triangles.Select(t => (t.A, t.B, t.C, t.Flags))]);
}
