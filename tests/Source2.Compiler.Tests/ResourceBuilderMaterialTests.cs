using Source2.Compiler;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Container-correctness pins for <see cref="ResourceBuilder.BuildMaterial"/>,
/// the from-scratch vmat authoring path behind <c>POST /models/build-material</c>.
///
/// Nothing covered this path before 2026-08-10, which is how a flagless texture
/// ref shipped: <c>ApplyMaterial</c> wrote <c>m_pValue</c> as a plain KV3 string
/// while every real Valve compile stores <c>resource:"..."</c>. CS2 then can't
/// resolve the texture, refuses the material, and any model wearing it dies with
/// "attempting to render with error material" (the L4D2 zoey v4 crash dump named
/// every authored vmat as failed-to-load).
///
/// The in-place override paths (<c>BuildSkinMaterialFromTemplate</c>,
/// <c>an in-place texture retarget</c>) already preserve the flag
/// off the value they replace and carry their own comments about it; these tests
/// close the gap on the authoring path, and pin the stock ground truth those
/// comments assert so a VRF re-vendor can't quietly change how the flag reads
/// back.
/// </summary>
public class ResourceBuilderMaterialTests
{
    /// <summary>A genuine Valve-compiled vmat from the CS2 paint-asset tree —
    /// used both as the ground-truth sample and as BuildMaterial's structural
    /// template (any valid .vmat_c works as a template).</summary>
    private const string StockVmatRel =
        "cs2-paint-assets/characters/models/shared/arms/glove_bloodhound/materials/glove_bloodhound_left.vmat_c";

    private static string? FindStockVmat() => CS2Fixtures.TemplatePath(".vmat_c");

    private static KVObject DataRoot(Resource res)
    {
        var data = res.GetBlockByType(BlockType.DATA);
        return data is KeyValuesOrNTRO kvn ? kvn.Data : data!.AsKeyValueCollection();
    }

    private static IEnumerable<KVObject> TextureParams(Resource res)
    {
        var arr = DataRoot(res)["m_textureParams"];
        Assert.NotNull(arr);
        Assert.True(arr!.IsArray, "m_textureParams is not an array");
        return arr.Values;
    }

    /// <summary>
    /// GROUND TRUTH: a real resourcecompiler.exe vmat stores every texture path
    /// as a Resource-flagged KV3 value. This is the invariant the authoring path
    /// has to reproduce; if a VRF re-vendor changes how the flag round-trips,
    /// this fails first and the regression pin below stays meaningful.
    /// </summary>
    [Fact]
    public void StockVmat_TextureParams_AreResourceFlagged()
    {
        var fixture = FindStockVmat();
        if (fixture is null)
        {
            Console.WriteLine($"[SKIP] {StockVmatRel} not available — install CS2 or set CS2_DIR.");
            return;
        }

        using var res = new Resource();
        res.Read(new MemoryStream(File.ReadAllBytes(fixture)));

        var seen = 0;
        foreach (var tp in TextureParams(res))
        {
            var name = tp.GetStringProperty("m_name");
            var val = tp["m_pValue"];
            Assert.NotNull(val);
            Assert.Equal(KVFlag.Resource, val!.Flag);
            seen++;
        }
        Assert.True(seen > 0, "stock vmat carried no texture params — wrong fixture?");
    }

