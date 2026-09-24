using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// How the world's triangle meshes in <c>world_physics.vmdl_c</c> are put
/// together, before <see cref="RnMeshBuilder"/> builds each one.
///
/// <para>physicsbuilder's <c>CPhysicsBuilder::Build</c> walks the map's node
/// tree depth first, children in stored order. Each world mesh is split by
/// material, and every piece whose material is solid becomes one node of an
/// in-memory ModelDoc (<c>CPhysicsBuilderWorld_ModelDoc</c>). resourcecompiler
/// compiles that document. Each node's shape is appended to the physics part,
/// and the whole list is re-sorted by shape type with tier0's
/// <c>V_qsort</c> after every append (<see cref="PartOrder"/>). The part
/// builder then gathers the mesh shapes into one soup per collision attribute
/// (<see cref="Group"/>).</para>
/// </summary>
public static class WorldCollision
{
    /// <summary>The part's shape type for a triangle mesh (a hull is 2).</summary>
    public const int MeshType = 3;

    /// <summary>
    /// What physicsbuilder reads off a material. <c>Solid</c> false drops the
    /// piece. <c>CollisionGroup</c> and <c>InteractAs</c> make the collision
    /// attribute, and <c>SurfaceProperty</c> is <c>PhysicsSurfaceProperties</c>.
    /// </summary>
    public sealed record MaterialPhysics(bool Solid, string CollisionGroup, string InteractAs, string SurfaceProperty)
    {
        public static readonly MaterialPhysics Default = new(true, "", "", "");

        /// <summary>The collision attribute this material's pieces carry.</summary>
        public string AttributeKey => (CollisionGroup.Length == 0 ? "default" : CollisionGroup) + "|" + InteractAs;
    }

    // physicsbuilder's table (0924: 180ba72b0): attribute, collision group,
    // interact-as tag, whether a match leaves the material solid, whether it
    // forces it solid, and whether it applies only to non-nodraw materials.
    private static readonly (string Attribute, string Group, string InteractAs, bool KeepsSolid, bool ForcesSolid, bool DrawnOnly)[] Table =
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
    /// physicsbuilder's material reader (0924: 1800132f0), from a compiled
    /// material's int and string attributes. Not yet read: its string
    /// attribute 0xeeb9d970 (a named collision property that overrides the
    /// group and tags), and how it strips "water" from the tags.
    /// </summary>
    public static MaterialPhysics ReadMaterial(IReadOnlyDictionary<string, long> ints, IReadOnlyDictionary<string, string> strings)
    {
        bool On(string key) => ints.TryGetValue(key, out var v) && v != 0;
        var drawn = !On("mapbuilder.nodraw");
        bool solid = true, forced = false;
        var group = "";
        var interactAs = "";
        foreach (var (attribute, g, tag, keeps, forces, drawnOnly) in Table)
        {
            if ((drawnOnly && !drawn) || !On(attribute))
                continue;
            if (!keeps && !forced)
                solid = false;
            else if (forces)
            {
                solid = true;
                forced = true;
            }
            if (g.Length > 0)
                group = g;
            if (tag.Length > 0)
                interactAs = interactAs.Length == 0 ? tag : interactAs + ", " + tag;
        }
        var surface = strings.TryGetValue("PhysicsSurfaceProperties", out var s) ? s : "";
        return new MaterialPhysics(solid, group, interactAs, surface);
    }

    /// <summary>One world piece: a mesh's triangles in one solid material.</summary>
    public sealed record Piece(int NodeId, int Material, string MaterialName, MaterialPhysics Physics, Vector3[] Points, int[] Indices);

    /// <summary>
    /// The world's solid pieces in the order physicsbuilder hands them to the
    /// ModelDoc: meshes in node-walk order, each split by material in
    /// material order (<see cref="BrushHulls.Pieces"/>), triangulated the way
    /// the shape reads its mesh (<see cref="BrushHulls.TriangleMesh"/>).
    /// </summary>
    public static List<Piece> Pieces(DmxBinary.Document doc, Func<string, MaterialPhysics> materials)
    {
        var world = doc.OfType("CMapWorld").First();
        var result = new List<Piece>();
        foreach (var mesh in Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup"))
        {
            var names = mesh.Element!.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials") ?? [];
            foreach (var (material, positions, faces, local) in BrushHulls.Pieces(mesh.Element!, world, mesh.Instances.Length > 0 ? mesh.Path : null))
            {
                var name = material < names.Length ? (names[material] as string ?? "") : "";
                var physics = materials(name);
                if (!physics.Solid)
                    continue;
                var (points, triangles) = BrushHulls.TriangleMesh(positions, faces, local);
                var indices = new int[triangles.Count * 3];
                for (var t = 0; t < triangles.Count; t++)
                    (indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2]) = triangles[t];
                result.Add(new Piece(mesh.NodeId, material, name, physics, [.. points], indices));
            }
        }
        return result;
    }

