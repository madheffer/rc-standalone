using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Source2.Compiler.Cubemaps;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Gpu;

/// <summary>
/// One BuildCubemaps face rendered on our device from our data alone, with Valve's own programs (docs/CUBEMAPS.md
/// "A face with no dump"). The frame is the captures' (probe01, s2c_l5probe and atixref order every face the same):
/// <list type="number">
/// <item>the shadow atlas cleared to depth 1, the light binner's depth-bin and tile dispatches;</item>
/// <item>the four sun cascades' DrawDepthOpaque lists into their atlas tiles;</item>
/// <item>the face's colour cleared to 0 and depth to 1 (stencil 0), the depth prepass (Large, Small, AlphaTested);</item>
/// <item>the half resolution depth (cleared over the face's half, then downsample_depth's checkerboard min/max);</item>
/// <item>the AO target cleared to white over the same half (no AO is drawn in the cubemap view);</item>
/// <item>the colour layer (generic in ToolsVis), then the sky cube;</item>
/// <item>the face's stencil cleared, the quarter resolution depth (downsample_depth's max).</item>
/// </list>
/// Every binding of every program is named by the program's own write sequence (the .vcs variable tables): a
/// constant buffer at its layout set and slot, $Globals at binding 0 of the stage's set, a texture or structured
/// buffer at 30 + slot, a UAV at 158 + slot, a sampler at 14 + slot; each name then picks one of our inputs. The
/// face's colour target after the sky is what the game reads back (CubemapFaceExr).
/// </summary>
public sealed unsafe class CubemapFaceRenderer : IDisposable
{
    /// <summary>One vertex buffer of a mesh: its bytes, stride and fields.</summary>
    public sealed record VertexStream(byte[] Data, uint Stride, CubemapFaceVertexLayout.Field[] Fields);

    /// <summary>
    /// A world node aggregate's model: its vertex streams and index buffer, per draw call the material and vertex
    /// format, and whether the aggregate is a model's (m_allFlags has OBJECT_TYPE_MODEL, worldrenderer's
    /// ObjectTypeFlags_t 8: props merged into the world nodes) rather than the world's brushes.
    /// </summary>
    public sealed record AggregateModel(string Model, IReadOnlyList<VertexStream> Streams, byte[] Indices, IndexType IndexType,
        IReadOnlyList<ModelDrawCall> DrawCalls, bool IsModel)
    {
        /// <summary>
        /// The draw call's +0x14 flags (<see cref="CubemapLayerFlags.DrawCallFlags"/>, meshsystem 180049520 / 180027830
        /// over the material's attributes); bit 2 is m_bUseShadowFastPath. The caller sets it from the layer membership.
        /// </summary>
        public ushort DrawCallFlags { get; init; }

        /// <summary>The first stream's bytes (the only one of a brush mesh).</summary>
        public byte[] Vertices => Streams[0].Data;
    }

    /// <summary>
    /// A draw call: its index range, base vertex, material, m_bUseCompressedNormalTangent and m_nDepthVertexBufferIndex
    /// (the stream the depth passes draw).
    /// </summary>
    public sealed record ModelDrawCall(int StartIndex, int IndexCount, int BaseVertex, string Material, bool CompressedNormals, int DepthStream = 0);

    /// <summary>One action of the face, as run (<see cref="RenderFace"/>'s <c>observe</c> sees each).</summary>
    public sealed record Action(string Label, CubemapFacePipelines.Pass? Pass, string? Colour, string? Depth);

    /// <summary>The live targets by name.</summary>
    public const string ShadowAtlas = "shadow atlas", FaceColour = "face colour", FaceDepth = "face depth", HalfDepth = "half depth",
        AmbientOcclusion = "ambient occlusion", QuarterDepth = "quarter depth";

    /// <summary>
    /// The window the engine renders into (1280 x 720 in every capture): the half depth and AO targets are its size,
    /// as are the per-view blocks' inverse window sizes (<see cref="CubemapFaceConstants.PerView"/>).
    /// </summary>
    public const int WindowWidth = 1280, WindowHeight = 720;

    /// <summary>The quarter resolution depth target: 256 x 256 in every capture.</summary>
    public const int QuarterSize = 256;

    private readonly GpuDevice _gpu;
    private readonly SceneDrawRunner _runner;
    private readonly string _game;
    private readonly Package _map;
    private readonly Package _csgo;
    private readonly Package _core;
    private readonly Package _csgoShaders;
    private readonly Package _coreShaders;
    private readonly IReadOnlyList<AggregateModel> _aggregates;
    private readonly IReadOnlyList<IReadOnlyList<AggregateCulling.Fragment>>? _fragments;
    private readonly string _skyMaterial;
    private readonly Dictionary<string, (int R, int G, int B)> _shaderIdColors;

    private readonly Dictionary<object, GpuDevice.GpuBuffer> _buffers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SceneDrawRunner.SceneImage> _textures = [];
    private readonly Dictionary<string, Sampler> _samplers = [];
    private readonly Dictionary<string, ValveProgram> _programs = [];
    private readonly Dictionary<string, Material> _materials = [];
    private readonly Dictionary<string, byte[]> _globals = [];
    private readonly Dictionary<string, SceneDrawRunner.SceneImage> _live = [];
    private readonly List<ImageView> _views = [];
    private readonly List<GpuDevice.GpuBuffer> _transient = [];

    /// <param name="gpu">The device to render on.</param>
    /// <param name="game">The game folder (.../game) holding csgo and core.</param>
    /// <param name="mapVpk">The compiled map's package: world nodes, models, lightmaps.</param>
    /// <param name="aggregates">The draw list's aggregates' models, in its order (<see cref="AggregateModels"/>).</param>
    /// <param name="skyMaterial">env_sky's material.</param>
    public CubemapFaceRenderer(GpuDevice gpu, string game, string mapVpk, IReadOnlyList<AggregateModel> aggregates, string skyMaterial,
        IReadOnlyList<IReadOnlyList<AggregateCulling.Fragment>>? fragments = null)
    {
        _fragments = fragments;
        _gpu = gpu;
        _runner = new SceneDrawRunner(gpu);
        _game = game;
        _map = Open(mapVpk);
        _csgo = Open(Path.Combine(game, "csgo", "pak01_dir.vpk"));
        _core = Open(Path.Combine(game, "core", "pak01_dir.vpk"));
        _csgoShaders = Open(Path.Combine(game, "csgo", "shaders_vulkan_dir.vpk"));
        _coreShaders = Open(Path.Combine(game, "core", "shaders_vulkan_dir.vpk"));
        _aggregates = aggregates;
        _skyMaterial = skyMaterial;
        _shaderIdColors = ProgramGlobals.ShaderIdColors(File.ReadAllText(Path.Combine(game, "csgo", "gameinfo.gi")));
    }

    private static Package Open(string path)
    {
        var p = new Package();
        p.Read(path);
        return p;
    }

