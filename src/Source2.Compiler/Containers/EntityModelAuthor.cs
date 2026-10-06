using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// A brush entity's model with render meshes
/// (<c>maps/&lt;map&gt;/entities/&lt;name&gt;_&lt;node&gt;.vmdl_c</c>): per embedded
/// mesh its buffers and MDAT, then PHYS, CTRL (the meshes and
/// <c>embedded_physics</c>), RERL, RED2 and DATA (atixref's 58 such models).
/// RED2 is a node model's with the entity's arguments
/// (<see cref="WorldNodeModel.EntityClass"/>), the bounds and counts over
/// every mesh, and the physics model's shape counts and surface properties.
/// </summary>
public static class EntityModelAuthor
{
    /// <summary>One embedded mesh: its CTRL name, description and encoded buffers in block order.</summary>
    public sealed record Mesh(string Name, WorldNodeModel Model, IReadOnlyList<(BlockType Type, byte[] Bytes)> Buffers, IReadOnlyList<string> Materials);

    public static KVObject Ctrl(IReadOnlyList<Mesh> meshes, int physBlock)
    {
        var list = KVObject.Array();
        var block = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            list.Add(WorldNodeModelTrees.CtrlMesh(meshes[i].Model, meshes[i].Name, i, block));
            block += meshes[i].Buffers.Count + 1;
        }
        var physics = KVObject.Collection();
        physics.Add("phys_data_block", new KVObject(physBlock));
        var root = KVObject.Collection();
        root.Add("embedded_meshes", list);
        root.Add("embedded_physics", physics);
        return root;
    }

    /// <summary>
    /// The edit info: the first mesh's arguments and the render counts over
    /// all meshes, then the physics RED2's shape counts (and its default
    /// surface flag) and its surface properties.
    /// </summary>
    public static KVObject Red2(IReadOnlyList<Mesh> meshes, KVObject physicsRed2)
    {
        var red2 = WorldNodeModelTrees.Red2(meshes[0].Model);
        var user = red2["m_SearchableUserData"]!;
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        long triangles = 0, vertices = 0;
        foreach (var m in meshes)
        {
            foreach (var so in m.Model.SceneObjects)
                (min, max) = (Vector3.Min(min, so.Min), Vector3.Max(max, so.Max));
            triangles += m.Model.SceneObjects.Sum(so => so.Draws.Sum(d => d.IndexCount / 3));
            vertices += m.Model.VertexBuffers.Count > 0 ? m.Model.VertexBuffers[0].Count : 0;
        }
        var size = max - min;
        var physicsUser = physicsRed2["m_SearchableUserData"]!;
        var merged = KVObject.Collection();
        foreach (var (key, value) in user.Children)
        {
            KVObject Count(long n) => n is 0 or 1 ? new KVObject(n) : new KVObject((int)n);
            merged.Add(key, key switch
            {
                "bounds_longest" => new KVObject((double)MathF.Max(size.X, MathF.Max(size.Y, size.Z))),
                "bounds_x" => new KVObject((double)size.X),
                "bounds_y" => new KVObject((double)size.Y),
                "bounds_z" => new KVObject((double)size.Z),
                "model_lod0_triangle_count" or "model_total_triangle_count" => Count(triangles),
                "model_lod0_vertex_count" or "model_total_vertex_count" => Count(vertices),
                _ => value,
            });
            if (key == "generate_meshlets" && physicsUser.Children.FirstOrDefault(c => c.Key == "has_default_surface_property") is { Value: not null } flag)
                merged.Add(flag.Key, flag.Value);
        }
        foreach (var (key, value) in physicsUser.Children.Where(c => c.Key.StartsWith("physics_shape_", StringComparison.Ordinal)))
            merged.Add(key, value);
        var root = KVObject.Collection();
        foreach (var (key, value) in red2.Children)
            root.Add(key, key switch
            {
                "m_SearchableUserData" => merged,
                "m_SubassetReferences" => physicsRed2["m_SubassetReferences"]!,
                _ => value,
            });
        return root;
    }

    /// <summary>The model data: a node model's, one mesh group mask and LOD mask per mesh.</summary>
    public static KVObject Data(WorldNodeModel first, int meshCount)
    {
        var data = WorldNodeModelTrees.Data(first);
        var root = KVObject.Collection();
        foreach (var (key, value) in data.Children)
        {
            if (key is "m_refMeshGroupMasks" or "m_refLODGroupMasks")
            {
                var a = KVObject.Array();
                for (var i = 0; i < meshCount; i++)
                    a.Add(key == "m_refMeshGroupMasks" ? new KVObject(ulong.MaxValue) : new KVObject(255u));
                root.Add(key, a);
            }
            else
                root.Add(key, value);
        }
        return root;
    }

    public static byte[] Container(IReadOnlyList<Mesh> meshes, KVObject phys, KVObject physicsRed2)
    {
        var generic = KV3IDLookup.Get("generic");
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, WorldNodeModelAuthor.ResourceVersion);
        foreach (var m in meshes)
        {
            foreach (var (type, bytes) in m.Buffers)
                resource.Blocks.Add(ResourceBuilder.RawBlock(type, bytes, resource));
            resource.Blocks.Add(AuthoredKv3.Block(WorldNodeModelTrees.Mdat(m.Model), generic, BlockType.MDAT, resource));
        }
        var physBlock = resource.Blocks.Count;
        resource.Blocks.Add(AuthoredKv3.Block(phys, generic, BlockType.PHYS, resource));
        resource.Blocks.Add(AuthoredKv3.Block(Ctrl(meshes, physBlock), generic, BlockType.CTRL, resource));
        var rerl = new ResourceExtRefList { Resource = resource };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in meshes.SelectMany(m => m.Materials))
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
                rerl.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo { Id = Source2ResourceId.ForPath(path), Name = path });
        if (rerl.ResourceRefInfoList.Count > 0)
            resource.Blocks.Add(rerl);
        resource.Blocks.Add(AuthoredKv3.Block(Red2(meshes, physicsRed2), generic, BlockType.RED2, resource));
        resource.Blocks.Add(AuthoredKv3.Block(Data(meshes[0].Model, meshes.Count), generic, BlockType.DATA, resource));
        return ValveLayout.Serialize(resource);
    }
}
