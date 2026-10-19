using System.Numerics;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// Nav generation's input. resourcecompiler (MapCompile_BuildNav, 1800fc370 in 20260923) hands
/// navsystem the physics world its map compile built (the settle world, one static body per map
/// mesh) and navsystem pulls the triangles out of it (180048b20):
/// <list type="bullet">
/// <item>every body whose type is below 2, in the world's order, whose bounds overlap the generation
/// bounds when there are any and are not inside a NONAV markup;</item>
/// <item>each of its shapes that passes the world's query filter, through vphysics2's triangle source
/// (1802661f0): a mesh (type 3, 1802643e0) gives its triangles in BVH leaf order when its flags have
/// bit 3, else in array order, three unshared vertices each, every vertex times the shape's scale
/// (180260f50);</item>
/// <item>then the body's transform on the new vertices.</item>
/// </list>
/// </summary>
public static class NavGenInput
{
    /// <summary>
    /// The query attributes navsystem filters shapes with (180028ae0): interacts with solid, npcclip,
    /// window, passbullets, player and npc (0xc3021, _DAT_1801290c0) plus navclip when the rules know
    /// that layer (hash 0xdd7f7e69); excludes NavIgnore (0x400000); queries as npc (0x80000); every
    /// entity and owner id -1; function mask 0xffff at index 0; static and keyframed bodies (3); group
    /// 3; flags 0x49.
    /// </summary>
    public static ulong QueryInteractsWith(ulong navclipBits) => 0xc3021ul | navclipBits;

    /// <summary>
    /// The world's shape test (vphysics2 180065b80 -> 1801ff7b0 -> 1802facf0) with navsystem's query:
    /// the body type against the type mask, then the shape's collision attributes.
    /// </summary>
    public static bool ShapePasses(in Simulation.CollisionAttributes s, int bodyType, ulong navclipBits, ushort[] groupTable)
    {
        const byte typeMask = 3, queryGroup = 3, queryFlags = 0x49;
        const ulong excludeAs = 0x400000, queryAs = 0x80000;
        const ushort functionMask = 0xffff;
        const byte functionIndex = 0;
        var interactsWith = QueryInteractsWith(navclipBits);
        if (bodyType is >= 0 and <= 2 && (typeMask & (1 << bodyType)) == 0)
            return false;
        // q + 0x31 bit 0x20 (pass everything) is clear.
        // The query's entity and owner ids (+0x18, +0x1c) are -1, so the shape's entity id never
        // matches them and the test goes on.
        if ((excludeAs & s.InteractsAs) != 0 || (s.InteractsExclude & queryAs) != 0 || (s.Flags & 2) == 0
            || ((functionMask >> (s.FunctionIndex & 0x1f)) & 1) == 0)
            return false;
        var mask = s.MaskIsDirect == 1 ? s.FunctionMask : (ushort)~s.FunctionMask;
        if (((mask >> (functionIndex & 0x1f)) & 1) == 0)
            return false;
        if (s.Group == 0 || queryGroup == 0)
            return true;
        if ((s.InteractsWith & queryAs) == 0 && (s.InteractsAs & interactsWith) == 0)
            return false;
        // 1802facb0 with the query's hierarchy ids 0: never a match. The query's ids (+0x20, +0x24,
        // +0x18, +0x1c) are all -1, so the id checks pass.
        var pair = groupTable[queryGroup * 64 + s.Group];
        var collide = (pair & 1) != 0;
        if ((queryFlags & 2) != 0 && (s.Flags & 1) == 0 && collide)
        {
            collide = false;
            pair = 0;
        }
        // queryFlags & 8 with the query's entity and owner ids both -1: no entity set lookup.
        if ((queryFlags & 1) == 0 || !collide)
            return !collide && (pair & 0x1c) != 0 && (queryFlags & 4) != 0;
        // The query's own layers (byte 0 bit 1) and (byte 8 bit 1) decide the trigger cases.
        var queryByte0 = (byte)interactsWith;
        return (((queryByte0 >> 1) & 1) == 0 || (s.Flags & 0x10) == 0) && ((queryFlags & 0x10) == 0 || (((excludeAs & 0xff) >> 1) & 1) == 0);
    }

