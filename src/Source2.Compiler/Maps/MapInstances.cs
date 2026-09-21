using System.Numerics;

namespace Source2.Compiler;

/// <summary>
/// The instances a map places, and what each one puts in the compiled lump.
///
/// <para>A <c>CMapInstance</c> has no children of its own. It carries a
/// <c>target</c> pointing at a <c>CMapGroup</c> elsewhere in the same tree, and
/// the compile writes one transformed COPY of that group's entities per placement
/// while shipping none of the group itself. atixref places 144 instances over 15
/// distinct groups, which is 199 of its 821 lump entities.</para>
/// </summary>
public static class MapInstances
{
    /// <summary>One copy of one template entity.</summary>
    /// <param name="EmitAt">The walk index the copy is written at.</param>
    /// <param name="Template">Walk index of the entity copied, which the copy
    /// carries as its compile_source_id.</param>
    /// <param name="NodeId">The copy's own hammerUniqueId.</param>
    /// <param name="Origin">World position after the placement's transform.</param>
    /// <param name="Angles">Rotation after the placement's transform.</param>
    public sealed record Copy(int EmitAt, int Template, int NodeId, Vector3 Origin, Vector3 Angles);

    /// <summary>
    /// Every copy the map's instances produce, in lump order, and the walk indices
    /// of the entities that are TEMPLATES and so ship as copies instead.
    /// </summary>
    public static (IReadOnlyList<Copy> Copies, IReadOnlySet<int> Templates) Expand(
        DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> walked)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(walked);

        var world = document.OfType("CMapWorld").FirstOrDefault();
        if (world is null)
            return ([], new HashSet<int>());

        var byNode = new Dictionary<int, int>();
        for (var i = 0; i < walked.Count; i++)
            byNode.TryAdd(walked[i].NodeId, i);

        var tree = Survey(world, walked);
        if (tree.Instances.Count == 0)
            return ([], new HashSet<int>());

        // A block of ids per instance, in tree order, starting one past the map's
        // own highest node id. That ceiling is over EVERY element, not only the
        // entities: atixref's entities stop at 7244 and its nodes at 7247, and
        // Valve's first block is 7248. The 144 blocks then end at 7882, which is
        // exactly where the copies made by its one NESTED instance begin. Every
        // instance takes a block, including one sitting inside a group that is
        // itself an instance target and so is never placed in its own right.
        var next = document.Elements.Max(e => e.GetValue<int>("nodeID") ?? 0) + 1;
        var block = new Dictionary<DmxBinary.Element, int>();
        foreach (var instance in tree.Instances)
        {
            block[instance.Node] = next;
            next += tree.Nodes.GetValueOrDefault(instance.Target) + 1;
        }

        var copies = new List<Copy>();
        var templates = new HashSet<int>();
        foreach (var instance in tree.Instances)
            if (!tree.InsideTarget.Contains(instance.Node))
                Place(instance, Transform.Identity, tree, byNode, block, ref next, copies, templates,
                      tree.SubtreeEnd.GetValueOrDefault(instance.Parent, walked.Count));

