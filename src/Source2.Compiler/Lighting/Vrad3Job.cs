using System.Globalization;
using System.Numerics;
using System.Text;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The job folder resourcecompiler hands vrad3 for a baked compile, built from the compile's own
/// state: scene_meshes.kv3 and scene_info.kv3 (WRB_BuildPathTraceSceneInfo 180285eb0),
/// lightmap_packing_geometry.dat, lightmap_info.dat and lightmap_seams.dat (BakeLighting), and
/// script-gpu.vrad3 (1801f26d3: the lightmap part 1801eda50, the probe volumes 1801ee0b0).
/// Each file is the one the matching test compares with Valve's (SceneInfoTests,
/// ChartMeshesTests); the script carries the bake's parameters, it is not shipped.
/// </summary>
public static class Vrad3Job
{
    /// <summary>The quality tables (resourcecompiler 182a88550..182a885c8), indexed by -lightmapVRadQuality.</summary>
    internal static readonly int[] LightmapMinSpp = [16, 128, 512], LightmapMaxSpp = [16, 512, 4096], LightmapMedian = [7, 7, 7];
    internal static readonly int[] LpvMinSpp = [64, 512, 1024], LpvMaxSpp = [64, 1024, 4096], LpvMedian = [3, 3, 3];
    internal static readonly float[] Error = [0.2f, 0.05f, 0.025f];
    internal static readonly bool[] Oidn = [false, true, true];

    /// <summary>One probe volume's compute_lpv routine: the volume's id, grid, local box and local-to-world (3 x 4, row major).</summary>
    public sealed record Volume(int Id, int X, int Y, int Z, Vector3 Mins, Vector3 Maxs, float[] LocalToWorld, bool Voxelize = true, bool FloodFill = true);

