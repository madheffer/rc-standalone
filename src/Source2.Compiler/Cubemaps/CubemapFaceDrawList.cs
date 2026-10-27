using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The world aggregates' draws of one BuildCubemaps face (docs/CUBEMAPS.md, "The draw list"): which views and layers
/// draw which aggregate, each list's indirect commands from <see cref="AggregateCulling"/>, and each view's viewport.
/// <list type="bullet">
/// <item>Views: the face (-1) and its four sun cascades (0 to 2 from one fit, 3 the viewmodel cascade), culled as
/// the per-view culling says (ledgers 139, 140).</item>
/// <item>Passes in a face, as all three captures order them: the cascades 0 to 3 into the shadow atlas, the depth
/// prepass's Large then Small layer, then the colour layer.</item>
/// <item>One list per aggregate and layer (180041a00 / 180041f30): the aggregate's visible fragments the layer's
/// size test keeps, as one CMeshDrawPrimitive_t (0x70 bytes) with its material.</item>
/// <item>Order within a layer: the layer's sort (scenesystem 18010f4e0 full, 1801112a0 batch; 1800ed4c0 picks by
/// layer flag 1, flag 2 is LAYERFLAGS_DO_NOT_SORT). The full sort compares the item's +0x58 first; both then
/// compare the object's class, two +0x62 bits, the draw's flag 6 and three heap pointers (the per-object
/// material at +0x28, +0x48, the material at +0x20) before the draw's +100. In probe01's colour layer the two
/// aggregates tie up to the material's address (GROUND_TRUTH 156), and the order changes the face where
/// coplanar surfaces of two materials pass the colour pass's depth test. <see cref="MaterialOrder"/> gives the
/// order from the materials; <see cref="Face"/> takes it as <c>materialOrder</c> (aggregate order without).</item>
/// <item>Instances: each (view, aggregate) gets a block of its visible count; a command's first instance is the
/// block's base plus the entry's visible position. The game hands blocks out by an atomic counter in thread order
/// (ledger 139); ours go face view first, then cascades 0 to 3, in aggregate order, from 1 (instance 0 is the
/// sky's null record, <see cref="CubemapFaceConstants.Instances"/>).</item>
/// </list>
/// Not modelled: layers other than the opaque ones (alpha-tested, translucent: atixref's foliage), non-aggregate
/// scene objects, the viewmodel's draws, shadowed lights' own views.
/// </summary>
public static class CubemapFaceDrawList
{
    /// <summary>A world node aggregate as the cull reads it.</summary>
    public sealed record Aggregate(IReadOnlyList<AggregateCulling.Fragment> Fragments, bool OwnTransforms, IReadOnlyList<AggregateCulling.LodSetup> Lods,
        Membership? Layers = null);

    /// <summary>
    /// What scenesystem's layer selection reads of an aggregate (<see cref="CubemapLayerFlags"/>, GROUND_TRUTH 159):
    /// the object's +0x78 and layer word +0x80 (<see cref="CubemapLayerFlags.ObjectWords"/>), each fragment's record
    /// flags, its draw call's +0x1c and +0x14 and the object's +0xe0. Without it every fragment joins the shadow,
    /// prepass Large / Small and colour layers, as before the selection was read.
    /// </summary>
    public sealed record Membership(uint Flags78, ulong Layer80, IReadOnlyList<ushort> FragmentFlags, uint DrawCall1c, ushort DrawCall14, uint ObjectE0);

    /// <summary>
    /// The scene layers a cubemap face draws world aggregates through, with the game's fields (layer_members.js on
    /// probe01 and atixref, 1.41.9.2: the BuildCubemaps view's layers and its cascades' "CSM" views'; every cascade
    /// variant captured gives the same result for world aggregates, GROUND_TRUTH 159). The view's other layers
    /// (SkyOccluder*, ToolsUtil Opaque, OpaqueRefractDepth, the cascades' Depth Prepass AlphaTested) draw no aggregate
    /// in RenderDoc's passes of probe01, s2c_l5probe and atixref.
    /// </summary>
    public static class CubemapLayers
    {
        public static readonly CubemapLayerFlags.Layer ShadowOpaque = new("DrawDepthOpaque", 0x1401800, 0xc000400001, 0x40001000100, 0x1100, 0, 0, 0, 2048, 0f);
        public static readonly CubemapLayerFlags.Layer PrepassLarge = new("Depth Prepass Large", 0x101400005, 0x400001, 0x46040020000000, 0x1100, 0, 0, 2097152, 1074595840, AggregateCulling.PrepassSplit);
        public static readonly CubemapLayerFlags.Layer PrepassSmall = new("Depth Prepass Small", 0x101400004, 0x400001, 0x46040030000000, 0x1100, 0, 0, 2097152, 1074595840, -AggregateCulling.PrepassSplit);
        public static readonly CubemapLayerFlags.Layer PrepassAlphaTested = new("Depth Prepass AlphaTested", 0x101400004, 0x20400001, 0x46040000000000, 0x1100, 0, 0, 2097152, 1074595840, 0f);
        public static readonly CubemapLayerFlags.Layer Colour = new("Static Opaque Forward", 0x400104, 0x1000400001, 0x4000000040000, 0x1100, 0, 0, 0, 1074595840, 0f);