        // Lump order is the walk with each instance's copies inserted where its
        // PARENT's subtree finishes. atixref's 17 instances under one group land at
        // walk 672, where that group ends, and its 126 root instances land after
        // every walked entity because the world's subtree ends there.
        return ([.. copies.OrderBy(c => c.EmitAt)], templates);
    }

    /// <summary>A placement's position and rotation, composed as instances nest.</summary>
    private readonly record struct Transform(Vector3 Origin, Vector3 Angles)
    {
        public static Transform Identity => new(Vector3.Zero, Vector3.Zero);

        public Transform Then(Vector3 origin, Vector3 angles)
            => new(Origin + Vector3.Transform(origin, Rotation(Angles)), Angles + angles);
    }

    private sealed record Instance(DmxBinary.Element Node, DmxBinary.Element Parent, DmxBinary.Element Target);

    private sealed record Tree(
        List<Instance> Instances,
        Dictionary<DmxBinary.Element, int> SubtreeEnd,
        Dictionary<DmxBinary.Element, int> Nodes,
        Dictionary<DmxBinary.Element, DmxBinary.Element> Parents,
        HashSet<DmxBinary.Element> InsideTarget);

    /// <summary>
    /// One walk for everything the expansion needs: the instances in tree order,
    /// how many entities each subtree has produced by the time it ends, each target
    /// group's node count, and which instances sit inside a target group and so are
    /// only ever reached through it.
    /// </summary>
    private static Tree Survey(DmxBinary.Element world, IReadOnlyList<MapEntities.Entity> walked)
    {
        var tree = new Tree([], [], [], [], []);
        var seen = new HashSet<DmxBinary.Element>();
        var produced = walked.Count > 0 && walked[0].IsWorld ? 1 : 0;
        Descend(world, tree, seen, ref produced);
        tree.SubtreeEnd[world] = produced;

        var targets = tree.Instances.Select(i => i.Target).ToHashSet();
        foreach (var target in targets)
            tree.Nodes[target] = Count(target);
        foreach (var instance in tree.Instances)
            for (var at = instance.Parent; at is not null; at = tree.Parents.GetValueOrDefault(at))
                if (targets.Contains(at))
                {
                    tree.InsideTarget.Add(instance.Node);
                    break;
                }
        return tree;
    }

    private static void Descend(
        DmxBinary.Element node, Tree tree, HashSet<DmxBinary.Element> seen, ref int produced)
    {
        foreach (var child in node.GetElements("children"))
        {
            if (!seen.Add(child))
                continue;
            tree.Parents[child] = node;
            if (child.Type is "CMapInstance" && child.Get<DmxBinary.Element>("target") is { } target)
                tree.Instances.Add(new Instance(child, node, target));
            else if (MapEntities.CarriesGameKeys(child))
                produced++;
            Descend(child, tree, seen, ref produced);
            tree.SubtreeEnd[child] = produced;
        }
    }

    private static int Count(DmxBinary.Element group)
    {
        var total = 0;
        foreach (var child in group.GetElements("children"))
            total += 1 + Count(child);
        return total;
    }

    /// <summary>
    /// Write one placement's copies, then recurse into any instance the group
    /// holds. A nested placement's ids continue past every top-level block, which
    /// is where atixref's four copies of its one nested group get 7882 to 7888.
    /// </summary>
    private static void Place(
        Instance instance, Transform outer, Tree tree, Dictionary<int, int> byNode,
        Dictionary<DmxBinary.Element, int> block, ref int next,
        List<Copy> copies, HashSet<int> templates, int emitAt)
    {
        var at = outer.Then(
            instance.Node.GetValue<Vector3>("origin") ?? Vector3.Zero,
            instance.Node.GetValue<Vector3>("angles") ?? Vector3.Zero);

        // A copy's id is its slot within the block, counted over EVERY node of the
        // group and not only the entities, so a filtered prop_static still takes
        // its place.
        var slot = 0;
        var nested = new List<DmxBinary.Element>();
        Emit(instance.Target, at, byNode, copies, templates, emitAt,
             block.GetValueOrDefault(instance.Node), ref slot, nested);

        foreach (var inner in nested)
            if (tree.Instances.FirstOrDefault(i => i.Node == inner) is { } found)
            {
                block[found.Node] = next;
                next += tree.Nodes.GetValueOrDefault(found.Target) + 1;
                Place(found, at, tree, byNode, block, ref next, copies, templates, emitAt);
            }
    }

    private static void Emit(
        DmxBinary.Element group, Transform at, Dictionary<int, int> byNode,
        List<Copy> copies, HashSet<int> templates, int emitAt, int start, ref int slot,
        List<DmxBinary.Element> nested)
    {
        foreach (var child in group.GetElements("children"))
        {
            var index = slot++;
            if (child.Type is "CMapInstance")
            {
                nested.Add(child);
            }
            else if (MapEntities.CarriesGameKeys(child)
                     && byNode.TryGetValue((int)(child.GetValue<int>("nodeID") ?? -1), out var template))
            {
                templates.Add(template);
                copies.Add(new Copy(
                    emitAt, template, start + index,
                    at.Origin + Vector3.Transform(
                        child.GetValue<Vector3>("origin") ?? Vector3.Zero, Rotation(at.Angles)),
                    Wrap((child.GetValue<Vector3>("angles") ?? Vector3.Zero) + at.Angles)));
            }
            Emit(child, at, byNode, copies, templates, emitAt, start, ref slot, nested);
        }
    }

    /// <summary>Source angles are pitch, yaw and roll about Y, Z and X, yaw
    /// first.</summary>
    private static Quaternion Rotation(Vector3 angles)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(angles.Y))
         * Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(angles.X))
         * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(angles.Z));

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    /// <summary>
    /// Angles compose by addition wrapped into [0, 360). One atixref template at
    /// yaw 270.00006 ships at 180.00006, 0, 270.00006 and 90.00005 under
    /// placements at yaw 270, 90, 0 and 180.
    /// </summary>
    private static Vector3 Wrap(Vector3 angles)
        => new(Wrap(angles.X), Wrap(angles.Y), Wrap(angles.Z));

    private static float Wrap(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0 ? wrapped + 360f : wrapped;
    }
}
