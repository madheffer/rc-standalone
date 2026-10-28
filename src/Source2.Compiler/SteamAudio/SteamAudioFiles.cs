using ValveKeyValue;

namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The reverb bake's settings (steamaudio 1023 18006bc60): the code defaults, then gameinfo's
/// <c>ResourceCompiler/SteamAudio/ReverbDefaults</c> (AmbisonicsOrder under <c>.../Reverb/</c>), then the worldspawn
/// keys (which carry the FGD defaults once the compile fills the entity).
/// </summary>
public sealed record ReverbSettings
{
    public int GenerationType;
    public bool FilterVolumes = true;
    public bool FilterNavMesh;
    public float GridSpacing = 4.0f;
    public float HeightAboveFloor = 1.5f;
    public int RebakeOption;
    public int Rays = 0x8000;
    public int Bounces = 0x20;
    public float IRDuration = 1.0f;
    public int AmbisonicsOrder = 1;
    public bool ExportScene;
    public bool ClusteringEnable;
    public int CubeMapResolution = 0x10;
    public float DepthThreshold = 10.0f;
    public bool CompressionEnable;
    public float CompressionQuality = 0.95f;

    /// <summary>
    /// The settings for a map. <paramref name="defaults"/>: gameinfo's ReverbDefaults block (null without one),
    /// read as KeyValues GetInt / GetFloat read a string (V_atoi / V_atof); <paramref name="gameinfoOrder"/>: its
    /// <c>Reverb/AmbisonicsOrder</c>. <paramref name="world"/>: a worldspawn key as the entity's KV3 holds it (int,
    /// float or bool after the FGD conversion), null when absent; 18006bc60 reads them with the KV3 getters
    /// 1801deba0 / 1801de860 / 1801de9f0, which keep the default for a missing key.
    /// </summary>
    public static ReverbSettings From(IReadOnlyDictionary<string, string>? defaults, string? gameinfoOrder, Func<string, object?> world)
    {
        var s = new ReverbSettings();
        if (defaults != null)
        {
            int I(string k, int d) => defaults.TryGetValue(k, out var v) ? (int)CNumbers.Atoi(v) : d;
            float F(string k, float d) => defaults.TryGetValue(k, out var v) ? CNumbers.Atof(v) : d;
            s.GenerationType = I("GridGenerationType", s.GenerationType);
            s.FilterVolumes = I("FilterUsingVolumes", s.FilterVolumes ? 1 : 0) != 0;
            s.FilterNavMesh = I("FilterUsingNavMesh", s.FilterNavMesh ? 1 : 0) != 0;
            s.GridSpacing = F("GridSpacing", 4.0f);
            s.HeightAboveFloor = F("HeightAboveFloor", 1.5f);
            s.RebakeOption = I("RebakeOption", s.RebakeOption);
            s.Rays = I("NumRays", 0x8000);
            s.Bounces = I("NumBounces", 0x20);
            s.IRDuration = F("IRDuration", 1.0f);
            s.AmbisonicsOrder = gameinfoOrder != null ? (int)CNumbers.Atoi(gameinfoOrder) : 1;
            s.ClusteringEnable = I("ClusteringEnabled", 0) != 0;
            s.CubeMapResolution = I("ClusteringCubemapResolution", 0x10);
            s.DepthThreshold = F("ClusteringDepthThreshold", 10.0f);
            s.CompressionEnable = I("CompressionEnabled", s.CompressionEnable ? 1 : 0) != 0;
            s.CompressionQuality = F("CompressionQuality", s.CompressionQuality);
        }
        s.GenerationType = Int(world, "steamaudio_reverb_generation_type", s.GenerationType);
        s.FilterVolumes = Bool(world, "steamaudio_reverb_filter_volumes", s.FilterVolumes);
        s.FilterNavMesh = Bool(world, "steamaudio_reverb_filter_navmesh", s.FilterNavMesh);
        s.GridSpacing = Float(world, "steamaudio_reverb_grid_spacing", s.GridSpacing);
        s.HeightAboveFloor = Float(world, "steamaudio_reverb_height_above_floor", s.HeightAboveFloor);
        s.RebakeOption = Int(world, "steamaudio_reverb_rebake_option", s.RebakeOption);
        s.Rays = Int(world, "steamaudio_reverb_rays", s.Rays);
        s.Bounces = Int(world, "steamaudio_reverb_bounces", s.Bounces);
        s.IRDuration = Float(world, "steamaudio_reverb_ir_duration", s.IRDuration);
        s.AmbisonicsOrder = Int(world, "steamaudio_reverb_ambisonic_order", s.AmbisonicsOrder);
        s.ExportScene = Bool(world, "steamaudio_reverb_export_scene", s.ExportScene);
        s.ClusteringEnable = Bool(world, "steamaudio_reverb_clustering_enable", s.ClusteringEnable);
        s.CubeMapResolution = Int(world, "steamaudio_reverb_clustering_cubemap_resolution", s.CubeMapResolution);
        s.DepthThreshold = Float(world, "steamaudio_reverb_clustering_depth_threshold", s.DepthThreshold);
        s.CompressionEnable = Bool(world, "steamaudio_reverb_compression_enable", s.CompressionEnable);
        s.CompressionQuality = Float(world, "steamaudio_reverb_compression_quality", s.CompressionQuality);
        return s;
    }

    // A value of the getter's own type. The KV3 conversions between types (tier0's GetValue paths behind
    // 1801df480 / 1801d6c60 / 180052810) are not ported, so another type is refused rather than converted.
    private static int Int(Func<string, object?> world, string key, int def) => world(key) switch
    {
        null => def,
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        var v => throw new NotSupportedException($"worldspawn {key}: a {v.GetType().Name}, not an int"),
    };

