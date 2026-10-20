using System.Numerics;
using System.Text.RegularExpressions;
using ValveKeyValue;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// Nav generation from the .vmap alone, the way resourcecompiler's nav step (1800fc370) drives
/// navsystem: the settings (gameinfo's NavSystem block, the hull vdata, the map's CMapNavData), the
/// nav world's triangles (the settle world through the nav node filter), then the stage list
/// (180022b60) through IslandRemoval (029), ClipPolysToHullClearance (022) casting through
/// <see cref="NavBoxWorld"/>. What is not ported refuses with <see cref="NotSupportedException"/>:
/// func_nav_markup properties (markups, 019, NONAV), nav links, func_ladder (028), sphere shapes and
/// casts against spheres and capsules, smart props and physics pins in the nav world's filter,
/// several hulls, a deprecated agent setup. Ladders made from ladder-material meshes (180fb21f0) are
/// not detected yet.
/// </summary>
public static class NavGenPipeline
{
    /// <summary>The polygons after stage 029 and the input they came from.</summary>
    public sealed record Result(NavGenPolys Polys, NavGenSettings Settings, List<Vector3> Verts, List<int> Indices);

    /// <summary>gameinfo.gi's NavSystem block as key/value pairs (null when absent).</summary>
    public static Dictionary<string, string>? NavSystemBlock(string gameinfo)
    {
        if (!File.Exists(gameinfo))
            return null;
        var lines = File.ReadAllLines(gameinfo);
        var start = Array.FindIndex(lines, l => l.Trim() == "NavSystem");
        if (start < 0)
            return null;
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line == "{") { depth++; continue; }
            if (line == "}") { if (--depth == 0) break; continue; }
            var m = Regex.Match(line, @"^""([^""]*)""\s+""([^""]*)""");
            if (depth == 1 && m.Success)
                keys[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return keys;
    }

    /// <summary>scripts/nav_hulls.vdata and scripts/nav_hulls_presets.vdata, read through <paramref name="read"/>.</summary>
    public static NavGenSettings.VData HullVData(Func<string, byte[]?> read)
    {
        KVObject Root(string name)
        {
            var bytes = read(name) ?? throw new FileNotFoundException(name);
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(bytes));
            return ((ValveResourceFormat.ResourceTypes.BinaryKV3)resource.DataBlock!).Data.Root;
        }
        static object Scalar(KVObject v) => v.ValueType == KVValueType.Boolean ? (bool)v : (double)v;
        var hulls = new Dictionary<string, IReadOnlyDictionary<string, object>>();
        foreach (var (name, hull) in Root("scripts/nav_hulls.vdata_c").Children)
            if (hull.ValueType == KVValueType.Collection)
                hulls[name] = hull.Children.Where(c => c.Value.ValueType != KVValueType.String)
                    .ToDictionary(c => c.Key, c => Scalar(c.Value));
        var presets = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (name, preset) in Root("scripts/nav_hulls_presets.vdata_c").Children)
            if (preset.ValueType == KVValueType.Collection && preset["m_vecNavHulls"] is { } list)
                presets[name] = [.. list.Values.Select(v => v.ToString()!)];
        return new NavGenSettings.VData(hulls, presets);
    }

    /// <summary>The settings resourcecompiler hands navsystem: gameinfo, the hull vdata, the map's CMapNavData.</summary>
    public static NavGenSettings Settings(DmxBinary.Document document, NavGenSettings.VData vdata, Dictionary<string, string>? navSystem)
    {
        var defaults = NavGenSettings.ProjectDefaults(navSystem is null ? null : k => navSystem.TryGetValue(k, out var v) ? v : null, vdata);
        var navData = document.OfType("CMapNavData").SingleOrDefault()?.Get<DmxBinary.Element>("navData");
        return navData is null ? defaults : NavGenSettings.FromMap(navData, defaults, vdata);
    }

    /// <summary>
    /// The stage list on the map's settle world (<paramref name="bodies"/>, from
    /// <see cref="SettleWorld.Build"/>; unsettled placement) through 029. <paramref name="stage"/>, when
    /// given, sees the polygons after stages 005, 018, 021, 022, 027 and 029 (by their number).
    /// </summary>
    public static Result Generate(DmxBinary.Document document, IReadOnlyList<SettleWorld.BodyBuild> bodies, SettleWorld.CollisionRules rules,
                                  NavGenSettings settings, FgdSchema schema, Action<string, NavGenPolys>? stage = null)
    {
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements))
        {
            var props = e.Get<DmxBinary.Element>("entity_properties");
            var cls = props?.Get<string>("classname");
            if (cls is "func_ladder" or "info_nav_link" or "point_nav_link")
                throw new NotSupportedException($"nav generation with {cls} is not ported");
            // 180f209f0: a func_nav_markup adds markups only through its navProperty_<tag> string keys
            // (non-empty) or a navProperty_NavAttributes array (180f22330); one with neither adds none.
            if (cls == "func_nav_markup" && props!.Attributes.Any(kv => kv.Key.StartsWith("navProperty_", StringComparison.OrdinalIgnoreCase)
                                                                        && kv.Value is not null && kv.Value.ToString() is { Length: > 0 }))
                throw new NotSupportedException("nav generation with func_nav_markup properties (markups) is not ported");
        }
        if (settings.Hulls.Count != 1)
            throw new NotSupportedException($"nav generation with {settings.Hulls.Count} hulls is not ported");
        var inNavWorld = NavGenInput.NavWorldFilter(document, schema);
        var (verts, indices) = NavGenInput.Extract(bodies, rules, inNavWorld: inNavWorld);
        var g = NavGenPolys.FromTriangles(verts, indices);
        if (!NavGenStages.BeforeRecast(g))
            throw new InvalidOperationException("nav generation: ValidateVertices failed");
        var config = RecastConfig.From(settings.Bytes(), settings.Hulls[0].Bytes(), NavGenStages.RadiusPad, 0);
        var tiles = new RecastTiles(config, RecastInput.From([.. g.Pool], [.. g.Polys.Select(p => p.Indices.ToArray())]));
        var (polys, pool) = tiles.Run();
        var recast = NavGenPolys.FromRecast(polys, pool);
        stage?.Invoke("005", recast);
        var oriented = document.Elements.Any(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "info_nav_oriented_mesh");
        NavGenStages.AfterRecast(recast, oriented);
        stage?.Invoke("018", recast);
        // 019 SplitPolysByMarkup (no markups: nothing), 020 WeldVertices, 021 RemoveDegens, 022 ClipPolysToHullClearance.
        var tol = (double)NavGenStages.WeldTolerance;
        recast.Weld(tol, tol);
        recast.RemoveDegens(tol);
        stage?.Invoke("021", recast);
        recast.ClipPolysToHullClearance(settings.Hulls[0], new NavBoxWorld(bodies, inNavWorld), oriented);
        stage?.Invoke("022", recast);
        NavGenStages.ConnectStages(recast, settings, new NavTriangleWorld([.. verts], [.. indices]));
        stage?.Invoke("027", recast);
        // 028 LadderConnect (no ladders: nothing), 029 IslandRemoval.
        NavGenStages.IslandStage(recast, NavIslandInput.FromSeeds(NavGenMapInput.Seeds(document)), settings);
        stage?.Invoke("029", recast);
        return new Result(recast, settings, verts, indices);
    }
}
