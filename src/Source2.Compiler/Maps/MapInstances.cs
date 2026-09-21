using System.Numerics;

namespace Source2.Compiler;

/// <summary>
/// The instances a map places, and what each one puts in the compiled lump.
///
/// <para>A <c>CMapInstance</c> has no children of its own. It carries a
/// <c>target</c> pointing at a <c>CMapGroup</c> elsewhere in the same tree, and
/// the compile writes one transformed COPY of that group's entities per placement
/// while shipping none of the group itself. atixref places 144 instances over 15
/// distinct groups, which is 199 of its 764 lump entities.</para>
/// </summary>
public static class MapInstances
{
    /// <summary>One placement of one group.</summary>
    /// <param name="AfterEntity">How many entities the walk had produced when it
    /// reached this instance, which is where its copies belong in the lump.</param>
    /// <param name="Origin">The placement's world position.</param>
    /// <param name="Angles">The placement's rotation, composed onto each member.</param>
    /// <param name="Templates">Walk indices of the group's entities, in group
    /// order. A copy carries its template's index as compile_source_id.</param>
    /// <param name="Nodes">Every node under the group, entities or not. The compile
    /// gives a placement a block of ids this wide plus one.</param>
    public sealed record Placement(
        int AfterEntity, Vector3 Origin, Vector3 Angles, IReadOnlyList<int> Templates, int Nodes);

    /// <summary>
    /// The placements, and the walk indices of every entity that belongs to a
    /// group some instance targets. Those are TEMPLATES: they are walked and
    /// numbered, and the copies ship instead of them.
    /// </summary>
    public static (IReadOnlyList<Placement> Placements, IReadOnlySet<int> Templates) Find(
        DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> walked)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(walked);

        var world = document.OfType("CMapWorld").FirstOrDefault();
        if (world is null)
            return ([], new HashSet<int>());

        // The walk index of an entity, by the node id it was read from. Node ids
        // are unique within a map source, which is what makes hammerUniqueId usable
        // as the comparison key in the first place.
        var byNode = new Dictionary<int, int>();
        for (var i = 0; i < walked.Count; i++)
            byNode.TryAdd(walked[i].NodeId, i);

        var placements = new List<Placement>();
        var templates = new HashSet<int>();
        var seen = new HashSet<DmxBinary.Element>();

        // The walk puts worldspawn at index 0 before descending, so the count of
        // entities produced before the first child is one, not zero.
        var produced = walked.Count > 0 && walked[0].IsWorld ? 1 : 0;
        Walk(world, byNode, placements, templates, seen, ref produced);
        return (placements, templates);
    }

    private static void Walk(
        DmxBinary.Element node, Dictionary<int, int> byNode, List<Placement> placements,
        HashSet<int> templates, HashSet<DmxBinary.Element> seen, ref int produced)
    {
        foreach (var child in node.GetElements("children"))
        {
            if (!seen.Add(child))
                continue;

            if (child.Type is "CMapInstance" && child.Get<DmxBinary.Element>("target") is { } target)
            {
                var members = new List<int>();
                var nodes = 0;
                Collect(target, byNode, members, ref nodes);
                foreach (var member in members)
                    templates.Add(member);
                placements.Add(new Placement(
                    produced,
                    child.GetValue<Vector3>("origin") ?? Vector3.Zero,
                    child.GetValue<Vector3>("angles") ?? Vector3.Zero,
                    members, nodes));
            }
            else if (MapEntities.CarriesGameKeys(child))
            {
                produced++;
            }
            Walk(child, byNode, placements, templates, seen, ref produced);
        }
    }

    /// <summary>The group's entities in group order, and its total node count.</summary>
    private static void Collect(
        DmxBinary.Element group, Dictionary<int, int> byNode, List<int> members, ref int nodes)
    {
        foreach (var child in group.GetElements("children"))
        {
            nodes++;
            if (byNode.TryGetValue((int)(child.GetValue<int>("nodeID") ?? -1), out var index)
                && MapEntities.CarriesGameKeys(child))
                members.Add(index);
            Collect(child, byNode, members, ref nodes);
        }
    }

    /// <summary>
    /// A template member as the placement ships it.
    ///
    /// <para>The offset is rotated by the placement and added to its origin, and
    /// the rotations compose. Valve's atixref shows the yaw composing by plain
    /// addition: one template at yaw 270.00006 ships at 180.00006, 0, 270.00006 and
    /// 90.00005 under placements at yaw 270, 90, 0 and 180, wrapped into
    /// [0, 360).</para>
    /// </summary>
    public static MapEntities.Entity Place(MapEntities.Entity template, Placement placement)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(placement);

        var angles = placement.Angles;
        return template with
        {
            Origin = placement.Origin + Vector3.Transform(template.Origin, Rotation(angles)),
            Angles = new Vector3(
                Wrap(template.Angles.X + angles.X),
                Wrap(template.Angles.Y + angles.Y),
                Wrap(template.Angles.Z + angles.Z)),
        };
    }

    /// <summary>Source angles are pitch, yaw and roll about Y, Z and X, applied
    /// yaw first.</summary>
    private static Quaternion Rotation(Vector3 angles)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(angles.Y))
         * Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(angles.X))
         * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(angles.Z));

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    private static float Wrap(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0 ? wrapped + 360f : wrapped;
    }
}
