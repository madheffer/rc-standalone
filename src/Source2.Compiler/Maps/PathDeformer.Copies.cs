using System.Numerics;
using System.Runtime.CompilerServices;

namespace Source2.Compiler.Maps;

/// <summary>
/// Path deformers an instance copy or a prefab's map places: what the bake
/// (MapDoc_BakeInstances, 180f60740) does to them, step by step. Captured
/// with tools/physics/capture_deformerevents.py (every recentre, update,
/// reference change, TransformBy and node origin store in call order) on
/// s2c_deformnest, s2c_deformnest2, s2c_deformpath3 and s2c_deformpath4, and
/// read from the decompile (resourcecompiler 0923):
/// <list type="bullet">
/// <item>A recentre (18117aeb0) runs on each change of the reference
/// (181075180 to vf 0x740) and on each change of the path it holds
/// (180f8f5a0 to the path's listeners): when the path is in the node's
/// "root" (180f8c0c0: the nearest ancestor that is a world or an instance's
/// target) the node's origin goes to the path's point, its subtree moved
/// along, and the lattice is fitted to the path as it then stands. A
/// recentre that leaves the origin where it is stores nothing (181077c20
/// compares the floats).</item>
/// <item>A path's change notifies its listeners only while it is not
/// already doing so (+0x2c8). A recentre that the reference started and
/// that moves the node's own target (a path below the deformer) is told
/// of that move k + 2 more times for a path of k nodes, each moving it by
/// the same step again (k + 3 recentres in all: 5, 6 and 7 for 2, 3 and 4
/// nodes); one the path's own notification started is not (the flag is
/// set).</item>
/// <item>An instance copy (<see cref="Stage.Instance"/>): CopyFrom
/// (181178b00) copies the reference; the id remap (180f90cb0 through vf
/// 0x210, 180f75580) points it at the copy of its target when the group
/// holds it, which recentres the copy where its source stands, with the
/// cascade above for a target below it; a target outside the copy is
/// recentred on where it stands. Then the group setter (180f834d0) moves
/// the copy by the step (the deformer by CTransform composition, its
/// subtree along), and each path the step moved recentres the deformers
/// holding it, in tree order: an outer one before an inner one, which its
/// move has carried along.</item>
/// <item>A prefab (<see cref="Stage.Prefab"/>): its map is loaded on its own
/// (<see cref="Resolve"/>) and its world placed (181019d00, the group
/// setter skipping instance targets, 180f8ba70), after which every path
/// deformer of the map is recentred once, in tree order. Its nodes then
/// move into the host world (180f96d40), where each reference resolves
/// again by the id its map gave it (180fca9b0 on the host world's table):
/// a host node with that referenceID wins over the prefab's own copy, which
/// still carries it (the host's own nodes are registered first; s2c_deformpath3
/// is built on a copy of s2c_deformpath, so its prefabs' deformers move onto the
/// host's paths). Each is recentred there, a target below it with the
/// cascade. Last the remap gives the copies new ids, and a reference the
/// host had taken comes back to its own copy, recentred again.</item>
/// <item>Copies inside a prefab's map collapse in a later round, from the
/// state the prefab left: their instance's target group (not moved by the
/// prefab) with the recentres it had.</item>
/// </list>
/// The lattice a copy ships is the one fitted last, at the copy's final
/// place and its target's.
/// </summary>
internal static partial class PathDeformer
{
    private const string ParentKey = "s2c.parent";
    private const string TargetGroupKey = "s2c.instancetarget";
    private const string AboveKey = "s2c.pathdeformersabove";

    /// <summary>The node a prefab map's deformer finds by its target's referenceID in the host world, and the prefabs whose map holds it (<see cref="AttachHosts"/>).</summary>
    private const string HostKey = "s2c.pathhost";
    private const string HostChainKey = "s2c.pathhostchain";
    private const string HostWrapsKey = "s2c.pathhostwraps";

    private static readonly object World = new();

    /// <summary>One recentre: the deformer's origin from one point to another, its subtree moved along.</summary>
    private sealed record Event(DmxBinary.Element Deformer, Vector3 From, Vector3 To);