        /// <summary>The layer pass context's +0x3c and +0x65 in every captured cube-view test.</summary>
        public const float Context3c = 1f;
        public const byte Context65 = 0;
    }

    /// <summary>The layers an aggregate list is drawn in.</summary>
    public enum Layer
    {
        /// <summary>A sun cascade's depth into the shadow atlas.</summary>
        Shadow,

        /// <summary>"Depth Prepass Large": fragments of size at least <see cref="AggregateCulling.PrepassSplit"/>.</summary>
        PrepassLarge,

        /// <summary>"Depth Prepass Small": the rest.</summary>
        PrepassSmall,

        /// <summary>"Depth Prepass AlphaTested": alpha-tested draw calls, after Small (atixref's capture).</summary>
        PrepassAlphaTested,


        /// <summary>The shaded opaque layer.</summary>
        Colour,
    }

    /// <summary>One indirect draw call: <paramref name="View"/> -1 is the face, 0 to 3 its cascades.</summary>
    public sealed record ListDraw(int View, Layer Layer, int Aggregate, IReadOnlyList<AggregateCulling.Command> Commands);

    /// <summary>A viewport and scissor rectangle in pixels.</summary>
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>The face's render size (BuildCubemaps' 1024).</summary>
    public const int FaceSize = 1024;

    /// <summary>
    /// The cascades' tiles in the shadow atlas at shadow quality 2 (1280 tiles; cascades 0 to 2 in the top row, the
    /// viewmodel cascade below). Measured on every face of probe01, s2c_l5probe and atixref; the atlas allocator
    /// itself is not read (CUBEMAPS.md roadmap 1).
    /// </summary>
    public static readonly (int X, int Y)[] CascadeTiles = [(0, 0), (1280, 0), (2560, 0), (0, 2048)];

    /// <summary>A view's viewport and scissor: the cascade's tile, or the whole face.</summary>
    public static Rect Viewport(int view)
        => view < 0 ? new Rect(0, 0, FaceSize, FaceSize)
            : new Rect(CascadeTiles[view].X, CascadeTiles[view].Y, CubemapShadowCascades.Inputs.Resolution, CubemapShadowCascades.Inputs.Resolution);

    /// <summary>
    /// The face's frustum (-1) and its cascades' (0 to 3): each view cascade after the first excludes what its
    /// predecessor covers, and every cascade of a fit culls to that fit's caster volume (ledger 140).
    /// </summary>
    public static Dictionary<int, AggregateCulling.Frustum> Views(CubemapFaceViews.View view, Vector3 toSun, float near, float far, bool scriptedCamera)
    {
        var cascades = CubemapShadowCascades.GameCascades(view, toSun, near, far, scriptedCamera);
        var views = new Dictionary<int, AggregateCulling.Frustum> { [-1] = AggregateCulling.FaceFrustum(view) };
        var worldToProjection = AggregateCulling.WorldToProjection(view);
        var inverse = AggregateCulling.Invert4(worldToProjection)!;
        var (viewReach, viewmodelReach) = CubemapShadowCascades.GameReaches(view, near, far, scriptedCamera);
        var viewVolume = AggregateCulling.CullingVolume(AggregateCulling.CasterVolume(worldToProjection, inverse, toSun, viewReach));
        var viewmodelVolume = AggregateCulling.CullingVolume(AggregateCulling.CasterVolume(worldToProjection, inverse, toSun, viewmodelReach));
        for (var c = 0; c < cascades.Count; c++)
        {
            var viewmodel = c == cascades.Count - 1;
            views[c] = AggregateCulling.CascadeFrustum(cascades[c], c > 0 && !viewmodel ? cascades[c - 1] : null, viewmodel ? viewmodelVolume : viewVolume);
        }
        return views;
    }

    /// <summary>
    /// The materials the engine creates before a map's own, in creation order (Resources/cubemap_engine_materials.txt,
    /// tools/cubehost/material_order.py from cubehost runs of probe01 and atixref, CS2 1.41.9.2 with -tools).
    /// </summary>
    public static readonly IReadOnlyList<string> EngineMaterials = LoadEngineMaterials();

    private static List<string> LoadEngineMaterials()
    {
        using var stream = typeof(CubemapFaceDrawList).Assembly.GetManifestResourceStream("cubemap_engine_materials.txt")
            ?? throw new InvalidOperationException("cubemap_engine_materials.txt is not embedded");
        using var reader = new StreamReader(stream);
        var names = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#')
                names.Add(line);
        return names;
    }

    /// <summary>
    /// The aggregates in the order the layer sort draws them, given each one's material path: the sort ends on
    /// the CMaterial2's address (GROUND_TRUTH 156). A material created before the map loads
    /// (<see cref="EngineMaterials"/>) always sorts before the map's own, and those among themselves follow their
    /// creation order. The map's own follow creation order only to about 96%, and that order is the order their
    /// loads complete, which is not data: they come after in ordinal path order (ours, not Valve's); aggregates
    /// without a material last, then aggregate order.
    /// </summary>
    public static int[] MaterialOrder(IReadOnlyList<string?> materials)
    {
        var engine = EngineIndex.Value;
        return [.. Enumerable.Range(0, materials.Count)
            .OrderBy(a => materials[a] is { } m && engine.TryGetValue(m, out var i) ? i : int.MaxValue)
            .ThenBy(a => materials[a] == null)
            .ThenBy(a => materials[a] ?? "", StringComparer.Ordinal)
            .ThenBy(a => a)];
    }

    private static readonly Lazy<Dictionary<string, int>> EngineIndex = new(() =>
        EngineMaterials.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal));

    /// <summary>
    /// Every aggregate draw of the face, in pass order (cascades 0 to 3, prepass Large, prepass Small, colour), and the
    /// number of instance records the blocks use (instance 0 included). <paramref name="materialOrder"/> lists the
    /// aggregates in the order the layer sort puts them (by default aggregate order).
    /// </summary>
    public static (List<ListDraw> Draws, int Instances) Face(CubemapFaceViews.View view, Vector3 toSun, IReadOnlyList<Aggregate> aggregates,
        bool scriptedCamera, float near = 1f, float far = 10000f, IReadOnlyList<int>? materialOrder = null)
    {
        var order = materialOrder ?? [.. Enumerable.Range(0, aggregates.Count)];
        if (order.Count != aggregates.Count || order.Distinct().Count() != aggregates.Count || order.Any(a => a < 0 || a >= aggregates.Count))
            throw new ArgumentException("not an order of the aggregates", nameof(materialOrder));
        var views = Views(view, toSun, near, far, scriptedCamera);
        // LODs are picked in the root view (the face) for its cascades too.
        var visible = views.ToDictionary(kv => kv.Key, kv => aggregates.Select(a => AggregateCulling.Visible(kv.Value, a.Fragments, a.Lods, view.Origin)).ToList());
        var bases = new Dictionary<(int, int), int>();
        var next = 1;
        foreach (var v in views.Keys.Order())
            for (var a = 0; a < aggregates.Count; a++)
                if (visible[v][a].Count > 0)
                {
                    bases[(v, a)] = next;
                    next += visible[v][a].Count;
                }
        var draws = new List<ListDraw>();
        void Add(int v, Layer layer, CubemapLayerFlags.Layer definition)
        {
            var split = definition.Split;
            foreach (var a in order)
            {
                var fragments = aggregates[a].Fragments;
                // scenesystem 1800f3b10's object test, 180041f30's of the draw call, then 180041a00's of each fragment
                // (the layer is its own view layer and no root; the visible entries' +0xc bit was 0 in every capture).
                var m = aggregates[a].Layers;
                if (m == null ? layer is Layer.PrepassAlphaTested
                        : !CubemapLayerFlags.ObjectPasses(definition, m.Flags78, m.Layer80)
                          || !CubemapLayerFlags.DrawCallPasses(definition, m.DrawCall1c, m.DrawCall14, m.ObjectE0, CubemapLayers.Context3c, CubemapLayers.Context65))
                    continue;
                var size = split == 0f ? 0f : AggregateCulling.AggregateSize(fragments, view.Origin, 1f);
                var list = visible[v][a].Select((f, position) => (Fragment: f, Position: position))
                    .Where(e => m == null || CubemapLayerFlags.FragmentPasses(definition, definition, m.FragmentFlags[e.Fragment], 0, viewIsRoot: false))
                    .Where(e => split == 0f || AggregateCulling.InLayer(split, AggregateCulling.ScreenSize(view.Origin, 1f, fragments[e.Fragment].Centre, fragments[e.Fragment].Radius), size))
                    .ToList();
                if (list.Count > 0)
                    draws.Add(new ListDraw(v, layer, a, AggregateCulling.Commands(list, fragments, bases[(v, a)], aggregates[a].OwnTransforms)));
            }
        }
        // The cascades' views also have a "Depth Prepass AlphaTested" layer, which the cascade passes do not draw
        // (atixref's capture: every alpha-tested caster's one cascade list is its DrawDepthOpaque one).
        for (var c = 0; c < views.Count - 1; c++)
            Add(c, Layer.Shadow, CubemapLayers.ShadowOpaque);
        Add(-1, Layer.PrepassLarge, CubemapLayers.PrepassLarge);
        Add(-1, Layer.PrepassSmall, CubemapLayers.PrepassSmall);
        Add(-1, Layer.PrepassAlphaTested, CubemapLayers.PrepassAlphaTested);
        Add(-1, Layer.Colour, CubemapLayers.Colour);
        return (draws, next);
    }
}
