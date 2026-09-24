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
/// </list>
///
/// <para>Not yet covered: a face with subdivision levels, which the compile
/// tessellates, and props, which the collector only adds when its model pass
/// is on (it is off in every compile measured so far).</para>
/// </summary>
public static class MapGeometry
{
    /// <summary>One traced triangle: its corners in the compile's order, and its material.</summary>
    public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, string Material);

    public static List<Triangle> RteTriangles(IEnumerable<MapMeshes.Mesh> meshes, Func<string, MaterialVisFlags> materials,
                                              Func<string, bool> rendersAsWorld)
    {
        var found = new List<Triangle>();
        foreach (var mesh in meshes)
        {
            if (mesh.ParentType == "CMapEntity" && !(mesh.ParentClass is { } c && rendersAsWorld(c)))
                continue;
            foreach (var face in mesh.Faces)
            {
                if (materials(face.Material).LeftOutOfTrace)
                    continue;
                int[] corners = face.Corners.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate(face.Corners);
                for (var t = 0; t < corners.Length; t += 3)
                    found.Add(new Triangle(face.Corners[corners[t]], face.Corners[corners[t + 1]], face.Corners[corners[t + 2]], face.Material));
            }
        }
        return found;
    }
}