    /// <summary>
    /// One collapse: an instance's copy (<see cref="Instance"/>, its target
    /// group the root), or a prefab's map (its world the root) with no
    /// instances after it, and the recentres it makes before its step
    /// (<see cref="Pre"/>, where the copy's source stands) and after.
    /// </summary>
    private sealed class Stage(IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs, DmxBinary.Element root, Stage? source)
    {
        public IReadOnlyList<DmxBinary.Element> Through { get; } = through;
        public IReadOnlyList<DmxBinary.Element> Prefabs { get; } = prefabs;
        public DmxBinary.Element Root { get; } = root;
        public Stage? Source { get; } = source;
        public bool Instance => Through.Count > 0;
        public bool Prefab => !Instance;
        public List<Event> Pre { get; } = [];
        public List<Event> Post { get; } = [];
        public bool Stepped { get; set; }
    }

    private sealed class ChainComparer : IEqualityComparer<DmxBinary.Element[]>
    {
        public static readonly ChainComparer Instance = new();

        public bool Equals(DmxBinary.Element[]? a, DmxBinary.Element[]? b)
            => a != null && b != null && a.Length == b.Length && a.Zip(b).All(p => ReferenceEquals(p.First, p.Second));

        public int GetHashCode(DmxBinary.Element[] chain)
            => chain.Aggregate(17, (h, e) => h * 31 + RuntimeHelpers.GetHashCode(e));
    }

    private static readonly ConditionalWeakTable<DmxBinary.Element, Dictionary<DmxBinary.Element[], Stage?>> Stages = [];

    /// <summary>
    /// For every path deformer of a prefab map loaded under <paramref name="host"/>:
    /// the node its reference finds when the prefab's nodes move into the host
    /// world, by the referenceID its own map gave the target, when that is not
    /// its own target. The host world's table (180fca9b0, +0x2b48) keeps the
    /// first node registered with an id: the host's own nodes, then each
    /// prefab's in the order the bake collapses them (the host's prefabs in
    /// tree order, then the prefabs inside their maps), a prefab's node taking
    /// an id nobody holds yet. A later prefab of the same map so finds the
    /// earlier one's copy (s2c_deformpath4: its second prefab of s2c_deformpath
    /// recentres onto the first one's paths, then back onto its own).
    /// </summary>
    internal static void AttachHosts(DmxBinary.Document host, FgdSchema? schema)
    {
        var registry = new Dictionary<ulong, (DmxBinary.Element Node, DmxBinary.Element[]? Chain)>();
        foreach (var e in host.Elements)
            if (e.GetValue<ulong>("referenceID") is { } id && id != 0)
                registry.TryAdd(id, (e, null));
        var round = PrefabsIn(host).Select(p => new[] { p }).ToList();
        while (round.Count > 0)
        {
            var next = new List<DmxBinary.Element[]>();
            foreach (var chain in round)
            {
                if (chain[^1].Get<DmxBinary.Document>(MapPrefabs.DocumentKey) is not { } map)
                    continue;
                foreach (var e in map.Elements)
                    if (e.GetValue<ulong>("referenceID") is { } id && id != 0)
                        registry.TryAdd(id, (e, chain));
                foreach (var d in map.Elements.Where(e => e.Type == "CMapDeformerPath"))
                {
                    if (d.Get<DmxBinary.Element>("targetPath")?.GetValue<ulong>("targetReferenceID") is not { } id || id == 0
                        || !registry.TryGetValue(id, out var found) || ReferenceEquals(found.Node, d.Get<DmxBinary.Element>(TargetKey)))
                        continue;
                    d.Attributes[HostKey] = found.Node;
                    if (found.Chain != null)
                        d.Attributes[HostChainKey] = found.Chain;
                    if (MapEntities.ReadNode(found.Node) is { PathNodes: not null } entity)
                        d.Attributes[HostWrapsKey] = EntityLumpAuthor.Wraps(entity, schema);
                }
                next.AddRange(PrefabsIn(map).Select(p => (DmxBinary.Element[])[.. chain, p]));
            }
            round = next;
        }
    }

    // A map's loaded prefabs in tree order, but for those an instance's target holds (its copies place them).
    private static IEnumerable<DmxBinary.Element> PrefabsIn(DmxBinary.Document map)
        => map.OfType("CMapWorld").SelectMany(w => Descendants(w))
              .Where(e => e.Type == "CMapPrefab" && e.Attributes.ContainsKey(MapPrefabs.DocumentKey) && !InTargetGroup(e));

