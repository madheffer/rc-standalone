using System.Numerics;
using System.Text.RegularExpressions;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Lighting;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Gpu;

/// <summary>
/// Hammer's build cubemaps step from a compiled map package (CUBEMAPS.md C.2): every cube of
/// <see cref="CubemapFaceBlocks.FromPackage"/> rendered by <see cref="CubemapFaceRenderer"/> (six faces at
/// <see cref="CubemapFaceDrawList.FaceSize"/>), reduced to gameinfo's EnvironmentMapFaceSize by
/// <see cref="CubemapFaceExr"/>, then the array's .vtex source and env_cubemap_array.vtex_c
/// (<see cref="CubemapArrayVtex"/>), as the game writes them under <c>_buildcubemaps</c> and compiles them.
/// <list type="bullet">
/// <item>Cubes go into the array in FromPackage's order (client 1801ea0a0's records). The game's own order for the
/// .vtex is not read; on probe01 it equals both that order and the EXRs' name order.</item>
/// <item>The search path is <c>csgo_addons/&lt;addon&gt;</c>, as probe01's array holds it.</item>
/// <item><paramref name="scriptedCamera"/> is the build state (roadmap 0e: a compile has no player, Hammer's
/// build may). probe01's builds saw one, s2c_l5probe's did not.</item>
/// </list>
/// What the renderer does not port (CUBEMAPS.md A, B, D: non-aggregate objects, shadowed lights, the viewmodel,
/// animated maps) throws from the renderer rather than giving a different cube.
/// </summary>
public static class CubemapBuild
{
    /// <summary>The game's folder for the step's files, in the addon.</summary>
    public const string Folder = "_buildcubemaps";

    /// <summary>The step's files: each cube's EXR (its path in the addon and bytes), the .vtex source and the compiled array.</summary>
    public sealed record Result(IReadOnlyList<(string RelativePath, byte[] Bytes)> Exrs, byte[] Vtex, byte[] Array);

    /// <summary>gameinfo's WorldRenderer EnvironmentMapFaceSize, the storage size (default 128); CS2's gameinfo quotes its keys and values.</summary>
    public static int StorageSize(string gameinfo)
        => Regex.Match(gameinfo, @"WorldRenderer\s*\{[^}]*?""?EnvironmentMapFaceSize""?\s+""?(\d+)") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 128;

    /// <summary>
    /// The build for <paramref name="vpk"/>, a package of <paramref name="addon"/> under <paramref name="game"/>
    /// (the install's <c>game</c> folder: gameinfo, pak01, the Vulkan shaders and the native BC6H encoder).
    /// Null when the map has no cube.
    /// </summary>
    public static Result? Build(string game, string vpk, string addon, bool scriptedCamera, TextWriter? log = null)
    {
        var gameinfo = File.ReadAllText(Path.Combine(game, "csgo", "gameinfo.gi"));
        if (Regex.Match(gameinfo, @"WorldRenderer\s*\{[^}]*?""?EnvironmentMapRenderSize""?\s+""?(\d+)") is { Success: true } r
            && int.Parse(r.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != CubemapFaceDrawList.FaceSize)
            throw new NotSupportedException($"EnvironmentMapRenderSize {r.Groups[1].Value}: the renderer draws {CubemapFaceDrawList.FaceSize}");
        var storage = StorageSize(gameinfo);

        List<CubemapFaceBlocks.Cube> cubes;
        string? skyMaterial;
        List<CubemapPackageAggregates.Aggregate> aggregates;
        Dictionary<string, CubemapFaceDrawList.Membership> memberships;
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            cubes = CubemapFaceBlocks.FromPackage(package, gameinfo, scriptedCamera, out skyMaterial);
            if (cubes.Count == 0)
                return null;
            aggregates = CubemapPackageAggregates.Read(package);
            using var content = new GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", addon));
            // The -vulkan game's programs, every render mode's (MaterialAttributes.OfModes).
            using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"), Path.Combine(game, "core", "shaders_vulkan_dir.vpk")) { Platform = "vulkan_50" };
            memberships = CubemapPackageAggregates.Memberships(CubemapPackageAggregates.LayerInputs(package, content, shaders));
        }

        // The draw list's aggregates (fragments, LODs) and their layer-selection inputs, in the renderer's model order.
        var models = CubemapFaceRenderer.AggregateModels(vpk);
        if (!aggregates.Select(a => Path.ChangeExtension(a.Model, ".vmdl_c")).SequenceEqual(models.Select(m => m.Model)))
            throw new InvalidDataException("the world nodes' aggregates and the renderer's models are not in the same order");
        models = [.. models.Select((m, i) => m with { DrawCallFlags = memberships[aggregates[i].Model].DrawCall14 })];
        var listAggregates = aggregates.Select(a => new CubemapFaceDrawList.Aggregate(a.Fragments, a.OwnTransforms, a.Lods, memberships[a.Model])).ToList();
        var order = CubemapFaceDrawList.MaterialOrder(CubemapFaceRenderer.AggregateMaterials(models));

        var exrs = new List<(string RelativePath, byte[] Bytes)>();
        using (var gpu = new GpuDevice(rayTracingShaderFeatures: true))
        using (var renderer = new CubemapFaceRenderer(gpu, game, vpk, models, skyMaterial, [.. aggregates.Select(a => (IReadOnlyList<AggregateCulling.Fragment>)a.Fragments)]))
        {
            foreach (var cube in cubes)
            {
                Vector3? toSun = cube.Scene.HasSun ? CubemapShadowCascades.SunDirections(cube.Scene.SunAngles).ToSun : null;
                var faces = new List<Half[]>();
                for (var f = 0; f < 6; f++)
                {
                    var blocks = CubemapFaceBlocks.Build(cube.Scene, f);
                    var (draws, instances) = CubemapFaceDrawList.Face(blocks.View, toSun, listAggregates, cube.Scene.ScriptedCamera, materialOrder: order);
                    faces.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(renderer.RenderFace(blocks, draws, instances)).ToArray());
                }
                exrs.Add(($"{Folder}/env_cubemap_{cube.Handshake}.exr", CubemapFaceExr.Build(faces, CubemapFaceDrawList.FaceSize, storage)));
                log?.WriteLine($"  cube {cube.Handshake} ({cube.Class} at {cube.Scene.Origin}): rendered");
            }
        }

        var vtexPath = $"{Folder}/env_cubemap_array.vtex";
        var vtex = System.Text.Encoding.UTF8.GetBytes(CubemapArrayVtex.Source([.. exrs.Select(e => e.RelativePath)]));
        var arrayCubes = exrs.Select(e => new CubemapArrayVtex.Cube(e.RelativePath, e.Bytes,
            CubemapArrayVtex.FacesFromStrip(LightmapTextures.Bitmap(Vrad3Exr.Read(e.Bytes))))).ToList();
        using var blur = new GpuCubemapBlur(Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        var array = CubemapArrayVtex.Build(arrayCubes, storage, faces => blur.Filter(faces, storage), vtexPath, vtex,
            $"csgo_addons/{addon}", Path.Combine(game, "bin", "win64"));
        return new Result(exrs, vtex, array);
    }
}