    /// <summary>
    /// The order shapes end up in the part (resourcecompiler 0923:
    /// 180c28150). Each one is appended and the whole list sorted by type
    /// with the CRT qsort. Equal types are not kept in order: the first eight
    /// of a run come out shuffled.
    /// </summary>
    public static List<T> PartOrder<T>(IEnumerable<T> shapes, Func<T, int> type)
    {
        var list = new List<T>();
        foreach (var shape in shapes)
        {
            list.Add(shape);
            Maps.CrtQsort.Sort(list, (a, b) => type(a) - type(b));
        }
        return list;
    }

    /// <summary>One triangle soup handed to RnMeshCreate.</summary>
    public sealed class Bucket
    {
        public int Attribute { get; internal set; }
        public int SurfaceProperty { get; internal set; }
        public List<Vector3> Vertices { get; } = [];
        public List<int> Indices { get; } = [];

        /// <summary>A surface property per triangle, once the soup mixes them.</summary>
        public List<byte>? Materials { get; internal set; }
    }

    /// <summary>
    /// The part builder's mesh gathering (resourcecompiler 0923: 180c29500,
    /// joining in 180c27e30). The shapes go in part order. A shape joins the
    /// newest soup of its collision attribute unless that soup already has
    /// triangles and the shape would need a per-triangle surface property
    /// above 255. Otherwise it starts a new soup, which becomes the
    /// attribute's newest. Soups left empty are removed, the last moving into
    /// the gap.
    /// </summary>
    public static List<Bucket> Group(IEnumerable<(int Attribute, int SurfaceProperty, Vector3[] Points, int[] Indices)> shapes)
    {
        var buckets = new List<Bucket>();
        var newest = new Dictionary<int, int>();
        foreach (var (attribute, surface, points, indices) in shapes)
        {
            Bucket? into = null;
            if (newest.TryGetValue(attribute, out var at))
            {
                var b = buckets[at];
                if (b.Indices.Count == 0)
                    into = b;
                else if (b.Materials == null)
                {
                    if (surface == b.SurfaceProperty || (surface <= 0xff && b.SurfaceProperty < 0x100))
                        into = b;
                }
                else if (surface < 0x100)
                    into = b;
            }
            if (into == null)
            {
                into = new Bucket();
                buckets.Add(into);
                newest[attribute] = buckets.Count - 1;
            }
            Join(into, attribute, surface, points, indices);
        }
        for (var i = 0; i < buckets.Count;)
        {
            if (buckets[i].Indices.Count != 0)
            {
                i++;
                continue;
            }
            var last = buckets.Count - 1;
            if (i != last)
                buckets[i] = buckets[last];
            buckets.RemoveAt(last);
        }
        return buckets;
    }

    private static void Join(Bucket b, int attribute, int surface, Vector3[] points, int[] indices)
    {
        var triangles = indices.Length / 3;
        if (triangles == 0)
            return;
        if (b.Indices.Count == 0)
        {
            b.Attribute = attribute;
            b.SurfaceProperty = surface;
        }
        else if (b.Materials == null)
        {
            if (b.SurfaceProperty != surface)
            {
                b.Materials = [.. Enumerable.Repeat((byte)b.SurfaceProperty, b.Indices.Count / 3)];
                b.Materials.AddRange(Enumerable.Repeat((byte)surface, triangles));
            }
        }
        else
            b.Materials.AddRange(Enumerable.Repeat((byte)surface, triangles));
        var baseVertex = b.Vertices.Count;
        b.Vertices.AddRange(points);
        foreach (var i in indices)
            b.Indices.Add(i + baseVertex);
    }
}
