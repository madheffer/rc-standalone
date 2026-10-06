using System.Numerics;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The DATA tree of a world node's scene file (<c>worldnodes/n0.vwnod_c</c>):
/// its render objects (<c>m_sceneObjects</c>), aggregate objects with one
/// mesh per fragment, the layer table and the node lighting info of a build
/// without baked lighting.
/// <para>Object flags are ObjectTypeFlags_t (enum values read from
/// worldrenderer's schema): a value with one bit set, or none, is written as
/// its enumerator name, any other as its integer.</para>
/// </summary>
public static class WorldNodeSceneTrees
{
    /// <summary>A render object: its model, object flags, overlay order, env map handshakes, emissive boost and fades.</summary>
    public sealed record SceneObject(string Model, uint Flags, int OverlayRenderOrder, int CubeMap, int LightProbe, float EmissiveBoost,
                                     float FadeStart = 0f, float FadeEnd = 0f, int Layer = 0);

    /// <summary>One fragment of an aggregate: its draw call, object flags, light probe handshake and tint (0-255).</summary>
    public sealed record AggregateMesh(int DrawCall, uint ObjectFlags, int LightProbe, Vector3 Tint)
    {
        /// <summary>A prop fragment's placement (3x4, row major), its LOD bit and LOD setup.</summary>
        public float[]? Transform { get; init; }
        public int LodMask { get; init; }
        public int LodSetup { get; init; } = -1;
    }

    /// <summary>A prop's LOD setup: origin, largest scale and switch distances.</summary>
    public sealed record LodSetup(Vector3 Origin, float MaxScale, IReadOnlyList<float> SwitchDistances);

    /// <summary>An aggregate object: its model, layer and fragments.</summary>
    public sealed record Aggregate(string Model, int Layer, IReadOnlyList<AggregateMesh> Meshes)
    {
        public IReadOnlyList<LodSetup> LodSetups { get; init; } = [];
    }

    // ObjectTypeFlags_t, from worldrenderer.dll's schema enumerators.
    private static readonly (uint Value, string Name)[] ObjectTypes =
    [
        (0, "OBJECT_TYPE_NONE"), (8, "OBJECT_TYPE_MODEL"), (16, "OBJECT_TYPE_BLOCK_LIGHT"), (32, "OBJECT_TYPE_NO_SHADOWS"),
        (64, "OBJECT_TYPE_WORLDSPACE_TEXURE_BLEND"), (128, "OBJECT_TYPE_DISABLED_IN_LOW_QUALITY"), (512, "OBJECT_TYPE_RENDER_WITH_DYNAMIC"),
        (1024, "OBJECT_TYPE_RENDER_TO_CUBEMAPS"), (2048, "OBJECT_TYPE_MODEL_HAS_LODS"), (8192, "OBJECT_TYPE_OVERLAY"),
        (16384, "OBJECT_TYPE_PRECOMPUTED_VISMEMBERS"), (32768, "OBJECT_TYPE_STATIC_CUBE_MAP"), (65536, "OBJECT_TYPE_DISABLE_VIS_CULLING"),
        (131072, "OBJECT_TYPE_BAKED_GEOMETRY"), (262144, "OBJECT_TYPE_NEEDS_DYNAMIC_SHADOWS"), (524288, "OBJECT_TYPE_HAS_AGGREGATE_RTPROXY"),
        (1048576, "OBJECT_TYPE_HAS_EMISSIVE_GI"),
    ];

