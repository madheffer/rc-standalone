using Source2.Compiler.Maps;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// Which layers a world aggregate's fragments join (docs/CUBEMAPS.md "The draw list", GROUND_TRUTH 157, 159), every
/// input from the code that makes it:
/// <list type="bullet">
/// <item>The draw call's flags (+0x1c): meshsystem 180027160 from the material's attributes (vf 0x48 bool, 0x50
/// int, keys lowercased MurmurHash2); the meshsystem object's flag callbacks it ends with were none in the
/// 1.41.9.2 runs (layer_members.js).</item>
/// <item>The draw call's +0x14: the model's draw-call booleans by meshsystem 180049520's table (180175160), then
/// 180027830's set and clear from three more attributes.</item>
/// <item>A fragment's flags (the 0x50-byte record's +0x14): worldrenderer 18001e920 from the aggregate mesh's
/// m_objectFlags and the draw call's material flags; the object's +0xe0 gains 0x40 for a fragment with 0x100
/// (scenesystem 1800380a0).</item>
/// <item>The tests: scenesystem 180041f30 (the draw call against the layer) and 180041a00 (each fragment).</item>
/// </list>
/// </summary>
public static class CubemapLayerFlags
{
    private static bool B(MeshEntryFlags.IAttributes a, uint key) => a.Bool(key) == true;

    private static bool B(MeshEntryFlags.IAttributes a, string name) => a.Bool(MeshEntryFlags.Key(name)) == true;

    private static int I(MeshEntryFlags.IAttributes a, uint key) => a.Int(key) ?? 0;

    /// <summary>meshsystem 180027160: a draw call's +0x1c from its material (0 without one, 180027140).</summary>
    public static uint MaterialFlags(MeshEntryFlags.IAttributes? a)
    {
        if (a == null)
            return 0;
        uint f = I(a, 0x4adb6806) != 0 ? 4u : B(a, "bShaderBlended") ? 8u : 1u;   // translucent (int)
        if (!B(a, 0xe0be7b6e))                                                   // donotcastshadows
            f |= 0x20;
        if ((f & 0x20) != 0 && B(a, "NeedsDynamicShadows"))
            f |= 0x40;
        if (B(a, 0x18b5f930))
            f |= 0x2000;
        if (!B(a, 0xf9f526f8))
            f |= 0x80;
        else if (B(a, "AlphaTestZPrepass"))
            f |= 0x180;
        if (B(a, "ForceZPrepass"))
            f |= 0x10080;
        if (B(a, "BindlessResources"))
            f |= 0x1000;
        if (B(a, "overlay"))
            f = (f & 0xfffffffa) | 0x10;
        if (B(a, 0xf25a4e33))                                                    // alphatest
            f |= 2;
        if (B(a, 0x8690adf1))
            f |= 0x200;
        if (B(a, 0x12b495c5))
            f |= 0x400;
        if (B(a, "DisableLODBlending"))
            f |= 0x800;
        if (B(a, 0xae07dd33))
            f |= 0x8000;
        if (B(a, 0xaacfcf0b))
            f |= 0x4000;
        if (B(a, 0x5cc51bf0))
            f |= 0x20000;
        return f;
    }

    /// <summary>A model draw call's booleans that meshsystem 180049520 packs into +0x14 when it has no m_nFlags.</summary>
    public sealed record DrawCallBools(bool UseShadowFastPath = false, bool UseCompressedNormalTangent = false, bool IsNotMatchedToMaterial = false,
        bool HasBakedLightingFromVertexStream = false, bool HasBakedLightingFromLightMap = false, bool CanBatchWithDynamicShaderConstants = false,
        bool DrawLast = false, bool BakedTwoSided = false, bool OnlyShadow = false, bool NoShadow = false);