    /// <summary>
    /// Writes the job into <paramref name="folder"/>. <paramref name="lumps"/> are the compile's
    /// entity lumps (path, bytes) in the order they were written and <paramref name="packageModel"/>
    /// reads a compiled model the map built (maps/.../entities/*.vmdl), for the lightmapstatic brush entities.
    /// </summary>
    public static void Write(string folder, DmxBinary.Document document, GameContent content, ShaderLibrary shaders,
                             Func<string, bool> rendersAsWorld, IEnumerable<(string Path, byte[] Bytes)> lumps,
                             Func<string, byte[]?> packageModel, IReadOnlyList<Volume> volumes, int quality = 0)
    {
        if (quality is < 0 or > 2)
            throw new NotSupportedException($"-lightmapVRadQuality {quality}");
        if (Oidn[quality])
            throw new NotSupportedException($"-lightmapVRadQuality {quality} denoises with OIDN (lightmap_image_filter_oidn), which is not ported");
        Directory.CreateDirectory(folder);
        var world = WorldNodeBuild.WorldEntries(document, content, shaders, rendersAsWorld, buildBakes: true);
        var props = WorldNodeBuild.PropEntries(document, content, shaders, buildBakes: true);

        // The charts: BakeLighting's inputs as the node bake sees them (LightmapNodeBake).
        var keys = document.OfType("CMapWorld").FirstOrDefault()?.Get<DmxBinary.Element>("entity_properties")?.Attributes
            .Where(a => a.Value is string).ToDictionary(a => a.Key, a => (string)a.Value!) ?? [];
        var settings = LightmapSettings.From(keys, 0x1000, 4096, sh2Dc: false);
        var backfaces = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool RenderBackfaces(string m)
        {
            if (!backfaces.TryGetValue(m, out var v))
                backfaces[m] = v = content.Material(m) is { } info
                    && (MaterialAttributes.Of(info, shaders, content.TextureSize).Int(MeshEntryFlags.Key("renderbackfaces")) ?? 0) != 0;
            return v;
        }
        var entries = WorldNodeBuild.BeforeWeld(world, props).Select(LightmapNodeBake.ToEntry).ToList();
        var charted = new List<PackingAssembly.Mesh>();
        var (geometry, packed) = ChartMeshes.Build(entries, WorldNodeBuild.ImportanceVolumes(world, props), RenderBackfaces, settings, 120f, charted: charted);
        if (!packed)
            throw new NotSupportedException("the lightmap charts did not pack");
        File.WriteAllBytes(Path.Combine(folder, "lightmap_packing_geometry.dat"), geometry.Write());
        var blockSize = 0x400;
        File.WriteAllBytes(Path.Combine(folder, "lightmap_info.dat"),
                           LightmapTexels.Write(LightmapTexels.ChartIds(geometry, settings.Width, settings.Height, settings.Mip), settings.Width, settings.Height, blockSize));
        var side = settings.Width >> (settings.Mip - 1);
        File.WriteAllBytes(Path.Combine(folder, "lightmap_seams.dat"), LightmapSeams.Write(charted, LightmapSeams.Snap(geometry.AtlasUv, side)));

        // The path-trace scene: the world's meshes and instances, then the static models'.
        var meshes = PathTraceScene.WorldMeshes(world);
        var instances = PathTraceScene.WorldInstances(world);
        var lumpEntities = lumps.SelectMany(l => LumpEntities(l.Bytes, l.Path)).ToList();
        var records = new List<PathTraceProps.Prop>();
        var (_, mapEntities) = MapMeshes.ReadWithEntities(document);
        foreach (var ent in mapEntities)
        {
            if (ent.Hidden)
                continue;
            if (ent.Element.Type == "CMapSmartProp")
                throw new NotSupportedException("a smart prop's path-trace records are not ported");
            var props0 = ent.Element.Get<DmxBinary.Element>("entity_properties");
            if (props0?.Get<string>("classname") != "prop_static")
                continue;
            var prop = Physics.WorldCollision.PropOf(ent);
            if (PathTraceProps.Record(props0.Get<string>("model") ?? "", k => props0.Get<string>(k), PropTransform.Matrix(prop.Origin, prop.Angles, prop.Scales), true) is { } r)
                records.Add(r);
        }
        foreach (var (_, key) in lumpEntities)
            if (key("model") is { } model && model.StartsWith("maps/", StringComparison.Ordinal) && model.Contains("/entities/", StringComparison.Ordinal)
                && PathTraceProps.Record(model, key, PropTransform.Matrix(Vec(key("origin")), Vec(key("angles")), Vec(key("scales") ?? "1 1 1")), false) is { } record)
                records.Add(record);
        Model? Load(string name)
        {
            if (!name.StartsWith("maps/", StringComparison.Ordinal))
                return content.LoadedModel(name + "_c");
            if (packageModel(name + "_c") is not { } bytes)
                return null;
            var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            return (Model)resource.DataBlock!;
        }
        PathTraceProps.Add(meshes, instances, records, Load);

        // The lights and the sky, from the compiled lumps' keys.
        var ids = new Dictionary<int, int>();
        var lights = new List<KVObject>();
        LightPrecompute.KeyReader? sky = null;
        foreach (var (cls, key) in lumpEntities)
        {
            LightShape shape;
            if (cls == "light_barn")
                shape = SceneLights.Barn(key, LightPrecompute.World(Vec(key("origin")), Vec(key("angles"))));
            else if (cls is "light_environment" or "light_directional")
            {
                shape = SceneLights.Directional(key, Vec(key("origin")), Vec(key("angles")));
                if (cls == "light_environment")
                    sky ??= key;
            }
            else if (cls.StartsWith("light_", StringComparison.Ordinal))
                throw new NotSupportedException($"{cls} in the path-trace scene");
            else
                continue;
            if (SceneLights.Entry(key, shape, ids) is { } e)
                lights.Add(e);
        }
        (LightPrecompute.KeyReader, float[])? SkyNamed(string name)
        {
            foreach (var (cls, key) in lumpEntities)
                if (cls == "env_sky" && key("targetname") == name)
                {
                    var m = MapMeshes.AngleMatrix(Vec(key("angles")));
                    var o = Vec(key("origin"));
                    (m[3], m[7], m[11]) = (o.X, o.Y, o.Z);
                    return (key, m);
                }
            return null;
        }
        File.WriteAllBytes(Path.Combine(folder, "scene_meshes.kv3"), PathTraceScene.MeshesFile(meshes));
        File.WriteAllText(Path.Combine(folder, "scene_info.kv3"), PathTraceScene.InfoFile(instances, SceneLights.Ibl(sky, SkyNamed), lights));
        File.WriteAllText(Path.Combine(folder, "script-gpu.vrad3"), Script(settings.Width >> (settings.Mip - 1), settings.Height >> (settings.Mip - 1), blockSize, settings.Mip, volumes, quality));
    }

