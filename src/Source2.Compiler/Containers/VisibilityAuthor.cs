using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using VoxelVisibility = Source2.Compiler.Maps.VoxelVisibility;

namespace Source2.Compiler;

/// <summary>
/// A map's <c>world_visibility.vvis_c</c>: RED2 (the vis compile's four
/// arguments, no special dependency), DATA (the scalars and the six block
/// ranges of <see cref="VoxelVisibility.Layout"/>) and VXVS (the payload).
/// The <c>___OverrideInputData___</c> fingerprint hashes the vis input blob,
/// whose hash is not derived (probe01 1380197390, cardtest 3853475347); it is
/// an input here, 0 when unknown.
/// </summary>
public static class VisibilityAuthor
{
    public static KVObject Data(VoxelVisibility vis)
    {
        var layout = VoxelVisibility.Layout.For(vis);
        var root = KVObject.Collection();
        root.Add("m_nBaseClusterCount", U32(vis.BaseClusterCount));
        root.Add("m_nPVSBytesPerCluster", U32(vis.PVSBytesPerCluster));
        root.Add("m_vMinBounds", Floats(vis.MinBounds));
        root.Add("m_vMaxBounds", Floats(vis.MaxBounds));
        root.Add("m_flGridSize", new KVObject((double)vis.GridSize));
        root.Add("m_nSkyVisibilityCluster", U32(vis.SkyVisibilityCluster));
        root.Add("m_nSunVisibilityCluster", U32(vis.SunVisibilityCluster));
        void Block(string name, VisBlockRef r)
        {
            var o = KVObject.Collection();
            o.Add("m_nOffset", U32((uint)r.Offset));
            o.Add("m_nElementCount", U32((uint)r.Count));
            root.Add(name, o);
        }
        Block("m_NodeBlock", layout.Nodes);
        Block("m_RegionBlock", layout.Regions);
        Block("m_EnclosedClusterListBlock", layout.EnclosedClusterList);
        Block("m_EnclosedClustersBlock", layout.EnclosedClusters);
        Block("m_MasksBlock", layout.Masks);
        Block("m_nVisBlocks", layout.VisBlocks);
        return root;
    }

    public static KVObject Red2(uint inputFingerprint)
    {
        KVObject Argument(string name, string type, long fingerprint)
        {
            var a = KVObject.Collection();
            a.Add("m_ParameterName", new KVObject(name));
            a.Add("m_ParameterType", new KVObject(type));
            a.Add("m_nFingerprint", fingerprint is 0 or 1 ? new KVObject(fingerprint) : new KVObject((uint)fingerprint));
            a.Add("m_nFingerprintDefault", new KVObject(0L));
            return a;
        }
        var args = KVObject.Array();
        args.Add(Argument("___OverrideInputData___", "BinaryBlobArg", inputFingerprint));
        args.Add(Argument("disablevis", "FloatArg", 0));
        args.Add(Argument("FlatVisClusterVector", "CustomArg", uint.MaxValue));
        args.Add(Argument("MutualVisibilityMatrix", "CustomArg", uint.MaxValue));
        var user = KVObject.Collection();
        user.Add("IsChildResource", new KVObject(1L));
        var root = KVObject.Collection();
        root.Add("m_InputDependencies", KVObject.Array());
        root.Add("m_AdditionalInputDependencies", KVObject.Array());
        root.Add("m_ArgumentDependencies", args);
        foreach (var key in new[] { "m_SpecialDependencies", "m_SpecialInputDependencies", "m_AdditionalRelatedFiles", "m_ChildResourceList", "m_WeakReferenceList" })
            root.Add(key, KVObject.Array());
        root.Add("m_SearchableUserData", user);
        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return root;
    }

    public static byte[] Container(VoxelVisibility vis, uint inputFingerprint = 0)
    {
        var generic = KV3IDLookup.Get("generic");
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, 7);
        resource.Blocks.Add(AuthoredKv3.Block(Red2(inputFingerprint), generic, BlockType.RED2, resource));
        resource.Blocks.Add(AuthoredKv3.Block(Data(vis), generic, BlockType.DATA, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.VXVS, vis.WriteVxvs(), resource));
        return ValveLayout.Serialize(resource);
    }

    // 0 and 1 take KV3's no-payload codes, the rest UInt32.
    private static KVObject U32(uint v) => v is 0 or 1 ? new KVObject((long)v) : new KVObject(v);

    private static KVObject Floats(System.Numerics.Vector3 v)
    {
        var a = KVObject.Array();
        a.Add(new KVObject(v.X));
        a.Add(new KVObject(v.Y));
        a.Add(new KVObject(v.Z));
        return a;
    }
}