    /// <summary>
    /// The draw call's +0x14: 180049520's table (meshsystem .rdata 180175160: m_bUseShadowFastPath 2,
    /// m_bUseCompressedNormalTangent 1, m_bIsNotMatchedToMaterial 4, m_bHasBakedLightingFromVertexStream 8,
    /// m_bHasBakedLightingFromLightMap 0x10, m_bCanBatchWithDynamicShaderConstants 0x20, m_bDrawLast 0x40,
    /// m_bBakedTwoSided 0x80, m_bOnlyShadow 0x100, m_bNoShadow 0x200), then 180027830 with a material.
    /// </summary>
    public static ushort DrawCallFlags(DrawCallBools d, MeshEntryFlags.IAttributes? a)
    {
        var f = (ushort)((d.UseShadowFastPath ? 2 : 0) | (d.UseCompressedNormalTangent ? 1 : 0) | (d.IsNotMatchedToMaterial ? 4 : 0)
            | (d.HasBakedLightingFromVertexStream ? 8 : 0) | (d.HasBakedLightingFromLightMap ? 0x10 : 0) | (d.CanBatchWithDynamicShaderConstants ? 0x20 : 0)
            | (d.DrawLast ? 0x40 : 0) | (d.BakedTwoSided ? 0x80 : 0) | (d.OnlyShadow ? 0x100 : 0) | (d.NoShadow ? 0x200 : 0));
        if (a == null)
            return f;
        ushort set = (ushort)(I(a, 0x4ca923cf) != 0 ? 0x40 : 0), clear = 0;
        if (B(a, 0x7746b263) || I(a, 0x2c5b5105) != 0)
            set |= 2;
        else
            clear |= 2;
        if (I(a, 0xd1c3c3b0) != 0)
            set |= 0x20;
        else
            clear |= 0x20;
        return (ushort)((set | f) & ~clear);
    }

    /// <summary>
    /// A fragment's record flags (+0x14): worldrenderer 18001e920 (18001f263 .. 18001f36b) from its aggregate mesh's
    /// m_objectFlags and its draw call's material flags, then scenesystem 1800380a0 adds 0x20 when the fragment has no
    /// 0x40 and a vis cluster member list. worldrenderer sizes that list from m_nVisClusterMemberCount for a mesh with
    /// OBJECT_TYPE_PRECOMPUTED_VISMEMBERS (0x4000); without it, and without OBJECT_TYPE_DISABLE_VIS_CULLING (0x10000,
    /// flag 0x40), it queries the vis system for the fragment's bounds, which is not ported.
    /// </summary>
    /// <remarks>
    /// The query is CPVS vf 8 (engine2 1.41.9.2 180239730, 10-15 180238060): one cluster when the world has no vis
    /// data or the box covers the vis bounds, else a walk of CPVS's runtime octree (180237ce0 / 1802378f0) setting
    /// each overlapped cluster. <paramref name="visClusters"/> is its result's count when the caller has it; null
    /// throws where the query decides.
    /// </remarks>
    public static ushort FragmentFlags(uint objectFlags, uint materialFlags, int visClusterMemberCount, int? visClusters = null)
    {
        var f = RecordFlags(objectFlags, materialFlags);
        if ((f & 0x40) != 0)
            return f;
        var members = (objectFlags & 0x4000) != 0 ? visClusterMemberCount
            : visClusters ?? throw new NotSupportedException("a fragment whose vis cluster members come from CPVS's box query (not ported)");
        return members > 0 ? (ushort)(f | 0x20) : f;
    }

    /// <summary>Whether a fragment's flags need CPVS's box query (no precomputed members, no flag 0x40).</summary>
    public static bool NeedsVisQuery(uint objectFlags) => (objectFlags & 0x4000) == 0 && ((objectFlags >> 16) & 1) == 0;

    /// <summary>worldrenderer 18001e920's flags for scenesystem's AddFragment (1800380a0's param_5).</summary>
    private static ushort RecordFlags(uint objectFlags, uint materialFlags)
    {
        var f = (ushort)((objectFlags >> 3) & 4);
        if ((objectFlags & 0x80) != 0)
            f |= 2;
        if (((objectFlags >> 10) & 1) != 0)
            f |= 1;
        if (((objectFlags >> 11) & 1) != 0)
            f |= 0x10;
        if (((objectFlags >> 16) & 1) != 0)
            f |= 0x40;
        if (((objectFlags >> 17) & 1) != 0)
            f |= 0x80;
        if ((materialFlags & 0x180) == 0)
            f |= 8;
        if (((objectFlags >> 18) & 1) != 0)
            f |= 0x100;
        if (((objectFlags >> 20) & 1) != 0)
            f |= 0x200;
        return f;
    }

