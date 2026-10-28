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

        /// <summary>
        /// The bone pose the settle wrote back on the node (CMapEntity +0x438,
        /// SettleWorld.Settlement.BoneTransforms), as ExportToLump prints it; null
        /// for none.
        /// </summary>
        public string? BoneTransforms { get; init; }

        /// <summary>A CMapNavLink's own fields (<see cref="NavLinkFields"/>); null for every other node.</summary>
        public NavLinkFields? NavLink { get; init; }

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

        /// <summary>The targetMapPath of the innermost prefab the node lies in, lowercase with
        /// forward slashes; null for a node of the map's own. A prefab map placed twice is one
        /// source: its nodes take one compile_source_id (<see cref="SourceIds"/>).</summary>
        public string? PrefabMap { get; init; }

        /// <summary>The keys ship in table order rather than backwards: a prefab map's node
        /// exported for the second, fourth... time (<see cref="SourceIds"/>).</summary>
        public bool KeysForward { get; init; }

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

    /// <summary>
    /// A CMapNavLink's fields (CMapNavLink, 0x18b0 bytes, factory rc 181063b90): navlink_type +0x1888,
    /// width +0x1858, locatoroffset +0x185c, locatorangles +0x1868, nav_link_movement +0x1878 and
    /// nav_link_movement_reverse +0x1880. The factory's defaults: type 0 (GENERIC), width 128, offset
    /// (128, 0, 0), angles (0, 180, 0), no movement.
    /// </summary>
    public sealed record NavLinkFields(int Type, float Width, Vector3 Offset, Vector3 LocatorAngles, string Movement, string MovementReverse)
    {
        public static readonly NavLinkFields Defaults = new(0, 128f, new Vector3(128f, 0f, 0f), new Vector3(0f, 180f, 0f), "", "");

        // The element attributes are the tool attributes' names (181068680). Only locatoroffset is measured
        // (s2c_navlink: Valve's link records and lump take it); the others refuse until a probe sets them.
        private static readonly string[] Unmeasured =
            ["navlink_type", "width", "locatorangles", "nav_link_movement", "nav_link_movement_reverse", "nav_link_preview", "nav_link_preview_reverse"];

        internal static NavLinkFields Read(DmxBinary.Element element)
        {
            foreach (var name in Unmeasured)
                if (element.Attributes.ContainsKey(name))
                    throw new NotSupportedException($"a CMapNavLink's {name} is not measured (only locatoroffset is)");
            return Defaults with { Offset = element.GetValue<Vector3>("locatoroffset") ?? Defaults.Offset };
        }
    }

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
        Walk(world, entities, new HashSet<DmxBinary.Element>(), null, new Prefab([], [], HiddenNodes(document), false, null, null));
        return entities;
    }

    /// <summary>Where a walk stands in prefabs: the prefab ids and elements outside it,
    /// the hidden node ids of the map being walked, and whether a hidden node encloses it.</summary>
    private sealed record Prefab(int[] Ids, DmxBinary.Element[] Chain, HashSet<int> Hidden, bool Enclosed, string? NamePrefix, string? Map);

    private static string MapPathOf(DmxBinary.Element prefab)
        => Tier0Strings.LowerAscii((prefab.Get<string>("targetMapPath") ?? "").Replace('\\', '/'));

    /// <summary>
    /// Each walked entity's compile_source_id: its walk index, except that a
    /// prefab map is one source however often it is placed. The first placement
    /// in the walk numbers its nodes, and every later one reuses those numbers
    /// (s2c_prefabprobe9: all three s2c_fixmid prefabs carry 18 to 101 and every
    /// s2c_fixinner one, nested or not, 103 to 179), as an instance copy carries
    /// its template's. <paramref name="forward"/> marks the placements whose keys
    /// ship in table order: the shared node's table is written backwards once
    /// per export, so every second placement of a map reads it forwards
    /// (s2c_prefabprobe9: s2c_fixmid's 112 and 114 backwards, 113 forwards; its
    /// ten s2c_fixinner placements alternate the same way, fixup or not).
    /// </summary>
    public static int[] SourceIds(IReadOnlyList<Entity> entities, out bool[] forward)
    {
        var ids = new int[entities.Count];
        forward = new bool[entities.Count];
        var first = new Dictionary<(string Map, int Node), (int Id, int Seen)>();
        var next = 0;
        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.PrefabMap is { } map && first.TryGetValue((map, e.NodeId), out var earlier))
            {
                ids[i] = earlier.Id;
                forward[i] = earlier.Seen % 2 == 1;
                first[(map, e.NodeId)] = (earlier.Id, earlier.Seen + 1);
                continue;
            }
            if (e.PrefabMap is { } m)
                first[(m, e.NodeId)] = (next, 1);
            ids[i] = next++;
        }
        return ids;
    }

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
    ///
    /// <para>A CMapNavLink is a CMapEntity (its IsA, rc 181064a10) and ships as one, with keys of
    /// its own (EntityLumpAuthor.NavLinkKeys): s2c_navlink's three links are compile_source_id 70 to 72.</para>
    /// </summary>
    private static readonly string[] GameKeyBearer = ["CMapEntity", "CMapPath", "CMapPathNode", "CMapCable", "CMapNavLink"];

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
        var prefabs = new List<(DmxBinary.Element Child, int Id, DmxBinary.Element World, bool Enclosed)>();
        foreach (var child in node.GetElements("children"))
        {
            // A map can nest groups inside groups, and a cycle would hang the walk.
            if (!seen.Add(child))
                continue;
            var id = child.GetValue<int>("nodeID") ?? int.MinValue;
            // force_hidden (CMapNode +0x366, 180f94900) fails the node filter (180f77240) whatever its flags, and
            // the visibility state (+0x370, 180f6ffc0: the filter through vf 0x358, the parent visible first) the
            // subasset gather reads (180fc9930) is off for the node and all below it: s2c_deformpath2's hidden
            // cable and its three path nodes, and its one hidden path node, are not in the .vmap_c's table.
            var within = child.GetValue<bool>("force_hidden") == true ? prefab with { Enclosed = true } : prefab;
            if (GameKeyBearer.Contains(child.Type) && Read(child, isWorld: false) is { } entity)
            {
                // An editorOnly node (CMapNode +0x365) fails the compile's node
                // filter (rc 180f77240) and ships nothing, as a hidden one: the
                // ten editorOnly sky_cameras of the corpus are in no Valve lump.
                var editorOnly = child.GetValue<bool>("editorOnly") == true;
                if (editorOnly && child.GetElements("children").Any())
                    throw new NotSupportedException($"node {id}: an editorOnly node with children is not measured");
                entity = entity with { Layer = layer, Hidden = within.Enclosed || prefab.Hidden.Contains(id) || editorOnly, Prefabs = prefab.Ids, NamePrefix = prefab.NamePrefix, PrefabMap = prefab.Map };
                // A node of a prefab's map is placed as the prefab moves it.
                if (prefab.Chain.Length > 0)
                {
                    // A path's nodes are nodes of the map too; the collapse moves each as it moves the path, its
                    // stored tangents turned (18110eae0), or under a deformer by the deformer's move
                    // (SettleWorld.PlacedPath; s2c_prefabinst's tangents).
                    if (entity.PathNodes is not null)
                        entity = Maps.SettleWorld.PlacedPath(entity, [], prefab.Chain, Maps.PathDeformer.UnderAnyDeformer(child), child);
                    else
                    {
                        var (origin, angles) = Maps.SettleWorld.PrefabPlacement(entity.Origin, entity.Angles, prefab.Chain);
                        entity = entity with { Origin = origin, Angles = angles };
                    }
                }
                entities.Add(entity);
            }
            if (child.Type is "CMapPrefab" && child.Get<DmxBinary.Element>(Maps.MapPrefabs.WorldKey) is { } prefabWorld)
            {
                prefabs.Add((child, id, prefabWorld, within.Enclosed));
                continue;
            }
            Walk(child, entities, seen,
                 child.Type is "CMapWorldLayer" ? child.Get<string>("worldLayerName") ?? layer : layer, within);
        }
        foreach (var (child, id, prefabWorld, enclosed) in prefabs)
        {
            // fixupEntityNames prefixes the contents' names with the prefab's
            // targetName when useTargetNameAsPrefix is on and it has one, else
            // its node id (a targetName alone does not count), then "_"; a fixup
            // prefab inside another appends its own to the outer prefix, and a
            // prefab without fixup keeps the outer one (carnival_d's 10665_;
            // s2c_prefabprobe9: 112_2156_, outerpre_tnpre_, 115_, 112:2158 under 112_).
            var namePrefix = prefab.NamePrefix;
            if (child.GetValue<bool>("fixupEntityNames") == true)
            {
                var own = child.GetValue<bool>("useTargetNameAsPrefix") == true && child.Get<string>("targetName") is { Length: > 0 } name
                    ? name : id.ToString(CultureInfo.InvariantCulture);
                namePrefix += own + "_";
            }
            // The compile numbers one node ahead of the prefab's contents
            // (s2c_prefabprobe: 108:3 is compile_source_id 14 after the map's
            // own 13); taken here as the prefab map's world, walked and never
            // shipped. Whether it is that world or the prefab node is not read.
            if (Read(prefabWorld, isWorld: false) is { } nested)
                entities.Add(nested with { Layer = layer, Hidden = true, Prefabs = [.. prefab.Ids, id], PrefabMap = MapPathOf(child) });
            Walk(prefabWorld, entities, seen, layer,
                 new Prefab([.. prefab.Ids, id], [.. prefab.Chain, child],
                            child.Get<HashSet<int>>(Maps.MapPrefabs.HiddenKey) ?? [], enclosed, namePrefix, MapPathOf(child)));
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

    /// <summary>The node as the walk reads it, where it stands in the source (no prefab or instance placement); null when it is no entity.</summary>
    internal static Entity? ReadNode(DmxBinary.Element element) => Read(element, isWorld: false);

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

        // A path's origin follows its first node: CMapPath::vf274 (rc 1810b66e0)
        // sets it from that node's whenever the node changes, loading included,
        // so whatever the source stores for the path itself never ships
        // (s2c_cablecoll: cables moved off their nodes keep the first node's
        // origin in Valve's lump and their nodes relative to it).
        var pathNodes = ReadPathNodes(element);
        var origin = pathNodes is [var first, ..] ? first.Origin : element.GetValue<Vector3>("origin") ?? Vector3.Zero;

        return new Entity(
            className,
            (int)(element.GetValue<int>("nodeID") ?? 0),
            keys,
            origin,
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
            PathNodes: pathNodes,
            ClosedLoop: element.GetValue<bool>("closedLoop") ?? false,
            // CMapPath's and CMapCable's factories (rc 1810b3e90, 18114e350) start it at 1.
            InterpolationType: element.GetValue<int>("interpolationType") ?? 1,
            // A cable's tint, which a cable_dynamic ships as its rendercolor. The
            // node starts white (0xffffffff, CMapCable's factory).
            Tint: element.Type is "CMapCable" ? element.Get<byte[]>("tintColor") ?? [255, 255, 255, 255] : null)
        {
            NavLink = element.Type is "CMapNavLink" ? NavLinkFields.Read(element) : null,
        };
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
