using System.Buffers.Binary;
using System.Globalization;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// One agent hull: resourcecompiler's 0x48-byte hull entry (20261015). A CNavHullVData (0x3c bytes) becomes
/// an entry through 1818cbfd0, which moves m_agentBorderErosion from +0x30 to +8 and shifts +8 .. +0x2c up
/// by four; the map's settingsAgent* arrays fill the same entry (1820567f0).
/// </summary>
public sealed class NavHull
{
    // CNavHullVData's constructor (1818f9e80) copies the static default hull at 183027660, clears the
    // flow map flag and sets its radius to 400; a vdata entry then overrides only the keys it lists.
    public bool Enabled = true;
    public float Radius = 15f;
    public int BorderErosion = -1;
    public float Height = 71f;
    public bool ShortHeightEnabled;
    public float ShortHeight = 35.5f;
    public bool CrawlEnabled;
    public float CrawlHeight = 17.5f;
    public float MaxClimb = 17.5f;
    public int MaxSlope = 50;
    public float MaxJumpDownDist = 240f;
    public float MaxJumpHorizDistBase = 64f;
    public float MaxJumpUpDist;
    public bool FlowMapGenerationEnabled;
    public float FlowMapNodeMaxRadius = 400f;
    public string Name = "";

    /// <summary>A vdata hull: the constructor's defaults, then the entry's keys (KV3 values as bool or number).</summary>
    public static NavHull FromVData(string name, IReadOnlyDictionary<string, object> keys)
    {
        var h = new NavHull { Name = name };
        static float F(object v) => Convert.ToSingle(v, CultureInfo.InvariantCulture);
        static int I(object v) => Convert.ToInt32(v, CultureInfo.InvariantCulture);
        static bool B(object v) => v is bool b ? b : Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0;
        foreach (var (k, v) in keys)
            switch (k)
            {
                case "m_bAgentEnabled": h.Enabled = B(v); break;
                case "m_agentRadius": h.Radius = F(v); break;
                case "m_agentHeight": h.Height = F(v); break;
                case "m_agentShortHeightEnabled": h.ShortHeightEnabled = B(v); break;
                case "m_agentShortHeight": h.ShortHeight = F(v); break;
                case "m_agentCrawlEnabled": h.CrawlEnabled = B(v); break;
                case "m_agentCrawlHeight": h.CrawlHeight = F(v); break;
                case "m_agentMaxClimb": h.MaxClimb = F(v); break;
                case "m_agentMaxSlope": h.MaxSlope = I(v); break;
                case "m_agentMaxJumpDownDist": h.MaxJumpDownDist = F(v); break;
                case "m_agentMaxJumpHorizDistBase": h.MaxJumpHorizDistBase = F(v); break;
                case "m_agentMaxJumpUpDist": h.MaxJumpUpDist = F(v); break;
                case "m_agentBorderErosion": h.BorderErosion = I(v); break;
                case "m_flowMapGenerationEnabled": h.FlowMapGenerationEnabled = B(v); break;
                case "m_flowMapNodeMaxRadius": h.FlowMapNodeMaxRadius = F(v); break;
            }
        return h;
    }

    /// <summary>The entry's first 0x40 bytes (the name string pointer at +0x40 is left zero).</summary>
    public byte[] Bytes()
    {
        var b = new byte[0x48];
        void F(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        b[0] = (byte)(Enabled ? 1 : 0);
        F(4, Radius);
        I(8, BorderErosion);
        F(0xc, Height);
        b[0x10] = (byte)(ShortHeightEnabled ? 1 : 0);
        F(0x14, ShortHeight);
        b[0x18] = (byte)(CrawlEnabled ? 1 : 0);
        F(0x1c, CrawlHeight);
        F(0x20, MaxClimb);
        I(0x24, MaxSlope);
        F(0x28, MaxJumpDownDist);
        F(0x2c, MaxJumpHorizDistBase);
        F(0x30, MaxJumpUpDist);
        b[0x34] = (byte)(FlowMapGenerationEnabled ? 1 : 0);
        F(0x38, FlowMapNodeMaxRadius);
        return b;
    }

    public NavHull Clone() => (NavHull)MemberwiseClone();
}

/// <summary>
/// The nav generation settings resourcecompiler hands navsystem (MapCompile_BuildNav, the runtime block of
/// CMapNavData + 0x498 chosen by 1818c95e0): the project defaults from gameinfo.gi's NavSystem block and the
/// hull presets, or the map's own settings* attributes.
/// </summary>
public sealed class NavGenSettings
{
    // The static block at 183027600 (20261015) before gameinfo is read.
    public bool UseProjectDefaults = true;
    public float TileSize = 128f, CellSize = 2f, CellHeight = 2f;
    public int RegionMinSize = 8, RegionMergeSize = 20;
    public float DetailSampleDist = 120f, DetailSampleMaxError = 2f;
    public int EdgeMaxLen = 1200;
    public float EdgeMaxError = 51f;
    public int VertsPerPoly = 4;
    public float SmallAreaOnEdgeRemovalSize = -1f;
    public string HullPreset = "", HullsVDataOverride = "";
    public List<NavHull> Hulls = [];
    public byte Byte58, Byte59;

