using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The overlay descriptors a world node's BuildNode projects
/// (WRBNode_GenerateOverlayMeshes): one per material of each visible
/// <c>CMapStaticOverlay</c>, in walk order, as
/// CMapStaticOverlay_GetOverlayDescs (181194f40) fills them. Each face is the
/// overlay's polygon in world space with its texcoords, corners from the
/// face's first half-edge.
/// </summary>
internal static class NodeOverlays
{
    public sealed record Face(Vector3[] Positions, Vector2[] Texcoords);

    public sealed record Descriptor(int NodeId, string Material, int Mode, float Far, int RenderOrder, bool BackFaces, float BackFacingAngle,
                                    uint Tint, int[] Targets, Face[] Faces);

    public static List<Descriptor> FromWorld(DmxBinary.Document doc)
    {
        var found = new List<Descriptor>();
        foreach (var overlay in MapMeshes.ReadOverlays(doc))
        {
            if (overlay.Hidden || overlay.Element is not { } e || e.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            var positions = Stream(data, "vertexData", "position");
            var texcoords = Stream(data, "faceVertexData", "texcoord");
            var materialIndex = Stream(data, "faceData", "materialindex");
            var materials = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
            var next = Ints(data, "edgeNextIndices");
            var to = Ints(data, "edgeVertexIndices");
            var vertexData = Ints(data, "vertexDataIndices");
            var cornerData = Ints(data, "edgeVertexDataIndices");
            var first = Ints(data, "faceEdgeIndices");
            var faceData = Ints(data, "faceDataIndices");
            var scales = e.GetValue<Vector3>("scales") ?? Vector3.One;
            var byMaterial = new SortedDictionary<int, List<Face>>();
            for (var f = 0; f < first.Length; f++)
            {
                var pts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var h = first[f];
                do
                {
                    pts.Add(Snap(MapMeshes.Transform(overlay.World, (Vector3)positions[vertexData[to[h]]]! * scales)));
                    uvs.Add(texcoords.Length > 0 ? (Vector2)texcoords[cornerData[h]]! : Vector2.Zero);
                    h = next[h];
                } while (h != first[f] && pts.Count <= next.Length);
                var m = materialIndex.Length > 0 && materialIndex[faceData[f]] is int mi ? mi : 0;
                if (!byMaterial.TryGetValue(m, out var faces))
                    byMaterial[m] = faces = [];
                faces.Add(new Face([.. pts], Recentre(uvs)));
            }
            var tint = e.GetValue<Vector4>("tintColor");
            var targets = (e.Get<object?[]>("projectionTargets") ?? []).OfType<int>().ToArray();
            foreach (var (m, faces) in byMaterial)
                found.Add(new Descriptor(overlay.NodeId, m < materials.Length ? materials[m] : "", e.GetValue<int>("projectionMode") ?? 0,
                                         e.GetValue<float>("projectionFar") ?? 0f, e.GetValue<int>("renderOrder") ?? 0, e.GetValue<bool>("projectOnBackFaces") ?? false,
                                         e.GetValue<float>("backFacingAngle") ?? 90f, 0, targets, [.. faces]));
        }
        return found;
    }

    /// <summary>
    /// A face's texcoords whose box leaves [0, 1] lose the box centre
    /// truncated toward zero (measured on all 297 atixref overlays: 1.5 and
    /// 2.5 lose 1 and 2, -1.6 loses -1; the code doing it is not read).
    /// </summary>
    private static Vector2[] Recentre(List<Vector2> uvs)
    {
        float minU = float.MaxValue, minV = float.MaxValue, maxU = -float.MaxValue, maxV = -float.MaxValue;
        foreach (var uv in uvs)
        {
            minU = MathF.Min(minU, uv.X);
            maxU = MathF.Max(maxU, uv.X);
            minV = MathF.Min(minV, uv.Y);
            maxV = MathF.Max(maxV, uv.Y);
        }
        if (!(minU < 0f || minV < 0f || 1f < maxU || 1f < maxV))
            return [.. uvs];
        var su = (float)(int)((maxU + minU) * 0.5f);
        var sv = (float)(int)((maxV + minV) * 0.5f);
        return [.. uvs.Select(uv => new Vector2(uv.X - su, uv.Y - sv))];
    }

    /// <summary>HammerMesh_SnapVertices (1810db6a0) at 1/8: floor(p / g + 0.5) * g per axis.</summary>
    internal static Vector3 Snap(Vector3 p)
    {
        const float g = 0.125f;
        return new(MathF.Floor((p.X / g) + 0.5f) * g, MathF.Floor((p.Y / g) + 0.5f) * g, MathF.Floor((p.Z / g) + 0.5f) * g);
    }

    private static object?[] Stream(DmxBinary.Element data, string holder, string name)
        => data.Get<DmxBinary.Element>(holder)?.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];

    private static int[] Ints(DmxBinary.Element e, string name)
        => (e.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
}