    /// <summary>
    /// Which settle-world bodies are in the world navsystem reads: resourcecompiler builds that world as
    /// its own PhysDoc whose node filter (20260923 180f4d220, 20261015 180f4d790) runs with flags 0x16
    /// (captured: every nav compile; the settle's PhysDoc has 0). With the node's owner the nearest
    /// entity at or above it: 0x10 and a true GenerateNav key keep it; a mesh under an entity needs
    /// solidity 2 (a missing key reads 1) and, with 0x10, no true AlwaysSolidIgnoreNav (flag 2); an
    /// owner whose solid key is 0 drops it (flag 4). Not modelled: flag 2's test for a non-mesh node
    /// (the entity's +0x550 and FUN_180dd2210), smart props and physics pins, and FUN_180f4bb00.
    /// </summary>
    public static Func<SettleWorld.BodyBuild, bool> NavWorldFilter(DmxBinary.Document document)
    {
        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements))
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        static string? Key(DmxBinary.Element entity, string name)
        {
            if (entity.Get<DmxBinary.Element>("entity_properties") is not { } props)
                return null;
            foreach (var (k, v) in props.Attributes)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    return v?.ToString();
            return null;
        }
        static int Int(string? v, int fallback) => int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : fallback;
        static bool Bool(string? v) => v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || Int(v, 0) != 0);
        return body =>
        {
            if (body.Node is not { } node)
                return true;
            var owner = node;
            while (owner is not null && owner.Type != "CMapEntity")
                owner = parents.GetValueOrDefault(owner);
            if (owner is null)
                return true;
            if (Bool(Key(owner, "GenerateNav")))
                return true;
            if (node.Type == "CMapMesh" && (Int(Key(owner, "solidity"), 1) != 2 || Bool(Key(owner, "AlwaysSolidIgnoreNav"))))
                return false;
            return Int(Key(owner, "solid"), -1) != 0;
        };
    }

    /// <summary>
    /// 180048b20 without generation bounds or NONAV markups (both unported): the settle world's
    /// bodies in order (the document's own body first, which has no shapes), skipping the ones in
    /// <paramref name="dynamicBodies"/> (type 2), each passing mesh shape's triangles, then the body
    /// transform. Hulls fan their faces over their own vertices (vphysics2 180130e60); spheres and
    /// capsules are not ported and throw.
    /// </summary>
    public static (List<Vector3> Verts, List<int> Indices) Extract(IReadOnlyList<SettleWorld.BodyBuild> bodies, SettleWorld.CollisionRules rules,
                                                                  ISet<int>? dynamicBodies = null, Func<SettleWorld.BodyBuild, bool>? inNavWorld = null)
    {
        var navclip = rules.TryLayer("navclip", out var bits) ? bits : 0ul;
        var table = Simulation.CollisionGroupTable.Build();
        var verts = new List<Vector3>();
        var indices = new List<int>();
        for (var bi = 0; bi < bodies.Count; bi++)
        {
            var body = bodies[bi];
            var bodyType = dynamicBodies?.Contains(bi) == true ? 2 : 0;
            if (bodyType >= 2 || inNavWorld?.Invoke(body) == false)
                continue;
            var start = verts.Count;
            foreach (var shape in body.Shapes)
            {
                if (!ShapePasses(shape.Attributes, bodyType, navclip, table))
                    continue;
                if (shape.Hull is { } hull && shape.Type == Simulation.BroadphaseShape.HullType)
                {
                    // vphysics2 180130e60: the hull's vertices times its scale, then each face fanned
                    // from its first half-edge's origin.
                    var at = verts.Count;
                    foreach (var p in hull.VertexPositions)
                        verts.Add(new Vector3(p.X * shape.HullScale, p.Y * shape.HullScale, p.Z * shape.HullScale));
                    foreach (var face in hull.Faces)
                    {
                        var first = hull.Edges[face];
                        var p1 = hull.Edges[first.Next];
                        var p2 = hull.Edges[p1.Next];
                        while (true)
                        {
                            indices.Add(at + first.Origin);
                            indices.Add(at + p1.Origin);
                            indices.Add(at + p2.Origin);
                            if (p2.Next == face)
                                break;
                            p1 = p2;
                            p2 = hull.Edges[p2.Next];
                        }
                    }
                    continue;
                }
                if (shape.Mesh is not { } mesh)
                    throw new NotSupportedException($"nav input from a type {shape.Type} shape is not ported");
                // RnMesh flags bit 3 walks the BVH leaves; map meshes have no flags.
                foreach (var t in MeshTriangleOrder(mesh, (mesh.Flags & 8) != 0))
                {
                    var (a, b, c) = mesh.Triangles[t];
                    foreach (var v in new[] { a, b, c })
                    {
                        indices.Add(verts.Count);
                        verts.Add(mesh.Vertices[v] * shape.MeshScale);
                    }
                }
            }
            for (var i = start; i < verts.Count; i++)
                verts[i] = TransformByBody(verts[i], body.Position, body.Scale, body.Orientation);
        }
        return (verts, indices);
    }

    /// <summary>The triangle order vphysics2's mesh source walks (1802643e0).</summary>
    public static IEnumerable<int> MeshTriangleOrder(RnMesh mesh, bool bvhOrder)
    {
        if (!bvhOrder)
        {
            for (var i = 0; i < mesh.Triangles.Length; i++)
                yield return i;
            yield break;
        }
        foreach (var node in mesh.Nodes)
        {
            if ((node.Children & 0xc0000000u) != 0xc0000000u)
                continue;
            var first = (int)node.TriangleOffset;
            var count = (int)(node.Children & 0x3fffffffu);
            for (var t = first; t < first + count; t++)
                yield return t;
        }
    }

    /// <summary>
    /// 180048b20's body transform (from body vfn 0x1b0: position, scale, quaternion):
    /// t = 2 (q x v); v' = ((q x t) + w t + v) s + p, summed as the code does.
    /// </summary>
    public static Vector3 TransformByBody(Vector3 v, Vector3 position, float scale, Quaternion q)
    {
        float x = v.X, y = v.Y, z = v.Z;
        float qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
        var tx = z * qy - y * qz;
        var ty = x * qz - z * qx;
        var tz = y * qx - x * qy;
        tx += tx;
        ty += ty;
        tz += tz;
        return new Vector3(
            ((tz * qy - ty * qz) + qw * tx + x) * scale + position.X,
            ((tx * qz - tz * qx) + qw * ty + y) * scale + position.Y,
            ((ty * qx - tx * qy) + qw * tz + z) * scale + position.Z);
    }
}