    /// <summary>
    /// script-gpu.vrad3 for an AHD lightmap at the default switches (no debug outputs, no
    /// ameliorate pass): the commands 1801eda50 and 1801ee0b0 print, with the quality's tables.
    /// </summary>
    public static string Script(int width, int height, int blockSize, int mips, IReadOnlyList<Volume> volumes, int quality = 0)
    {
        var c = CultureInfo.InvariantCulture;
        var s = new StringBuilder();
        s.Append("gpu_init\nlightmap_type ahd\nsceneinfo_gpu scene_info.kv3\n");
        s.Append(c, $"lightmap_image_gpu {width} {height} {blockSize}\n");
        s.Append("lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat\n\nrun compute_all_lightmap_blocks\nrun process_all_lightmap_blocks\n\n");
        s.Append("process_all_lightmap_blocks:\n");
        s.Append(c, $"    lightmap_image_consolidate_small_charts {mips}\n");
        s.Append("    lightmap_image_remove_fireflies 4.0 fireflies.exr\n");
        s.Append(c, $"    lightmap_image_filter_median {LightmapMedian[quality]}\n");
        s.Append("    lightmap_image_dilate_over_invalid_jfa\n    lightmap_image_encode_ahd\n");
        s.Append(c, $"    lightmap_image_fill_gutters {mips}\n");
        s.Append("    uber_weld lightmap_seams.dat\n");
        s.Append("    lightmap_image_write_ahd irradiance.exr directional_irradiance.exr\n");
        s.Append("    lightmap_image_write_direct_light_shadows direct_light_shadows.exr -indexlights 4 \n");
        s.Append(c, $"    lightmap_image_write_chart_color   lightmap_chart_color.exr {LightmapMinSpp[quality]} {LightmapMaxSpp[quality]}\n");
        s.Append("    done\n\ncompute_all_lightmap_blocks:\n    timer_start\n");
        var blocks = (width + blockSize - 1) / blockSize * ((height + blockSize - 1) / blockSize);
        for (var b = 0; b < blocks; b++)
        {
            s.Append(c, $"    lightmap_load_block_gpu lightmap_info.dat {b}\n");
            s.Append(c, $"    lightmap_compute_block_gpu {LightmapMinSpp[quality]} {LightmapMaxSpp[quality]} {Error[quality]:F3}\n");
        }
        s.Append("    timer_stop\n    timer_poll\n    println Lightmapping took $timer seconds\n    done\n\nlightmap_free_memory\n");
        if (volumes.Count == 0)
            return s.ToString();
        s.Append("\n//\n// Light Probe Volumes\n//\nlpv_type ambient_cube\n");
        foreach (var v in volumes)
            s.Append(c, $"run compute_lpv_{v.Id}\n");
        foreach (var v in volumes)
        {
            s.Append(c, $"\ncompute_lpv_{v.Id}:\n    lpv_cells {v.X} {v.Y} {v.Z}\n");
            s.Append(c, $"    lpv_box {v.Mins.X:F6} {v.Mins.Y:F6} {v.Mins.Z:F6} {v.Maxs.X:F6} {v.Maxs.Y:F6} {v.Maxs.Z:F6}\n");
            s.Append("    lpv_local_to_world " + string.Join(' ', v.LocalToWorld.Select(f => f.ToString("F6", c))) + "\n");
            s.Append("    lpv_init\n    lpv_image_gpu 96 96 96\n");
            if (v.Voxelize)
            {
                s.Append("    lpv_voxelize_gpu\n");
                if (v.FloodFill)
                    s.Append("    lpv_flood_fill\n");
            }
            for (var z = 0; z < v.Z; z += 96)
                for (var y = 0; y < v.Y; y += 96)
                    for (var x = 0; x < v.X; x += 96)
                        s.Append(c, $"    lpv_compute_block_gpu {x} {y} {z} {Math.Min(x + 96, v.X)} {Math.Min(y + 96, v.Y)} {Math.Min(z + 96, v.Z)} {LpvMinSpp[quality]} {LpvMaxSpp[quality]} {Error[quality]:F3}\n");
            s.Append(c, $"    lpv_image_ambient_occlusion\n    lpv_image_write_ambientcube lpv_{v.Id}_ambientcube_noisy.exr\n");
            s.Append(c, $"    lpv_image_remove_fireflies 3 lpv_{v.Id}_fireflies.exr\n    lpv_image_resolve \n    lpv_image_median_filter {LpvMedian[quality]}\n");
            s.Append(c, $"    lpv_image_write_ambientcube lpv_{v.Id}_ambientcube.exr\n    lpv_image_write_direct_light_shadows  lpv_{v.Id}_dlshd.exr\n    done\n");
        }
        return s.ToString();
    }

    /// <summary>A compiled lump's entities as key readers (strings as the entity system holds them), with their class names.</summary>
    public static List<(string Class, LightPrecompute.KeyReader Key)> LumpEntities(byte[] lump, string name)
    {
        using var resource = new Resource { FileName = name };
        resource.Read(new MemoryStream(lump));
        var data = (EntityLump)resource.DataBlock!;
        var list = new List<(string, LightPrecompute.KeyReader)>();
        foreach (var entity in data.Data.GetArray("m_entityKeyValues"))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values")?.Children ?? [])
                values[kv.Key] = Text(kv.Value);
            list.Add((values.GetValueOrDefault("classname", ""), k => values.TryGetValue(k, out var v) ? v : null));
        }
        return list;
    }

    private static string Text(KVObject v)
    {
        var c = CultureInfo.InvariantCulture;
        if (v.IsArray)
            return string.Join(" ", v.Values.Select(Text));
        return v.ValueType switch
        {
            KVValueType.Boolean => v.ToBoolean(c) ? "1" : "0",
            KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => ((float)v.ToDouble(c)).ToString("R", c),
            _ => v.ToString(c),
        };
    }

    private static Vector3 Vec(string? s)
    {
        var p = (s ?? "0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new(p[0], p[1], p[2]);
    }
}
