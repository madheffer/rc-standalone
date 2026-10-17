using System.Globalization;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The files resourcecompiler writes under maps/&lt;map&gt;/lightmaps from
/// vrad3's output folder, after ProcessReceivedLightingPackets:
/// <list type="bullet">
/// <item>each lightmap channel's .vtex_c (<see cref="LightmapTextures.Vtex"/>) from
/// its EXR (irradiance, directional_irradiance, direct_light_shadows,
/// debug_chart_color from lightmap_chart_color.exr), when vrad3 wrote it;</item>
/// <item>lightmap_query_data.kv3 from lightmap_packing_geometry.dat
/// (<see cref="LightmapQueryData"/>), the uv scale from the lightmap's size;</item>
/// <item>with LPVAtlas, the probe volume atlases: the volumes in the script's
/// compute_lpv order, placed by <see cref="ProbeAtlas.Pack"/>, their
/// ambient cube and shadow images copied in (<see cref="LightProbeAtlasImages"/>)
/// and written as env_light_probe_volume_atlas (BC6H, six slices per layer)
/// and env_light_probe_volume_atlas_dlshd (BC7).</item>
/// </list>
/// probe01's seven files come out byte for byte (CpuBakeProbe.LightmapOutputsFiles).
/// </summary>
public static class LightmapOutputs
{
    private static readonly (string Exr, string Channel)[] Channels =
    [
        ("irradiance.exr", "irradiance"),
        ("directional_irradiance.exr", "directional_irradiance"),
        ("direct_light_shadows.exr", "direct_light_shadows"),
        ("lightmap_chart_color.exr", "debug_chart_color"),
    ];

    /// <param name="folder">vrad3's output folder (the EXRs, lightmap_packing_geometry.dat, script-gpu.vrad3).</param>
    /// <param name="map">The map's path under maps/, without extension.</param>
    /// <param name="addon">The addon, for the argument file's search path.</param>
    /// <param name="nativeFolder">The CS2 install's game/bin/win64 (ispc_texcomp.dll for BC6H).</param>
    /// <param name="resolutionVolumes">The map's lightmap resolution volumes (<see cref="WorldNodeBuild.ImportanceVolumes"/>).</param>
    public static List<MapCompile.Output> FromVrad3(string folder, string map, string addon, string nativeFolder, IReadOnlyList<Vector3[]> resolutionVolumes,
        bool lpvAtlas = true, IReadOnlyDictionary<string, LightmapTextures.ChannelSettings>? channels = null)
    {
        channels ??= LightmapTextures.Cs2Channels;
        var searchPath = $"csgo_addons/{addon}";
        var prefix = $"maps/{map}/lightmaps/".ToLowerInvariant();
        var outputs = new List<MapCompile.Output>();
        int width = 0, height = 0;
        foreach (var (exrName, channel) in Channels)
        {
            var path = Path.Combine(folder, exrName);
            if (!File.Exists(path) || !channels.TryGetValue(channel, out var settings))
                continue;
            var exr = Vrad3Exr.Read(File.ReadAllBytes(path));
            if (channel == "irradiance")
                (width, height) = (exr.Width, exr.Height);
            var bytes = LightmapTextures.Vtex(channel, LightmapTextures.Bitmap(exr), settings, searchPath, nativeFolder: nativeFolder);
            outputs.Add(new($"{prefix}{channel}.vtex_c", bytes));
        }

        var packing = Path.Combine(folder, "lightmap_packing_geometry.dat");
        if (File.Exists(packing) && width > 0)
            outputs.Add(new($"{prefix}lightmap_query_data.kv3",
                LightmapQueryData.Build(PackingGeometry.Read(File.ReadAllBytes(packing)), width, height, resolutionVolumes)));

        if (lpvAtlas && Volumes(Path.Combine(folder, "script-gpu.vrad3")) is { Count: > 0 } volumes)
        {
            var places = ProbeAtlas.Pack(volumes.Select(v => new ProbeAtlas.Place(v.X, v.Y, v.Z)).ToList());
            var list = volumes.Select((v, i) => new LightProbeAtlasImages.Volume(v.X, v.Y, v.Z, places[i],
                LightProbeAtlasImages.Rgba(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, $"lpv_{v.Id}_ambientcube.exr")))),
                LightProbeAtlasImages.Rgba(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(folder, $"lpv_{v.Id}_dlshd.exr")))))).ToList();
            var size = LightProbeAtlasImages.Size(list);
            var ambient = new RcTexture.RcMipChain.Level(size.X * size.Z, size.Y * 7, LightProbeAtlasImages.AmbientCube(size, list));
            var shadows = new RcTexture.RcMipChain.Level(size.X * size.Z, size.Y, LightProbeAtlasImages.Shadows(size, list));
            outputs.Add(new($"{prefix}env_light_probe_volume_atlas.vtex_c",
                LightmapTextures.AtlasVtex(ambient, size.X, size.Y, size.Z * 6, LightmapTextures.ImageFormat.Bc6h, searchPath, nativeFolder: nativeFolder, ambientCube: true)));
            outputs.Add(new($"{prefix}env_light_probe_volume_atlas_dlshd.vtex_c",
                LightmapTextures.AtlasVtex(shadows, size.X, size.Y, size.Z, LightmapTextures.ImageFormat.Bc7, searchPath, nativeFolder: nativeFolder)));
        }
        return outputs;
    }

    /// <summary>The script's compute_lpv routines in order: each volume's id and lpv_cells.</summary>
    public static List<(string Id, int X, int Y, int Z)> Volumes(string script)
    {
        var volumes = new List<(string, int, int, int)>();
        if (!File.Exists(script))
            return volumes;
        string? id = null;
        foreach (var raw in File.ReadLines(script))
        {
            var line = raw.Trim();
            if (line.StartsWith("compute_lpv_", StringComparison.Ordinal) && line.EndsWith(':'))
                id = line["compute_lpv_".Length..^1];
            else if (line.StartsWith("lpv_cells ", StringComparison.Ordinal) && id != null)
            {
                var c = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..].Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                volumes.Add((id, c[0], c[1], c[2]));
                id = null;
            }
        }
        return volumes;
    }
}
