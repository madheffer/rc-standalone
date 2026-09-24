using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The polygon meshes of a <c>.vmap</c> (<c>CMapMesh</c>, each holding a
/// <c>CDmePolygonMesh</c>), read into world space face by face.
///
/// <para>The mesh is half-edge: <c>faceEdgeIndices</c> gives each face one of
/// its half-edges, <c>edgeNextIndices</c> walks the loop and
/// <c>edgeVertexIndices</c> names the vertex a half-edge points to, which is
/// the face's corner order. Positions are local to the mesh, whose
/// <c>origin</c>, <c>angles</c> and <c>scales</c> place it; a face's material is
/// <c>materials[materialindex]</c>.</para>
/// </summary>
public static class MapMeshes
{
    /// <summary>One face: its corners in world space, in loop order, and its material.</summary>
    public sealed record Face(Vector3[] Corners, string Material);

    /// <summary>One mesh and where it hangs: under the world, or under an entity (with its classname).</summary>
    public sealed record Mesh(int NodeId, string ParentType, string? ParentClass, Face[] Faces);

    public static List<Mesh> Read(DmxBinary.Document doc)
    {
        var parent = new Dictionary<DmxBinary.Element, DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in doc.Elements)
        {
            foreach (var child in e.GetElements("children"))
                parent.TryAdd(child, e);
        }
        var meshes = new List<Mesh>();
        foreach (var m in doc.OfType("CMapMesh"))
        {
            var p = parent.GetValueOrDefault(m);
            var className = p?.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname");
            meshes.Add(new Mesh(m.GetValue<int>("nodeID") ?? -1, p?.Type ?? "", className, Faces(m)));
        }
        return meshes;
    }

    private static Face[] Faces(DmxBinary.Element mesh)
    {
        var data = mesh.Get<DmxBinary.Element>("meshData") ?? throw new InvalidDataException("CMapMesh without meshData.");
        var positions = Stream(data, "vertexData", "position");
        var materials = Ints(data, "faceData", "materialindex");
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var next = IntArray(data, "edgeNextIndices");
        var to = IntArray(data, "edgeVertexIndices");
        var first = IntArray(data, "faceEdgeIndices");

        var origin = mesh.GetValue<Vector3>("origin") ?? Vector3.Zero;
        var angles = mesh.GetValue<Vector3>("angles") ?? Vector3.Zero;
        var scales = mesh.GetValue<Vector3>("scales") ?? Vector3.One;
        var basis = Basis(angles);

        var faces = new Face[first.Length];
        for (var f = 0; f < first.Length; f++)
        {
            var corners = new List<Vector3>();
            var e = first[f];
            do
            {
                corners.Add(Place((Vector3)positions[to[e]]!, origin, basis, scales));
                e = next[e];
            }
            while (e != first[f] && corners.Count <= next.Length);
            var material = f < materials.Length && materials[f] >= 0 && materials[f] < names.Length ? names[materials[f]] : "";
            faces[f] = new Face([.. corners], material);
        }
        return faces;
    }

    // A local position into world space. With no rotation and unit scale this
    // is local + origin, exact; the rotated case follows the classic
    // AngleMatrix and has not been checked against the compile.
    private static Vector3 Place(Vector3 v, Vector3 origin, (Vector3 X, Vector3 Y, Vector3 Z) basis, Vector3 scales)
    {
        if (basis.X == Vector3.UnitX && basis.Y == Vector3.UnitY && basis.Z == Vector3.UnitZ && scales == Vector3.One)
            return new Vector3(v.X + origin.X, v.Y + origin.Y, v.Z + origin.Z);
        float x = v.X * scales.X, y = v.Y * scales.Y, z = v.Z * scales.Z;
        return new Vector3(
            (basis.X.X * x) + (basis.Y.X * y) + (basis.Z.X * z) + origin.X,
            (basis.X.Y * x) + (basis.Y.Y * y) + (basis.Z.Y * z) + origin.Y,
            (basis.X.Z * x) + (basis.Y.Z * y) + (basis.Z.Z * z) + origin.Z);
    }

    // Pitch, yaw, roll in degrees to the forward, left and up axes.
    private static (Vector3 X, Vector3 Y, Vector3 Z) Basis(Vector3 angles)
    {
        if (angles == Vector3.Zero)
            return (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        float p = angles.X * (MathF.PI / 180f), y = angles.Y * (MathF.PI / 180f), r = angles.Z * (MathF.PI / 180f);
        float sp = MathF.Sin(p), cp = MathF.Cos(p), sy = MathF.Sin(y), cy = MathF.Cos(y), sr = MathF.Sin(r), cr = MathF.Cos(r);
        return (new Vector3(cp * cy, cp * sy, -sp),
                new Vector3((sr * sp * cy) - (cr * sy), (sr * sp * sy) + (cr * cy), sr * cp),
                new Vector3((cr * sp * cy) + (sr * sy), (cr * sp * sy) - (sr * cy), cr * cp));
    }

    private static object?[] Stream(DmxBinary.Element data, string array, string name)
    {
        var holder = data.Get<DmxBinary.Element>(array) ?? throw new InvalidDataException($"meshData without {array}.");
        var stream = holder.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal))
                     ?? throw new InvalidDataException($"{array} without a {name} stream.");
        return stream.Get<object?[]>("data") ?? [];
    }

    private static int[] Ints(DmxBinary.Element data, string array, string name)
    {
        var holder = data.Get<DmxBinary.Element>(array);
        var stream = holder?.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal));
        return (stream?.Get<object?[]>("data") ?? []).Select(x => x is int i ? i : -1).ToArray();
    }

    private static int[] IntArray(DmxBinary.Element e, string name)
        => (e.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
}
