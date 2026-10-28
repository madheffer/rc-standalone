using System.Globalization;
using System.Numerics;
using Source2.Compiler.Maps.NavGen;
using Source2.Compiler.Physics;
using ValveKeyValue;

namespace Source2.Compiler.Maps;

/// <summary>
/// compile-map's nav step and the CS2 bomb damage post step, from the .vmap: the generator
/// (<see cref="NavGenPipeline"/>) on the map's settle world, the mesh and its CS custom data
/// (<see cref="NavAnalysis"/>, traced on the same world) through <see cref="NavWriter"/>, then
/// <see cref="BombDamage"/> sampled from that nav over the world's collision.
/// </summary>
public static class NavCompile
{
    /// <summary>The generated nav: the .nav bytes and the mesh they hold.</summary>
    public sealed record Nav(byte[] Bytes, NavWriter.Mesh Mesh);

    /// <summary>
    /// resourcecompiler's nav step with generation (1800fc370): navsystem's stages on the
    /// unsettled settle world, the mesh build, CS's "Compute AI Data" (occupy times from the
    /// info_player_terrorist and info_player_counterterrorist origins in entity order, hiding,
    /// encounter and approach data, sphere traces on the same world) and the save. The custom
    /// data's from / to / order ids are heap pointers in Valve's file; ours are the area and spot
    /// ids. Movable meshes and the survival play area are refused. With <paramref name="analyze"/>
    /// false the analysis is skipped and the custom data is the unanalysed mesh's (a mesh-only check).
    /// </summary>
    public static Nav Generate(DmxBinary.Document document, FgdSchema schema, GameContent assets, string gameinfo,
                               IReadOnlyList<MapEntities.Entity> entities, bool analyze = true)
    {
        if (entities.Any(e => !e.Hidden && e.ClassName == "trigger_survival_playarea"))
            throw new NotSupportedException("the nav analysis of a map with trigger_survival_playarea is not ported");
        var locators = MapCompile.Locators(assets);
        var rules = new SettleWorld.CollisionRules();
        var bodies = SettleWorld.Build(document, assets, schema, SmartProps.NodesCreatedOnLoad(document, locators),
            d => SmartProps.NodesCreatedOnLoad(d, locators), rules);
        var vdata = NavGenPipeline.HullVData(assets.Read);
        var settings = NavGenPipeline.Settings(document, vdata, NavGenPipeline.NavSystemBlock(gameinfo));
        var result = NavGenPipeline.Generate(document, bodies, rules, settings, schema, assets.Material);
        var mesh = Mesh(document, schema, bodies, result);
        if (mesh.Movable.Count > 0)
            throw new NotSupportedException("a nav with movable meshes is not ported");
        if (!analyze)
            return new Nav(NavWriter.Write(mesh, result.Settings, new NavAnalysis(mesh).CustomData()), mesh);
        // The analysis traces the nav PhysDoc's world, the bodies navsystem's filter keeps (cardtest: all
        // 29,921 of its 29,924 casts before the save).
        var world = new NavSphereWorld(bodies, NavGenInput.NavWorldFilter(document, schema));
        var analysis = new NavAnalysis(mesh)
        {
            Trace = (s, e) => world.CastSphere(new Vector3(s.X, s.Y, s.Z), new Vector3(e.X - s.X, e.Y - s.Y, e.Z - s.Z)).Fraction
        };
        List<NavAnalysis.V> Spawns(string cls)
            => [.. entities.Where(e => !e.Hidden && e.ClassName == cls).Select(e => new NavAnalysis.V(e.Origin.X, e.Origin.Y, e.Origin.Z))];
        analysis.Analyze(Spawns("info_player_terrorist"), Spawns("info_player_counterterrorist"));
        return new Nav(NavWriter.Write(mesh, result.Settings, analysis.CustomData()), mesh);
    }

    /// <summary>
    /// The mesh build (rc 1818d63c0) from the generator's result: the areas, then the ladders, which
    /// trace the nav world with the static-only group 4 query (1818d2c10).
    /// </summary>
    public static NavWriter.Mesh Mesh(DmxBinary.Document document, FgdSchema schema, IReadOnlyList<SettleWorld.BodyBuild> bodies,
                                      NavGenPipeline.Result result)
    {
        var mesh = NavWriter.Build(result.Polys);
        if (result.Ladders.Count > 0)
        {
            var ladderWorld = new NavSphereWorld(bodies, NavGenInput.NavWorldFilter(document, schema), queryGroup: 4, staticOnly: true);
            NavMeshLadders.Build(mesh, result.Ladders, result.LadderAreas, (s, d) => ladderWorld.CastSphereNearest(s, d));
        }
        return mesh;
    }

    /// <summary>The nav mesh's areas as the bake samples them: each area's polygon corners, in the mesh's order.</summary>
    public static List<BombDamage.Area> Areas(NavWriter.Mesh mesh)
        => [.. mesh.Areas.Select(a => new BombDamage.Area([.. mesh.Polygons[a.Polygon].Select(k => new Vector3(mesh.Corners[k].X, mesh.Corners[k].Y, mesh.Corners[k].Z))]))];

