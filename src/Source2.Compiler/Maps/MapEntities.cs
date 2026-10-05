using Source2.Compiler.Io;
using System.Globalization;
using System.Numerics;

namespace Source2.Compiler;

/// <summary>
/// The entities of a map source, lifted out of its DMX element graph.
///
/// <para>Hammer keeps an entity's game keys in an <c>EditGameClassProps</c> element
/// hanging off the map node, and its placement (<c>origin</c>, <c>angles</c>,
/// <c>scales</c>) on the node itself. Every value in the props is a string, whatever
/// the key means.</para>
/// </summary>
public static class MapEntities
{
    /// <summary>One entity as the source states it.</summary>
    /// <param name="ClassName">The <c>classname</c> key, which every entity has.</param>
    /// <param name="NodeId">Hammer's node id, which the compile records as <c>hammerUniqueId</c>.</param>
    /// <param name="Keys">The game keys, in source order, values still strings.</param>
    /// <param name="Origin">Placement, in world space for a point entity.</param>
    /// <param name="Angles">Pitch/yaw/roll as authored.</param>
    /// <param name="Scales">Per-axis scale; 1,1,1 unless the mapper changed it.</param>
    /// <param name="IsWorld">True for the <c>CMapWorld</c> node, which compiles to worldspawn.</param>
    /// <param name="Connections">The entity's outputs.</param>
    /// <param name="HasGeometry">The entity owns brush geometry, so the compile builds
    /// it a model and points the entity at it.</param>
    /// <param name="Hidden">The map's visibility manager has the node hidden, so the
    /// compile walks and numbers it but ships nothing.</param>
    /// <param name="PathNodes">A CMapPath's nodes; null for anything else.</param>
    /// <param name="ClosedLoop">A CMapPath's own closedLoop.</param>
    /// <param name="Layer">The world layer the node sits in (its nearest
    /// CMapWorldLayer's worldLayerName), whose lump it ships in; null for the
    /// world's own, which is default_ents.</param>
    /// <param name="Instanced">A copy an instance placed, which the bake builds as
    /// a new entity of its class rather than reading from the source.</param>
    /// <param name="InterpolationType">A CMapPath's own interpolationType: 0 and 1
    /// decide every node's tangents, anything else leaves each node's own.</param>
    public sealed record Entity(
        string ClassName,
        int NodeId,
        IReadOnlyList<KeyValuePair<string, string>> Keys,
        Vector3 Origin,
        Vector3 Angles,
        Vector3 Scales,
        bool IsWorld,
        IReadOnlyList<Connection> Connections,
        bool HasGeometry = false,
        bool Hidden = false,
        IReadOnlyList<PathNode>? PathNodes = null,
        bool ClosedLoop = false,
        int InterpolationType = 0,
        string? Layer = null,
        bool Instanced = false,
        byte[]? Tint = null)
    {
        /// <summary>
        /// Values that replace class defaults in place, for keys the source
        /// does not carry: a later pass set them on the node's table after
        /// SetClass filled it (the settle's CMapEntity_SetStartAsleep).
        /// </summary>
        public IReadOnlyDictionary<string, string>? Defaults { get; init; }

        /// <summary>The <c>CMapPrefab</c> node ids the node was reached through, outermost first
        /// (<see cref="Maps.MapPrefabs"/>); empty for a node of the map's own.</summary>
        public int[] Prefabs { get; init; } = [];

        /// <summary>
        /// The name prefix a prefab with fixupEntityNames gives its contents,
        /// "&lt;prefab node id&gt;_" (dkr_m2_carnival_d's prefab 10665: Valve's lump
        /// names [PR#]10665_carousel_gate_door), written after the [PR#] marker;
        /// null outside such a prefab.
        /// </summary>
        public string? NamePrefix { get; init; }

        /// <summary>The node's id path as the compile formats it (NodeIdPath_Format):
        /// the prefab ids, then the node's own, joined by ':'.</summary>
        public string IdPath => Prefabs.Length == 0
            ? NodeId.ToString(CultureInfo.InvariantCulture)
            : string.Join(':', Prefabs.Append(NodeId).Select(i => i.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// One node of a path, as the source states it.
    /// </summary>
    /// <param name="Origin">World position; the lump stores it relative to the
    /// path.</param>
    /// <param name="Keys">The node's own game keys, which the path aggregates into
    /// parallel arrays.</param>
    /// <param name="InTangent">The authored incoming tangent.</param>
    /// <param name="OutTangent">The authored outgoing tangent.</param>
    /// <param name="InTangentType">How the incoming tangent is derived.</param>
    /// <param name="OutTangentType">How the outgoing tangent is derived.</param>
    public sealed record PathNode(
        Vector3 Origin, IReadOnlyList<KeyValuePair<string, string>> Keys,
        Vector3 InTangent = default, Vector3 OutTangent = default,
        int InTangentType = 0, int OutTangentType = 0);

    /// <summary>One output wired to an input on another entity.</summary>
    public sealed record Connection(
        string OutputName,
        string TargetName,
        string InputName,
        string OverrideParam,
        float Delay,
        int TimesToFire);

    /// <summary>
    /// Every entity in the document, the world first, then the map entities in the
    /// order the source lists them.
    /// </summary>
    public static IReadOnlyList<Entity> From(DmxBinary.Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var world = document.OfType("CMapWorld").FirstOrDefault();
        if (world is null)
            return [];

        // The order is the world's own child TREE, walked depth first - not the
        // order elements happen to sit in the file. Checked against Valve's
        // compile of cardtest, whose lump runs 2138, 4, 5, 6, 7, ... exactly as
        // the tree does while the file lists those elements far apart.
        var entities = new List<Entity>();
        if (Read(world, isWorld: true) is { } worldspawn)
            entities.Add(Upgrade(worldspawn, document.FormatVersion));
        Walk(world, entities, new HashSet<DmxBinary.Element>(), null, new Prefab([], [], HiddenNodes(document), false, null));
        return entities;
    }

    /// <summary>Where a walk stands in prefabs: the prefab ids and elements outside it,
    /// the hidden node ids of the map being walked, and whether a hidden node encloses it.</summary>
    private sealed record Prefab(int[] Ids, DmxBinary.Element[] Chain, HashSet<int> Hidden, bool Enclosed, string? NamePrefix);

    /// <summary>
    /// The map loader's upgrade for files saved before vmap 38 (FUN_180d7c3f0,
    /// one step of the version upgrade table): the world is given
    /// prefab_has_runtime_entity_by_default "0" when it lacks the key, appended
    /// after its own. atixref and ze_hold_em_p (vmap 37) and probe01 (35) ship the
    /// key; Mako and untitled_1 (40) do not, and cardtest (40) ships it because its
    /// source already carries it.
    /// </summary>
    private static Entity Upgrade(Entity world, int formatVersion)
        => formatVersion >= 38 || world.Keys.Any(k => k.Key.EqualsAscii(
               "prefab_has_runtime_entity_by_default"))
            ? world
            : world with { Keys = [.. world.Keys, new("prefab_has_runtime_entity_by_default", "0")] };

    /// <summary>
    /// Node ids the map's visibility manager has hidden.
    ///
    /// <para>A hidden node is walked and numbered and then not shipped, which is
    /// the whole of two gaps that looked like separate mysteries: ze_hold_em_p
    /// hides 28 game_weapon_manager and Valve's lump has none of them, and
    /// atixref hides one light_environment and Valve's lump has none of it. The
    /// manager keeps two parallel arrays, the nodes and a flag each.</para>
    /// </summary>
    internal static HashSet<int> HiddenNodes(DmxBinary.Document document)
    {
        var hidden = new HashSet<int>();
        var manager = document.OfType("CVisibilityMgr").FirstOrDefault();
        if (manager is null)
            return hidden;

        // Both arrays are stored untyped, so the flag is read as whatever integer
        // the file used rather than assumed to be one width.
        var nodes = manager.GetElements("nodes").ToList();
        var flags = manager.Get<object[]>("hiddenFlags") ?? [];
        for (var i = 0; i < nodes.Count && i < flags.Length; i++)
            if (flags[i] is IConvertible flag && flag.ToInt64(CultureInfo.InvariantCulture) != 0
                && nodes[i].GetValue<int>("nodeID") is { } id)
                hidden.Add(id);
        return hidden;
    }

    /// <summary>
    /// Whether the map asks for entity-name fixup, which the compile answers by
    /// prefixing every name and every reference to one.
    /// </summary>
    public static bool FixupEntityNames(DmxBinary.Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.OfType("CMapWorld").FirstOrDefault()?.GetValue<bool>("fixupEntityNames") ?? false;
    }

    /// <summary>
    /// Node types that carry an <c>EditGameClassProps</c> and so take a place in
    /// the compile's numbering.
    ///
    /// <para>It is not only <c>CMapEntity</c>. A rope is authored as a
    /// <c>CMapPath</c> of <c>CMapPathNode</c> children, and every one of them owns
    /// game keys: atixref holds 4,588 CMapEntity and 4,601 EditGameClassProps, the
    /// difference being its 4 paths and 8 path nodes. Walking only CMapEntity left
    /// every compile_source_id past the first path 12 too low, which reads as a
    /// difference on every class in the map. A CMapCable is a path too: Mako's
    /// one cable_dynamic and its seven nodes put every later id 8 out.</para>
    /// </summary>
    private static readonly string[] GameKeyBearer = ["CMapEntity", "CMapPath", "CMapPathNode", "CMapCable"];

    /// <summary>Node types that are paths and carry CMapPathNode children.</summary>
    private static bool IsPath(DmxBinary.Element element) => element.Type is "CMapPath" or "CMapCable";

    /// <summary>Whether the node owns an <c>EditGameClassProps</c>, and so takes a
    /// place in the compile's numbering.</summary>
    public static bool CarriesGameKeys(DmxBinary.Element node)
        => node is not null && GameKeyBearer.Contains(node.Type);

    private static void Walk(DmxBinary.Element node, List<Entity> entities, HashSet<DmxBinary.Element> seen,
                             string? layer, Prefab prefab)
    {
        // A prefab's map is walked where its parent's children end, after its
        // siblings, as an instance's copies are written: the collapse
        // (MapDoc_CollapseInstance) puts the copy there. c2m2_fairgrounds_csgo_multi
        // numbers its own entities 2119 to 2123, last in the tree, 1 to 4 and
        // its environment prefab (first in the tree) from 5.
        var prefabs = new List<(DmxBinary.Element Child, int Id, DmxBinary.Element World)>();
        foreach (var child in node.GetElements("children"))
        {
            // A map can nest groups inside groups, and a cycle would hang the walk.
            if (!seen.Add(child))
                continue;
            var id = child.GetValue<int>("nodeID") ?? int.MinValue;
            if (GameKeyBearer.Contains(child.Type) && Read(child, isWorld: false) is { } entity)
            {
                // An editorOnly node (CMapNode +0x365) fails the compile's node
                // filter (rc 180f77240) and ships nothing, as a hidden one: the
                // ten editorOnly sky_cameras of the corpus are in no Valve lump.
                var editorOnly = child.GetValue<bool>("editorOnly") == true;
                if (editorOnly && child.GetElements("children").Any())
                    throw new NotSupportedException($"node {id}: an editorOnly node with children is not measured");
                entity = entity with { Layer = layer, Hidden = prefab.Enclosed || prefab.Hidden.Contains(id) || editorOnly, Prefabs = prefab.Ids, NamePrefix = prefab.NamePrefix };
                // A node of a prefab's map is placed as the prefab moves it.
                if (prefab.Chain.Length > 0)
                {
                    var (origin, angles) = Maps.SettleWorld.PrefabPlacement(entity.Origin, entity.Angles, prefab.Chain);
                    // A path's nodes are nodes of the map too; the collapse moves them as it moves the path.
                    var nodes = entity.PathNodes?.Select(n => n with
                    {
                        Origin = Maps.SettleWorld.PrefabPlacement(n.Origin, Vector3.Zero, prefab.Chain).Origin,
                    }).ToList();
                    entity = entity with { Origin = origin, Angles = angles, PathNodes = nodes ?? entity.PathNodes };
                }
                entities.Add(entity);
            }
            if (child.Type is "CMapPrefab" && child.Get<DmxBinary.Element>(Maps.MapPrefabs.WorldKey) is { } prefabWorld)
            {
                prefabs.Add((child, id, prefabWorld));
                continue;
            }
            Walk(child, entities, seen,
                 child.Type is "CMapWorldLayer" ? child.Get<string>("worldLayerName") ?? layer : layer, prefab);
        }
        foreach (var (child, id, prefabWorld) in prefabs)
        {
            // fixupEntityNames prefixes the contents' names with the prefab's
            // node id (captured on carnival_d, where the prefab has no
            // targetName and useTargetNameAsPrefix off). A targetName prefix
            // and a fixup prefab inside another are not measured.
            var namePrefix = prefab.NamePrefix;
            if (child.GetValue<bool>("fixupEntityNames") == true)
            {
                if (child.GetValue<bool>("useTargetNameAsPrefix") == true && !string.IsNullOrEmpty(child.Get<string>("targetName")))
                    throw new NotSupportedException($"prefab {id}: useTargetNameAsPrefix is not measured");
                if (namePrefix != null)
                    throw new NotSupportedException($"prefab {id}: a fixup prefab inside another is not measured");
                namePrefix = id.ToString(CultureInfo.InvariantCulture) + "_";
            }
            // The compile numbers one node ahead of the prefab's contents
            // (s2c_prefabprobe: 108:3 is compile_source_id 14 after the map's
            // own 13); taken here as the prefab map's world, walked and never
            // shipped. Whether it is that world or the prefab node is not read.
            if (Read(prefabWorld, isWorld: false) is { } nested)
                entities.Add(nested with { Layer = layer, Hidden = true, Prefabs = [.. prefab.Ids, id] });
            Walk(prefabWorld, entities, seen, layer,
                 new Prefab([.. prefab.Ids, id], [.. prefab.Chain, child],
                            child.Get<HashSet<int>>(Maps.MapPrefabs.HiddenKey) ?? [], prefab.Enclosed, namePrefix));
        }
    }

    /// <summary>
    /// The map's world layers, in tree order. Each compiles to a lump of its own,
    /// <c>world_layer_&lt;name&gt;</c>: Mako has four, and default_ents lists
    /// their lumps first among its children.
    /// </summary>
    public static IReadOnlyList<string> WorldLayers(DmxBinary.Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layers = new List<string>();
        void Visit(DmxBinary.Element node, HashSet<DmxBinary.Element> seen)
        {
            foreach (var child in node.GetElements("children"))
            {
                if (!seen.Add(child))
                    continue;
                if (child.Type is "CMapWorldLayer" && child.Get<string>("worldLayerName") is { Length: > 0 } name
                    && !layers.Contains(name))
                    layers.Add(name);
                Visit(child, seen);
            }
        }
        if (document.OfType("CMapWorld").FirstOrDefault() is { } world)
            Visit(world, []);
        return layers;
    }

    /// <summary>Whether the walk reads the node as an entity (as it does a prefab map's world, never shipped).</summary>
    internal static bool ReadsAsEntity(DmxBinary.Element element) => Read(element, isWorld: false) is not null;

    private static Entity? Read(DmxBinary.Element element, bool isWorld)
    {
        if (element.Get<DmxBinary.Element>("entity_properties") is not { } props)
            return null;

        var keys = new List<KeyValuePair<string, string>>();
        string? className = null;
        foreach (var (key, value) in props.Attributes)
        {
            if (value is not string text)
                continue;
            if (Tier0Strings.EqualsIgnoreCase(key, "classname"))
                className = text;
            keys.Add(new(key, text));
        }
        if (className is null)
            return null;

        return new Entity(
            className,
            (int)(element.GetValue<int>("nodeID") ?? 0),
            keys,
            element.GetValue<Vector3>("origin") ?? Vector3.Zero,
            element.GetValue<Vector3>("angles") ?? Vector3.Zero,
            element.GetValue<Vector3>("scales") ?? Vector3.One,
            isWorld,
            ReadConnections(element),
            // A brush entity is a node with mesh children. The world has meshes too,
            // but those are the world's own geometry rather than an entity model. A
            // cable is its own geometry: Mako's cable_dynamic ships a model of its
            // own, unnamed_20788.vmdl.
            !isWorld && (element.Type is "CMapCable" || element.GetElements("children").Any(c => c.Type is "CMapMesh")),
            Hidden: false,
            PathNodes: ReadPathNodes(element),
            ClosedLoop: element.GetValue<bool>("closedLoop") ?? false,
            InterpolationType: element.GetValue<int>("interpolationType") ?? 0,
            // A cable's tint, which a cable_dynamic ships as its rendercolor. The
            // node starts white (0xffffffff, CMapCable's factory).
            Tint: element.Type is "CMapCable" ? element.Get<byte[]>("tintColor") ?? [255, 255, 255, 255] : null);
    }

    /// <summary>
    /// A path's nodes, in the order the source lists them. Only a
    /// <c>CMapPath</c> has any; everything else gets none rather than an empty
    /// list, so the lump can tell "no nodes" from "not a path".
    /// </summary>
    private static IReadOnlyList<PathNode>? ReadPathNodes(DmxBinary.Element element)
    {
        if (!IsPath(element))
            return null;

        var nodes = new List<PathNode>();
        foreach (var child in element.GetElements("children"))
        {
            if (child.Type is not "CMapPathNode")
                continue;
            var keys = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in child.Get<DmxBinary.Element>("entity_properties")?.Attributes ?? [])
                if (value is string text)
                    keys.Add(new(key, text));
            nodes.Add(new PathNode(
                child.GetValue<Vector3>("origin") ?? Vector3.Zero, keys,
                child.GetValue<Vector3>("inTangent") ?? Vector3.Zero,
                child.GetValue<Vector3>("outTangent") ?? Vector3.Zero,
                child.GetValue<int>("inTangentType") ?? 0,
                child.GetValue<int>("outTangentType") ?? 0));
        }
        return nodes;
    }

    private static IReadOnlyList<Connection> ReadConnections(DmxBinary.Element element)
    {
        var connections = new List<Connection>();
        foreach (var c in element.GetElements("connectionsData"))
            connections.Add(new Connection(
                c.Get<string>("m_outputName") ?? c.Get<string>("outputName") ?? "",
                c.Get<string>("m_targetName") ?? c.Get<string>("targetName") ?? "",
                c.Get<string>("m_inputName") ?? c.Get<string>("inputName") ?? "",
                c.Get<string>("m_overrideParam") ?? c.Get<string>("overrideParam") ?? "",
                c.GetValue<float>("m_flDelay") ?? c.GetValue<float>("delay") ?? 0f,
                c.GetValue<int>("m_nTimesToFire") ?? c.GetValue<int>("timesToFire") ?? -1));
        return connections;
    }
}