    /// <summary>scenesystem 1800380a0: the aggregate object's +0xe0 gains 0x40 for any fragment with flag 0x100.</summary>
    public static uint ObjectFlags(IEnumerable<ushort> fragmentFlags) => fragmentFlags.Any(f => (f & 0x100) != 0) ? 0x40u : 0u;

    /// <summary>
    /// The aggregate scene object's two flag words (worldrenderer 18001e920 at 164 .. 196 of the decompile, then
    /// 499): +0x78 from m_allFlags, and the 64-bit layer word +0x80 by meshsystem 180012150 (the material system's
    /// vf 0xd0) from the first draw call's material flags, +0x78 and a base from m_allFlags / m_anyFlags, ORed into
    /// the constructor's 0x40000000 (captured: the create receives the word without it), bit 61 set and 60 cleared
    /// after creation. The layer pre-filter (scenesystem 1800f3b10) reads +0x78 and the object's cull record,
    /// whose flags equal +0x80 (captured, probe01).
    /// </summary>
    public static (uint Flags78, ulong Layer80) ObjectWords(uint allFlags, uint anyFlags, uint materialFlags)
    {
        var u19 = (ulong)(~allFlags & 0x20) << 0x21;
        var u22 = (ulong)(allFlags & 0x80) << 0x1c;
        var u23 = u22 | 0x4000000;
        var u29 = u19 | (((anyFlags >> 10) & 1) != 0 ? 0x9100400001ul : 0x9100000001ul);
        if ((allFlags & 8) == 0)
            u23 = u22 | 0x4200000;
        var flags = ((allFlags >> 0x10) & 1) != 0 ? u23 : u23 | 8;
        if (((anyFlags >> 0x12) & 1) != 0)
            u29 |= 0x2000000000;
        if (((allFlags >> 0xd) & 1) != 0)
            u29 = (u29 & 0xffffff3ffffffffe) | 8;
        // The object's constructor (CAggregateSceneObjectDesc vf 0xb8, scenesystem 18003aa10) sets 0x40000000, the
        // init (1800d5260) ORs the word in, then worldrenderer sets bit 61 and clears bit 60.
        return ((uint)flags, ((LayerWord(materialFlags, flags, u29) | 0x40000000) | 0x2000000000000000) & 0xefffffffffffffff);
    }

    /// <summary>meshsystem 180012150: the object's layer word from the material flags, the object flags and a base.</summary>
    private static ulong LayerWord(uint material, ulong objectFlags, ulong word)
    {
        if (((objectFlags >> 0x14) & 1) != 0)
            return word;
        var m = material;
        if (((material >> 0xe) & 1) != 0)
        {
            m = material & 0xffffff78;
            word &= 0xfffffffffffffffc;
        }
        if (((objectFlags >> 0xf) & 1) != 0)
            return word;
        var kind = m & 5;
        var w = kind switch
        {
            4 => (word & 0xfffffffffffffffe) | 2,
            1 => (word & 0xfffffffffffffffd) | 1,
            5 => word | 3,
            _ => word,
        };
        var v = w & 0xffffff7fffffffff;
        if ((m & 0x20) != 0)
        {
            var x = kind != 4 ? w | 0x8000000000 : w & 0xffffff7fffffffff;
            if (((x >> 0x24) & 1) != 0 && ((m >> 0xd) & 1) == 0)
                x |= 0x200000000000;
            v = (m & 0x40) != 0 ? x | 0x2000000000 : x;
        }
        w = ((m >> 7) & 1) == 0 ? v | 0x40000000000 : v & 0xfffffbffffffffff;
        v = ((m >> 8) & 1) != 0 ? (w & 0xfffffbffffffffff) | 0x20000000 : w & 0xffffffffdfffffff;
        w = ((m >> 7) & 1) != 0 ? (v & 0xffff7fffffffffff) | 0x400000000000 : (v & 0xffffbfffffffffff) | 0x800000000000;
        v = (m & 0x10) != 0 ? w | 8 : w & 0xfffffffffffffff7;
        return (m & 2) != 0 ? v | 4 : v & 0xfffffffffffffffb;
    }

