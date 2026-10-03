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

        /// <summary>The mesh face it comes from, -1 when not known.</summary>
        public int Face { get; init; } = -1;
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
        // The material system answers F_RENDER_BACKFACES as renderbackfaces
        // (atixref's hr_metal_grating_002: every triangle carries 0x2).
        if (On("renderbackfaces") || (info.Params.TryGetValue("F_RENDER_BACKFACES", out var backfaces) && backfaces != 0))
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
    /// The mesh entry's bits the collector puts under the material's
    /// (WRB_CollectRteMeshes). A mesh marked visexclude is not traced (0x800),
    /// measured on every excluded triangle of cardtest and probe01. The shadow
    /// bits come from the entry's shadow mode (WRB_MeshEntryFlags: the mesh's
    /// <c>disableShadows</c>, forced to 1 by the material's DoNotCastShadows):
    /// mode 1 sets entry bits 0x30000 and the word 0xa000, mode 2 bit 0x10000
    /// and 0x2000, mode 3 bit 0x20000 alone and nothing. Measured for mode 1
    /// (atixref and Mako: disableShadows 1, F_DO_NOT_CAST_SHADOWS, water);
    /// modes 2 and 3 are read, no specimen has them. The entry's 0x80 (object
    /// flag 0x80) is not ported; no specimen's world meshes carry it.
    /// </summary>
    public static ushort EntryFlags(DmxBinary.Element? mesh, SettleWorld.MaterialInfo? material)
    {
        ushort flags = mesh?.GetValue<bool>("visexclude") == true ? (ushort)0x800 : (ushort)0;
        var mode = mesh?.GetValue<int>("disableShadows") ?? 0;
        if (material is not null && DoNotCastShadows(material))
            mode = 1;
        return (ushort)(flags | mode switch { 1 => 0xa000, 2 => 0x2000, _ => 0 });
    }

    /// <summary>
    /// The material's DoNotCastShadows: its own int attribute, its
    /// F_DO_NOT_CAST_SHADOWS, or a shader that declares it (of the csgo
    /// shaders only csgo_water_fancy does, on every combo).
    /// </summary>
    public static bool DoNotCastShadows(SettleWorld.MaterialInfo material)
        => (material.Ints.TryGetValue("DoNotCastShadows", out var v) && v != 0)
           || (material.Params.TryGetValue("F_DO_NOT_CAST_SHADOWS", out var f) && f != 0)
           || Path.GetFileNameWithoutExtension(material.Shader).Equals("csgo_water_fancy", StringComparison.OrdinalIgnoreCase);

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
            var start = found.Count;
            // The emitter writes a mesh's triangles as the exported mesh holds
            // them: one face set per (lightmap scale bias, material), biases
            // ascending, materials in the mesh's materials array order (as
            // MapMeshCorners cuts pieces), face order kept within a set.
            var meshData = mesh.Element?.Get<DmxBinary.Element>("meshData");
            var faceData = (meshData?.Get<object?[]>("faceDataIndices") ?? []).Select(x => x is int i ? i : 0).ToArray();
            var biases = (meshData?.Get<DmxBinary.Element>("faceData")?.GetElements("streams")
                .FirstOrDefault(st => st.Name.StartsWith("lightmapScaleBias", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [])
                .Select(x => x is int i ? i : 0).ToArray();
            int Bias(int face) => face >= 0 && face < faceData.Length && faceData[face] < biases.Length ? biases[faceData[face]] : 0;
            void GroupByMaterial()
            {
                var names = (meshData?.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToList();
                int Rank(string m) => names.FindIndex(n => n.Equals(m, StringComparison.OrdinalIgnoreCase)) is var r and >= 0 ? r : names.Count;
                var grouped = found.Skip(start).Select((t, i) => (t, i)).OrderBy(x => Bias(x.t.Face)).ThenBy(x => Rank(x.t.Material ?? "")).ThenBy(x => x.i).Select(x => x.t).ToList();
                found.RemoveRange(start, found.Count - start);
                found.AddRange(grouped);
            }
            ushort Flags(string m)
            {
                var info = material(m);
                return (ushort)(EntryFlags(mesh.Element, info) | MaterialFlags(info));
            }
            if (Subdivided(mesh) is { } baked)
            {
                if (mesh.ParentType == "CMapEntity" && !(mesh.ParentClass is { } c && rendersAsWorld(c)))
                    continue;
                foreach (var t in baked)
                    if (!visFlags(t.Material).LeftOutOfTrace)
                        found.Add(new Triangle(t.A, t.B, t.C, Flags(t.Material)) { Material = t.Material, Node = mesh.NodeId, Face = t.Face });
                GroupByMaterial();
                continue;
            }
            foreach (var t in MapGeometry.RteTriangles([mesh], visFlags, rendersAsWorld))
                found.Add(new Triangle(t.A, t.B, t.C, Flags(t.Material)) { Material = t.Material, Node = mesh.NodeId, Face = t.Face });
            GroupByMaterial();
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
                triangles.Add(new MapGeometry.Triangle(a, b, c, name, f));
        }
        return triangles;
    }

    /// <summary>The scene as visibility reads it, each triangle's id its material's resource id.</summary>
    public static RayTraceEnvironment Environment(IReadOnlyList<Triangle> triangles)
        => RayTraceEnvironment.FromTriangles([.. triangles.Select(t => (t.A, t.B, t.C, t.Flags, Source2ResourceId.ForPath(t.Material)))]);
}
