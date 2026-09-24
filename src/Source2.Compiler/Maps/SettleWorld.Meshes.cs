using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>
    /// One triangle soup of a map mesh (FUN_181059770's 0x90-byte entries):
    /// the material record its faces share, and its points in the order the
    /// triangles first reach them.
    /// </summary>
    private sealed class Soup
    {
        public required Record Record { get; init; }
        public List<Vector3> Points { get; } = [];
        public List<int> Indices { get; } = [];
    }

    /// <summary>
    /// A mesh material's physics (FUN_1810564a0's 0x60-byte record): the
    /// surface property's name, the collision attributes, the material the
    /// surface gives, and whether the faces are dropped (group 1, "never",
    /// which the record starts as and a non-solid material keeps).
    /// </summary>
    private sealed record Record(string Name, CollisionAttributes Attributes, ContactSolver.Material Material)
    {
        public bool Dropped => Attributes.Group == 1;
    }

    /// <summary>
    /// A map mesh's body and shapes (FUN_18105d760): a static body at the
    /// node's world matrix (position, longest column as scale, the unscaled
    /// rotation's quaternion with no bind pose), then one shape per soup
    /// (FUN_181057020) by physics type: 2 hulls the soup, 3 hulls each group
    /// of triangles joined by a vertex, 4 makes a triangle mesh. Type 0
    /// ("none") makes no body at all.
    /// </summary>
    private static void FromMesh(DmxBinary.Element mesh, float[] world, int nodeId, DmxBinary.Element? owner, Context context)
    {
        var type = MeshPhysicsType(mesh, owner, context.Schema);
        if (type == 0)
            return;
        var longest = Enumerable.Range(0, 3)
            .Select(c => MathF.Sqrt(((world[c] * world[c]) + (world[4 + c] * world[4 + c])) + (world[8 + c] * world[8 + c])))
            .Aggregate(0f, (a, b) => a <= b ? b : a);
        var body = new BodyBuild(nodeId, new Vector3(world[3], world[7], world[11]), longest, Orientation(Unscaled(world)), []) { Node = mesh };
        foreach (var soup in Soups(mesh, owner, context))
        {
            if (soup.Record.Dropped || soup.Points.Count == 0 || soup.Indices.Count <= 2)
                continue;
            var name = soup.Record.Name;
            switch (type)
            {
                case 2:
                    if (RnHullBuilder.Create([.. soup.Points], MeshHullOptions, out _) is { } hull)
                        body.Shapes.Add(new ShapeBuild(BroadphaseShape.HullType, hull, 1f, null, Vector3.One, soup.Record.Attributes, soup.Record.Material, name));
                    break;
                case 3:
                    foreach (var part in Components(soup.Points, soup.Indices))
                        if (RnHullBuilder.Create(part, MeshHullOptions, out _) is { } piece)
                            body.Shapes.Add(new ShapeBuild(BroadphaseShape.HullType, piece, 1f, null, Vector3.One, soup.Record.Attributes, soup.Record.Material, name));
                    break;
                case 4:
                    if (RnMeshBuilder.Create([.. soup.Indices], [.. soup.Points], null) is { } triangles)
                        body.Shapes.Add(new ShapeBuild(BroadphaseShape.MeshType, null, 0, triangles, Vector3.One, soup.Record.Attributes, soup.Record.Material, name));
                    break;
            }
        }
        context.Bodies.Add(body);
    }

    /// <summary>
    /// RnHullCreate's options for a map mesh hull: none at all for type 2,
    /// which RnHullCreate fills with a 5 degree merge angle, and the same
    /// block spelled out for type 3 (FUN_1819c9db0).
    /// </summary>
    private static readonly RnHullBuilder.Options MeshHullOptions = new() { Angle = 5f };

    /// <summary>
    /// MapBuilder_ResolvePhysicsType (FUN_181083540): "none" is 0; "default"
    /// is a mesh (4) outside an entity, and inside one a multi convex (3)
    /// unless the class inherits PhysicsTypeOverride_Mesh (4) or
    /// PhysicsTypeOverride_SingleConvex (2).
    /// </summary>
    private static int MeshPhysicsType(DmxBinary.Element mesh, DmxBinary.Element? owner, FgdSchema? schema)
    {
        var stored = mesh.Get<string>("physicsType") ?? "default";
        switch (stored)
        {
            case "none":
                return 0;
            case "default":
                break;
            case "mesh":
                return 4;
            case "single_convex":
                return 2;
            case "multi_convex":
                return 3;
            default:
                throw new NotSupportedException($"physicsType {stored}");
        }
        if (owner == null)
            return 4;
        var className = owner.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
        if (schema == null)
            throw new InvalidOperationException("an entity's mesh needs the FGD to resolve its physics type");
        if (schema.Inherits(className, "PhysicsTypeOverride_Mesh"))
            return 4;
        if (schema.Inherits(className, "PhysicsTypeOverride_MultiConvex"))
            return 3;
        return schema.Inherits(className, "PhysicsTypeOverride_SingleConvex") ? 2 : 3;
    }

    /// <summary>
    /// FUN_181059770: the mesh's faces triangulated in order (each face cut
    /// by <see cref="PolygonTriangulator"/>), its vertex positions times the
    /// node's scales, one record per material (FUN_1810562e0) merged where
    /// equal (FUN_1810578b0), and each triangle's corners appended to its
    /// material's soup, a vertex taking a new point the first time that soup
    /// meets it.
    /// </summary>
    private static List<Soup> Soups(DmxBinary.Element mesh, DmxBinary.Element? owner, Context context)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException("CMapMesh without meshData.");
        var next = MeshInts(data, "edgeNextIndices");
        var to = MeshInts(data, "edgeVertexIndices");
        var first = MeshInts(data, "faceEdgeIndices");
        var vertexData = MeshInts(data, "vertexDataIndices");
        var faceData = MeshInts(data, "faceDataIndices");
        var positions = MeshStream(data, "vertexData", "position");
        var faceMaterials = MeshStream(data, "faceData", "materialindex").Select(x => x is int i ? i : 0).ToArray();
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;

        var records = names.Select(n => MaterialRecord(n, mesh, owner, context)).ToList();
        var soups = new List<Soup>();
        var soupOf = new int[records.Count];
        for (var m = 0; m < records.Count; m++)
        {
            var at = soups.FindIndex(s => s.Record == records[m]);
            if (at < 0)
            {
                at = soups.Count;
                soups.Add(new Soup { Record = records[m] });
            }
            soupOf[m] = at;
        }

        var vertexCount = vertexData.Length;
        var pointOf = soups.Select(_ => Enumerable.Repeat(-1, vertexCount).ToArray()).ToArray();
        Vector3 Position(int v)
        {
            var p = (Vector3)positions[vertexData[v]]!;
            return scales == Vector3.One ? p : p * scales;
        }
        for (var f = 0; f < first.Length; f++)
        {
            var loop = new List<int>();
            var e = first[f];
            do
            {
                loop.Add(to[e]);
                e = next[e];
            } while (e != first[f] && loop.Count <= next.Length);
            if (loop.Count < 3)
                continue;
            var corners = loop.Select(v => (Vector3)positions[vertexData[v]]!).ToArray();
            // Every face goes through the triangulator, a triangle too: its
            // ear comes out starting at the second corner.
            var cut = PolygonTriangulator.Triangulate(corners);
            var material = faceMaterials.Length == 0 ? 0 : faceMaterials[faceData[f]];
            if (material < 0 || material >= soupOf.Length)
                continue;
            var s = soupOf[material];
            var soup = soups[s];
            foreach (var j in cut)
            {
                var v = loop[j];
                if (pointOf[s][v] < 0)
                {
                    pointOf[s][v] = soup.Points.Count;
                    soup.Points.Add(Position(v));
                }
                soup.Indices.Add(pointOf[s][v]);
            }
        }
        return soups;
    }

    /// <summary>
    /// FUN_1810564a0 for one of the mesh's materials. The record starts
    /// dropped (group 1). A material the compile cannot find loads without
    /// attributes. The surface is the material's PhysicsSurfaceProperties
    /// ("default" when unknown), and a solid material (see <see cref="ReadMaterialPhysics"/>)
    /// gets its layer lists, its group (Default, with CONTENTS_SOLID when it
    /// names no layer, if the group is unknown), the mesh node's overrides,
    /// then the owner's rules (FUN_18105ce70) or, in the world, StaticLevel.
    /// </summary>
    private static Record MaterialRecord(string path, DmxBinary.Element mesh, DmxBinary.Element? owner, Context context)
    {
        var dropped = new CollisionAttributes { EntityId = -1, OwnerId = -1, Group = 1, Flags = 7 };
        // A material the compile cannot find still loads (as the error
        // material), with no attributes: atixref's milwall001 faces join the
        // soup of the other plain material beside them.
        var info = context.Models.Material(path) ?? new MaterialInfo(new Dictionary<string, long>(), new Dictionary<string, string>());
        var surface = info.Strings.GetValueOrDefault("PhysicsSurfaceProperties", "");
        var material = SurfaceMaterial(surface, context.Models);
        var physics = ReadMaterialPhysics(info, context.Models);
        if (!physics.Solid)
            return new Record(surface, dropped, material);

        var rules = context.Rules;
        var a = new CollisionAttributes
        {
            InteractsAs = rules.Mask(physics.InteractAs),
            InteractsWith = rules.Mask(physics.InteractWith),
            InteractsExclude = rules.Mask(physics.InteractExclude),
            EntityId = -1,
            OwnerId = -1,
            Flags = 7,
        };
        var group = physics.Group.Length == 0 ? -1 : rules.Group(physics.Group);
        if (group < 0)
        {
            group = 4;
            if (physics.InteractAs.Length == 0)
                a.InteractsAs |= CollisionRules.ContentsSolid;
        }
        a.Group = (byte)group;
        // The mesh node's own settings (vfuncs 0x690 to 0x6a8).
        if (mesh.Get<string>("physicsGroup") is { Length: > 0 } g && rules.Group(g) is >= 0 and var gi)
            a.Group = (byte)gi;
        a.InteractsAs |= rules.Mask(mesh.Get<string>("physicsInteractsAs") ?? "");
        a.InteractsWith |= rules.Mask(mesh.Get<string>("physicsInteractsWith") ?? "");
        a.InteractsExclude |= rules.Mask(mesh.Get<string>("physicsInteractsExclude") ?? "");
        if (owner == null)
            a.InteractsAs |= 0x40000000;
        else
            OwnerRules(owner, context.Schema, ref a);
        return new Record(surface, a, material);
    }

    /// <summary>
    /// FUN_18105ce70 for an entity's mesh: a class that is not a solid class
    /// (or not a static prop), or one that inherits BasePhysicsSimulated,
    /// also interacts with solid, window, passbullets, player and npc
    /// (0xc3001); the class's collision_group metadata sets the group; and
    /// any class but a static prop does not block sound.
    /// </summary>
    private static void OwnerRules(DmxBinary.Element owner, FgdSchema? schema, ref CollisionAttributes a)
    {
        if (schema == null)
            throw new InvalidOperationException("an entity's mesh needs the FGD for its collision rules");
        var className = owner.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "";
        var staticProp = schema.HasFlag(className, "static_prop");
        if ((!schema.IsSolidClass(className) && !staticProp) || schema.Inherits(className, "BasePhysicsSimulated"))
            a.InteractsWith |= 0xc3001;
        if (schema.MetadataOf(className, "collision_group") is { Length: > 0 } g && new CollisionRules().Group(g) is >= 0 and var gi)
            a.Group = (byte)gi;
        if (!staticProp)
            a.InteractsAs &= ~0x400ul;
    }

    private static ContactSolver.Material SurfaceMaterial(string name, IModels models)
        => (name.Length > 0 ? models.Surface(NameHash(name)) : null) ?? models.Surface(DefaultSurface) ?? DefaultMaterial;

    /// <summary>What FUN_1802e3120 reads off a material for physics.</summary>
    private sealed record MaterialPhysics(bool Solid, string Group, string InteractAs, string InteractWith, string InteractExclude);

    // FUN_1802e3120's table (0923: 183074a70): attribute, collision group,
    // interact-as tag, whether a match keeps the material solid, whether it
    // forces it solid, and whether it applies only to a drawn material.
    private static readonly (string Attribute, string Group, string Tag, bool Keeps, bool Forces, bool DrawnOnly)[] MaterialTable =
    [
        ("mapbuilder.nodraw", "", "", true, false, false),
        ("mapbuilder.nonsolid", "", "", false, false, false),
        ("mapbuilder.ladder", "", "ladder", true, false, false),
        ("mapbuilder.blocklos", "conditionallysolid", "blocklos", true, false, false),
        ("mapbuilder.blocksound", "conditionallysolid", "blocksound", true, false, false),
        ("mapbuilder.passbullets", "conditionallysolid", "passbullets", true, false, false),
        ("mapbuilder.npcclip", "conditionallysolid", "npcclip", true, false, false),
        ("mapbuilder.playerclip", "conditionallysolid", "playerclip", true, false, false),
        ("mapbuilder.sky", "conditionallysolid", "sky", true, false, false),
        ("mapbuilder.water", "conditionallysolid", "water", true, true, false),
        ("mapbuilder.teleportclip", "conditionallysolid", "teleportclip", true, false, false),
        ("mapbuilder.navclip", "conditionallysolid", "navclip", true, false, false),
        ("translucent", "conditionallysolid", "window", true, false, true),
    ];

    /// <summary>
    /// FUN_1802e3120: the table's attributes in order (a match that does not
    /// keep the material solid clears it unless a forcing one came first; a
    /// group replaces the last; tags join with ", "), then the named
    /// collision property (mapbuilder.collisionproperties) sets the group,
    /// makes it solid and appends its lists. "Drawn" is not nodraw and the
    /// int attribute 0x84ce10dd unset. Not ported: removing "water" from the
    /// tags, and the two tag lists (string attributes 0xbc105ab and after),
    /// which throw when present.
    /// </summary>
    private static MaterialPhysics ReadMaterialPhysics(MaterialInfo info, IModels models)
    {
        bool On(string key) => info.Ints.TryGetValue(key, out var v) && v != 0;
        var drawn = !On("mapbuilder.nodraw");
        bool solid = true, forced = false;
        string group = "", tags = "", with = "", exclude = "";
        foreach (var (attribute, g, tag, keeps, forces, drawnOnly) in MaterialTable)
        {
            if ((drawnOnly && !drawn) || !On(attribute))
                continue;
            if (keeps || forced)
            {
                if (forces)
                {
                    solid = true;
                    forced = true;
                }
            }
            else
                solid = false;
            if (g.Length > 0)
                group = g;
            if (tag.Length > 0)
                tags = tags.Length == 0 ? tag : tags + ", " + tag;
        }
        if (info.Strings.TryGetValue("mapbuilder.collisionproperties", out var named) && models.CollisionProperty(named) is { } p)
        {
            group = p.Group;
            solid = true;
            static string Join(string a, string b) => a.Length == 0 ? b : a + ", " + b;
            tags = Join(tags, p.InteractAs);
            with = Join(with, p.InteractWith);
            exclude = Join(exclude, p.InteractExclude);
        }
        if (tags.Contains("water", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("a material tagged water");
        return new MaterialPhysics(solid, group, tags, with, exclude);
    }

    /// <summary>
    /// FUN_1812d87e0 and FUN_1819c9db0: the soup's vertices joined by a
    /// union-find over each triangle's edges (union by rank, the second
    /// root joining the first on a tie), numbered by first vertex, and each
    /// group's points in vertex order.
    /// </summary>
    private static List<Vector3[]> Components(List<Vector3> points, List<int> indices)
    {
        var n = points.Count;
        var rank = new int[n];
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int v)
        {
            var root = v;
            while (parent[root] != root)
                root = parent[root];
            while (parent[v] != root)
                (v, parent[v]) = (parent[v], root);
            return root;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (rank[rb] < rank[ra])
                parent[rb] = ra;
            else if (rank[ra] < rank[rb])
                parent[ra] = rb;
            else if (ra != rb)
            {
                parent[rb] = ra;
                rank[ra]++;
            }
        }
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            Union(indices[t], indices[t + 1]);
            Union(indices[t + 1], indices[t + 2]);
            Union(indices[t + 2], indices[t]);
        }
        var label = Enumerable.Repeat(-1, n).ToArray();
        var count = 0;
        var of = new int[n];
        for (var v = 0; v < n; v++)
        {
            var root = Find(v);
            if (label[root] < 0)
                label[root] = count++;
            of[v] = label[root];
        }
        var groups = Enumerable.Range(0, count).Select(_ => new List<Vector3>()).ToList();
        for (var v = 0; v < n; v++)
            groups[of[v]].Add(points[v]);
        return [.. groups.Select(g => g.ToArray())];
    }

    /// <summary>MurmurHash2 (32 bit) of the lowercased name with seed 0x31415926, the string token hash.</summary>
    internal static uint NameHash(string name)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant());
        const uint M = 0x5bd1e995;
        var h = 0x31415926u ^ (uint)data.Length;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var k = BitConverter.ToUInt32(data, i);
            k *= M;
            k ^= k >> 24;
            k *= M;
            h *= M;
            h ^= k;
        }
        switch (data.Length - i)
        {
            case 3:
                h ^= (uint)data[i + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)data[i + 1] << 8;
                goto case 1;
            case 1:
                h ^= data[i];
                h *= M;
                break;
        }
        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;
        return h;
    }

    private static int[] MeshInts(DmxBinary.Element data, string name)
        => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : 0).ToArray();

    private static object?[] MeshStream(DmxBinary.Element data, string array, string name)
    {
        var streams = data.Get<DmxBinary.Element>(array)?.GetElements("streams") ?? [];
        var stream = streams.FirstOrDefault(s => s.Name.Split(':')[0] == name || s.Name.StartsWith(name + "$", StringComparison.Ordinal));
        return stream?.Get<object?[]>("data") ?? [];
    }
}
