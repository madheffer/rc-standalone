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
/// (<see cref="LightmapQueryData"/>), the uv scale from the side left after the mip
/// and MinSpecLightmapSize halvings;</item>
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

    /// <summary>
    /// The map compile's lighting switches BakeLighting reads (f_atlas_caller: lightmapMipCount,
    /// lightmapVRadQuality, lightmapCompressionDisabled; gameinfo MinSpecLightmapSize and LPVAtlas)
    /// and the command-line arguments the texture compiler records.
    /// </summary>
    public sealed record Options(int MipCount = 1, int Quality = 0, bool Compressed = true, int MinSpecLightmapSize = 4096,
        bool LpvAtlas = true, IReadOnlyDictionary<string, object>? CompileArguments = null, bool NoRepack = false)
    {
        public static readonly Options Default = new();

        /// <summary>From a compile's arguments, as BakeLighting reads them (GetInt / GetBool through the processor's settings).</summary>
        public static Options From(IReadOnlyDictionary<string, object> arguments, int minSpec = 4096, bool lpvAtlas = true)
        {
            int Int(string name, int fallback) => arguments.TryGetValue(name, out var v) ? v switch { int i => i, float f => (int)f, _ => fallback } : fallback;
            return new Options(Int("lightmapMipCount", 1), Int("lightmapVRadQuality", 0), Int("lightmapCompressionDisabled", 0) == 0, minSpec, lpvAtlas, arguments,
                               Int("noRepack", 0) != 0 || Int("disableLightingCalculations", 0) != 0);
        }
    }

    /// <param name="folder">vrad3's output folder (the EXRs, lightmap_packing_geometry.dat, script-gpu.vrad3).</param>
    /// <param name="map">The map's path under maps/, without extension.</param>
    /// <param name="addon">The addon, for the argument file's search path.</param>
    /// <param name="nativeFolder">The CS2 install's game/bin/win64 (ispc_texcomp.dll for BC6H).</param>
    /// <param name="resolutionVolumes">The map's lightmap resolution volumes (<see cref="WorldNodeBuild.ImportanceVolumes"/>).</param>
    /// <param name="options">The compile's lighting switches (<see cref="Options.From"/>).</param>
    /// <param name="note">Told about anything written short of Valve's (the BC6H hue-shift fixup).</param>
    public static List<MapCompile.Output> FromVrad3(string folder, string map, string addon, string nativeFolder, IReadOnlyList<Vector3[]> resolutionVolumes,
        Options? options = null, IReadOnlyDictionary<string, LightmapTextures.ChannelSettings>? channels = null, Action<string>? note = null)
    {
        options ??= Options.Default;
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
            // The atlas is square; BakeLighting's mip count comes from its side.
            var (mips, _, _) = LightmapSettings.MipChain(exr.Width, options.MipCount, options.MinSpecLightmapSize);
            var serialization = new LightmapTextures.Serialization(mips, options.Quality, options.Compressed, options.CompileArguments);
            var bytes = LightmapTextures.Vtex(channel, LightmapTextures.Bitmap(exr), settings, searchPath, options: serialization, nativeFolder: nativeFolder, note: note);
            outputs.Add(new($"{prefix}{channel}.vtex_c", bytes));
        }

        var packing = Path.Combine(folder, "lightmap_packing_geometry.dat");
        if (File.Exists(packing) && width > 0)
        {
            // The uv scale is from the side left after the mip and MinSpec halvings (180218096).
            var (_, w, h) = LightmapSettings.MipChain(width, options.MipCount, options.MinSpecLightmapSize);
            var geometry = PackingGeometry.Read(File.ReadAllBytes(packing));
            // At quality 1 and 2 vrad3 repacks charts; BakeLighting moves the uvs the query data sees.
            var repacking = Path.Combine(folder, "chart_repacking.dat");
            if (LightmapChartRepacking.RepacksAt(options.Quality) && !options.NoRepack && File.Exists(repacking)
                && LightmapChartRepacking.Read(File.ReadAllBytes(repacking), geometry.Charts.Length) is { } repack)
                geometry = geometry.WithAtlasUv(repack.Apply(geometry));
            outputs.Add(new($"{prefix}lightmap_query_data.kv3", LightmapQueryData.Build(geometry, w, h, resolutionVolumes)));
        }

        if (options.LpvAtlas && Volumes(Path.Combine(folder, "script-gpu.vrad3")) is { Count: > 0 } volumes)
        {
            var (places, size) = ProbeAtlas.PackWithSize(volumes.Select(v => new ProbeAtlas.Place(v.X, v.Y, v.Z)).ToList());
            // A volume image that does not load (missing, or without R, G or B) fails the atlas
            // builders (1801f64f0 / 1801f69c0 return 0) and 1801eee20 writes no probe volume texture.
            float[]? Load(string name)
            {
                var path = Path.Combine(folder, name);
                return File.Exists(path) ? LightProbeAtlasImages.Rgba(Vrad3Exr.Read(File.ReadAllBytes(path))) : null;
            }
            var images = volumes.Select(v => (Ambient: Load($"lpv_{v.Id}_ambientcube.exr"), Shadows: Load($"lpv_{v.Id}_dlshd.exr"))).ToList();
            if (images.Any(i => i.Ambient is null || i.Shadows is null))
            {
                note?.Invoke("a light probe volume image is missing or has no R, G or B: no probe volume atlas (as resourcecompiler)");
                return outputs;
            }
            var list = volumes.Select((v, i) => new LightProbeAtlasImages.Volume(v.X, v.Y, v.Z, places[i], images[i].Ambient!, images[i].Shadows!)).ToList();
            var ambient = new RcTexture.RcMipChain.Level(size.X * size.Z, size.Y * 7, LightProbeAtlasImages.AmbientCube(size, list));
            var shadows = new RcTexture.RcMipChain.Level(size.X * size.Z, size.Y, LightProbeAtlasImages.Shadows(size, list));
            outputs.Add(new($"{prefix}env_light_probe_volume_atlas.vtex_c",
                LightmapTextures.AtlasVtex(ambient, size.X, size.Y, size.Z * 6, LightmapTextures.ImageFormat.Bc6h, searchPath, nativeFolder: nativeFolder, ambientCube: true,
                                           compileArguments: options.CompileArguments)));
            outputs.Add(new($"{prefix}env_light_probe_volume_atlas_dlshd.vtex_c",
                LightmapTextures.AtlasVtex(shadows, size.X, size.Y, size.Z, LightmapTextures.ImageFormat.Bc7, searchPath, nativeFolder: nativeFolder,
                                           compileArguments: options.CompileArguments)));
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
