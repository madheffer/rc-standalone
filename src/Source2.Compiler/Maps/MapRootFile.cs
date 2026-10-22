using System.IO.Hashing;
using Source2.Compiler.Io;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// The .vmap_c, the root of a compiled map: a RERL of its children and the assets the map refers
/// to directly (<see cref="MapAssets"/>), a RED2, and an empty DATA. The RED2 is resourcecompiler's
/// edit info for the map compile: the inputs (the texture-argument files a baked compile asks for,
/// each prefab's map and the map, sorted), the arguments the compile's steps read, the identities
/// of the children's compilers, the child list, the searchable flags and the subasset table
/// (<see cref="MapSubassets"/>). Measured on s2c_rc_probe's 24 compiles (MapRootFileTests).
/// </summary>
public static class MapRootFile
{
    /// <summary>What the compile was.</summary>
    /// <param name="Addon">The addon (the inputs' search path is csgo_addons/&lt;addon&gt;).</param>
    /// <param name="Inputs">Each .vmap read (the map and its prefabs' maps) with its bytes' CRC32.</param>
    /// <param name="Baked">The compile produced lightmaps (their texture arguments are read).</param>
    /// <param name="ProbeVolumes">The map has light probe volumes (the probe texture argument is read).</param>
    /// <param name="Arguments">The compile's arguments as MapCompileArgs types them.</param>
    /// <param name="Steps">The builders that ran (MapCompileArgs.Builders names); null for a full build.</param>
    /// <param name="FlattenProps">A static model record is made (<see cref="ReadsStaticModelRecords"/>).</param>
    /// <param name="EntitiesOnly">An Only Entities build (MapCompileArgs.EntitiesOnly): no bake decision is read.</param>
    /// <param name="EntityRenderModels">The build compiles an entity model with a render mesh, or one that fails on a
    /// missing material (<see cref="ReadsMeshArguments"/>).</param>
    public sealed record Compile(string Addon, IReadOnlyList<(string Path, uint Crc)> Inputs, bool Baked, bool ProbeVolumes,
                                 IReadOnlyDictionary<string, object> Arguments, IReadOnlySet<string>? Steps = null,
                                 bool FlattenProps = false, bool EntitiesOnly = false, bool EntityRenderModels = false)
    {
        public bool Runs(string step) => Steps is null ? step is "world" or "phys" or "vis" or "bakedlighting" : Steps.Contains(step);
    }

    /// <summary>
    /// Whether EntityLump_StaticModelRecord (0923 180245770) runs: for every visible prop_static and smart
    /// prop model (180244d30) and every brush entity whose model has lightmapstatic set (180240a60; its
    /// other conditions are not read). Without baked lighting it reads flattenPropsWithBakedLighting.
    /// </summary>
    public static bool ReadsStaticModelRecords(DmxBinary.Document document)
    {
        var (_, entities) = MapMeshes.ReadWithEntities(document);
        foreach (var ent in entities)
        {
            if (ent.Hidden)
                continue;
            if (ent.Element.Type == "CMapSmartProp")
                return true;
            var props = ent.Element.Get<DmxBinary.Element>("entity_properties");
            if (props?.Get<string>("classname") == "prop_static")
                return true;
            if (props?.Get<string>("lightmapstatic") is { Length: > 0 } v && CNumbers.Atoi(v) != 0 && HasMesh(ent.Element))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the entity models' compiles read the mesh arguments (ModelCompile_ReadMeshArgs, 0923 1802f9cc0):
    /// a model with a render mesh does, and so does one that fails on a missing material (cardtest's impmodel
    /// reads them first). force_up_to_date_materials is given to no context, so its row goes up to the map;
    /// a physics-only model reads only keep_vertices and its classname, which its own context is given
    /// (capture_argdeps.py --trace on Valve's Only Entities compiles of probe01 and cardtest, ledger 119).
    /// </summary>
    public static bool ReadsMeshArguments(IEnumerable<Physics.EntityPhysicsModels.Model> models, IReadOnlyCollection<string> failed)
        => failed.Count > 0 || models.Any(m => !m.PhysicsOnly);

    // A brush entity: a CMapMesh among its children (the model it gets).
    private static bool HasMesh(DmxBinary.Element element)
        => element.GetElements("children").Any(c => c.Type == "CMapMesh" || HasMesh(c));

    public static uint Crc(byte[] bytes) => Crc32.HashToUInt32(bytes);

    /// <summary>The content-relative maps every loaded prefab under the document reads (MapPrefabs.Attach), depth first.</summary>
    public static IEnumerable<string> PrefabMaps(DmxBinary.Document document)
    {
        foreach (var p in document.OfType("CMapPrefab"))
            if (p.Get<string>("targetMapPath") is { Length: > 0 } path && p.Attributes.TryGetValue(MapPrefabs.DocumentKey, out var loaded)
                && loaded is DmxBinary.Document inner)
            {
                yield return path;
                foreach (var nested in PrefabMaps(inner))
                    yield return nested;
            }
    }

    public static byte[] Write(Compile compile, IReadOnlyList<string> children, IEnumerable<string> assets,
                               IReadOnlyList<(string Type, string Name, int Count)> subassets)
    {
        var childList = children.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var references = childList.Concat(assets).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var generic = KV3IDLookup.Get("generic");
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, 1);
        var rerl = new ResourceExtRefList { Resource = resource };
        foreach (var path in references)
            rerl.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo { Id = Source2ResourceId.ForPath(path), Name = path });
        resource.Blocks.Add(rerl);
        resource.Blocks.Add(AuthoredKv3.Block(EditInfo(compile, childList, subassets), generic, BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, [], resource));
        return ValveLayout.Serialize(resource);
    }

