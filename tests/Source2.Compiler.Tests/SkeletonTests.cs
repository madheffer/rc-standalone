using System.Numerics;
using Source2.Compiler.Meshes;
using ValvePak;
using ValveKeyValue;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// A model's skeleton and mesh groups, read as ValveResourceFormat reads them (all 13,105 pak01
/// models and three community packs agreed bone for bone when this was written).
/// </summary>
public class SkeletonTests
{
    private static KVObject Vector(params float[] values)
    {
        var a = KVObject.Array();
        foreach (var v in values)
            a.Add(new KVObject(v));
        return a;
    }

    private static CompiledModel SyntheticModel()
    {
        var skeleton = KVObject.Collection();
        var names = KVObject.Array();
        names.Add(new KVObject("root"));
        names.Add(new KVObject("Hand_R"));
        skeleton.Add("m_boneName", names);
        var parents = KVObject.Array();
        parents.Add(new KVObject(-1));
        parents.Add(new KVObject(0));
        skeleton.Add("m_nParent", parents);
        var flags = KVObject.Array();
        flags.Add(new KVObject(0));
        flags.Add(new KVObject(0x80));
        skeleton.Add("m_nFlag", flags);
        var positions = KVObject.Array();
        positions.Add(Vector(0, 0, 0));
        positions.Add(Vector(1, 2, 3));
        skeleton.Add("m_bonePosParent", positions);
        var rotations = KVObject.Array();
        rotations.Add(Vector(0, 0, 0, 1));
        rotations.Add(Vector(0, 0, 0.70710677f, 0.70710677f));
        skeleton.Add("m_boneRotParent", rotations);

        var data = KVObject.Collection();
        data.Add("m_name", new KVObject("models/test.vmdl"));
        data.Add("m_modelSkeleton", skeleton);
        var groups = KVObject.Array();
        foreach (var g in new[] { "body", "hat", "gloves" })
            groups.Add(new KVObject(g));
        data.Add("m_meshGroups", groups);
        data.Add("m_nDefaultMeshGroupMask", new KVObject(0b101UL));

        var resource = new CompiledResource(new ResourceFile { Version = 1 });
        resource.SetKv3("DATA", data, Kv3Formats.Get("generic"));
        return CompiledModel.Read(resource.Write());
    }

    [Fact]
    public void Skeleton_HierarchyBindPoseAndLookup()
    {
        var skeleton = SyntheticModel().Skeleton;
        Assert.Equal("models/test.vmdl", skeleton.Name);
        Assert.Equal(2, skeleton.Bones.Length);
        Assert.Equal([0], skeleton.Roots.Select(r => r.Index));
        var hand = skeleton["hand_r"]!;
        Assert.Same(skeleton.Bones[0], hand.Parent);
        Assert.Equal(ModelSkeletonBoneFlags.Mesh, hand.Flags);
        Assert.Equal(Matrix4x4.CreateFromQuaternion(hand.Angle) * Matrix4x4.CreateTranslation(new Vector3(1, 2, 3)), hand.BindPose);

        var world = new Matrix4x4[2];
        skeleton.ComputeWorldPose([], world);
        Assert.Equal(hand.BindPose, world[1]);
    }

    [Fact]
    public void DefaultMeshGroups_AreTheMaskedOnes()
    {
        var model = SyntheticModel();
        Assert.Equal(["body", "hat", "gloves"], model.MeshGroups);
        Assert.Equal(["body", "gloves"], model.DefaultMeshGroups);
    }

    [Fact]
    public void StockModels_HaveConsistentSkeletons()
    {
        if (CS2Fixtures.StockPak() is not { } path)
            return;
        using var pak = new Package();
        pak.Read(path);
        var checkedModels = 0;
        foreach (var entry in Io.VpkEntries.ByExtension(pak)["vmdl_c"].OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pak.ReadEntry(entry, out var bytes);
            var skeleton = CompiledModel.Read(bytes).Skeleton;
            if (skeleton.Bones.Length == 0)
                continue;
            foreach (var bone in skeleton.Bones)
            {
                // A repeated name finds its last bone, as the engine's token lookup does.
                Assert.Equal(bone.Name, skeleton.Bones[skeleton.GetBoneIndex(bone.Name)].Name, ignoreCase: true);
                Assert.True(bone.Parent is null || bone.Parent.Children.Contains(bone));
            }
            if (++checkedModels == 200)
                break;
        }
        Assert.True(checkedModels > 0);
    }
}
