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
/// coplanar surfaces of two materials pass the colour pass's depth test. Ours is aggregate order unless the
/// caller gives one (<see cref="Face"/>'s <c>materialOrder</c>).</item>
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
    public sealed record Aggregate(IReadOnlyList<AggregateCulling.Fragment> Fragments, bool OwnTransforms, IReadOnlyList<AggregateCulling.LodSetup> Lods);

    /// <summary>The layers an aggregate list is drawn in.</summary>
    public enum Layer
    {
        /// <summary>A sun cascade's depth into the shadow atlas.</summary>
        Shadow,

        /// <summary>"Depth Prepass Large": fragments of size at least <see cref="AggregateCulling.PrepassSplit"/>.</summary>
        PrepassLarge,

        /// <summary>"Depth Prepass Small": the rest.</summary>
        PrepassSmall,

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
        void Add(int v, Layer layer, float split)
        {
            foreach (var a in order)
            {
                var fragments = aggregates[a].Fragments;
                var size = split == 0f ? 0f : AggregateCulling.AggregateSize(fragments, view.Origin, 1f);
                var list = visible[v][a].Select((f, position) => (Fragment: f, Position: position))
                    .Where(e => split == 0f || AggregateCulling.InLayer(split, AggregateCulling.ScreenSize(view.Origin, 1f, fragments[e.Fragment].Centre, fragments[e.Fragment].Radius), size))
                    .ToList();
                if (list.Count > 0)
                    draws.Add(new ListDraw(v, layer, a, AggregateCulling.Commands(list, fragments, bases[(v, a)], aggregates[a].OwnTransforms)));
            }
        }
        for (var c = 0; c < views.Count - 1; c++)
            Add(c, Layer.Shadow, 0f);
        Add(-1, Layer.PrepassLarge, AggregateCulling.PrepassSplit);
        Add(-1, Layer.PrepassSmall, -AggregateCulling.PrepassSplit);
        Add(-1, Layer.Colour, 0f);
        return (draws, next);
    }
}
