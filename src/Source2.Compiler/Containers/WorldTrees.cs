using System.Numerics;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The DATA tree of a map's <c>world.vwrld_c</c> for a build without baked
/// lighting: the builder parameters, one world node (its bounds and file
/// prefix), the empty lighting info and the entity lumps.
/// <para><c>m_nCompileTimestamp</c> is the compile's time and
/// <c>m_nCompileFingerprint</c> changes with every compile (probe01 and
/// cardtest, the same geometry, differ), so both are inputs.</para>
/// </summary>
public static class WorldTrees
{
    public sealed record Node(Vector3 Min, Vector3 Max, string Prefix);

    public static KVObject Data(float minDrawVolumeSize, IReadOnlyList<Node> nodes, IReadOnlyList<string> entityLumps, uint timestamp, ulong fingerprint)
    {
        var root = KVObject.Collection();
        var builder = KVObject.Collection();
        builder.Add("m_flMinDrawVolumeSize", new KVObject((double)minDrawVolumeSize));
        builder.Add("m_bBuildBakedLighting", new KVObject(false));
        builder.Add("m_bAggregateInstanceStreams", new KVObject(false));
        builder.Add("m_bakedLightingInfo", LightingInfo());
        builder.Add("m_nCompileTimestamp", new KVObject(timestamp));
        builder.Add("m_nCompileFingerprint", new KVObject(fingerprint));
        root.Add("m_builderParams", builder);
        var list = KVObject.Array();
        foreach (var n in nodes)
        {
            var o = KVObject.Collection();
            o.Add("m_vOrigin", Floats(Vector3.Zero));
            o.Add("m_vMinBounds", Floats(n.Min));
            o.Add("m_vMaxBounds", Floats(n.Max));
            o.Add("m_worldNodePrefix", new KVObject(n.Prefix));
            list.Add(o);
        }
        root.Add("m_worldNodes", list);
        root.Add("m_worldLightingInfo", LightingInfo());
        var lumps = KVObject.Array();
        foreach (var l in entityLumps)
            lumps.Add(new KVObject(l) { Flag = KVFlag.Resource });
        root.Add("m_entityLumps", lumps);
        return root;
    }

    private static KVObject LightingInfo()
    {
        var o = KVObject.Collection();
        o.Add("m_nLightmapVersionNumber", new KVObject(0u));
        o.Add("m_nLightmapGameVersionNumber", new KVObject(0u));
        var scale = KVObject.Array();
        scale.Add(new KVObject(1.0));
        scale.Add(new KVObject(1.0));
        o.Add("m_vLightmapUvScale", scale);
        o.Add("m_bHasLightmaps", new KVObject(false));
        o.Add("m_bBakedShadowsGamma20", new KVObject(false));
        o.Add("m_bCompressionEnabled", new KVObject(false));
        o.Add("m_nLPVEncoding", new KVObject(-1));
        o.Add("m_nLightmapEncoding", new KVObject(-1));
        o.Add("m_nChartPackIterations", new KVObject(0u));
        o.Add("m_nVradQuality", new KVObject(0u));
        o.Add("m_lightMaps", KVObject.Array());
        o.Add("m_bakedShadows", KVObject.Array());
        return o;
    }

    // Vectors here are double arrays.
    private static KVObject Floats(Vector3 v)
    {
        var a = KVObject.Array();
        a.Add(new KVObject((double)v.X));
        a.Add(new KVObject((double)v.Y));
        a.Add(new KVObject((double)v.Z));
        return a;
    }
}