    private static float Float(Func<string, object?> world, string key, float def) => world(key) switch
    {
        null => def,
        float f => f,
        double d => (float)d,
        var v => throw new NotSupportedException($"worldspawn {key}: a {v.GetType().Name}, not a float"),
    };

    private static bool Bool(Func<string, object?> world, string key, bool def) => world(key) switch
    {
        null => def,
        bool b => b,
        var v => throw new NotSupportedException($"worldspawn {key}: a {v.GetType().Name}, not a bool"),
    };
}

/// <summary>
/// The <c>.sareverb</c> and <c>.sapaths</c> files: binary KV3 (generic format) of steamaudio's schema classes
/// CSteamAudioBakedReverbData and the pathing data, saved binary_auto, with the probe batch appended as the
/// <c>m_pProbeBatch</c> blob (CSteamAudioBakedReverbData::SetKV3FromProbeBatch, steamaudio 1023 180071240).
/// Field order and names are the schema tables' (180325d60 and the tables it nests); pointer fields
/// (m_probes, m_clusteredProbes, the scene's) are not serialized by the schema.
/// </summary>
public static class SteamAudioFiles
{
    internal static readonly Guid GenericFormat = new("7412167c-06e9-4698-aff2-e63eb59037e7");

    private const int Bands = 3;

    private static KVObject Floats(int n)
    {
        var a = KVObject.Array();
        for (var i = 0; i < n; i++)
            a.Add(new KVObject(0f));
        return a;
    }

    private static KVObject Compressed()
    {
        var c = KVObject.Collection();
        c.Add("m_nChannels", new KVObject(0));
        c.Add("m_nBands", new KVObject(0));
        c.Add("m_nBins", new KVObject(0));
        c.Add("m_nProbes", new KVObject(0));
        c.Add("m_vecNumSingularValues", KVObject.Array());
        c.Add("m_vecDictionary", KVObject.Array());
        c.Add("m_vecCompressedData", KVObject.Array());
        return c;
    }

    private static KVObject Movables()
    {
        var m = KVObject.Collection();
        m.Add("m_vecData", KVObject.Array());
        m.Add("m_vecInitialTransforms", KVObject.Array());
        m.Add("m_vecAABBs", KVObject.Array());
        m.Add("m_vecKeys", KVObject.Array());
        return m;
    }

    /// <summary>A <c>.sareverb</c> for an automatic bake (no grid, clustering or compression data) of <paramref name="batch"/>.</summary>
    public static byte[] Reverb(byte[] batch, ReverbSettings s)
    {
        if (s.ClusteringEnable || s.CompressionEnable)
            throw new NotSupportedException("Steam Audio reverb clustering and compression are not ported");
        var root = KVObject.Collection();
        root.Add("m_nBands", new KVObject(Bands));
        root.Add("m_scene", KVObject.Collection());
        var grid = KVObject.Collection();
        var aabb = KVObject.Collection();
        aabb.Add("m_vMinBounds", Floats(3));
        aabb.Add("m_vMaxBounds", Floats(3));
        grid.Add("m_aabb", aabb);
        grid.Add("m_flSpacing", new KVObject(0d));
        grid.Add("m_nx", new KVObject(0));
        grid.Add("m_ny", new KVObject(0));
        grid.Add("m_nz", new KVObject(0));
        grid.Add("m_vecLineSegments", KVObject.Array());
        grid.Add("m_vecProbes", KVObject.Array());
        root.Add("m_grid", grid);
        var reverb = KVObject.Collection();
        reverb.Add("m_nNumRays", new KVObject(s.Rays));
        reverb.Add("m_nNumBounces", new KVObject(s.Bounces));
        reverb.Add("m_flIRDuration", new KVObject((double)s.IRDuration));
        reverb.Add("m_nAmbisonicsOrder", new KVObject(s.AmbisonicsOrder));
        reverb.Add("m_bExportScene", new KVObject(s.ExportScene));
        root.Add("m_reverbSettings", reverb);
        var clustering = KVObject.Collection();
        clustering.Add("m_bEnableClustering", new KVObject(s.ClusteringEnable));
        clustering.Add("m_nCubeMapResolution", new KVObject(s.CubeMapResolution));
        clustering.Add("m_flDepthThreshold", new KVObject((double)s.DepthThreshold));
        root.Add("m_reverbClusteringSettings", clustering);
        var compression = KVObject.Collection();
        compression.Add("m_bEnableCompression", new KVObject(s.CompressionEnable));
        compression.Add("m_flQuality", new KVObject((double)s.CompressionQuality));
        root.Add("m_reverbCompressionSettings", compression);
        root.Add("m_vecClusterForProbe", KVObject.Array());
        root.Add("m_compressedData", Compressed());
        root.Add("m_compressedClusteredData", Compressed());
        root.Add("m_movables", Movables());
        root.Add("m_pProbeBatch", KVObject.Blob(batch));
        return Kv3V5Writer.Write(root, GenericFormat);
    }

    /// <summary>A <c>.sapaths</c> holding <paramref name="batch"/>.</summary>
    public static byte[] Paths(byte[] batch)
    {
        var root = KVObject.Collection();
        root.Add("m_nBands", new KVObject(Bands));
        var probes = KVObject.Collection();
        probes.Add("m_pProbeBatch", KVObject.Blob(batch));
        root.Add("m_probes", probes);
        root.Add("m_movables", Movables());
        return Kv3V5Writer.Write(root, GenericFormat);
    }
}