    /// <summary>
    /// The aggregates of a package's world nodes in the draw list's order (the .vwnod_c files by path, each one's
    /// m_aggregateSceneObjects in order), each with its m_renderableModel's first mesh.
    /// </summary>
    public static List<AggregateModel> AggregateModels(string vpk)
    {
        using var package = Open(vpk);
        var result = new List<AggregateModel>();
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal))
                     .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            foreach (var o in ((WorldNode)nodeResource.DataBlock!).Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var name = Path.ChangeExtension(o.GetStringProperty("m_renderableModel") ?? "", ".vmdl_c");
                package.ReadEntry(package.FindEntry(name) ?? throw new FileNotFoundException(name), out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var (mesh, _, _, _) = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First();
                var ib = mesh.VBIB.IndexBuffers[0];
                var calls = mesh.Data.GetArray("m_sceneObjects").SelectMany(so => so.GetArray("m_drawCalls"))
                    .Select(dc => new ModelDrawCall(dc.GetInt32Property("m_nStartIndex"), dc.GetInt32Property("m_nIndexCount"), dc.GetInt32Property("m_nBaseVertex"),
                        dc.GetStringProperty("m_material"), dc.ContainsKey("m_bUseCompressedNormalTangent") && dc.GetBooleanProperty("m_bUseCompressedNormalTangent"),
                        dc.ContainsKey("m_nDepthVertexBufferIndex") ? dc.GetInt32Property("m_nDepthVertexBufferIndex") : 0)).ToList();
                var streams = mesh.VBIB.VertexBuffers.Select(vb => new VertexStream(vb.Data, vb.ElementSizeInBytes,
                    [.. vb.InputLayoutFields.Select(x => new CubemapFaceVertexLayout.Field(x.SemanticName, x.SemanticIndex, CubemapFaceVertexLayout.FromDxgi(x.Format.ToString()), x.Offset))])).ToList();
                // m_allFlags reads as the flag names where the writer kept them, else as the number.
                var flags = o.Children.FirstOrDefault(c => c.Key == "m_allFlags").Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var isModel = long.TryParse(flags, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    ? (number & 8) != 0
                    : flags.Split(['|', ' '], StringSplitOptions.RemoveEmptyEntries).Contains("OBJECT_TYPE_MODEL");
                result.Add(new AggregateModel(name, streams, ib.Data, ib.ElementSizeInBytes == 4 ? IndexType.Uint32 : IndexType.Uint16, calls, isModel));
            }
        }
        return result;
    }

    /// <summary>The aggregates' materials (each one's first draw call's), for <see cref="CubemapFaceDrawList.MaterialOrder"/>.</summary>
    public static string?[] AggregateMaterials(IReadOnlyList<AggregateModel> aggregates) => [.. aggregates.Select(a => a.DrawCalls.FirstOrDefault()?.Material)];

    /// <summary>
    /// Renders one face: <paramref name="blocks"/> from <see cref="CubemapFaceBlocks.Build"/>, <paramref name="draws"/>
    /// and <paramref name="instances"/> from <see cref="CubemapFaceDrawList.Face"/>. Returns the colour target after
    /// the sky (RGBA16F, 1024 x 1024). <paramref name="observe"/> sees each action after it ran (read targets with
    /// <see cref="ReadTarget"/>).
    /// </summary>
    public byte[] RenderFace(CubemapFaceBlocks.Face blocks, IReadOnlyList<CubemapFaceDrawList.ListDraw> draws, int instances, Action<Action>? observe = null)
    {
        ResetTargets();
        var face = CubemapFaceDrawList.FaceSize;
        var (transforms, instanceRecords) = InstanceBuffers(draws, instances, _fragments);
        var instanceIds = CubemapFaceMeshes.InstanceIds(instanceRecords.Length / 32 * 4);

        // 1. The shadow atlas, cleared whole to depth 1 (its own pass, before the binner).
        Clear(ShadowAtlas, Format.D16Unorm, (uint)CubemapShadowCascades.Inputs.AtlasWidth, (uint)CubemapShadowCascades.Inputs.AtlasHeight,
            new(0, 0, CubemapShadowCascades.Inputs.AtlasWidth, CubemapShadowCascades.Inputs.AtlasHeight), depth: 1f, stencil: null, colour: null);

        // The light binner (scenesystem 1801b2850): the depth bins over (bins, mask words, 1) threads, then the tiles
        // over (tiles x, tiles y, mask words); the backend divides by the program's workgroup size (rendersystemvulkan
        // 180072850). The cull bits hold the bins' words, then the tiles'. Every word in either range is stored by one
        // thread (one OpStore, no atomics), so the buffer starts as zeros of its size.
        var bins = BinnerBins();
        var tilesX = (face + CubemapFaceBlocks.BinnerTilePixels - 1) / CubemapFaceBlocks.BinnerTilePixels;
        var tilesY = tilesX;
        var cullBits = _gpu.Upload(new byte[(bins + tilesX * tilesY) * Math.Max(1, blocks.BinnerMaskWords) * 4], BufferUsageFlags.StorageBufferBit);
        _transient.Add(cullBits);
        var depthBin = Program(_coreShaders, "compute_depthbin_cullbits", "cs", [], []);
        var tile = Program(_coreShaders, "compute_tile_cullbits", "cs", [], new() { ["D_SUBGROUP_SIZE_32"] = 1 });
        var records = Pad(blocks.BinnerRecords, 80 * 32);
        var edges = Pad(blocks.BinnerEdges, 16);
        if (bins > 1)
            Dispatch(depthBin, (uint)bins, (uint)blocks.BinnerMaskWords, 1, name => name switch
            {
                "CullParams_t" => (Buffer(blocks.BinnerParams), false),
                "CullItems" => (Buffer(records), false),
                "OutputCullBits" => (cullBits, true),
                _ => throw Unbound(depthBin, name),
            });
        Dispatch(tile, (uint)tilesX, (uint)tilesY, (uint)blocks.BinnerMaskWords, name => name switch
        {
            "CullParams_t" => (Buffer(blocks.BinnerParams), false),
            "CullPlanes" => (Buffer(edges), false),
            "CullItems" => (Buffer(records), false),
            "OutputCullBits" => (cullBits, true),
            _ => throw Unbound(tile, name),
        });
        observe?.Invoke(new Action("light binner", null, null, null));

        // 2. The sun cascades into their atlas tiles (the atlas is loaded, each pass's area its tile).
        foreach (var d in draws.Where(d => d.Layer == CubemapFaceDrawList.Layer.Shadow))
            foreach (var c in d.Commands)
                WorldDraw(d, c, CubemapFacePipelines.Pass.SunShadow, blocks.CascadeViews[d.View], blocks, transforms, instanceRecords, instanceIds, null, ShadowAtlas, observe);

        // 3. The face cleared, then the depth prepass.
        CreateTarget(FaceColour, Format.R16G16B16A16Sfloat, (uint)face, (uint)face);
        CreateTarget(FaceDepth, Format.D24UnormS8Uint, (uint)face, (uint)face);
        Clear(FaceColour, Format.R16G16B16A16Sfloat, (uint)face, (uint)face, new(0, 0, face, face), null, null, [0f, 0f, 0f, 0f]);
        Clear(FaceDepth, Format.D24UnormS8Uint, (uint)face, (uint)face, new(0, 0, face, face), 1f, 0, null);
        foreach (var d in draws.Where(d => d.Layer is CubemapFaceDrawList.Layer.PrepassLarge or CubemapFaceDrawList.Layer.PrepassSmall or CubemapFaceDrawList.Layer.PrepassAlphaTested))
        {
            foreach (var c in d.Commands)
                WorldDraw(d, c, CubemapFacePipelines.Pass.DepthPrepass, blocks.WorldView, blocks, transforms, instanceRecords, instanceIds, null, FaceDepth, observe);
        }

        // 4. The half resolution depth: cleared over the face's half, then the checkerboard min/max downsample.
        var half = face / 2;
        CreateTarget(HalfDepth, Format.D24UnormS8Uint, WindowWidth, WindowHeight);
        Clear(HalfDepth, Format.D24UnormS8Uint, WindowWidth, WindowHeight, new(0, 0, half, half), 1f, 0, null);
        Downsample(CubemapFacePipelines.Pass.DownsampleHalf, new() { ["D_MINMAX_CHECKER_2X2"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }, 2f, FaceDepth, HalfDepth, half, observe);

        // 5. The AO target, cleared to white over the same half; nothing draws AO in the cubemap view.
        CreateTarget(AmbientOcclusion, Format.R8G8B8A8Srgb, WindowWidth, WindowHeight);
        Clear(AmbientOcclusion, Format.R8G8B8A8Srgb, WindowWidth, WindowHeight, new(0, 0, half, half), null, null, [1f, 1f, 1f, 1f]);

        // 6. The colour layer, then the sky.
        foreach (var d in draws.Where(d => d.Layer == CubemapFaceDrawList.Layer.Colour))
            foreach (var c in d.Commands)
                WorldDraw(d, c, CubemapFacePipelines.Pass.Colour, blocks.WorldView, blocks, transforms, instanceRecords, instanceIds, cullBits, FaceDepth, observe, FaceColour);
        Sky(blocks, instanceIds, observe);

        // 7. The face's stencil cleared, then the quarter resolution depth.
        Clear(FaceDepth, Format.D24UnormS8Uint, (uint)face, (uint)face, new(0, 0, face, face), null, 0, null);
        CreateTarget(QuarterDepth, Format.D24UnormS8Uint, QuarterSize, QuarterSize);
        Downsample(CubemapFacePipelines.Pass.DownsampleQuarter, new() { ["D_MAX"] = 1, ["D_NON_MSAA_DEPTH"] = 1 }, 4f, FaceDepth, QuarterDepth, QuarterSize, observe);

        // The face's uploads go with it (the next face's blocks are new arrays).
        foreach (var b in _transient.Concat(_buffers.Values))
            _gpu.Destroy(b);
        _transient.Clear();
        _buffers.Clear();
        return ReadTarget(FaceColour);
    }

    /// <summary>Every program the faces rendered so far loaded (each one's SPIR-V is a module of Valve's shader packages).</summary>
    public IEnumerable<ValveProgram> LoadedPrograms => _programs.Values;

    /// <summary>
    /// g_transformBuffer and g_instanceBuffer for a face's draw list: each (view, aggregate) block's record i is the
    /// fragment Visible[i]'s, with its slot's packed tint and an index to its slot's 3 x 4 (one entry per aggregate and
    /// slot, ours in first-use order: the shader only reads the entry the index names). Instance 0 is the null record;
    /// transforms 0 and 1 are identities. Without fragments, every record points at transform 1 (the world nodes'
    /// shared identity, as the captures' brush records do).
    /// </summary>
    public static (byte[] Transforms, byte[] Instances) InstanceBuffers(IReadOnlyList<CubemapFaceDrawList.ListDraw> draws, int instances,
        IReadOnlyList<IReadOnlyList<AggregateCulling.Fragment>>? fragments)
    {
        var records = Enumerable.Repeat((0xffffffffu, 1), Math.Max(1, instances - 1)).ToArray();
        var matrices = new List<float[]> { CubemapFaceConstants.Identity3x4, CubemapFaceConstants.Identity3x4 };
        if (fragments != null)
        {
            var slots = new Dictionary<(int, int), int>();
            foreach (var d in draws)
            {
                if (d.Visible == null)
                    continue;
                for (var i = 0; i < d.Visible.Count; i++)
                {
                    var f = fragments[d.Aggregate][d.Visible[i]];
                    if (!slots.TryGetValue((d.Aggregate, f.TransformSlot), out var index))
                    {
                        slots[(d.Aggregate, f.TransformSlot)] = index = matrices.Count;
                        matrices.Add(f.SlotTransform ?? CubemapFaceConstants.Identity3x4);
                    }
                    records[d.InstanceBase + i - 1] = (f.SlotTint, index);
                }
            }
        }
        return (CubemapFaceConstants.Transforms(matrices), CubemapFaceConstants.Instances(records));
    }

    /// <summary>The light binner's depth bins: lb_bin_slices clamped to 1 .. 8192 and rounded up to 32 (1801a8150).</summary>
    private static int BinnerBins() => (int)(((uint)Math.Clamp(CubemapFaceBlocks.BinnerBinSlices, 1, 0x2000) + 31) & ~31u);

    /// <summary>A live target's mip 0 (depth targets: the depth plane, then the stencil plane), in SceneDrawRunner.Read's layout.</summary>
    public byte[] ReadTarget(string name)
    {
        var image = _live[name];
        return _runner.Read(image, SceneDrawRunner.IsDepth(image.Format) ? 0u : (uint)TexelBytes(image.Format));
    }

    private static int TexelBytes(Format f) => f switch
    {
        Format.R16G16B16A16Sfloat => 8,
        _ => 4,
    };

    private static InvalidDataException Unbound(ValveProgram program, string name) => new($"{program.Name}: no input for {name}");

    private static byte[] Pad(byte[] data, int minimum) => data.Length >= minimum ? data : [.. data, .. new byte[minimum - data.Length]];

    private GpuDevice.GpuBuffer Buffer(byte[] data)
    {
        if (!_buffers.TryGetValue(data, out var b))
            _buffers[data] = b = _gpu.Upload(data, BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit);
        return b;
    }

    private ValveProgram Program(Package shaders, string shader, string stage, Dictionary<string, int> statics, Dictionary<string, int> dynamics, IReadOnlyDictionary<string, int>? features = null)
        => ProgramOrNone(shaders, shader, stage, statics, dynamics, features) ?? throw new InvalidDataException($"{shader} {stage}: no shader for the combo");

    private readonly HashSet<string> _none = [];

    private ValveProgram? ProgramOrNone(Package shaders, string shader, string stage, Dictionary<string, int> statics, Dictionary<string, int> dynamics, IReadOnlyDictionary<string, int>? features = null)
    {
        var key = $"{shader} {stage} {string.Join(",", statics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))} {string.Join(",", dynamics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))} "
                  + string.Join(",", (features ?? new Dictionary<string, int>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        if (_programs.TryGetValue(key, out var p))
            return p;
        if (_none.Contains(key))
            return null;
        p = ValveProgram.LoadOrNone(shaders, shader, "vulkan_50", stage, statics, dynamics, features);
        if (p == null)
            _none.Add(key);
        else
            _programs[key] = p;
        return p;
    }

    private Material Material(string path)
    {
        if (_materials.TryGetValue(path, out var m))
            return m;
        foreach (var pak in new[] { _map, _csgo, _core })
            if (pak.FindEntry(path + "_c") is { } e)
            {
                pak.ReadEntry(e, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                return _materials[path] = (Material)resource.DataBlock!;
            }
        throw new FileNotFoundException(path);
    }

    private static Dictionary<string, int> Features(Material material) => material.IntParams.ToDictionary(kv => kv.Key, kv => (int)kv.Value);

    /// <summary>
    /// A world draw's programs for a material in a face pass, by the shader's own render modes (its features file's
    /// mode table): the depth passes draw the material's "Depth" mode, the colour pass its "ToolsVis" mode (the engine
    /// runs with -tools, as Hammer's build does; ledger 120). A mode names another shader (generic's Depth is
    /// csgo_depth_only) or a static combo of the shader's own (csgo_foliage's S_MODE_DEPTH=1); the material's features
    /// pick the other static combos. Dynamic combos: D_COMPRESSED_NORMALS_AND_TANGENTS from the draw call, the
    /// colour pass lightmapped (D_BAKED_LIGHTING_FROM_LIGHTMAP), the alpha-tested prepass layer D_ALPHA_TEST_PREPASS.
    /// The pixel program is none where its combo has no shader (<see cref="ValveProgram.LoadOrNone"/>).
    /// </summary>
    public (ValveProgram Vertex, ValveProgram? Pixel) Programs(AggregateModel model, ModelDrawCall call, CubemapFacePipelines.Pass pass, bool alphaTestedPrepass)
        => pass == CubemapFacePipelines.Pass.SunShadow && (model.DrawCallFlags & 2) != 0
            ? Programs(ShadowFastPathMaterial, CompressedNormals(model, call, pass), pass, false, Lighting(model))
            : Programs(call.Material, CompressedNormals(model, call, pass), pass, alphaTestedPrepass, Lighting(model));

    /// <summary>
    /// A colour draw's baked lighting combo from the draw call's +0x14 (meshsystem 180049520's table, ledger 157):
    /// m_bHasBakedLightingFromLightMap (0x10) the lightmap, m_bHasBakedLightingFromVertexStream (0x8) the vertex
    /// stream, neither the light probe volumes.
    /// </summary>
    public static string Lighting(AggregateModel model)
        => (model.DrawCallFlags & 0x10) != 0 ? "D_BAKED_LIGHTING_FROM_LIGHTMAP"
            : (model.DrawCallFlags & 0x8) != 0 ? "D_BAKED_LIGHTING_FROM_VERTEX_STREAM" : "D_BAKED_LIGHTING_FROM_PROBE";

    /// <summary>
    /// The material a shadow fast path caster draws the cascades with (m_bUseShadowFastPath): every such batch of the
    /// captures names materials/dev/reflectivity_90.vmat (generic: its Depth mode is csgo_depth_only's plain combo).
    /// </summary>
    public const string ShadowFastPathMaterial = "materials/dev/reflectivity_90.vmat";

    /// <summary>
    /// The streams a world draw binds: the depth passes the draw call's depth stream alone, the colour pass every
    /// stream (atixref's captures: a prop's position stream in the depth passes, both in colour).
    /// </summary>
    public static IReadOnlyList<VertexStream> BoundStreams(AggregateModel model, ModelDrawCall call, CubemapFacePipelines.Pass pass)
        => pass == CubemapFacePipelines.Pass.Colour ? model.Streams
            : call.DepthStream >= 0 && call.DepthStream < model.Streams.Count ? [model.Streams[call.DepthStream]]
            // 255: no depth stream of its own (single-stream meshes); the draw binds its first stream (the captures).
            : call.DepthStream == 255 ? [model.Streams[0]]
            : throw new InvalidDataException($"{model.Model}: depth stream {call.DepthStream} of {model.Streams.Count}");

    /// <summary>
    /// D_COMPRESSED_NORMALS_AND_TANGENTS: the draw call's m_bUseCompressedNormalTangent where a bound stream holds the
    /// normal (measured on every world draw of atixref's face 0: the depth passes of two-stream props and brushes
    /// draw 0, single-stream foliage 1, every colour draw 1).
    /// </summary>
    public static bool CompressedNormals(AggregateModel model, ModelDrawCall call, CubemapFacePipelines.Pass pass)
        => call.CompressedNormals && BoundStreams(model, call, pass).Any(s => s.Fields.Any(f => f.Semantic == "NORMAL"));

    private (ValveProgram Vertex, ValveProgram? Pixel) Programs(string materialPath, bool compressedNormals, CubemapFacePipelines.Pass pass, bool alphaTestedPrepass, string lighting)
    {
        var material = Material(materialPath);
        var features = Features(material);
        var own = Path.GetFileNameWithoutExtension(material.ShaderName);
        var modeName = pass == CubemapFacePipelines.Pass.Colour ? "ToolsVis" : "Depth";
        var (_, fallback, staticName, staticValue) = ShaderModes(own).FirstOrDefault(m => m.Name == modeName);
        if (fallback == null)
            throw new InvalidDataException($"{own} has no {modeName} mode");
        var shader = fallback.Length > 0 ? Path.GetFileNameWithoutExtension(fallback) : own;
        // S_SHADER_QUALITY is set by the system, not the material (its combo source type): 1 in the captures' video
        // config (as GpuMaterialSampler's). The file's static rules drop it where a mode excludes it.
        var statics = new Dictionary<string, int> { ["S_SHADER_QUALITY"] = ShaderQuality };
        if (staticName.Length > 0 && fallback.Length == 0)
            statics[staticName] = staticValue;
        var compressed = compressedNormals ? 1 : 0;
        var vsDynamics = new Dictionary<string, int> { ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = compressed };
        var psDynamics = new Dictionary<string, int>();
        // The colour pass's baked lighting (the vertex programs have no probe combo).
        if (pass == CubemapFacePipelines.Pass.Colour)
        {
            psDynamics[lighting] = 1;
            if (lighting != "D_BAKED_LIGHTING_FROM_PROBE")
                vsDynamics[lighting] = 1;
        }
        if (alphaTestedPrepass)
            psDynamics["D_ALPHA_TEST_PREPASS"] = 1;
        var shaders = Shaders(shader);
        var vs = ProgramOrNone(shaders, shader, "vs", statics, vsDynamics, features)
            ?? throw new InvalidDataException($"{shader}: no vertex program for {materialPath} in {modeName}");
        var ps = ProgramOrNone(shaders, shader, "ps", statics, psDynamics, features);
        if (pass == CubemapFacePipelines.Pass.Colour && ps == null)
            throw new InvalidDataException($"{shader}: no colour pixel program for {materialPath}");
        return (vs, ps);
    }

    /// <summary>The system's shader quality combo (S_SHADER_QUALITY): 1, the captures' video config ("High").</summary>
    public const int ShaderQuality = 1;

    /// <summary>A material's int parameter (a feature such as F_ALPHA_TEST), 0 when absent.</summary>
    public long MaterialInt(string materialPath, string name) => Material(materialPath).IntParams.GetValueOrDefault(name);

    private Package Shaders(string shader) => _csgoShaders.FindEntry($"shaders/vfx/{shader}_vulkan_50_features.vcs") != null ? _csgoShaders : _coreShaders;

    private readonly Dictionary<string, List<(string Name, string ShaderFallback, string StaticComboName, int StaticComboValue)>> _modes = [];

    /// <summary>A shader's render modes (its features file's header).</summary>
    private List<(string Name, string ShaderFallback, string StaticComboName, int StaticComboValue)> ShaderModes(string shader)
    {
        if (_modes.TryGetValue(shader, out var modes))
            return modes;
        var pak = Shaders(shader);
        var entry = pak.FindEntry($"shaders/vfx/{shader}_vulkan_50_features.vcs") ?? throw new FileNotFoundException($"{shader} features");
        pak.ReadEntry(entry, out var bytes);
        using var file = new VfxProgramData();
        file.Read($"{shader}_vulkan_50_features.vcs", new MemoryStream(bytes));
        return _modes[shader] = [.. file.FeaturesHeader!.Modes];
    }

    /// <summary>
    /// A material's $Globals for a pixel program: <see cref="ProgramGlobals.Fill"/> with g_bRenderingToCubemaps = 1 and
    /// gameinfo's ShaderIDColors (ledger 120), the block the program's reflection declares.
    /// </summary>
    private byte[] Globals(string key, ValveProgram program, Material material, Dictionary<string, VfxExpression.Value> attributes, VfxExpression.Value? shaderIdColor = null,
        Func<string, VfxVariableDescription, int>? textureSlot = null, Func<string, int>? samplerSlot = null)
    {
        if (_globals.TryGetValue(key, out var made))
            return made;
        var size = program.Reflection.Resources.First(r => r.Binding == 0 && r.Kind == SpirvReflection.Kind.UniformBuffer).BlockSize;
        var block = new byte[(size + 15) & ~15];
        var tokens = new Dictionary<string, VfxExpression.Value>(attributes, StringComparer.OrdinalIgnoreCase);
        ProgramGlobals.Fill(program, block, new ProgramGlobals.MaterialScope(material, program, tokens, shaderIdColor), textureSlot ?? ((_, _) => -1), samplerSlot ?? (_ => -1));
        // S2C_CUBE_GLOBALS_OUT=<dir>: each block as made, to compare with a capture's (diagnosis).
        if (Environment.GetEnvironmentVariable("S2C_CUBE_GLOBALS_OUT") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            var file = Path.Combine(dump, string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_')));
            File.WriteAllBytes(file + ".bin", block);
            // The program's SPIR-V hash and its constants (name, offset, type), to find the capture's draws of it.
            File.WriteAllLines(file + ".txt", [Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(program.Spirv)),
                .. program.Constants.Select(c => $"{c.Offset} {c.Name} {c.Variable.VfxType}")]);
        }
        return _globals[key] = block;
    }

    // ---- targets and clears ----

    private void ResetTargets()
    {
        foreach (var v in _views)
            _runner.DestroyView(v);
        _views.Clear();
        foreach (var i in _live.Values)
            _runner.Destroy(i);
        _live.Clear();
    }

    /// <summary>A target that starts as a fresh one: colour 0, depth 1, stencil 0 (the chained replay's start).</summary>
    private SceneDrawRunner.SceneImage CreateTarget(string name, Format format, uint width, uint height)
    {
        if (_live.TryGetValue(name, out var had))
            return had;
        var image = _runner.CreateImage(format, width, height, 1, 1, 1, ImageViewType.Type2D,
            SceneDrawRunner.IsDepth(format) ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit);
        var texels = (int)(width * height);
        var data = new byte[SceneDrawRunner.IsDepth(format) ? texels * (int)SceneDrawRunner.DepthTexelBytes(format) + (SceneDrawRunner.HasStencil(format) ? texels : 0) : texels * TexelBytes(format)];
        if (format == Format.D16Unorm)
            data.AsSpan(0, texels * 2).Fill(0xff);
        else if (format == Format.D24UnormS8Uint)
            for (var i = 0; i < texels; i++)
                data[i * 4] = data[i * 4 + 1] = data[i * 4 + 2] = 0xff;
        _runner.Write(image, 0, 0, data);
        return _live[name] = image;
    }

    /// <summary>
    /// A render pass's clears over its area, as vkCmdBeginRendering's load op CLEAR does (the passes of every capture,
    /// tools/cubehost/rdc_passes.py): depth, stencil and colour where given.
    /// </summary>
    private void Clear(string name, Format format, uint width, uint height, CubemapFaceDrawList.Rect area, float? depth, byte? stencil, float[]? colour)
    {
        var image = CreateTarget(name, format, width, height);
        var data = ReadTarget(name);
        var texels = (int)(width * height);
        for (var y = area.Y; y < area.Y + area.Height && y < height; y++)
            for (var x = area.X; x < area.X + area.Width && x < width; x++)
            {
                var i = (int)(y * width + x);
                if (depth is { } d)
                {
                    if (format == Format.D16Unorm)
                        BitConverter.TryWriteBytes(data.AsSpan(i * 2), (ushort)MathF.Round(d * 65535f));
                    else
                    {
                        // D24's depth is the texel's low 24 bits; its fourth byte is not depth.
                        var v = (uint)MathF.Round(d * 16777215f);
                        (data[i * 4], data[i * 4 + 1], data[i * 4 + 2]) = ((byte)v, (byte)(v >> 8), (byte)(v >> 16));
                    }
                }
                if (stencil is { } s)
                    data[texels * (int)SceneDrawRunner.DepthTexelBytes(format) + i] = s;
                if (colour != null)
                {
                    if (format == Format.R16G16B16A16Sfloat)
                        for (var c = 0; c < 4; c++)
                            BitConverter.TryWriteBytes(data.AsSpan(i * 8 + c * 2), (Half)colour[c]);
                    else
                        for (var c = 0; c < 4; c++)
                            data[i * 4 + c] = colour[c] is 0f or 1f ? (byte)(colour[c] * 255f) : throw new NotSupportedException($"clear {colour[c]} of {format}");
                }
            }
        _runner.Write(image, 0, 0, data);
    }

    /// <summary>A live target through another view format of its class (the colour pass reads the sRGB AO target as UNORM).</summary>
    private SceneDrawRunner.SceneImage Viewed(string name, Format format)
    {
        var image = _live[name];
        if (image.Format == format)
            return image;
        var view = _runner.ViewAs(image, format);
        _views.Add(view);
        return new SceneDrawRunner.SceneImage
        {
            Image = image.Image, Memory = image.Memory, View = view, Format = format, Width = image.Width, Height = image.Height,
            Depth = image.Depth, Mips = image.Mips, Layers = image.Layers,
        };
    }

    // ---- bindings from the programs' tables ----

    /// <summary>What a program's write sequence names at a set and binding (null: binding 0 of the stage's set, $Globals).</summary>
    private static VfxVariableDescription? Named(ValveProgram program, int set, int binding, SpirvReflection.Kind kind)
    {
        foreach (var f in program.Sequence.RenderState)
        {
            if (f.LayoutSet != set)
                continue;
            var v = program.Program.VariableDescriptions[f.VariableIndex];
            var at = v.RegisterType switch
            {
                VfxRegisterType.ConstantBuffer => f.BindingSlot,
                VfxRegisterType.Texture => 30 + f.BindingSlot,
                VfxRegisterType.Uav => 158 + f.BindingSlot,
                VfxRegisterType.SamplerState => 14 + f.BindingSlot,
                _ => -1,
            };
            if (at != binding)
                continue;
            var fits = kind switch
            {
                SpirvReflection.Kind.UniformBuffer => v.RegisterType == VfxRegisterType.ConstantBuffer,
                SpirvReflection.Kind.StorageBuffer => v.RegisterType is VfxRegisterType.Texture or VfxRegisterType.Uav,
                SpirvReflection.Kind.SampledImage => v.RegisterType == VfxRegisterType.Texture,
                SpirvReflection.Kind.Sampler => v.RegisterType == VfxRegisterType.SamplerState,
                _ => false,
            };
            if (fits)
                return v;
        }
        return null;
    }

    // ---- the global late-bound bindless set (set 4) ----

    /// <summary>Set 4's bindless sampler array (g_globalLateBoundBindlessSet's samplers; csgo_foliage's vertex program).</summary>
    private const int BindlessSamplerBinding = 29;

    /// <summary>Our indices into the bindless texture arrays, from 1 (0 is no texture), and each one's image and view type.</summary>
    private readonly Dictionary<string, (int Index, SceneDrawRunner.SceneImage Image, ImageViewType ViewType)> _bindlessTextures = [];

    /// <summary>The engine samplers registered in the bindless sampler array, by slot.</summary>
    private readonly Dictionary<int, Sampler> _bindlessSamplers = [];

    private static ImageViewType? IndexViewType(VfxVariableType type) => type switch
    {
        VfxVariableType.Texture2DIndex => ImageViewType.Type2D,
        VfxVariableType.Texture3DIndex => ImageViewType.Type3D,
        VfxVariableType.TextureCubeIndex => ImageViewType.TypeCube,
        VfxVariableType.Texture2DArrayIndex => ImageViewType.Type2DArray,
        VfxVariableType.TextureCubeArrayIndex => ImageViewType.TypeCubeArray,
        _ => null,
    };

    private static string Dim(ImageViewType type) => type switch
    {
        ImageViewType.Type2D => "2D", ImageViewType.Type3D => "3D", ImageViewType.TypeCube => "Cube",
        ImageViewType.Type2DArray => "2DArray", ImageViewType.TypeCubeArray => "CubeArray", var t => t.ToString(),
    };

    /// <summary>The program's bindless texture arrays in set 4 (the variables named g_bindless_*), as resources.</summary>
    private static IEnumerable<SpirvReflection.Resource> BindlessArrays(ValveProgram program)
        => program.Reflection.Resources.Where(r => r.Set == 4 && r.Kind == SpirvReflection.Kind.SampledImage
            && Named(program, r.Set, r.Binding, r.Kind) is { } v && v.Name.StartsWith("g_bindless_", StringComparison.Ordinal));

    /// <summary>
    /// A texture index variable's slot (ProgramGlobals.Fill): the material's texture under the variable's name, in
    /// our next free index. A program with no bindless array of that dimension keeps -1, as it had before bindless.
    /// </summary>
    private int BindlessTexture(ValveProgram program, string name, VfxVariableDescription v, Material material)
    {
        if (IndexViewType(v.VfxType) is not { } viewType || !BindlessArrays(program).Any(r => r.Dim == Dim(viewType)))
            return -1;
        var path = material.TextureParams.GetValueOrDefault(name)
                   ?? throw new InvalidDataException($"{program.Name}: no material texture {name} for the bindless {Dim(viewType)} array");
        var key = $"{path} {v.SrgbRead} {viewType}";
        if (!_bindlessTextures.TryGetValue(key, out var entry))
            _bindlessTextures[key] = entry = (_bindlessTextures.Count + 1, Vtex(path, v.SrgbRead, viewType), viewType);
        return entry.Index;
    }

    /// <summary>The first slot of the bindless sampler array that material-configured samplers take, after the engine's.</summary>
    private const int BindlessUserSamplerBase = 32;

    /// <summary>
    /// A sampler index variable's slot: an engine sampler by name (<see cref="CubemapFaceSamplers.Engine"/>), or for a
    /// g_sUserConfig* name the material-configured state, which is the program's sampler group whose Filter is the
    /// material's (255) or absent, evaluated on the material (as GpuMaterialSampler takes g_sUserConfig). Each distinct
    /// state gets its own slot. The AllowGlobalMipBias variants take no bias: the global mip bias is 0 in the captures.
    /// </summary>
    private int BindlessSampler(ValveProgram program, string name, Material material)
    {
        if (!program.Reflection.Resources.Any(r => r.Set == 4 && r.Binding == BindlessSamplerBinding && r.Kind == SpirvReflection.Kind.Sampler))
            return -1;
        if (CubemapFaceSamplers.Engine.TryGetValue(name, out var s))
        {
            if (!_bindlessSamplers.ContainsKey(s.Slot))
                _bindlessSamplers[s.Slot] = SamplerOf(CubemapFaceSamplers.State(s.Filter, s.AddressU, s.AddressV, s.AddressW, s.Compare, null, null));
            return s.Slot;
        }
        if (!name.StartsWith("g_sUserConfig", StringComparison.Ordinal))
            throw new NotSupportedException($"{program.Name}: the engine sampler {name} is not measured");
        var groups = CubemapFaceSamplers.Groups(program, new ProgramGlobals.MaterialScope(material, program, new Dictionary<string, VfxExpression.Value>()))
            .Where(g => g.Filter is null or 255).Select(g => g.State).Distinct().ToList();
        if (groups.Count != 1)
            throw new NotSupportedException($"{program.Name}: {name} has {groups.Count} material-filter sampler states to take its state from");
        var sampler = SamplerOf(groups[0]);
        foreach (var (slot, had) in _bindlessSamplers)
            if (slot >= BindlessUserSamplerBase && had.Handle == sampler.Handle)
                return slot;
        var next = Math.Max(BindlessUserSamplerBase, _bindlessSamplers.Keys.DefaultIfEmpty(0).Max() + 1);
        _bindlessSamplers[next] = sampler;
        return next;
    }

    /// <summary>
    /// Set 4's bindless arrays: every registered sampler at its slot, every registered texture of the array's
    /// dimension at its index. False for any other binding.
    /// </summary>
    private bool BindBindless(ValveProgram program, SpirvReflection.Resource r, SceneDrawRunner.Bindings bindings)
    {
        if (r.Set != 4)
            return false;
        if (r.Kind == SpirvReflection.Kind.Sampler && r.Binding == BindlessSamplerBinding && Named(program, r.Set, r.Binding, r.Kind) == null)
        {
            foreach (var (slot, sampler) in _bindlessSamplers)
                bindings.Sampler(r.Set, r.Binding, (uint)slot, sampler);
            return true;
        }
        if (r.Kind == SpirvReflection.Kind.SampledImage && Named(program, r.Set, r.Binding, r.Kind) is { } v && v.Name.StartsWith("g_bindless_", StringComparison.Ordinal))
        {
            foreach (var (_, (index, image, viewType)) in _bindlessTextures)
                if (Dim(viewType) == r.Dim)
                    bindings.Image(r.Set, r.Binding, (uint)index, image.View);
            return true;
        }
        return false;
    }

    private byte[]? _probeVolumeBuffer;

    /// <summary>
    /// PerViewLightProbeVolumeConstantBuffer_t from the map's env_combined_light_probe_volume entities in lump order
    /// (<see cref="LightProbeVolumeRecords"/>), the default probe texture registered in the bindless set. The +0x8c
    /// default (type 0x15) gets no texture: no program reads it on atixref, and its image is not read.
    /// </summary>
    private byte[] ProbeVolumeBuffer()
    {
        if (_probeVolumeBuffer != null)
            return _probeVolumeBuffer;
        var volumes = new List<LightProbeVolumeRecords.Volume>();
        foreach (var entry in (_map.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            _map.ReadEntry(entry, out var bytes);
            foreach (var (_, key) in Source2.Compiler.Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "env_combined_light_probe_volume"))
            {
                static System.Numerics.Vector3 V(string? t) => t!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is var a ? new(a[0], a[1], a[2]) : default;
                static int N(string? t) => int.Parse(t ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                if (key("lightprobetexture") is { Length: > 0 } texture && _map.FindEntry(texture + "_c") != null)
                    throw new NotSupportedException("a baked light probe volume: only the default probe texture's record is ported");
                volumes.Add(new(V(key("origin")), V(key("angles")), V(key("box_mins")), V(key("box_maxs")),
                    N(key("light_probe_size_x")), N(key("light_probe_size_y")), N(key("light_probe_size_z"))));
            }
        }
        return _probeVolumeBuffer = LightProbeVolumeRecords.Buffer(volumes, DefaultProbeTexture(), 0);
    }

    /// <summary>The engine's default probe texture (vfn 0x198 type 6): 2 x 2 x 2 R8G8B8A8 zeros, measured; our bindless index.</summary>
    private int DefaultProbeTexture()
    {
        const string key = "engine default probe texture";
        if (!_bindlessTextures.TryGetValue(key, out var entry))
        {
            const uint side = LightProbeVolumeRecords.DefaultTextureSide;
            var image = _runner.CreateImage(Format.R8G8B8A8Unorm, side, side, side, 1, 1, ImageViewType.Type3D, ImageUsageFlags.SampledBit);
            _runner.Write(image, 0, 0, new byte[side * side * side * 4]);
            _textures[key] = image;
            _bindlessTextures[key] = entry = (_bindlessTextures.Count + 1, image, ImageViewType.Type3D);
        }
        return entry.Index;
    }

    /// <summary>The error for a binding <see cref="Named"/> cannot place, with what the write sequence does list in that set.</summary>
    private static string Unnamed(ValveProgram program, int set, int binding, SpirvReflection.Kind kind)
        => $"{program.Name}: set {set} binding {binding} ({kind}) is named by no variable; the sequence lists in set {set}: "
           + string.Join(", ", program.Sequence.RenderState.Where(f => f.LayoutSet == set).Select(f =>
               $"{program.Program.VariableDescriptions[f.VariableIndex].Name} {program.Program.VariableDescriptions[f.VariableIndex].RegisterType} slot {f.BindingSlot}"));

    private void Dispatch(ValveProgram program, uint x, uint y, uint z, Func<string, (GpuDevice.GpuBuffer Buffer, bool Written)> input)
    {
        var bindings = new SceneDrawRunner.Bindings();
        foreach (var r in program.Reflection.Resources)
        {
            var v = Named(program, r.Set, r.Binding, r.Kind) ?? throw new InvalidDataException(Unnamed(program, r.Set, r.Binding, r.Kind));
            var name = v.RegisterType == VfxRegisterType.ConstantBuffer ? v.Name : v.SourceString;
            bindings.Buffer(r.Set, r.Binding, 0, input(name).Buffer, 0, Vk.WholeSize);
        }
        var (lx, ly, lz) = LocalSize(program.Spirv);
        _runner.Dispatch(program.Spirv, bindings, (x + lx - 1) / lx, (y + ly - 1) / ly, (z + lz - 1) / lz);
    }

    /// <summary>The compute program's workgroup size (OpExecutionMode LocalSize).</summary>
    private static (uint, uint, uint) LocalSize(byte[] spirv)
    {
        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        for (var i = 5; i < words.Length;)
        {
            var (count, op) = ((int)(words[i] >> 16), words[i] & 0xffff);
            if (op == 16 && words[i + 2] == 17)
                return (words[i + 3], words[i + 4], words[i + 5]);
            i += count;
        }
        throw new InvalidDataException("no LocalSize");
    }

    /// <summary>One indirect command of an aggregate list.</summary>
    private void WorldDraw(CubemapFaceDrawList.ListDraw list, AggregateCulling.Command command, CubemapFacePipelines.Pass pass, byte[] perView, CubemapFaceBlocks.Face blocks,
        byte[] transforms, byte[] instanceRecords, byte[] instanceIds, GpuDevice.GpuBuffer? cullBits, string depthTarget,
        Action<Action>? observe, string? colourTarget = null)
    {
        var model = _aggregates[list.Aggregate];
        // A command covers consecutive fragments, which may be several draw calls of the list's one material.
        var calls = model.DrawCalls.Where(c => c.StartIndex < command.FirstIndex + command.IndexCount && command.FirstIndex < c.StartIndex + c.IndexCount).ToList();
        if (calls.Count == 0 || calls.Any(c => c.Material != calls[0].Material || c.CompressedNormals != calls[0].CompressedNormals || c.DepthStream != calls[0].DepthStream))
            throw new InvalidDataException($"{model.Model}: indices {command.FirstIndex} + {command.IndexCount} are not one material's draw calls");
        var call = calls[0];
        var (vs, ps) = Programs(model, call, pass, list.Layer == CubemapFaceDrawList.Layer.PrepassAlphaTested);
        var streams = BoundStreams(model, call, pass);
        var material = Material(call.Material);
        var bindings = new SceneDrawRunner.Bindings();
        // Both stages' $Globals first: filling them registers the bindless textures and samplers the draw indexes.
        var globals = new[] { vs, ps }.Select((program, stage) => program == null || !program.Reflection.Resources.Any(r => r.Kind == SpirvReflection.Kind.UniformBuffer && r.Binding == 0 && r.Set == stage) ? null : Globals(call.Material + " " + program.Name, program, material,
            new() { ["g_bRenderingToCubemaps"] = VfxExpression.Value.Of(1f) }, ShaderIdColor(material),
            (name, v) => BindlessTexture(program, name, v, material), name => BindlessSampler(program, name, material))).ToArray();
        foreach (var (program, stage) in new[] { (vs, 0), (ps, 1) })
        {
            if (program == null)
                continue;
            foreach (var r in program.Reflection.Resources)
            {
                if (r.Kind == SpirvReflection.Kind.UniformBuffer && r.Binding == 0 && r.Set == stage)
                {
                    bindings.Buffer(r.Set, 0, 0, Buffer(globals[stage]!), 0, Vk.WholeSize);
                    continue;
                }
                if (BindBindless(program, r, bindings))
                    continue;
                var v = Named(program, r.Set, r.Binding, r.Kind) ?? throw new InvalidDataException(Unnamed(program, r.Set, r.Binding, r.Kind));
                switch (r.Kind)
                {
                    case SpirvReflection.Kind.UniformBuffer:
                        bindings.Buffer(r.Set, r.Binding, 0, Buffer(v.Name switch
                        {
                            "PerViewConstantBuffer_t" => perView,
                            "PerViewConstantBufferCsgo_t" => blocks.Csgo,
                            "BakedLightingConstantBuffer_t" => blocks.Baked,
                            "PerViewLightProbeVolumeConstantBuffer_t" => ProbeVolumeBuffer(),
                            "PerViewLightingConstantBufferGpu_t" => blocks.Lights,
                            _ => throw Unbound(program, v.Name),
                        }), 0, Vk.WholeSize);
                        break;
                    case SpirvReflection.Kind.StorageBuffer:
                        bindings.Buffer(r.Set, r.Binding, 0, v.SourceString switch
                        {
                            "TransformBuffer" => Buffer(transforms),
                            "InstanceBuffer" => Buffer(instanceRecords),
                            "CullBits" => cullBits ?? throw Unbound(program, v.SourceString),
                            // No barn light is binned in the cubemap view (the light block lists none): no thread reads it.
                            "BarnLights" => Buffer(BarnLightsNone),
                            _ => throw Unbound(program, v.SourceString),
                        }, 0, Vk.WholeSize);
                        break;
                    case SpirvReflection.Kind.SampledImage:
                        bindings.Image(r.Set, r.Binding, 0, Texture(program, v, r, material).View);
                        break;
                    case SpirvReflection.Kind.Sampler:
                        bindings.Sampler(r.Set, r.Binding, 0, MaterialSampler(program, material, r.Binding - 14));
                        break;
                    default:
                        throw new NotSupportedException($"{program.Name}: {r.Kind} at set {r.Set} binding {r.Binding}");
                }
            }
        }
        var colourFormat = colourTarget != null ? _live[colourTarget].Format : (Format?)null;
        var rect = CubemapFaceDrawList.Viewport(list.View);
        var template = new SceneDrawState
        {
            VertexSpirv = vs.Spirv,
            PixelSpirv = ps?.Spirv,
            Bindings = CubemapFaceVertexLayout.Bindings([.. streams.Select(s => s.Stride)], true),
            Attributes = CubemapFaceVertexLayout.Attributes(vs, [.. streams.Select(s => (IReadOnlyList<CubemapFaceVertexLayout.Field>)s.Fields)], true),
            Viewport = new Viewport(rect.X, rect.Y, rect.Width, rect.Height, 0f, 1f),
            Scissor = new Rect2D(new Offset2D(rect.X, rect.Y), new Extent2D((uint)rect.Width, (uint)rect.Height)),
            ColorFormat = colourFormat,
            DepthFormat = _live[depthTarget].Format,
        };
        var state = CubemapFacePipelines.Apply(template, pass, ps != null ? (VfxRenderStateInfoPixelShader)ps.RenderState : null);
        (GpuDevice.GpuBuffer, ulong)[] vertexBuffers = [.. streams.Select(s => (Buffer(s.Data), 0ul)), (Buffer(instanceIds), 0ul), (Buffer(Zero16), 0ul)];
        _runner.Draw(state, bindings, vertexBuffers, (Buffer(model.Indices), 0ul, model.IndexType),
            new SceneDrawArgs(true, (uint)command.IndexCount, (uint)command.InstanceCount, (uint)command.FirstIndex, command.VertexOffset, (uint)command.FirstInstance),
            colourTarget != null ? _live[colourTarget] : null, _live[depthTarget]);
        observe?.Invoke(new Action($"{pass} view {list.View} aggregate {list.Aggregate} {command}", pass, colourTarget, depthTarget));
    }

    private static readonly byte[] BarnLightsNone = new byte[4096];

    private VfxExpression.Value ShaderIdColor(Material material)
    {
        var (r, g, b) = _shaderIdColors.GetValueOrDefault(material.ShaderName, (0, 0, 0));
        return ProgramGlobals.ShaderIdColor(r, g, b);
    }

    private Sampler MaterialSampler(ValveProgram program, Material material, int slot)
    {
        var states = CubemapFaceSamplers.Samplers(program, new ProgramGlobals.MaterialScope(material, program, new Dictionary<string, VfxExpression.Value>()));
        if (!states.TryGetValue(slot, out var info))
            throw new InvalidDataException($"{program.Name}: no sampler state at slot {slot}");
        return SamplerOf(info);
    }

    private Sampler SamplerOf(SamplerCreateInfo info)
    {
        var key = $"{info.MagFilter} {info.MinFilter} {info.MipmapMode} {info.AddressModeU} {info.AddressModeV} {info.AddressModeW} {info.MipLodBias} {info.AnisotropyEnable} {info.MaxAnisotropy} {info.CompareEnable} {info.CompareOp} {info.MinLod} {info.MaxLod} {info.BorderColor}";
        if (!_samplers.TryGetValue(key, out var s))
            _samplers[key] = s = _runner.CreateSampler(info);
        return s;
    }

    /// <summary>
    /// A texture binding by its variable: an artist texture from the material, the lightmaps from the map's package,
    /// the face's live targets, 1 x 1 white for SSAO and particle shadows, a 1 x 1 cube of zeros for the fog cube, and
    /// zeros for the light cookies and environment maps (no light or environment map is binned in the cubemap view,
    /// so no pixel reads them). The view's format is sRGB where the variable reads sRGB.
    /// </summary>
    private SceneDrawRunner.SceneImage Texture(ValveProgram program, VfxVariableDescription v, SpirvReflection.Resource r, Material? material)
    {
        var viewType = r.Dim switch
        {
            "2D" => ImageViewType.Type2D, "2DArray" => ImageViewType.Type2DArray, "3D" => ImageViewType.Type3D,
            "Cube" => ImageViewType.TypeCube, "CubeArray" => ImageViewType.TypeCubeArray,
            var d => throw new NotSupportedException($"{program.Name}: {v.Name} of dimension {d}"),
        };
        if (v.VariableSource == VfxVariableSourceType.__SetByArtist__)
        {
            var path = material?.TextureParams.GetValueOrDefault(v.Name) ?? throw new InvalidDataException($"{program.Name}: no material texture {v.Name}");
            return Vtex(path, v.SrgbRead, viewType);
        }
        return v.SourceString switch
        {
            "direct_light_shadows" or "irradiance" or "directional_irradiance" or "debug_chart_color" => Lightmap(v.SourceString, v.SrgbRead, viewType),
            "ShadowDepthBuffer" => _live[ShadowAtlas],
            "DynamicAmbientOcclusion" => Viewed(AmbientOcclusion, v.SrgbRead ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm),
            "DynamicAmbientOcclusionDepth" => _live[HalfDepth],
            "DepthBuffer" => throw new InvalidOperationException("the downsamples bind their source themselves"),
            "SSAOOcclusionFactor" or "ParticleShadowBuffer" => Constant($"white {viewType}", Format.R8G8B8A8Unorm, viewType, 0xff),
            "CubemapFogTexture" or "LightCookieTexture" or "EnvironmentMap" => Constant($"zero {viewType}", Format.R8G8B8A8Unorm, viewType, 0),
            var s => throw new InvalidDataException($"{program.Name}: no input for attribute {s}"),
        };
    }

    /// <summary>A 1 x 1 texture (six layers for a cube view) of one byte value.</summary>
    private SceneDrawRunner.SceneImage Constant(string key, Format format, ImageViewType viewType, byte value)
    {
        if (_textures.TryGetValue(key, out var image))
            return image;
        var layers = viewType is ImageViewType.TypeCube or ImageViewType.TypeCubeArray ? 6u : 1u;
        image = _runner.CreateImage(format, 1, 1, 1, 1, layers, viewType, ImageUsageFlags.SampledBit);
        for (var l = 0u; l < layers; l++)
            _runner.Write(image, 0, l, [value, value, value, value]);
        return _textures[key] = image;
    }

    private SceneDrawRunner.SceneImage Lightmap(string name, bool srgb, ImageViewType viewType)
    {
        var entry = (_map.Entries!.GetValueOrDefault("vtex_c") ?? []).Single(e => e.DirectoryName.EndsWith("lightmaps", StringComparison.Ordinal) && e.FileName == name);
        _map.ReadEntry(entry, out var bytes);
        return Upload($"lightmap {name} {srgb} {viewType}", bytes, srgb, viewType);
    }

    private SceneDrawRunner.SceneImage Vtex(string path, bool srgb, ImageViewType viewType)
    {
        var key = $"{path} {srgb} {viewType}";
        if (_textures.TryGetValue(key, out var made))
            return made;
        foreach (var pak in new[] { _map, _csgo, _core })
            if (pak.FindEntry(path + "_c") is { } e)
            {
                pak.ReadEntry(e, out var bytes);
                return Upload(key, bytes, srgb, viewType);
            }
        throw new FileNotFoundException(path);
    }

    /// <summary>
    /// A .vtex_c with every mip it stores (the captures' textures hold the whole chain), each level's layers one after
    /// another (a cube's six faces as layers).
    /// </summary>
    private SceneDrawRunner.SceneImage Upload(string key, byte[] vtex, bool srgb, ImageViewType viewType)
    {
        if (_textures.TryGetValue(key, out var made))
            return made;
        using var resource = new Resource();
        resource.Read(new MemoryStream(vtex));
        var tex = (Texture)resource.DataBlock!;
        var format = GpuTextures.VulkanFormat(tex.Format, srgb) ?? throw new NotSupportedException($"{key}: {tex.Format}");
        var layers = tex.Depth > 1 && viewType != ImageViewType.Type3D ? tex.Depth : (tex.Flags & VTexFlags.CUBE_TEXTURE) != 0 ? 6 : 1;
        var metrics = tex.GetEveryMipLevelMetrics().ToList();
        var image = _runner.CreateImage(format, (uint)tex.Width, (uint)tex.Height, viewType == ImageViewType.Type3D ? (uint)tex.Depth : 1u, (uint)metrics.Count, (uint)layers, viewType, ImageUsageFlags.SampledBit);
        foreach (var (level, _, _, _, size) in metrics)
        {
            var data = new byte[size];
            tex.ReadTextureMipLevel(data, level);
            var per = data.Length / layers;
            for (var l = 0; l < layers; l++)
                _runner.Write(image, level, (uint)l, data.AsSpan(l * per, per));
        }
        return _textures[key] = image;
    }

    /// <summary>
    /// A depth downsample (downsample_depth with the material materials/dev/downsample_depth.vmat): a 4-vertex strip
    /// over the target's top left <paramref name="size"/> square, reading the source's depth; $Globals from the
    /// layer's g_nDownsampleRatio (the half layer 2: r_csgo_mixed_resolution_particles_scale, client 1811e7ae0; the
    /// quarter 4, 1811d84d0).
    /// </summary>
    private void Downsample(CubemapFacePipelines.Pass pass, Dictionary<string, int> dynamics, float ratio, string source, string target, int size, Action<Action>? observe)
    {
        var vs = Program(_coreShaders, "downsample_depth", "vs", [], []);
        var material = Material("materials/dev/downsample_depth.vmat");
        var ps = Program(_coreShaders, "downsample_depth", "ps", [], dynamics, Features(material));
        var bindings = new SceneDrawRunner.Bindings();
        foreach (var r in ps.Reflection.Resources)
        {
            if (r.Kind == SpirvReflection.Kind.UniformBuffer && r.Set == 1 && r.Binding == 0)
                bindings.Buffer(1, 0, 0, Buffer(Globals($"downsample {ratio}", ps, material, new() { ["g_nDownsampleRatio"] = VfxExpression.Value.Of(ratio) })), 0, Vk.WholeSize);
            else if (Named(ps, r.Set, r.Binding, r.Kind) is { SourceString: "DepthBuffer" })
                bindings.Image(r.Set, r.Binding, 0, _live[source].View);
            else
                throw new InvalidDataException($"{ps.Name}: set {r.Set} binding {r.Binding} not bound");
        }
        var template = new SceneDrawState
        {
            VertexSpirv = vs.Spirv,
            PixelSpirv = ps.Spirv,
            Bindings = CubemapFaceVertexLayout.Bindings(32, false),
            Attributes = CubemapFaceVertexLayout.Attributes(vs, CubemapFaceVertexLayout.DownsampleQuad, false),
            Viewport = new Viewport(0, 0, size, size, 0f, 1f),
            Scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)size, (uint)size)),
            DepthFormat = _live[target].Format,
        };
        var state = CubemapFacePipelines.Apply(template, pass, (VfxRenderStateInfoPixelShader)ps.RenderState);
        _runner.Draw(state, bindings, [(Buffer(Quad), 0ul), (Buffer(Zero16), 0ul)], null, new SceneDrawArgs(false, 4, 1, 0, 0, 0), null, _live[target]);
        observe?.Invoke(new Action(pass.ToString(), pass, null, target));
    }

    private static readonly byte[] Quad = CubemapFaceMeshes.DownsampleQuad();
    private static readonly byte[] Zero16 = new byte[16];
    private static readonly byte[] SkyVertices = MemoryMarshal.AsBytes(CubemapFaceMeshes.SkyCubeVertices.AsSpan()).ToArray();
    private static readonly byte[] SkyIndices = MemoryMarshal.AsBytes(CubemapFaceMeshes.SkyCubeIndices.AsSpan()).ToArray();

    /// <summary>
    /// The sky: scenesystem's cube (<see cref="CubemapFaceMeshes"/>), instance 0, with env_sky's material (sky.vfx,
    /// D_FOG_TYPE 0): g_matSkyWsToOs as the vertex $Globals, the material's pixel $Globals with SkyTint white
    /// (probe01's env_sky), its g_tSkyTexture.
    /// </summary>
    private void Sky(CubemapFaceBlocks.Face blocks, byte[] instanceIds, Action<Action>? observe)
    {
        var material = Material(_skyMaterial);
        var features = Features(material);
        var vs = Program(_csgoShaders, "sky", "vs", [], [], features);
        var ps = Program(_csgoShaders, "sky", "ps", [], new() { ["D_FOG_TYPE"] = 0 }, features);
        var bindings = new SceneDrawRunner.Bindings();
        foreach (var (program, stage) in new[] { (vs, 0), (ps, 1) })
            foreach (var r in program.Reflection.Resources)
            {
                if (r.Kind == SpirvReflection.Kind.UniformBuffer && r.Set == stage && r.Binding == 0)
                {
                    bindings.Buffer(r.Set, 0, 0, Buffer(stage == 0 ? blocks.SkyWsToOs
                        : Globals("sky " + _skyMaterial, ps, material, new() { ["SkyTint"] = VfxExpression.Value.Of(1f, 1f, 1f, 0f) })), 0, Vk.WholeSize);
                    continue;
                }
                var v = Named(program, r.Set, r.Binding, r.Kind) ?? throw new InvalidDataException(Unnamed(program, r.Set, r.Binding, r.Kind));
                switch (r.Kind)
                {
                    case SpirvReflection.Kind.UniformBuffer when v.Name == "PerViewConstantBuffer_t":
                        bindings.Buffer(r.Set, r.Binding, 0, Buffer(blocks.SkyView), 0, Vk.WholeSize);
                        break;
                    case SpirvReflection.Kind.SampledImage:
                        bindings.Image(r.Set, r.Binding, 0, Texture(program, v, r, material).View);
                        break;
                    case SpirvReflection.Kind.Sampler:
                        bindings.Sampler(r.Set, r.Binding, 0, SamplerOf(CubemapFaceSamplers.Samplers(program)[r.Binding - 14]));
                        break;
                    default:
                        throw Unbound(program, v.Name);
                }
            }
        var face = CubemapFaceDrawList.FaceSize;
        var template = new SceneDrawState
        {
            VertexSpirv = vs.Spirv,
            PixelSpirv = ps.Spirv,
            Bindings = CubemapFaceVertexLayout.Bindings(12, true),
            Attributes = CubemapFaceVertexLayout.Attributes(vs, CubemapFaceVertexLayout.SkyCube, true),
            Viewport = new Viewport(0, 0, face, face, 0f, 1f),
            Scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)face, (uint)face)),
            ColorFormat = _live[FaceColour].Format,
            DepthFormat = _live[FaceDepth].Format,
        };
        var state = CubemapFacePipelines.Apply(template, CubemapFacePipelines.Pass.Sky, (VfxRenderStateInfoPixelShader)ps.RenderState);
        _runner.Draw(state, bindings, [(Buffer(SkyVertices), 0ul), (Buffer(instanceIds), 0ul), (Buffer(Zero16), 0ul)], (Buffer(SkyIndices), 0ul, IndexType.Uint16),
            new SceneDrawArgs(true, 36, 1, 0, 0, 0), _live[FaceColour], _live[FaceDepth]);
        observe?.Invoke(new Action("sky", CubemapFacePipelines.Pass.Sky, FaceColour, FaceDepth));
    }

    public void Dispose()
    {
        ResetTargets();
        foreach (var b in _buffers.Values)
            _gpu.Destroy(b);
        foreach (var b in _transient)
            _gpu.Destroy(b);
        foreach (var t in _textures.Values)
            _runner.Destroy(t);
        foreach (var s in _samplers.Values)
            _gpu.Vk.DestroySampler(_gpu.Device, s, null);
        foreach (var p in new[] { _map, _csgo, _core, _csgoShaders, _coreShaders })
            p.Dispose();
    }
}