    /// <summary>A .nav's areas read back (a package's nav this build did not generate).</summary>
    public static List<BombDamage.Area> Areas(byte[] nav)
    {
        var file = new ValveResourceFormat.NavMesh.NavMeshFile();
        file.Read(new MemoryStream(nav));
        return [.. file.Areas.Values.Select(a => new BombDamage.Area(a.Corners))];
    }

    /// <summary>
    /// The bombsites (18003b5d0): func_bomb_target entities with bomb_damage_power above 0, in
    /// entity order. Each site's bounds are its trigger mesh's world box, and a cell belongs to it
    /// when the trigger overlaps the cell's box (half 5; touching counts). Only a trigger that is
    /// an axis-aligned box is ported, where the overlap is the box test.
    /// </summary>
    public static List<BombDamage.Site> Sites(DmxBinary.Document document, FgdSchema schema, IReadOnlyList<MapEntities.Entity> entities)
    {
        var targets = entities.Where(e => !e.Hidden && e.ClassName == "func_bomb_target").ToList();
        var sites = new List<BombDamage.Site>();
        if (targets.Count == 0)
            return sites;
        var fallback = schema.KeyOf("func_bomb_target", "bomb_damage_power")?.Default ?? "3000";
        var meshes = MapMeshes.Read(document);
        foreach (var target in targets)
        {
            var power = float.Parse(target.Keys.FirstOrDefault(k => k.Key.Equals("bomb_damage_power", StringComparison.OrdinalIgnoreCase)).Value ?? fallback,
                                    NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!(power > 0f))
                continue;
            if (target.Instanced || target.Prefabs.Length > 0)
                throw new NotSupportedException("a func_bomb_target in an instance or prefab is not ported (bomb damage)");
            var node = document.Elements.First(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == target.NodeId);
            var children = node.GetElements("children").ToHashSet(ReferenceEqualityComparer.Instance);
            var corners = meshes.Where(m => m.Element is { } el && children.Contains(el) && m.Instances.Length == 0)
                .SelectMany(m => m.Faces).SelectMany(f => f.Corners).ToList();
            if (corners.Count == 0)
                throw new NotSupportedException($"func_bomb_target {target.NodeId} has no mesh (bomb damage)");
            Vector3 lo = corners.Aggregate(Vector3.Min), hi = corners.Aggregate(Vector3.Max);
            if (corners.Any(c => (c.X != lo.X && c.X != hi.X) || (c.Y != lo.Y && c.Y != hi.Y) || (c.Z != lo.Z && c.Z != hi.Z)))
                throw new NotSupportedException($"func_bomb_target {target.NodeId} is not an axis-aligned box: its trigger overlap is not ported (bomb damage)");
            const float h = BombDamage.Cell * 0.5f;
            sites.Add(new BombDamage.Site(lo, hi, power,
                p => lo.X <= p.X + h && p.X - h <= hi.X && lo.Y <= p.Y + h && p.Y - h <= hi.Y && lo.Z <= p.Z + h && p.Z - h <= hi.Z));
        }
        return sites;
    }

    /// <summary>The world's collision the bake casts against: the meshes of world_physics.vmdl_c (hulls are not ported).</summary>
    public static BombDamage.MeshWorld World(byte[] worldPhysics)
    {
        using var resource = new ValveResourceFormat.Resource();
        resource.Read(new MemoryStream(worldPhysics));
        var phys = ((ValveResourceFormat.ResourceTypes.Model)resource.DataBlock!).GetEmbeddedPhys()
                   ?? throw new InvalidOperationException("world_physics has no physics");
        if (phys.Parts.Any(p => p.Shape.Hulls.Length > 0 || p.Shape.Spheres.Length > 0 || p.Shape.Capsules.Length > 0))
            throw new NotSupportedException("bomb damage over a world with hulls, spheres or capsules (the hull cast and ray) is not ported");
        return new BombDamage.MeshWorld([.. phys.Parts.SelectMany(p => p.Shape.Meshes).Select(m => SettleWorld.Mesh(m.Shape))]);
    }

    /// <summary>
    /// The CS2BombDamage post step (18003ad70): the grid, each site's distances, then
    /// maps/&lt;map&gt;/baked_bomb_damage.vdata_c compiled as the map's child; null with no site.
    /// </summary>
    public static byte[]? BombDamageFile(IReadOnlyList<BombDamage.Area> areas, IReadOnlyList<BombDamage.Site> sites, BombDamage.IWorld world,
                                         string addon, string map)
    {
        if (sites.Count == 0)
            return null;
        var grid = BombDamage.Build(areas, sites, world);
        for (var s = 0; s < sites.Count; s++)
            if (!BombDamage.Distances(grid, s, world))
                throw new InvalidOperationException($"bomb damage: site {s}'s distances failed");
        return BombDamage.VdataC(BombDamage.Bombsites(sites), BombDamage.Positions(grid), BombDamage.DamageValues(grid, sites), addon, map);
    }
}