    /// <summary>The hulls and presets of scripts/nav_hulls.vdata and scripts/nav_hulls_presets.vdata.</summary>
    public sealed record VData(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> Hulls,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Presets);

    public NavGenSettings Clone()
    {
        var c = (NavGenSettings)MemberwiseClone();
        c.Hulls = [.. Hulls.Select(h => h.Clone())];
        return c;
    }

    /// <summary>
    /// The project defaults (1818ca904): the static block, gameinfo's NavSystem keys through KeyValues
    /// GetFloat/GetInt, then the hulls of preset NavHullsPreset ("default" when absent). <paramref name="navSystem"/>
    /// looks up a NavSystem key (null when gameinfo has no NavSystem block).
    /// </summary>
    public static NavGenSettings ProjectDefaults(Func<string, string?>? navSystem, VData vdata)
    {
        var s = new NavGenSettings();
        if (navSystem is null)
            return s;
        float F(string key, float fallback) => navSystem(key) is { } v ? (float)Atof(v) : fallback;
        int I(string key, int fallback) => navSystem(key) is { } v ? Atoi(v) : fallback;
        s.TileSize = F("NavTileSize", s.TileSize);
        s.CellSize = F("NavCellSize", s.CellSize);
        s.CellHeight = F("NavCellHeight", s.CellHeight);
        s.RegionMinSize = I("NavRegionMinSize", s.RegionMinSize);
        s.RegionMergeSize = I("NavRegionMergeSize", s.RegionMergeSize);
        s.DetailSampleDist = F("NavDetailSampleDistance", s.DetailSampleDist);
        s.DetailSampleMaxError = F("NavDetailSampleMaxError", s.DetailSampleMaxError);
        s.EdgeMaxLen = I("NavEdgeMaxLen", s.EdgeMaxLen);
        s.EdgeMaxError = F("NavEdgeMaxError", s.EdgeMaxError);
        s.VertsPerPoly = I("NavVertsPerPoly", s.VertsPerPoly);
        s.SmallAreaOnEdgeRemovalSize = F("NavSmallAreaOnEdgeRemovalSize", s.SmallAreaOnEdgeRemovalSize);
        if (!string.IsNullOrEmpty(navSystem("NavAgentNumHulls")))
            throw new NotSupportedException("gameinfo NavAgentNumHulls (the deprecated agent path, 1818ca170) is not ported");
        s.LoadPreset(navSystem("NavHullsPreset") ?? "default", vdata);
        return s;
    }

    /// <summary>
    /// The hulls of a preset (1818c9910): false, hulls untouched, when the preset is missing or lists none;
    /// otherwise the hull list is cleared and each listed hull found in nav_hulls is appended in order.
    /// </summary>
    public bool LoadPreset(string preset, VData vdata)
    {
        if (!vdata.Presets.TryGetValue(preset, out var names) || names.Count == 0)
            return false;
        Hulls.Clear();
        foreach (var name in names)
            if (vdata.Hulls.TryGetValue(name, out var keys))
                Hulls.Add(NavHull.FromVData(name, keys));
        return true;
    }