    /// <summary>
    /// The deformer of a CMapDeformerPath node an instance copy or a prefab's
    /// map placed: the lattice transform at the copy's final place, fitted to
    /// its final target (the copy's own, or the host's or outer node it holds).
    /// </summary>
    internal static void Place(LatticeDeformer d, DmxBinary.Element node, IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
    {
        var stage = StageOf(through, prefabs);
        var (origin, angles) = StateIn(stage, node, Stored(node), after: true);
        d.Transform = LatticeTransform(origin, angles, d.Size);
        if (FinalTarget(stage, node) is not { } target || ControlsIn(stage, target) is not { } controls)
            return;
        d.Points = [];
        d.Handles = [];
        d.Segments = d.DivisionsY = d.DivisionsZ = 0;
        d.Mode = 1;
        if (Polygon(controls, d.Transform) is { Length: > 0 } polygon)
            FitLattice(d, polygon, SettingsOf(node));
    }

    /// <summary>
    /// Each placed path deformer's final origin and angles (float bits) in
    /// every copy the map's instances and prefabs make of it, for comparing
    /// with a capture (tools/physics/capture_deformerevents.py).
    /// </summary>
    internal static IEnumerable<string> Describe(DmxBinary.Element node, IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
    {
        var stage = StageOf(through, prefabs);
        var (o, a) = StateIn(stage, node, Stored(node), after: true);
        static string F(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");
        yield return $"{node.GetValue<int>("nodeID")} o {o.X:R} {o.Y:R} {o.Z:R} a {a.X:R} {a.Y:R} {a.Z:R} bits {F(a.X)} {F(a.Y)} {F(a.Z)}";
        foreach (var e in stage?.Pre ?? [])
            yield return $"  pre {e.Deformer.GetValue<int>("nodeID")} {e.From:R} -> {e.To:R}";
        foreach (var e in stage?.Post ?? [])
            yield return $"  post {e.Deformer.GetValue<int>("nodeID")} {e.From:R} -> {e.To:R}";
    }

    /// <summary>Whether a node's copies move with path deformer recentres: one below a path deformer, or one itself.</summary>
    internal static bool Recentred(DmxBinary.Element node)
        => node.Attributes.ContainsKey(AboveKey) || node.Type == "CMapDeformerPath";

    /// <summary>
    /// Where a copy of <paramref name="node"/> (below a path deformer, or one
    /// itself) ends: <paramref name="placed"/> stands for its stored origin
    /// and angles (a path node's, a lattice locator's), moved as the copy
    /// moves them.
    /// </summary>
    internal static (Vector3 Origin, Vector3 Angles) Placed(DmxBinary.Element node, (Vector3 Origin, Vector3 Angles) placed,
                                                           IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
        => StateIn(StageOf(through, prefabs), node, placed, after: true);

    private static (Vector3 Origin, Vector3 Angles) Stored(DmxBinary.Element node)
        => (node.GetValue<Vector3>("origin") ?? Vector3.Zero, node.GetValue<Vector3>("angles") ?? Vector3.Zero);

    private static Stage? StageOf(IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
    {
        if (through.Count == 0 && prefabs.Count == 0)
            return null;
        DmxBinary.Element[] chain = [.. prefabs, .. through];
        var cache = Stages.GetValue(chain[0], _ => new Dictionary<DmxBinary.Element[], Stage?>(ChainComparer.Instance));
        lock (cache)
        {
            if (cache.TryGetValue(chain, out var known))
                return known;
            var stage = Build(through, prefabs);
            cache[chain] = stage;
            return stage;
        }
    }

    private static Stage? Build(IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
    {
        if (through.Count > 0)
        {
            if (through[^1].Get<DmxBinary.Element>("target") is not { } group)
                return null;
            var stage = new Stage(through, prefabs, group, prefabs.Count > 0 ? StageOf([], prefabs) : null);
            var deformers = Deformers(group);
            foreach (var d in deformers)
                Recentre(stage, stage.Pre, d, Own(d), told: true);
            stage.Stepped = true;
            foreach (var d in deformers)
                if (Own(d) is { } own && Below(own.Path, group))
                    Recentre(stage, stage.Post, d, own, told: false);
            return stage;
        }
        if (prefabs[^1].Get<DmxBinary.Element>(MapPrefabs.WorldKey) is not { } world)
            return null;
        var placed = new Stage(through, prefabs, world, null) { Stepped = true };
        var all = Deformers(world);
        foreach (var d in all)
            Recentre(placed, placed.Post, d, Own(d), told: false);
        var hosted = new List<DmxBinary.Element>();
        foreach (var d in all)
        {
            var host = Host(d);
            if (host != null)
                hosted.Add(d);
            Recentre(placed, placed.Post, d, host ?? Own(d), told: true);
        }
        foreach (var d in hosted)
            Recentre(placed, placed.Post, d, Own(d), told: true);
        return placed;
    }

    // The path deformers below a root that hold a resolved reference or a host's node, in tree order.
    private static List<DmxBinary.Element> Deformers(DmxBinary.Element root)
        => [.. Descendants(root).Where(e => e.Type == "CMapDeformerPath" && (e.Attributes.ContainsKey(TargetKey) || e.Attributes.ContainsKey(HostKey)))];

    // A target path, and for one an earlier prefab's map holds, that prefab chain (its copy's stage places it).
    private readonly record struct Target(DmxBinary.Element Path, bool Wraps, DmxBinary.Element[]? Chain = null);

    private static Target? Own(DmxBinary.Element d)
        => d.Get<DmxBinary.Element>(TargetKey) is { } path ? new Target(path, d.GetValue<bool>(WrapsKey) ?? false) : null;

    private static Target? Host(DmxBinary.Element d)
        => d.Get<DmxBinary.Element>(HostKey) is { } path ? new Target(path, d.GetValue<bool>(HostWrapsKey) ?? false, d.Get<DmxBinary.Element[]>(HostChainKey)) : null;

    private static Target? FinalTarget(Stage? stage, DmxBinary.Element d)
        => Own(d) ?? (stage is { Prefab: true } ? Host(d) : null);

    // One recentre of a copy onto a target as the stage then has it, and, when the reference
    // started it and the target is below the deformer, the k + 2 more its target's moves send.
    private static void Recentre(Stage stage, List<Event> events, DmxBinary.Element d, Target? target, bool told)
    {
        if (target is not { } t || t.Path.Type is not ("CMapPath" or "CMapCable") || !ReferenceEquals(RootOf(t.Path, stage), RootOf(d, stage)))
            return;
        var times = told && Below(t.Path, d) ? 3 + (MapEntities.ReadNode(t.Path)?.PathNodes?.Count ?? 0) : 1;
        var settings = SettingsOf(d);
        for (var i = 0; i < times; i++)
        {
            var from = StateIn(stage, d, Stored(d), after: stage.Stepped).Origin;
            if (ControlsIn(stage, t) is not { } controls || Centre(controls, settings) is not { } to)
                return;
            if (from.X == to.X && from.Y == to.Y && from.Z == to.Z)
                return;
            events.Add(new Event(d, from, to));
        }
    }

    // A node's origin and angles in a stage: as its source stage leaves them, the recentres made before
    // the step, the step if the stage moves the node, then (after) the recentres made after it.
    private static (Vector3 Origin, Vector3 Angles) StateIn(Stage? stage, DmxBinary.Element node, (Vector3 Origin, Vector3 Angles) at, bool after)
    {
        if (stage == null)
            return at;
        var copied = stage.Instance ? Below(node, stage.Root) : ReferenceEquals(TopOf(node), stage.Root) && !InTargetGroup(node);
        if (stage.Instance && !copied)
            return at;
        if (stage.Source != null)
            at = StateIn(stage.Source, node, at, after: true);
        at = Apply(stage.Pre, node, at);
        if (!after)
            return at;
        if (copied)
            at = SettleWorld.UnderDeformerStep(at.Origin, at.Angles, stage.Through, stage.Prefabs);
        return Apply(stage.Post, node, at);
    }

    private static (Vector3 Origin, Vector3 Angles) Apply(List<Event> events, DmxBinary.Element node, (Vector3 Origin, Vector3 Angles) at)
    {
        foreach (var e in events)
        {
            if (ReferenceEquals(e.Deformer, node))
                at = (e.To, at.Angles);
            else if (node.Get<DmxBinary.Element[]>(AboveKey) is { } above && above.Contains(e.Deformer))
                at = Moved(at.Origin, at.Angles, e.From, e.To);
        }
        return at;
    }

    // A target path's world control points in a stage: placed by the stage that holds it (the copy
    // that copied it, an outer instance's, the prefab's, or the host's own when no stage does).
    private static float[]? ControlsIn(Stage? stage, Target target)
    {
        var (path, wraps) = (target.Path, target.Wraps);
        if (MapEntities.ReadNode(path) is not { PathNodes: not null } entity)
            return null;
        var holder = target.Chain is { } chain ? StageOf([], chain) : HolderOf(stage, path);
        if (holder == null)
            return EntityLumpAuthor.PathControlsWorld(entity, wraps);
        MapEntities.Entity placed;
        if (Recentred(path) || UnderDeformer(path))
        {
            var after = holder.Stepped;
            var nodes = entity.PathNodes.Select(n => n with { Origin = StateIn(holder, path, (n.Origin, Vector3.Zero), after).Origin }).ToList();
            var (o, a) = StateIn(holder, path, (entity.Origin, entity.Angles), after);
            placed = entity with { Origin = nodes is [var head, ..] ? head.Origin : o, Angles = a, PathNodes = nodes };
        }
        else
        {
            // Before its step a copy stands where its source does (a target group the prefab did not move).
            var moved = holder.Instance ? holder.Stepped : !InTargetGroup(path);
            placed = moved ? SettleWorld.PlacedPath(entity, holder.Through, holder.Prefabs) : entity;
        }
        return EntityLumpAuthor.PathControlsWorld(placed, wraps);
    }

    // The stage whose copy holds a path: the stage itself, the outer instances' (the ones whose target
    // groups hold it, outermost first) or the prefab's, null for a host node.
    private static Stage? HolderOf(Stage? stage, DmxBinary.Element path)
    {
        if (stage == null)
            return null;
        if (stage.Instance)
        {
            if (Below(path, stage.Root))
                return stage;
            var moves = stage.Through.TakeWhile(i => i.Get<DmxBinary.Element>("target") is { } g && Below(path, g)).ToList();
            return StageOf(moves, stage.Prefabs) is { } outer && (outer.Instance || ReferenceEquals(TopOf(path), outer.Root)) ? outer : null;
        }
        return ReferenceEquals(TopOf(path), stage.Root) ? stage : null;
    }

    // 180f8c0c0 from the node's parent: the nearest instance target above it, or the world it is in.
    // A copy's nodes are in the host world.
    private static object RootOf(DmxBinary.Element node, Stage stage)
    {
        if (stage.Instance && Below(node, stage.Root))
            return World;
        for (var p = node.Get<DmxBinary.Element>(ParentKey); p != null; p = p.Get<DmxBinary.Element>(ParentKey))
        {
            if (p.Type == "CMapWorld")
                return World;
            if (p.Attributes.ContainsKey(TargetGroupKey))
                return p;
        }
        return World;
    }

    private static bool InTargetGroup(DmxBinary.Element node)
    {
        for (var p = node.Get<DmxBinary.Element>(ParentKey); p != null; p = p.Get<DmxBinary.Element>(ParentKey))
            if (p.Attributes.ContainsKey(TargetGroupKey))
                return true;
        return false;
    }

    // Whether a CMapDeformer of any kind stands above the node in its map (its TransformBy moves the node).
    private static bool UnderDeformer(DmxBinary.Element node)
    {
        for (var p = node.Get<DmxBinary.Element>(ParentKey); p != null && p.Type != "CMapWorld" && !p.Attributes.ContainsKey(TargetGroupKey); p = p.Get<DmxBinary.Element>(ParentKey))
            if (p.Type.StartsWith("CMapDeformer", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static DmxBinary.Element TopOf(DmxBinary.Element node)
    {
        var top = node;
        while (top.Get<DmxBinary.Element>(ParentKey) is { } p)
            top = p;
        return top;
    }

    private static bool Below(DmxBinary.Element node, DmxBinary.Element root)
    {
        for (var e = node; e != null; e = e.Get<DmxBinary.Element>(ParentKey))
            if (ReferenceEquals(e, root))
                return true;
        return false;
    }
}
