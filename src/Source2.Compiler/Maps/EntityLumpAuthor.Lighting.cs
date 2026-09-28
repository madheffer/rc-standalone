using System.Globalization;
using System.Text;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The keys the compile adds to cubemaps, light probe volumes and lights, in
/// the map preprocessing step (Map PreProcess) and in the lump export
/// (EntityLump_ExportNodeLate). Which of them appear depends on whether the
/// lighting is baked: the builder context's "baked lighting available" flag
/// (set when the world stage bakes, or an entities-only build finds lightmaps).
/// </summary>
public static partial class EntityLumpAuthor
{
    /// <summary>What the lighting keys are built against.</summary>
    /// <param name="MapPath">The map's content-relative path without its extension, forward slashes (<c>maps/cardtest</c>).</param>
    /// <param name="Baked">Whether baked lighting is available to this compile.</param>
    /// <param name="Handshake">Each probe or cubemap entity's handshake, in export order.</param>
    /// <param name="CubeIndex">Each cubemap-carrying entity's cube array index, in export order.</param>
    /// <param name="Atlas">Each probe volume's place in the probe atlas, when the atlas is packed.</param>
    public sealed record Lighting(string MapPath, bool Baked,
                                  IReadOnlyDictionary<MapEntities.Entity, int> Handshake,
                                  IReadOnlyDictionary<MapEntities.Entity, int> CubeIndex,
                                  IReadOnlyDictionary<MapEntities.Entity, Maps.ProbeAtlas.Place>? Atlas = null)
    {
        /// <summary>
        /// The counters over the entities in export order. The handshake starts
        /// at the map path's MurmurHash2 (seed 0x3501a674, lower case, back
        /// slashes, 31 bits, backed off by 0xff near the top) and takes one per
        /// cubemap or probe entity; the cube array index counts from 0 over the
        /// entities that carry a cubemap.
        /// </summary>
        /// <param name="packAtlas">Whether the probe atlas is packed: CS2's gameinfo sets
        /// LPVAtlas 1, and the builder packs it in an entities-only build
        /// (CWorldRendererBuilder_Build) and when it bakes lighting.</param>
        /// <param name="schema">The FGD, for each probe volume's grid.</param>
        public static Lighting For(string mapPath, bool baked, IEnumerable<MapEntities.Entity> exportOrder,
                                   bool packAtlas = false, FgdSchema? schema = null)
        {
            var bytes = Encoding.UTF8.GetBytes(mapPath.Replace('/', '\\'));
            var seed = Io.ResourceNames.Hash(bytes, bytes.Length, 0x3501a674) & 0x7fffffff;
            if (seed + 0xff > 0x7fffffff)
                seed -= 0xff;
            var handshake = new Dictionary<MapEntities.Entity, int>(ReferenceEqualityComparer.Instance);
            var cube = new Dictionary<MapEntities.Entity, int>(ReferenceEqualityComparer.Instance);
            var volumes = new List<MapEntities.Entity>();
            foreach (var e in exportOrder)
            {
                if (HandshakeClasses.Contains(e.ClassName))
                    handshake[e] = (int)seed + handshake.Count;
                if (CubemapClasses.Contains(e.ClassName))
                    cube[e] = cube.Count;
                if (ProbeVolumeClasses.Contains(e.ClassName))
                    volumes.Add(e);
            }
            // Each probe volume leaves a record in export order (180240a60) whose
            // sizes are its grid; the packer places them (1801f5a80).
            Dictionary<MapEntities.Entity, Maps.ProbeAtlas.Place>? atlas = null;
            if (packAtlas)
            {
                var places = Maps.ProbeAtlas.Pack([.. volumes.Select(v => ProbeGrid(KeyTable(v, schema)))
                                                         .Select(g => new Maps.ProbeAtlas.Place(g.X, g.Y, g.Z))]);
                atlas = new(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < volumes.Count; i++)
                    atlas[volumes[i]] = places[i];
            }
            return new Lighting(mapPath, baked, handshake, cube, atlas);
        }
    }

