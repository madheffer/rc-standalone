using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The world renderer builder's node mesh entries as BuildNode
/// (18024b400) receives them: each visible world mesh's face sets, one per
/// (lightmap scale bias, material), in walk order, one vertex per triangle
/// corner. A corner carries position, texcoord, normal, tangent and, when the
/// mesh has it, PerVertexLighting (<see cref="MapMeshCorners"/>, cut on world
/// positions). Hammer's mesh export moves positions to the world by the
/// node's matrix and rotates normals and tangents by it
/// (HammerMesh_TransformToWorld, 1810c4e50); the builder's mesh then holds
/// them renormalised: squares summed z, y, x, times the reciprocal of the
/// root (measured exact on probe01; the function doing it is not read).
/// PerVertexLighting is baked lighting Hammer applies from a previous bake
/// (180f0ea90), the .vmap's own stream otherwise; it belongs to the baked
/// halves and is taken as given.
/// </summary>
internal static class NodeMeshEntries
{
    public sealed record Entry(int NodeId, string Material, int Stride, IReadOnlyList<Physics.MeshWeld.Stream> Streams, float[] Vertices, int[] Indices);

    /// <param name="keepsTexcoords">Whether a material stops the texcoord shift
    /// (<see cref="MapMeshCorners.KeepsTexcoords"/>); none does when absent.</param>
    public static List<Entry> FromWorld(DmxBinary.Document doc, Func<string, bool>? keepsTexcoords = null)
    {
        var entries = new List<Entry>();
        foreach (var mesh in MapMeshes.Read(doc))
        {
            if (mesh.Element is null || mesh.Hidden || mesh.ParentType != "CMapWorld")
                continue;
            var names = mesh.Element.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials")?.OfType<string>().ToArray() ?? [];
            var shift = keepsTexcoords is null || !names.Any(keepsTexcoords);
            var world = mesh.World;
            foreach (var piece in MapMeshCorners.Build(mesh.Element, shift, p => MapMeshes.Transform(world, p), withTangent: true))
            {
                var v = (float[])piece.Vertices.Clone();
                var s = piece.Stride;
                var directions = piece.Streams.Where(x => x.Name is "normal" or "tangent").Select(x => x.First).ToArray();
                for (var c = 0; c < v.Length / s; c++)
                {
                    var at = c * s;
                    var p = MapMeshes.Transform(world, new Vector3(v[at], v[at + 1], v[at + 2]));
                    (v[at], v[at + 1], v[at + 2]) = (p.X, p.Y, p.Z);
                    foreach (var first in directions)
                    {
                        var d = Normalise(Rotate(world, new Vector3(v[at + first], v[at + first + 1], v[at + first + 2])));
                        (v[at + first], v[at + first + 1], v[at + first + 2]) = (d.X, d.Y, d.Z);
                    }
                }
                var name = piece.Material < names.Length ? names[piece.Material] : "";
                entries.Add(new Entry(mesh.NodeId, name, s, piece.Streams, v, piece.Indices));
            }
        }
        return entries;
    }

    /// <summary>Matrix3x4_Rotate (18125d1b0): (x r0 + y r1) + z r2 per row.</summary>
    internal static Vector3 Rotate(float[] m, Vector3 d)
        => new(m[0] * d.X + m[1] * d.Y + m[2] * d.Z, m[4] * d.X + m[5] * d.Y + m[6] * d.Z, m[8] * d.X + m[9] * d.Y + m[10] * d.Z);

    /// <summary>Squares summed z, y, x; each component times the reciprocal of the root; zero stays zero.</summary>
    internal static Vector3 Normalise(Vector3 d)
    {
        var len = MathF.Sqrt(d.Z * d.Z + d.Y * d.Y + d.X * d.X);
        if (len == 0f)
            return d;
        var inv = 1f / len;
        return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
    }
}
