using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Hammer's deformers on the map's own meshes. CMapMesh_ConvertMeshForBuilder
/// (1810dff20) hands the converted mesh to ApplyDeformer (1810c4000) when the
/// mesh's deformer lookup (181022480, the climb of 181022540) finds a set
/// lattice, unless the parent is an entity whose class asks for
/// generate_deformable_mesh: every vertex position, in the mesh's own space,
/// goes through PropDeformer_Transform with the node's matrix (vtable 0xa0),
/// before the move to the world. Every later step, render and physics, reads
/// the deformed mesh; so does the port, the positions being replaced in the
/// document once when it is loaded (<see cref="Apply"/>).
/// <para>Not ported, and thrown: a subdivided mesh (the bake runs before the
/// deformer), a scaled mesh, a mesh reached through an instance or prefab.
/// The normals and tangents the deformer also turns are left as they are
/// (only positions reach collision).</para>
/// </summary>
public static class MapDeformers
{
    private const string AppliedKey = "\u0001s2c.deformed";

    /// <summary>Deforms, in place, every mesh of the document that a deformer reaches.</summary>
    public static void Apply(DmxBinary.Document document)
    {
        var parents = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in document.Elements)
            foreach (var child in e.GetElements("children"))
                parents.TryAdd(child, e);
        foreach (var mesh in document.Elements.Where(e => e.Type == "CMapMesh"))
        {
            if (mesh.Attributes.ContainsKey(AppliedKey) || Lookup(mesh, parents) is not { } node)
                continue;
            var deformer = LatticeDeformer.FromNode(node);
            if (!deformer.Enabled)
                continue;
            var id = mesh.GetValue<int>("nodeID");
            if ((mesh.GetValue<Vector3>("scales") ?? Vector3.One) != Vector3.One)
                throw new NotSupportedException($"mesh {id}: a scaled mesh under a deformer is not ported");
            if (Physics.WorldCollision.Subdivided(mesh))
                throw new NotSupportedException($"mesh {id}: a subdivided mesh under a deformer is not ported");
            var data = mesh.Get<DmxBinary.Element>("meshData")?.Get<DmxBinary.Element>("vertexData");
            var stream = data?.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith("position:", StringComparison.Ordinal));
            if (stream?.Get<object?[]>("data") is not { } positions)
                continue;
            var evaluator = deformer.For(MapMeshes.Local(mesh));
            // Normals and tangents first, at the undeformed positions, as
            // PropDeformer_Transform takes them; each corner at its vertex.
            var meshData = mesh.Get<DmxBinary.Element>("meshData")!;
            var corners = meshData.Get<DmxBinary.Element>("faceVertexData");
            var cornerVertex = CornerVertices(meshData);
            foreach (var corner in corners?.GetElements("streams") ?? [])
            {
                var isNormal = corner.Name.StartsWith("normal:", StringComparison.Ordinal);
                var isTangent = corner.Name.StartsWith("tangent:", StringComparison.Ordinal);
                if ((!isNormal && !isTangent) || corner.Get<object?[]>("data") is not { } values)
                    continue;
                corner.Attributes["data"] = values.Select((v, c) =>
                {
                    var at = c < cornerVertex.Length && cornerVertex[c] >= 0 ? (Vector3)positions[cornerVertex[c]]! : Vector3.Zero;
                    return v switch
                    {
                        Vector3 n when isNormal => (object?)evaluator.DeformNormal(at, n),
                        Vector4 t when isTangent => evaluator.DeformTangent(at, new Vector3(t.X, t.Y, t.Z)) is var d ? new Vector4(d, t.W) : t,
                        _ => v,
                    };
                }).ToArray();
            }
            stream.Attributes["data"] = positions.Select(p => (object?)evaluator.Deform((Vector3)p!)).ToArray();
            mesh.Attributes[AppliedKey] = true;
        }
    }

    // Each corner's position index: half-edge e's corner (edgeVertexDataIndices)
    // stands at its vertex (edgeVertexIndices) through vertexDataIndices.
    private static int[] CornerVertices(DmxBinary.Element meshData)
    {
        int[] Ints(string name) => (meshData.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var to = Ints("edgeVertexIndices");
        var vertexData = Ints("vertexDataIndices");
        var cornerData = Ints("edgeVertexDataIndices");
        var result = Enumerable.Repeat(-1, cornerData.Length == 0 ? 0 : cornerData.Max() + 1).ToArray();
        for (var e = 0; e < cornerData.Length && e < to.Length; e++)
            if (cornerData[e] >= 0 && to[e] >= 0 && to[e] < vertexData.Length)
                result[cornerData[e]] = vertexData[to[e]];
        return result;
    }

    // 181022480: from the mesh's parent upwards, the first CMapDeformer, when
    // the climb is let through (MapMeshes.DeformerBelow); none past an
    // instance target's root or the world. A mesh under an entity whose class
    // has generate_deformable_mesh would be skipped here; no CS2 class has it.
    private static DmxBinary.Element? Lookup(DmxBinary.Element mesh, Dictionary<DmxBinary.Element, DmxBinary.Element> parents)
    {
        for (var node = parents.GetValueOrDefault(mesh); node != null; node = parents.GetValueOrDefault(node))
        {
            if (node.Type.StartsWith("CMapDeformer", StringComparison.Ordinal))
                return node;
            if (MapMeshes.DeformerBelow(node, node) == null)
                return null;
        }
        return null;
    }
}