    private static readonly HashSet<string> HandshakeClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "env_cubemap", "env_cubemap_box", "env_light_probe_volume", "env_combined_light_probe_volume",
        "func_combined_light_probe_volume",
    };

    private static readonly HashSet<string> CubemapClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "env_cubemap", "env_cubemap_box", "env_combined_light_probe_volume", "func_combined_light_probe_volume",
    };

    private static readonly HashSet<string> ProbeVolumeClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "env_light_probe_volume", "env_combined_light_probe_volume", "func_combined_light_probe_volume",
    };

    /// <summary>The classes whose light description is the legacy one (Light_LegacyDescription).</summary>
    private static readonly HashSet<string> LegacyLights = new(StringComparer.OrdinalIgnoreCase)
    {
        "light_environment", "light_directional", "light_omni", "light_spot", "light_ortho", "light_capsule",
    };

    /// <summary>
    /// The preprocessing step's keys, added to the key table after the class's
    /// defaults (so they lead the exported entity):
    /// <list type="bullet">
    /// <item>bakeresource keys (FUN_180ef48e0): an empty one is filled with
    /// <c>_bakeresourcecache/&lt;map&gt;_baked/&lt;prefix&gt;_&lt;hammer id path&gt;.&lt;ext&gt;</c>,
    /// one with a value gets <c>custom&lt;key&gt;</c> "1";</item>
    /// <item>brightness (FUN_180eee150): a legacy light, whose description has no
    /// unit factors, gets brightness_lumens "0" and brightness_legacy
    /// "-nan(ind)"; a light whose brightness_units is outside 0 to 4 has no
    /// intensity, so brightness is -inf and the other unit keys 0.</item>
    /// </list>
    /// </summary>
    private static void Preprocess(MapEntities.Entity entity, List<KeyValuePair<string, string>> table, FgdSchema? schema,
                                   Lighting? lighting)
    {
        if (lighting is null)
            return;
        foreach (var r in schema?.BakeResourcesOf(entity.ClassName) ?? [])
        {
            var at = table.FindIndex(k => k.Key.Equals(r.Key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && table[at].Value.Length > 0)
            {
                Set(table, "custom" + r.Key, "1");
                continue;
            }
            var path = $"_bakeresourcecache/{lighting.MapPath}_baked/{r.Prefix}_{entity.IdPath.Replace(':', '_')}.{r.Extension}";
            Set(table, r.Key, path);
        }

        if (LegacyLights.Contains(entity.ClassName))
        {
            Set(table, "brightness_lumens", "0");
            Set(table, "brightness_legacy", "-nan(ind)");
        }
        else if (table.FirstOrDefault(k => k.Key.Equals("brightness_units", StringComparison.OrdinalIgnoreCase)).Value is { } units
                 && CNumbers.Atoi(units) is < 0 or > 4)
        {
            // No intensity: every unit key the class declares is 0 and brightness log2(0).
            Set(table, "brightness", "-inf");
            foreach (var key in new[] { "brightness_lumens", "brightness_nits", "brightness_candelas", "brightness_legacy" })
                if (table.Any(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    Set(table, key, "0");
        }
    }

    private static void Set(List<KeyValuePair<string, string>> table, string key, string value)
    {
        var at = table.FindIndex(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            table[at] = new(table[at].Key, value);
        else
            table.Add(new(key, value));
    }

    /// <summary>
    /// The export's keys, after hammerUniqueId (EntityLump_ExportNodeLate):
    /// handshake, the light probe grid (light_probe_size_x/y/z), the probe
    /// textures when lighting is baked (the atlas and its direct light
    /// shadows; the SH2 pair empty either way), the cubemap texture and its
    /// cube array index. The probe atlas position keys are the bake's and are
    /// not written here.
    /// </summary>
    private static void ExportLighting(MapEntities.Entity entity, KVObject values, List<KeyValuePair<string, string>> table,
                                       Lighting? lighting)
    {
        if (lighting is null)
            return;
        if (lighting.Handshake.TryGetValue(entity, out var handshake))
            values.Add("handshake", new KVObject(handshake));
        if (ProbeVolumeClasses.Contains(entity.ClassName))
        {
            var (x, y, z) = ProbeGrid(table);
            values.Add("light_probe_size_x", new KVObject(x));
            values.Add("light_probe_size_y", new KVObject(y));
            values.Add("light_probe_size_z", new KVObject(z));
            if (lighting.Baked)
            {
                Put(values, "lightprobetexture", ResourceValue($"{lighting.MapPath}/lightmaps/env_light_probe_volume_atlas.vtex"));
                Put(values, "lightprobetexture_dlshd", ResourceValue($"{lighting.MapPath}/lightmaps/env_light_probe_volume_atlas_dlshd.vtex"));
            }
            Put(values, "lightprobetexture_sh2_dc", ResourceValue(""));
            Put(values, "lightprobetexture_sh2_l1", ResourceValue(""));
        }
        if (lighting.CubeIndex.TryGetValue(entity, out var index)
            && !(values.ContainsKey("customcubemaptexture") && values["customcubemaptexture"] is { } custom && IsTrue(custom)))
        {
            Put(values, "cubemaptexture", ResourceValue($"{lighting.MapPath}/cubemaps/env_cubemap_array.vtex"));
            values.Add("array_index", Integer(index));
        }
    }

    /// <summary>
    /// The probe grid (FUN_18023ae00): per axis ceil((box_maxs - box_mins) /
    /// max(voxel_size, 1)); when the largest axis is over 128 each axis
    /// becomes axis * 128 / largest (integer division); with the probe atlas
    /// on (CS2's gameinfo LPVAtlas 1) every axis is at least 2.
    /// </summary>
    private static (int X, int Y, int Z) ProbeGrid(List<KeyValuePair<string, string>> table)
    {
        string Key(string name, string fallback)
            => table.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? fallback;
        var mins = CNumbers.FloatArray(Key("box_mins", "-72 -72 -72"), 3);
        var maxs = CNumbers.FloatArray(Key("box_maxs", "72 72 72"), 3);
        var voxel = MathF.Max(CNumbers.ToFloat32(Key("voxel_size", "12.0")), 1f);
        int Axis(int i) => (int)MathF.Ceiling((maxs[i] - mins[i]) / voxel);
        int x = Axis(0), y = Axis(1), z = Axis(2);
        var largest = Math.Max(x, Math.Max(y, z));
        if (largest > 128)
            (x, y, z) = (x * 128 / largest, y * 128 / largest, z * 128 / largest);
        return (Math.Max(x, 2), Math.Max(y, 2), Math.Max(z, 2));
    }

    /// <summary>
    /// The probe atlas keys, which the packer sets on the exported entity after
    /// the lump is built, so they follow every other key: light_probe_atlas_x,
    /// light_probe_atlas_Y and light_probe_atlas_Z, spelled so.
    /// </summary>
    private static void AtlasKeys(MapEntities.Entity entity, KVObject values, Lighting? lighting)
    {
        if (lighting?.Atlas is not { } atlas || !atlas.TryGetValue(entity, out var place))
            return;
        Put(values, "light_probe_atlas_x", Integer(place.X));
        Put(values, "light_probe_atlas_Y", Integer(place.Y));
        Put(values, "light_probe_atlas_Z", Integer(place.Z));
    }

    /// <summary>A key set where it stands, or added at the end.</summary>
    private static void Put(KVObject values, string key, KVObject value)
    {
        var existing = values.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            values[existing] = value;
        else
            values.Add(key, value);
    }

    private static KVObject ResourceValue(string path) => new(path) { Flag = KVFlag.ResourceName };

    private static bool IsTrue(KVObject value) => value.ToString() is "1" or "true" or "True";

    /// <summary>
    /// A legacy light's directlight when lighting is not baked (FUN_180240e48):
    /// any mode but 0 is rewritten to the Int32 2.
    /// </summary>
    private static bool RewritesDirectLight(MapEntities.Entity entity, Lighting? lighting)
        => lighting is { Baked: false } && LegacyLights.Contains(entity.ClassName);
}