    /// <summary>
    /// REGRESSION PIN (2026-08-10): BuildMaterial's authored texture params must
    /// carry KVFlag.Resource, and the synthesized RERL must list the same paths.
    /// A flagless m_pValue is a load-time FATAL in CS2, not a cosmetic diff —
    /// see the class doc. Fails against the pre-fix `new KVObject(v)`.
    /// </summary>
    [Fact]
    public void BuildMaterial_AuthoredTextureParams_AreResourceFlaggedAndInRerl()
    {
        var fixture = FindStockVmat();
        if (fixture is null)
        {
            Console.WriteLine($"[SKIP] {StockVmatRel} not available — install CS2 or set CS2_DIR.");
            return;
        }

        var def = new ResourceBuilder.MaterialDef
        {
            Name = "materials/vpkedit/smoke_material.vmat",
            Shader = "csgo_complex.vfx",
        };
        def.TextureParams["g_tColor"] = "materials/vpkedit/smoke_color.vtex";
        def.TextureParams["g_tNormal"] = "materials/vpkedit/smoke_normal.vtex";
        def.IntParams["F_TRANSLUCENT"] = 0;

        var compiled = ResourceBuilder.BuildMaterial(File.ReadAllBytes(fixture), def);
        Assert.NotEmpty(compiled);

        using var res = new Resource { FileName = "smoke_material.vmat_c" };
        res.Read(new MemoryStream(compiled));

        var authored = TextureParams(res)
            .ToDictionary(tp => tp.GetStringProperty("m_name"), tp => tp["m_pValue"]!, StringComparer.Ordinal);

        Assert.Equal(2, authored.Count);
        foreach (var (name, val) in authored)
        {
            Assert.Equal(KVFlag.Resource, val.Flag);
            Assert.StartsWith("materials/vpkedit/smoke_", (string)val);
        }

        // The flag alone isn't enough — CS2 resolves the ref through the RERL,
        // so the authored paths must appear there too (ApplyMaterial rebuilds it
        // from def.TextureParams, no stock leftovers).
        var rerl = res.GetBlockByType(BlockType.RERL) as ResourceExtRefList;
        Assert.NotNull(rerl);
        var refs = rerl!.ResourceRefInfoList.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("materials/vpkedit/smoke_color.vtex", refs);
        Assert.Contains("materials/vpkedit/smoke_normal.vtex", refs);
        Assert.DoesNotContain(refs, r => r.Contains("bloodhound", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A genuine Valve-compiled vmdl that carries material groups —
    /// the ground truth for the sibling defect below.</summary>
    private const string StockVmdlRel =
        "cs2stock/agents/models/shared/arms/glove_bloodhound/glove_bloodhound.vmdl_c";

    /// <summary>A stock model that actually declares material groups: the
    /// ground-truth half of this test is vacuous against one that has none, and
    /// plenty of CS2 models (props, gestures) carry no groups at all.</summary>
    private static string? FindStockVmdl() => CS2Fixtures.TemplatePathWhere(
        "vmdl_with_material_groups", ".vmdl_c", HasMaterialGroups);

    private static bool HasMaterialGroups(byte[] vmdlC)
    {
        using var res = new Resource();
        res.Read(new MemoryStream(vmdlC));
        return (DataRoot(res)["m_materialGroups"]?.Values ?? [])
            .SelectMany(g => g["m_materials"]?.Values ?? [])
            .Any();
    }

    /// <summary>
    /// The same defect lived in the sibling authoring path behind
    /// <c>POST /models/build-model</c>: ApplyModel wrote every material-group
    /// entry as a plain string. Stock vmdl_c stores them Resource-flagged, so a
    /// flagless one is the identical "error material" fatal one level up.
    /// Both halves are asserted here so the pin carries its own ground truth.
    /// </summary>
    [Fact]
    public void BuildModel_MaterialGroupEntries_AreResourceFlaggedLikeStock()
    {
        var fixture = FindStockVmdl();
        if (fixture is null)
        {
            Console.WriteLine($"[SKIP] {StockVmdlRel} not available — install CS2 or set CS2_DIR.");
            return;
        }

        var stockBytes = File.ReadAllBytes(fixture);

        // GROUND TRUTH: the stock model's own material groups are Resource-flagged.
        using (var stock = new Resource())
        {
            stock.Read(new MemoryStream(stockBytes));
            var stockMats = (DataRoot(stock)["m_materialGroups"]?.Values ?? [])
                .SelectMany(g => g["m_materials"]?.Values ?? [])
                .ToList();
            Assert.NotEmpty(stockMats);
            Assert.All(stockMats, m => Assert.Equal(KVFlag.Resource, m.Flag));
        }

        var def = new ResourceBuilder.ModelDef { Name = "models/vpkedit/smoke_model.vmdl" };
        def.MaterialGroups.Add(("default", ["materials/vpkedit/smoke_material.vmat"]));

        var compiled = ResourceBuilder.BuildModel(stockBytes, def);
        Assert.NotEmpty(compiled);

        using var res = new Resource { FileName = "smoke_model.vmdl_c" };
        res.Read(new MemoryStream(compiled));

        var authored = (DataRoot(res)["m_materialGroups"]?.Values ?? [])
            .SelectMany(g => g["m_materials"]?.Values ?? [])
            .ToList();
        var only = Assert.Single(authored);
        Assert.Equal(KVFlag.Resource, only.Flag);
        Assert.Equal("materials/vpkedit/smoke_material.vmat", (string)only);
    }
}
