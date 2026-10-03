using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The map mesh triangles the compile puts in its ray trace environment.
///
/// <list type="bullet">
/// <item>A mesh under the world or a group counts. One under an entity counts
/// only when the entity's class renders as world: <c>FUN_1810045b0</c> gives 2
/// for a solid class whose metadata sets
/// <c>render_as_world_but_physics_as_entity</c> and 1 (an entity model of its
/// own) for any other solid class.</item>
/// <item>A face whose material is left out of the trace
/// (<see cref="MaterialVisFlags.LeftOutOfTrace"/>) is dropped.</item>
/// <item>Faces are triangulated as the compile does
/// (<see cref="PolygonTriangulator"/>); a triangle passes through as is.</item>
/// <item>A sliver triangle the emitter drops is dropped (<see cref="Emitted"/>).</item>
/// </list>
///
/// <para>Not yet covered: a face with subdivision levels, which the compile
/// tessellates, and props, which the collector only adds when its model pass
/// is on (it is off in every compile measured so far).</para>
/// </summary>
public static class MapGeometry
{
    /// <summary>One traced triangle: its corners in the compile's order, its material, and the mesh face it comes from.</summary>
    public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, string Material, int Face = -1);

    public static List<Triangle> RteTriangles(IEnumerable<MapMeshes.Mesh> meshes, Func<string, MaterialVisFlags> materials,
                                              Func<string, bool> rendersAsWorld)
    {
        var found = new List<Triangle>();
        foreach (var mesh in meshes)
        {
            if (mesh.ParentType == "CMapEntity" && !(mesh.ParentClass is { } c && rendersAsWorld(c)))
                continue;
            for (var f = 0; f < mesh.Faces.Length; f++)
            {
                var face = mesh.Faces[f];
                if (materials(face.Material).LeftOutOfTrace)
                    continue;
                int[] corners = face.Corners.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate(face.Corners);
                for (var t = 0; t < corners.Length; t += 3)
                    if (Emitted(face.Corners[corners[t]], face.Corners[corners[t + 1]], face.Corners[corners[t + 2]]))
                        found.Add(new Triangle(face.Corners[corners[t]], face.Corners[corners[t + 1]], face.Corners[corners[t + 2]], face.Material, f));
            }
        }
        return found;
    }

    /// <summary>
    /// Whether the emitter (WRB_EmitRteTriangles, resourcecompiler 1802821f0)
    /// keeps a triangle: its three edge lengths, each summed
    /// <c>(d.z^2 + d.y^2) + d.x^2</c> for edge 0-1 and <c>(d.y^2 + d.z^2) + d.x^2</c>
    /// for 1-2 and 2-0 and square rooted in float, sorted; the longest must
    /// be at least 0.0001 and, times 1.0001, no longer than the other two
    /// together. A sliver whose corners are all but in line is dropped
    /// (116 of atixref's triangles, all of them absent from its .rte).
    /// </summary>
    public static bool Emitted(Vector3 a, Vector3 b, Vector3 c)
    {
        static float Z(float d) => d * d;
        var e01 = MathF.Sqrt((Z(b.Z - a.Z) + Z(b.Y - a.Y)) + Z(b.X - a.X));
        var e12 = MathF.Sqrt((Z(c.Y - b.Y) + Z(c.Z - b.Z)) + Z(c.X - b.X));
        var e20 = MathF.Sqrt((Z(a.Y - c.Y) + Z(a.Z - c.Z)) + Z(a.X - c.X));
        // The binary's three compare-and-swaps, in its order.
        if (e01 > e12)
            (e01, e12) = (e12, e01);
        if (e12 > e20)
            (e12, e20) = (e20, e12);
        if (e01 > e12)
            (e01, e12) = (e12, e01);
        return !(0.0001f > e20) && !(e20 * 1.0001f > e12 + e01);
    }
}