    /// <summary>
    /// The map's runtime settings (1820567f0 from the CMapNavData's navData element, then 1818cbdd0 for the
    /// hulls) and the choice MapCompile_BuildNav makes from them (1818c95e0).
    /// </summary>
    public static NavGenSettings FromMap(DmxBinary.Element navData, NavGenSettings projectDefaults, VData vdata)
    {
        var map = new NavGenSettings
        {
            UseProjectDefaults = navData.GetValue<bool>("settingsUseProjectDefaults") ?? false,
            TileSize = navData.GetValue<float>("settingsTileSize") ?? 0,
            CellSize = navData.GetValue<float>("settingsCellSize") ?? 0,
            CellHeight = navData.GetValue<float>("settingsCellHeight") ?? 0,
            RegionMinSize = navData.GetValue<int>("settingsRegionMinSize") ?? 0,
            RegionMergeSize = navData.GetValue<int>("settingsRegionMergeSize") ?? 0,
            DetailSampleDist = navData.GetValue<float>("settingsDetailSampleDist") ?? 0,
            DetailSampleMaxError = navData.GetValue<float>("settingsDetailSampleMaxError") ?? 0,
            EdgeMaxLen = navData.GetValue<int>("settingsEdgeMaxLen") ?? 0,
            EdgeMaxError = navData.GetValue<float>("settingsEdgeMaxError") ?? 0,
            VertsPerPoly = navData.GetValue<int>("settingsVertsPerPoly") ?? 0,
            SmallAreaOnEdgeRemovalSize = navData.GetValue<float>("settingsSmallAreaOnEdgeRemovalSize") ?? 0,
            HullPreset = navData.Get<string>("settingsAgentHullPreset") ?? "",
            HullsVDataOverride = navData.Get<string>("settingsAgentHullsVDataOverride") ?? "",
            Byte58 = 0,
        };
        var own = MapHulls(navData);
        // 1818cbdd0: an override vdata (own settings only), else the named preset, else the project's hulls
        // or the map's arrays.
        if (!map.UseProjectDefaults && map.HullsVDataOverride.Length > 0)
            throw new NotSupportedException("settingsAgentHullsVDataOverride (1818ca4c0) is not ported");
        if (map.HullPreset.Length == 0 || !map.LoadPreset(map.HullPreset, vdata))
            map.Hulls = map.UseProjectDefaults ? [.. projectDefaults.Hulls.Select(h => h.Clone())] : own;

        // 1818c95e0: no preset named -> the project's block when UseProjectDefaults, else the map's own;
        // a named preset -> the project's numbers with the map's strings, hulls and +0x58/+0x59, byte 0 clear.
        if (map.HullPreset.Length == 0)
            return map.UseProjectDefaults ? projectDefaults.Clone() : map;
        var s = projectDefaults.Clone();
        s.UseProjectDefaults = false;
        s.HullPreset = map.HullPreset;
        s.HullsVDataOverride = map.HullsVDataOverride;
        s.Hulls = map.Hulls;
        s.Byte58 = map.Byte58;
        s.Byte59 = map.Byte59;
        return s;
    }

    /// <summary>The settingsAgent* arrays, settingsAgentNumHulls entries (1820567f0).</summary>
    private static List<NavHull> MapHulls(DmxBinary.Element e)
    {
        var n = e.GetValue<int>("settingsAgentNumHulls") ?? 0;
        T At<T>(string name, int i) where T : struct => e.Get<object?[]>(name) is { } a && i < a.Length && a[i] is T t ? t : default;
        var hulls = new List<NavHull>();
        for (var i = 0; i < n; i++)
            hulls.Add(new NavHull
            {
                Enabled = At<bool>("settingsAgentEnabled", i),
                Radius = At<float>("settingsAgentRadius", i),
                Height = At<float>("settingsAgentHeight", i),
                ShortHeightEnabled = At<bool>("settingsAgentShortHeightEnabled", i),
                ShortHeight = At<float>("settingsAgentShortHeight", i),
                CrawlEnabled = At<bool>("settingsAgentCrawlEnabled", i),
                CrawlHeight = At<float>("settingsAgentCrawlHeight", i),
                MaxClimb = At<float>("settingsAgentMaxClimb", i),
                MaxSlope = At<int>("settingsAgentMaxSlope", i),
                MaxJumpDownDist = At<float>("settingsAgentMaxJumpDownDist", i),
                MaxJumpHorizDistBase = At<float>("settingsAgentMaxJumpHorizDistBase", i),
                MaxJumpUpDist = At<float>("settingsAgentMaxJumpUpDist", i),
                BorderErosion = At<int>("settingsAgentBorderErosion", i),
                // +0x34 / +0x38 are not written for map hulls (uninitialised in Valve's vector); Recast never reads them.
                FlowMapGenerationEnabled = false,
                FlowMapNodeMaxRadius = 0,
            });
        return hulls;
    }

    /// <summary>The block's 0x60 bytes as navsystem receives it (string and vector pointers zero, count kept).</summary>
    public byte[] Bytes()
    {
        var b = new byte[0x60];
        void F(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        b[0] = (byte)(UseProjectDefaults ? 1 : 0);
        F(4, TileSize);
        F(8, CellSize);
        F(0xc, CellHeight);
        I(0x10, RegionMinSize);
        I(0x14, RegionMergeSize);
        F(0x18, DetailSampleDist);
        F(0x1c, DetailSampleMaxError);
        I(0x20, EdgeMaxLen);
        F(0x24, EdgeMaxError);
        I(0x28, VertsPerPoly);
        F(0x2c, SmallAreaOnEdgeRemovalSize);
        I(0x40, Hulls.Count);
        b[0x58] = Byte58;
        b[0x59] = Byte59;
        return b;
    }

    // KeyValues::GetFloat / GetInt on a string value: the C runtime's atof / atoi (leading number, else 0).
    private static double Atof(string s)
    {
        s = s.TrimStart();
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or '-' or '+' or 'e' or 'E'))
            end++;
        for (; end > 0; end--)
            if (double.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        return 0;
    }

    private static int Atoi(string s)
    {
        s = s.TrimStart();
        var end = s.Length > 0 && s[0] is '-' or '+' ? 1 : 0;
        while (end < s.Length && char.IsDigit(s[end]))
            end++;
        return int.TryParse(s.AsSpan(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }
}
