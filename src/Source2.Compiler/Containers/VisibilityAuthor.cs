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
/// The <c>___OverrideInputData___</c> fingerprint is the CRC32 of the vis
/// input blob (<see cref="InputBlob"/>; captured 2026-10-09 with
/// capture_overrides.py on probe01 -world -vis, byte for byte; probe01
/// 1380197390 and cardtest 3853475347 reproduce).
/// </summary>
public static class VisibilityAuthor
{
    /// <summary>
    /// The buffer RC hands the argument recorder for a vis compile: binary
    /// KeyValues ("VBKV", the CRC32 of the body, then the body) of a
    /// <c>vvis</c> key holding four strings: <c>rte</c> and <c>viscfg</c>,
    /// the files RC writes under the temp folder
    /// (<c>&lt;temp&gt;\csgo_addons\&lt;addon&gt;\maps\&lt;map&gt;</c>);
    /// <c>los</c>, the game folder's .los path lowercased; <c>los_errors</c>,
    /// the content folder's .los path as cased. Separators are backslashes.
    /// The paths make the fingerprint machine specific, as Valve's is.
    /// </summary>
    public static byte[] InputBlob(string temp, string game, string content, string addon, string map)
    {
        static string W(string p) => p.Replace('/', '\\').TrimEnd('\\');
        var stem = $@"csgo_addons\{addon}\maps\{W(map)}";
        var body = new List<byte> { 0 };
        void Text(string s)
        {
            body.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
            body.Add(0);
        }
        void Pair(string key, string value)
        {
            body.Add(1);
            Text(key);
            Text(value);
        }
        Text("vvis");
        Pair("rte", $@"{W(temp)}\{stem}.rte");
        Pair("viscfg", $@"{W(temp)}\{stem}.viscfg");
        Pair("los", $@"{W(game)}\{stem}.los".ToLowerInvariant());
        Pair("los_errors", $@"{W(content)}\{stem}.los");
        body.Add(0x0b);
        body.Add(0x0b);
        var blob = new byte[8 + body.Count];
        "VBKV"u8.CopyTo(blob);
        BitConverter.TryWriteBytes(blob.AsSpan(4), System.IO.Hashing.Crc32.HashToUInt32(body.ToArray()));
        body.CopyTo(blob, 8);
        return blob;
    }

    /// <summary>RED2's <c>___OverrideInputData___</c> fingerprint: the CRC32 of <see cref="InputBlob"/>.</summary>
    public static uint InputFingerprint(string temp, string game, string content, string addon, string map)
        => System.IO.Hashing.Crc32.HashToUInt32(InputBlob(temp, game, content, addon, map));

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
            a.Add("m_nFingerprint", new KVObject((uint)fingerprint));
            a.Add("m_nFingerprintDefault", new KVObject(0u));
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

    private static KVObject U32(uint v) => new(v);

    private static KVObject Floats(System.Numerics.Vector3 v)
    {
        var a = KVObject.Array();
        a.Add(new KVObject(v.X));
        a.Add(new KVObject(v.Y));
        a.Add(new KVObject(v.Z));
        return a;
    }
}
