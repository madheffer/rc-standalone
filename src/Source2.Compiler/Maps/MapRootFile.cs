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
    /// <param name="Baked">The compile baked lighting.</param>
    /// <param name="ProbeVolumes">The map has light probe volumes (the probe texture argument is read).</param>
    /// <param name="Arguments">The compile's arguments as MapCompileArgs types them.</param>
    public sealed record Compile(string Addon, IReadOnlyList<(string Path, uint Crc)> Inputs, bool Baked, bool ProbeVolumes,
                                 IReadOnlyDictionary<string, object> Arguments);

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

    /// <summary>The 22 arguments every map compile's steps read; a baked compile reads 11 more (and 2 with probe volumes).</summary>
    private static readonly string[] Unbaked =
    [
        "debugLightmapSets", "debugvisgeo", "disableLightingCalculations", "disablevis", "dumptris", "force_up_to_date_materials",
        "keep_vertices", "lightmapChartPackIterations", "lightmapCompressionDisabled", "lightmapMaxResolution", "lightmapMipCount",
        "lightmapNoWeld", "lightmapVRadQuality", "mapbuilder_entity_classname", "nobakedlighting", "noLightProbeVolumes", "noRepack",
        "norte", "noVolumetricFog", "skipauxfiles", "skipmapload",
    ];

    private static readonly string[] BakedExtra =
    [
        "debugColorClustering", "fast", "hueShiftFixup", "lightmapDisableFiltering", "lightmapMaxResolution#texture", "lightmapTextureArg",
        "maxmipsize", "meshletMaxTriangles", "meshletMaxVertices", "minmipsize", "vrad3LargeBlockSize",
    ];

    private static KVObject EditInfo(Compile compile, IReadOnlyList<string> children, IReadOnlyList<(string Type, string Name, int Count)> subassets)
    {
        var searchPath = $"csgo_addons/{compile.Addon}";
        var inputs = new List<(string Path, uint Crc, bool Optional, bool Exists)>();
        if (compile.Baked)
        {
            inputs.Add(("lightmaptexturearg.txt", 0, true, false));
            if (compile.ProbeVolumes)
                inputs.Add(("lightprobevolumetexturearg.txt", 0, true, false));
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

        // The arguments, case-insensitively sorted with ___OverrideInputData___ first.
        var names = new List<string>(Unbaked);
        if (compile.Baked)
        {
            names.AddRange(BakedExtra);
            if (compile.ProbeVolumes)
                names.AddRange(["final", "lightProbeVolumeTextureArg"]);
        }
        var args = KVObject.Array();
        args.Add(Argument("___OverrideInputData___", "BinaryBlobArg", 0, 0));
        foreach (var name in names.OrderBy(n => n.Split('#')[0], StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Contains('#')))
        {
            var plain = name.Split('#')[0];
            var (type, value, fallback) = plain switch
            {
                "lightmapTextureArg" or "lightProbeVolumeTextureArg" => ("BinaryBlobArg", 0u, 0u),
                "mapbuilder_entity_classname" => ("StringArg", 0u, 0u),
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