    // The arguments a compile's steps read (ArgumentDependencies records each one read), by step. Readers
    // in resourcecompiler 0923: skipmapload 1800fd550, skipauxfiles 180244d30, force_up_to_date_materials
    // 1802f9cc0; debugColorClustering CVisibilityMeshMerger_MergeMeshes and meshletMax* the node models'
    // mesh build (18025fab0), so only with world node models; keep_vertices and mapbuilder_entity_classname
    // the physics compile; norte CWorldRendererBuilder_Build when vis is on (CMapBuilderContext vfunc 0xe8,
    // the builder table at +0x609, index 2) or the context's +0x605 flag (vfunc 0x100, not read yet), then
    // debugvisgeo and dumptris in CreateRayTracingEnvironment, disablevis in the vis builder;
    // flattenPropsWithBakedLighting 180245770 when the compile does not bake (vfunc 0x108), and bakelighting
    // when it does not. Measured on 56 Valve compiles (world only, world+phys+vis+nav, full; ledger 119).
    private static readonly string[] Always = ["force_up_to_date_materials", "skipauxfiles", "skipmapload"];
    private static readonly string[] WorldNodeModels = ["debugColorClustering", "meshletMaxTriangles", "meshletMaxVertices"];
    private static readonly string[] Physics = ["keep_vertices", "mapbuilder_entity_classname"];
    private static readonly string[] Visibility = ["debugvisgeo", "disablevis", "dumptris", "norte"];

    private static readonly string[] Lighting =
    [
        "debugLightmapSets", "disableLightingCalculations", "lightmapChartPackIterations", "lightmapCompressionDisabled",
        "lightmapMaxResolution", "lightmapMipCount", "lightmapNoWeld", "lightmapVRadQuality", "nobakedlighting",
        "noLightProbeVolumes", "noRepack", "noVolumetricFog",
    ];

    // The lightmaps' texture compiles read these (only when there are lightmaps).
    private static readonly string[] LightmapTextures =
    [
        "fast", "hueShiftFixup", "lightmapDisableFiltering", "lightmapMaxResolution#texture", "lightmapTextureArg",
        "maxmipsize", "minmipsize", "vrad3LargeBlockSize",
    ];

    // The order a compile's steps first read each argument: the recorder (0923 180174f90) appends a row the
    // context has not seen, and the edit info writer (18016c650) std::sorts that list (18017a9c0) by
    // V_stricmp_fast on the name, so the two lightmapMaxResolution rows keep whatever order the sort leaves
    // them in. Captured with tools/re/capture_argdeps.py (s2c_l5probe and s2c_l5farfield128 full,
    // probe01 -world -phys -vis -nav; ledger 119). flattenPropsWithBakedLighting's place is not captured; it
    // ties with no other name, so it cannot move a row.
    private static readonly string[] ReadOrder =
    [
        "___OverrideInputData___", "skipmapload", "nobakedlighting", "bakelighting", "skipauxfiles", "flattenPropsWithBakedLighting",
        "norte", "dumptris", "debugvisgeo", "disablevis",
        "disableLightingCalculations", "lightmapMaxResolution", "lightmapMipCount", "lightmapNoWeld", "lightmapVRadQuality",
        "lightmapCompressionDisabled", "noVolumetricFog", "noLightProbeVolumes", "debugLightmapSets", "lightmapChartPackIterations",
        "noRepack", "lightmapDisableFiltering", "vrad3LargeBlockSize",
        "minmipsize", "maxmipsize", "fast", "final", "hueShiftFixup", "lightmapMaxResolution#texture",
        "lightProbeVolumeTextureArg", "fogIrradianceVolumeTextureArg", "farfieldIndirectionTextureArg", "farfieldAtlasTextureArg",
        "lightmapTextureArg",
        "debugColorClustering", "meshletMaxVertices", "meshletMaxTriangles", "force_up_to_date_materials",
        "keep_vertices", "mapbuilder_entity_classname",
    ];

