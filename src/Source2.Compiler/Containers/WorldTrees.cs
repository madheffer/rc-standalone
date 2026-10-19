using System.Numerics;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The DATA tree of a map's <c>world.vwrld_c</c>: the builder parameters, one
/// world node (its bounds and file prefix), the lighting info and the entity
/// lumps. Without baked lighting the lighting info is the empty one; with it
/// (<see cref="BakedLighting"/>) m_bBuildBakedLighting is set and
/// m_worldLightingInfo lists the lightmaps and the baked shadows, while
/// m_builderParams.m_bakedLightingInfo stays the empty one (probe01,
/// cardtest, ze_hold_em_p, cs_script_demo).
/// <para><c>m_nCompileTimestamp</c> is the compile's time and
/// <c>m_nCompileFingerprint</c> changes with every compile (probe01 and
/// cardtest, the same geometry, differ), so both are inputs.</para>
/// </summary>
public static class WorldTrees
{
    public sealed record Node(Vector3 Min, Vector3 Max, string Prefix);

    /// <summary>One light's baked shadow: its light_path_uniqueid, light_map_uniqueid and bakedshadowindex.</summary>
    public readonly record struct BakedShadow(uint LightHash, uint MapHash, long Channel);

    /// <summary>
    /// A baked compile's m_worldLightingInfo. <paramref name="UvScale"/> is 65535 over the
    /// lightmap's uv range per axis (as the query data's, from the side left after the mip and
    /// MinSpec halvings: 1.14284 at 4096, 1.032242 at 1024). The lightmaps are listed
    /// direct_light_shadows, irradiance, debug_chart_color, directional_irradiance, the
    /// shadows in the lump's order of the lights that took a channel.
    /// </summary>
    public sealed record BakedLighting(float UvScaleU, float UvScaleV, bool Compressed, uint ChartPackIterations, uint VradQuality,
                                       IReadOnlyList<string> LightMaps, IReadOnlyList<BakedShadow> Shadows);

    public static KVObject Data(float minDrawVolumeSize, IReadOnlyList<Node> nodes, IReadOnlyList<string> entityLumps, uint timestamp, ulong fingerprint,
                                BakedLighting? baked = null)
    {
        var root = KVObject.Collection();
        var builder = KVObject.Collection();
        builder.Add("m_flMinDrawVolumeSize", new KVObject((double)minDrawVolumeSize));
        builder.Add("m_bBuildBakedLighting", new KVObject(baked is not null));
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
        root.Add("m_worldLightingInfo", baked is null ? LightingInfo() : LitInfo(baked));
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

    private static KVObject LitInfo(BakedLighting b)
    {
        var o = KVObject.Collection();
        o.Add("m_nLightmapVersionNumber", new KVObject(8u));
        o.Add("m_nLightmapGameVersionNumber", new KVObject(2u));
        var scale = KVObject.Array();
        scale.Add(new KVObject((double)b.UvScaleU));
        scale.Add(new KVObject((double)b.UvScaleV));
        o.Add("m_vLightmapUvScale", scale);
        o.Add("m_bHasLightmaps", new KVObject(true));
        o.Add("m_bBakedShadowsGamma20", new KVObject(false));
        o.Add("m_bCompressionEnabled", new KVObject(b.Compressed));
        o.Add("m_nLPVEncoding", new KVObject(0L));
        o.Add("m_nLightmapEncoding", new KVObject(0L));
        o.Add("m_nChartPackIterations", new KVObject(b.ChartPackIterations));
        o.Add("m_nVradQuality", new KVObject(b.VradQuality));
        var maps = KVObject.Array();
        foreach (var m in b.LightMaps)
            maps.Add(new KVObject(m) { Flag = KVFlag.Resource });
        o.Add("m_lightMaps", maps);
        var shadows = KVObject.Array();
        foreach (var s in b.Shadows)
        {
            var e = KVObject.Collection();
            e.Add("m_nLightHash", new KVObject(s.LightHash));
            e.Add("m_nMapHash", new KVObject(s.MapHash));
            e.Add("m_nShadowChannel", new KVObject(s.Channel));
            shadows.Add(e);
        }
        o.Add("m_bakedShadows", shadows);
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