    public static KVObject Data(IReadOnlyList<SceneObject> objects, IReadOnlyList<Aggregate> aggregates)
    {
        var root = KVObject.Collection();
        var list = KVObject.Array();
        foreach (var o in objects)
        {
            var so = KVObject.Collection();
            so.Add("m_nObjectID", Int(0));
            so.Add("m_vTransform", Identity());
            so.Add("m_flFadeStartDistance", new KVObject((double)o.FadeStart));
            so.Add("m_flFadeEndDistance", new KVObject((double)o.FadeEnd));
            so.Add("m_vTintColor", Doubles(1, 1, 1, 1));
            so.Add("m_skin", new KVObject(""));
            so.Add("m_nObjectTypeFlags", Flags(o.Flags));
            so.Add("m_vLightingOrigin", Doubles(float.MaxValue, float.MaxValue, float.MaxValue));
            so.Add("m_nOverlayRenderOrder", Int(o.OverlayRenderOrder));
            so.Add("m_nLODOverride", Int(-1));
            so.Add("m_nCubeMapPrecomputedHandshake", Int(o.CubeMap));
            so.Add("m_nLightProbeVolumePrecomputedHandshake", Int(o.LightProbe));
            so.Add("m_flEmissiveLightingBoost", new KVObject((double)o.EmissiveBoost));
            so.Add("m_renderableModel", new KVObject(o.Model) { Flag = KVFlag.Resource });
            so.Add("m_renderable", new KVObject("") { Flag = KVFlag.Resource });
            list.Add(so);
        }
        root.Add("m_sceneObjects", list);
        root.Add("m_visClusterMembership", KVObject.Array());
        var aggs = KVObject.Array();
        foreach (var a in aggregates)
        {
            var ao = KVObject.Collection();
            uint all = a.Meshes.Aggregate(uint.MaxValue, (x, m) => x & m.ObjectFlags), any = a.Meshes.Aggregate(0u, (x, m) => x | m.ObjectFlags);
            ao.Add("m_allFlags", Flags(a.Meshes.Count == 0 ? 0 : all));
            ao.Add("m_anyFlags", Flags(any));
            ao.Add("m_nLayer", Int(a.Layer));
            ao.Add("m_instanceStream", Int(-1));
            ao.Add("m_vertexAlbedoStream", Int(-1));
            ao.Add("m_vertexEmissiveStream", Int(-1));
            var meshes = KVObject.Array();
            foreach (var m in a.Meshes)
            {
                var mo = KVObject.Collection();
                mo.Add("m_nVisClusterMemberOffset", Int(0));
                mo.Add("m_nVisClusterMemberCount", Int(0));
                mo.Add("m_bHasTransform", new KVObject(m.Transform != null));
                // Written Int32 when set (atixref's power_outlet_campground: 1).
                mo.Add("m_nLODGroupMask", m.LodMask == 0 ? Int(0) : new KVObject(m.LodMask));
                mo.Add("m_nDrawCallIndex", Int(m.DrawCall));
                mo.Add("m_nLODSetupIndex", Int(m.LodSetup));
                var tint = KVObject.Array();
                tint.Add(new KVObject((uint)m.Tint.X));
                tint.Add(new KVObject((uint)m.Tint.Y));
                tint.Add(new KVObject((uint)m.Tint.Z));
                mo.Add("m_vTintColor", tint);
                mo.Add("m_objectFlags", Flags(m.ObjectFlags));
                mo.Add("m_nLightProbeVolumePrecomputedHandshake", m.LightProbe == 0 ? Int(0) : new KVObject(m.LightProbe));
                mo.Add("m_nInstanceStreamOffset", Int(0));
                mo.Add("m_nVertexAlbedoStreamOffset", Int(0));
                mo.Add("m_nVertexEmissiveStreamOffset", Int(0));
                mo.Add("m_instanceStreams", new KVObject("AGGREGATE_INSTANCE_STREAM_NONE"));
                mo.Add("m_fEmissiveFactor", new KVObject(0.0));
                meshes.Add(mo);
            }
            ao.Add("m_aggregateMeshes", meshes);
            var setups = KVObject.Array();
            foreach (var l in a.LodSetups)
            {
                var lo = KVObject.Collection();
                lo.Add("m_vLODOrigin", Doubles(l.Origin.X, l.Origin.Y, l.Origin.Z));
                lo.Add("m_fMaxObjectScale", new KVObject((double)l.MaxScale));
                lo.Add("m_fSwitchDistances", Doubles([.. l.SwitchDistances.Select(x => (double)x)]));
                setups.Add(lo);
            }
            ao.Add("m_lodSetups", setups);
            ao.Add("m_visClusterMembership", KVObject.Array());
            var transforms = KVObject.Array();
            foreach (var m in a.Meshes.Where(m => m.Transform != null))
                transforms.Add(Doubles([.. m.Transform!.Select(x => (double)x)]));
            ao.Add("m_fragmentTransforms", transforms);
            ao.Add("m_renderableModel", new KVObject(a.Model) { Flag = KVFlag.Resource });
            aggs.Add(ao);
        }
        root.Add("m_aggregateSceneObjects", aggs);
        foreach (var key in new[] { "m_clutterSceneObjects", "m_rtProxies", "m_extraVertexStreamOverrides", "m_materialOverrides",
                                    "m_extraVertexStreams", "m_aggregateInstanceStreams", "m_vertexAlbedoStreams", "m_vertexEmissiveStreams" })
            root.Add(key, KVObject.Array());
        var layers = KVObject.Array();
        layers.Add(new KVObject("world_layer_base"));
        root.Add("m_layerNames", layers);
        var indices = KVObject.Array();
        foreach (var o in objects)
            indices.Add(new KVObject((uint)o.Layer));
        root.Add("m_sceneObjectLayerIndices", indices);
        root.Add("m_grassFileName", new KVObject(""));
        var lighting = KVObject.Collection();
        lighting.Add("m_nLightmapVersionNumber", Int(0));
        lighting.Add("m_nLightmapGameVersionNumber", Int(0));
        lighting.Add("m_vLightmapUvScale", Doubles(1, 1));
        lighting.Add("m_bHasLightmaps", new KVObject(false));
        lighting.Add("m_bBakedShadowsGamma20", new KVObject(false));
        lighting.Add("m_bCompressionEnabled", new KVObject(false));
        lighting.Add("m_nLPVEncoding", Int(-1));
        lighting.Add("m_nLightmapEncoding", Int(-1));
        lighting.Add("m_nChartPackIterations", Int(0));
        lighting.Add("m_nVradQuality", Int(0));
        lighting.Add("m_lightMaps", KVObject.Array());
        lighting.Add("m_bakedShadows", KVObject.Array());
        root.Add("m_nodeLightingInfo", lighting);
        root.Add("m_bHasBakedGeometryFlag", new KVObject(true));
        return root;
    }

    private static KVObject Flags(uint flags)
    {
        foreach (var (value, name) in ObjectTypes)
            if (value == flags)
                return new KVObject(name);
        return Int((int)flags);
    }

    private static KVObject Int(int v) => v is 0 or 1 ? new KVObject((long)v) : new KVObject(v);

    private static KVObject Identity()
    {
        var m = KVObject.Array();
        m.Add(Doubles(1, 0, 0, 0));
        m.Add(Doubles(0, 1, 0, 0));
        m.Add(Doubles(0, 0, 1, 0));
        return m;
    }

    // The scene object's matrix, tint and lighting origin are double arrays; other vectors float.
    private static KVObject Doubles(params double[] values)
    {
        var a = KVObject.Array();
        foreach (var v in values)
            a.Add(new KVObject(v));
        return a;
    }

    private static KVObject Floats(params float[] values)
    {
        var a = KVObject.Array();
        foreach (var v in values)
            a.Add(new KVObject(v));
        return a;
    }
}