    private static KVObject EditInfo(Compile compile, IReadOnlyList<string> children, IReadOnlyList<(string Type, string Name, int Count)> subassets)
    {
        var searchPath = $"csgo_addons/{compile.Addon}";
        bool Child(string suffix) => children.Any(c => c.Contains("/lightmaps/", StringComparison.Ordinal) && c.EndsWith(suffix, StringComparison.Ordinal));
        var fog = compile.Baked && Child("/fog_irradiance_volume.vtex");
        var farfield = compile.Baked && Child("_indirection.vtex");
        var inputs = new List<(string Path, uint Crc, bool Optional, bool Exists)>();
        if (compile.Baked)
        {
            inputs.Add(("lightmaptexturearg.txt", 0, true, false));
            if (compile.ProbeVolumes)
                inputs.Add(("lightprobevolumetexturearg.txt", 0, true, false));
            if (fog)
                inputs.Add(("fogirradiancevolumetexturearg.txt", 0, true, false));
            if (farfield)
            {
                inputs.Add(("farfieldatlastexturearg.txt", 0, true, false));
                inputs.Add(("farfieldindirectiontexturearg.txt", 0, true, false));
            }
        }
        inputs.AddRange(compile.Inputs.Select(i => (i.Path, i.Crc, false, true)));
        var inputArray = KVObject.Array();
        foreach (var i in inputs.OrderBy(i => i.Path, StringComparer.Ordinal))
        {
            var d = KVObject.Collection();
            d.Add("m_RelativeFilename", new KVObject(i.Path));
            d.Add("m_SearchPath", new KVObject(searchPath));
            d.Add("m_nFileCRC", new KVObject(i.Crc));
            d.Add("m_bOptional", new KVObject(i.Optional));
            d.Add("m_bFileExists", new KVObject(i.Exists));
            d.Add("m_bIsGameFile", new KVObject(false));
            inputArray.Add(d);
        }

        // The arguments in read order, then MSVC's std::sort by V_stricmp_fast (ReadOrder).
        // An Only Entities build reads force_up_to_date_materials only through its entity models (ReadsMeshArguments).
        var names = new List<string>(compile.EntitiesOnly && !compile.EntityRenderModels ? Always.Skip(1) : Always);
        if (children.Any(c => c.Contains("/worldnodes/", StringComparison.Ordinal) && c.EndsWith(".vmdl", StringComparison.Ordinal)))
            names.AddRange(WorldNodeModels);
        if (compile.Runs("phys"))
            names.AddRange(Physics);
        if (compile.Runs("vis"))
            names.AddRange(Visibility);
        if (compile.Runs("bakedlighting"))
        {
            names.AddRange(Lighting);
            if (compile.Baked)
            {
                names.AddRange(LightmapTextures);
                if (compile.ProbeVolumes)
                    names.AddRange(["final", "lightProbeVolumeTextureArg"]);
                if (fog)
                    names.Add("fogIrradianceVolumeTextureArg");
                if (farfield)
                    names.AddRange(["farfieldAtlasTextureArg", "farfieldIndirectionTextureArg"]);
            }
        }
        else if (!compile.EntitiesOnly)
        {
            names.Add("bakelighting");
            if (compile.FlattenProps)
                names.Add("flattenPropsWithBakedLighting");
        }
        names.Add("___OverrideInputData___");
        var rows = names.OrderBy(n => Array.IndexOf(ReadOrder, n) is var i and >= 0 ? i
                                     : throw new InvalidOperationException($"{n} has no read position")).ToArray();
        MsvcSort.Sort(rows, (a, b) => Tier0Strings.StricmpFast(a.Split('#')[0], b.Split('#')[0]) < 0);
        var args = KVObject.Array();
        foreach (var name in rows)
        {
            var plain = name.Split('#')[0];
            var (type, value, fallback) = plain switch
            {
                "___OverrideInputData___" or "lightmapTextureArg" or "lightProbeVolumeTextureArg" or "fogIrradianceVolumeTextureArg"
                    or "farfieldAtlasTextureArg" or "farfieldIndirectionTextureArg" => ("BinaryBlobArg", 0u, 0u),
                "mapbuilder_entity_classname" => ("StringArg", 0u, 0u),
                // A switch given bare (-skipauxfiles) is an int 1 (Valve's Fast and Only Entities compiles, 10-22).
                _ when !name.Contains('#') && compile.Arguments.TryGetValue(plain, out var given) && given is int i => ("IntArg", (uint)i, FloatBits(null, name)),
                _ => ("FloatArg", FloatBits(compile.Arguments, name), FloatBits(null, name)),
            };
            args.Add(Argument(plain, type, value, fallback));
        }

        var special = KVObject.Array();
        foreach (var dep in Identities(children))
        {
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(dep.Name));
            d.Add("m_CompilerIdentifier", new KVObject(dep.Compiler));
            d.Add("m_nFingerprint", new KVObject((uint)dep.Fingerprint));
            d.Add("m_nUserData", new KVObject((uint)dep.UserData));
            special.Add(d);
        }
        var childArray = KVObject.Array();
        foreach (var c in children)
            childArray.Add(new KVObject(c));
        var user = KVObject.Collection();
        user.Add("IsChildResource", new KVObject(0L));
        user.Add("UsesBuilderEntityKeys", new KVObject(1L));
        user.Add("WorldModelDocAll", new KVObject(1L));