    /// <summary>
    /// scenesystem 1800f3b10's test (decompile lines 214 .. 223) of a visible object against the layer before it is
    /// offered to 180041f30: the record's flags (+0x80) against +0x480 / +0x488, the object's +0x78 against +0x490
    /// / +0x498, and the layer's +0x6dc against the object's +0xc4 unless the layer has flag bit 26. The branch
    /// taken first when the layer's +0x6dc is set (the object's +0xa8 entry) is not reached: +0x6dc was 0 on every
    /// captured cube-view layer.
    /// </summary>
    public static bool ObjectPasses(Layer l, uint flags78, ulong layer80, int objectC4 = 0)
    {
        if (l.F6dc != 0)
            throw new NotSupportedException("a layer with +0x6dc set (the object's +0xa8 branch, not ported)");
        return (l.F488 & layer80) == 0 && (l.F480 & layer80) == l.F480
            && (((l.F48 >> 0x1a) & 1) != 0 || l.F6dc == objectC4)
            && (l.F498 & flags78) == 0 && (l.F490 & flags78) == l.F490;
    }

    /// <summary>
    /// A scene layer's fields the tests read: +0x48 (flags), +0x480, +0x488, +0x490, +0x498, +0x4a0 .. +0x4ac, +0x6dc and the
    /// prepass split +0x6d0 (<see cref="AggregateCulling.InLayer"/>). The values are the game's (captured per layer
    /// of the BuildCubemaps view and its cascades, <see cref="CubemapLayers"/>).
    /// </summary>
    public sealed record Layer(string Name, ulong F48, ulong F480, ulong F488, ulong F498, uint F4a0, uint F4a4, uint F4a8, uint F4ac, float Split,
        ulong F490 = 0, int F6dc = 0);

    /// <summary>
    /// scenesystem 180041f30 before its call to 180041a00: the draw call (its +0x1c and +0x14, the object's +0xe0)
    /// against the layer. <paramref name="context3c"/> and <paramref name="context65"/> are the layer pass context's.
    /// </summary>
    public static bool DrawCallPasses(Layer l, uint drawCall1c, ushort drawCall14, uint objectE0, float context3c, byte context65)
    {
        if (((l.F480 >> 1) & 1) != 0)
            return false;
        var flags = drawCall1c | objectE0;
        var u12 = l.F4a0;
        uint u4 = 0x100;
        var s2c = l.F4ac & 0xffff;
        if (((l.F48 >> 24) & 1) != 0 && (l.F480 & 0xc000000000) == 0xc000000000)
        {
            u4 = 0x200;
            u12 |= 0x20;
        }
        if (context65 == 0)
        {
            if ((l.F480 & 1) != 0)
                u12 |= 1;
        }
        else
            u12 &= 0xfffffffa;
        var u18 = l.F4a4 & flags;
        var u6 = context3c >= 0f ? l.F4a8 : l.F4a8 | 0x800;
        return (flags & u6) == 0 && (drawCall14 & (s2c | u4)) == 0 && ((u12 & flags) == u12 || u18 != 0);
    }

    /// <summary>scenesystem 180041a00: a visible fragment's flags against the layer (lVar15 the view's own layer).</summary>
    public static bool FragmentPasses(Layer l, Layer view, ushort fragmentFlags, byte entryC, bool viewIsRoot)
    {
        uint excl = 0;
        if ((l.F48 & 0x1000000) != 0)
        {
            excl = (uint)((view.F480 >> 0x24) & 4);
            if ((view.F488 & 0x840000000000) != 0)
                excl |= 8;
        }
        if ((view.F498 & 0x800000000) != 0)
            excl |= 2;
        var req = (uint)((view.F480 >> 0x16) & 1);
        if ((view.F480 & 0x2000000000) != 0)
            req |= 0x100;
        var side = (ulong)(entryC & 1) == ((l.F48 >> 0x1d) & 1) || viewIsRoot;
        return side && (fragmentFlags & req) == req && (excl & fragmentFlags) == 0;
    }
}