        var root = KVObject.Collection();
        root.Add("m_InputDependencies", inputArray);
        root.Add("m_AdditionalInputDependencies", KVObject.Array());
        root.Add("m_ArgumentDependencies", args);
        root.Add("m_SpecialDependencies", special);
        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());
        root.Add("m_ChildResourceList", childArray);
        root.Add("m_WeakReferenceList", KVObject.Array());
        root.Add("m_SearchableUserData", user);
        if (subassets.Count == 0)
            root.Add("m_SubassetReferences", KVObject.Null());
        else
        {
            var refs = KVObject.Collection();
            foreach (var group in subassets.GroupBy(s => s.Type))
            {
                var t = KVObject.Collection();
                foreach (var s in group)
                    t.Add(s.Name, new KVObject(s.Count));
                refs.Add(group.Key, t);
            }
            root.Add("m_SubassetReferences", refs);
        }
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return root;
    }

    private static KVObject Argument(string name, string type, uint fingerprint, uint fingerprintDefault)
    {
        var a = KVObject.Collection();
        a.Add("m_ParameterName", new KVObject(name));
        a.Add("m_ParameterType", new KVObject(type));
        a.Add("m_nFingerprint", new KVObject(fingerprint));
        a.Add("m_nFingerprintDefault", new KVObject(fingerprintDefault));
        return a;
    }

    /// <summary>A FloatArg's fingerprint, the float bits of its value (given, else the compile's default).</summary>
    private static uint FloatBits(IReadOnlyDictionary<string, object>? given, string name)
    {
        var plain = name.Split('#')[0];
        // The texture compile's own lightmapMaxResolution row reads no value (0).
        if (name.EndsWith("#texture", StringComparison.Ordinal))
            return 0;
        if (given is not null && given.TryGetValue(plain, out var v))
            return BitConverter.SingleToUInt32Bits(v switch { float f => f, int i => i, _ => 0f });
        return plain switch
        {
            "lightmapChartPackIterations" => BitConverter.SingleToUInt32Bits(4f),
            "lightmapMaxResolution" => BitConverter.SingleToUInt32Bits(4096f),
            "lightmapMipCount" => BitConverter.SingleToUInt32Bits(1f),
            "maxmipsize" => BitConverter.SingleToUInt32Bits(1048576f),
            "meshletMaxTriangles" or "meshletMaxVertices" => BitConverter.SingleToUInt32Bits(-1f),
            _ => 0,
        };
    }

    /// <summary>The identities each kind of child brings, sorted by name (ordinal).</summary>
    private static IEnumerable<(string Name, string Compiler, int Fingerprint, int UserData)> Identities(IReadOnlyList<string> children)
    {
        var deps = new List<(string, string, int, int)>
        {
            ("Map Compiler Version", "CompileMap", 2, 0),
            ("Manifest Compiler Version", "CompileResourceManifest", 2, 0),
        };
        foreach (var kind in children.Select(Path.GetExtension).Distinct(StringComparer.OrdinalIgnoreCase))
            switch (kind)
            {
                case ".vents": deps.Add(("Entity Lump Compiler Version", "CompileEntityLump", 3, 0)); break;
                case ".vdata":
                    deps.Add(("KV3 Compiler Version", "CompileVData", 2, 0));
                    deps.Add(("VData Compiler Version", "CompileVData", 1, 0));
                    break;
                case ".vmdl": deps.Add(("ModelDoc Compiler Version", "CompileModel", 3, 0)); break;
                case ".vsnap": deps.Add(("SNAP Compiler Version", "CompileSnap", 1, 0)); break;
                case ".vwrld": deps.Add(("World Compiler Version", "CompileWorld", 1, 0)); break;
                case ".vwnod": deps.Add(("World Node Compiler Version", "CompileWorldNode", 1, 0)); break;
                case ".vtex":
                    deps.Add(("Texture Compiler Version", "CompileTexture", 11, 0));
                    deps.Add(("Texture Compiler Version Mip None", "CompileTexture", 1, 0));
                    deps.Add(("Texture Encode Quality", "CompileTexture", 1, 3));
                    break;
            }
        return deps.OrderBy(d => d.Item1, StringComparer.Ordinal);
    }
}
