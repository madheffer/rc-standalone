using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The claim these pin: a compiled texture, and a compiled material, can be
/// produced with no donor file of that type - and the result still matches what
/// a stock compile looks like where stock is invariant.
///
/// <para>The texture case needs nothing at all. The material case needs the
/// shader's vertex input signature, which is the measured boundary (see
/// <see cref="MaterialAuthor"/>); the test states it explicitly rather than
/// lifting one, so it runs with no game installed.</para>
/// </summary>
public class TemplateFreeAuthoringTests
{
    private static ResourceBuilder.TextureDef Checkerboard(int size = 32)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var on = ((i / size) + (i % size)) % 2 == 0;
            rgba[i * 4 + 0] = on ? (byte)255 : (byte)0;
            rgba[i * 4 + 1] = 0;
            rgba[i * 4 + 2] = on ? (byte)0 : (byte)255;
            rgba[i * 4 + 3] = 255;
        }
        return new ResourceBuilder.TextureDef
        {
            RawRgba = rgba,
            RawWidth = size,
            RawHeight = size,
            Compression = ResourceBuilder.TextureCompression.BC7,
        };
    }

    [Fact]
    public void Texture_AuthoredWithNoTemplate_MatchesStockContainerShape()
    {
        var def = Checkerboard();
        def.SourceName = "materials/mine/checker.png";

        var bytes = ResourceBuilder.BuildTexture(def);

        using var res = new Resource { FileName = "checker.vtex_c" };
        res.Read(new MemoryStream(bytes));

        // Every stock .vtex_c sampled (200 of 71,175) is Version 1 with exactly
        // these two blocks and no RERL.
        Assert.Equal(ResourceType.Texture, res.ResourceType);
        Assert.Equal(Source2ContainerAuthor.TextureResourceVersion, res.Version);
        Assert.Equal([BlockType.RED2, BlockType.DATA], res.Blocks.Select(b => b.Type).ToArray());
        Assert.Null(res.GetBlockByType(BlockType.RERL));

        // The two dependencies every stock texture carries.
        var deps = res.EditInfo!.SpecialDependencies;
        Assert.Contains(deps, d => d.String == "Texture Compiler Version" && d.CompilerIdentifier == "CompileTexture" && d.Fingerprint == 11);
        Assert.Contains(deps, d => d.String == "Texture Encode Quality" && d.CompilerIdentifier == "CompileTexture" && d.UserData == 3);

        // It describes its own compile, not somebody else's.
        Assert.Contains(res.EditInfo.InputDependencies, d => d.ContentRelativeFilename == "materials/mine/checker.png");

        var tex = (Texture)res.DataBlock!;
        Assert.Equal(32, tex.Width);
        Assert.Equal(32, tex.Height);
        Assert.True(tex.NumMipLevels >= 1);
        Assert.NotEmpty(tex.GenerateBitmap().Bytes);      // the pixels survive a real decode
    }

    [Fact]
    public void Texture_AuthoredAndTemplated_AgreeByteForByte()
    {
        // A template contributes the container frame and nothing else, so with
        // the same encoding semantics stated either way the outputs must match.
        // This is what makes "the game is a bootstrap, not a dependency" testable.
        var seedDef = Checkerboard();
        seedDef.SourceName = "materials/mine/checker.png";
        var authored = ResourceBuilder.BuildTexture(seedDef);

        var againDef = Checkerboard();
        againDef.SourceName = "materials/mine/checker.png";
        var templated = ResourceBuilder.BuildTexture(authored, againDef);

        Assert.Equal(authored, templated);
    }

    [Fact]
    public void Texture_StatesItsEncodingSemantics()
    {
        // With no template there is nowhere for a mip-algorithm dependency to
        // ride in from, so the caller states it. Dropping one is the defect that
        // makes a consumer decode a packed normal map as plain RGB.
        var def = Checkerboard();
        def.EncodingSemantics.Add(
            new Source2ContainerAuthor.SpecialDep("Texture Compiler Version Mip HemiOctAnisoRoughness", "CompileTexture", 3));

        using var res = new Resource();
        res.Read(new MemoryStream(ResourceBuilder.BuildTexture(def)));

        Assert.Contains(res.EditInfo!.SpecialDependencies,
            d => d.String == "Texture Compiler Version Mip HemiOctAnisoRoughness" && d.Fingerprint == 3);

        // And stating one must not displace the two every stock texture carries.
        // It did: BuildTextureEditInfo treats a non-empty list as "the template's
        // own set, use it instead of the fallback", so the first caller-stated
        // dependency silently took the base set's place.
        Assert.Contains(res.EditInfo.SpecialDependencies, d => d.String == "Texture Compiler Version");
        Assert.Contains(res.EditInfo.SpecialDependencies, d => d.String == "Texture Encode Quality");
    }

    [Fact]
    public void Material_AuthoredWithNoTemplate_MatchesStockContainerShape()
    {
        var signature = new[]
        {
            new Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement("vPositionOs", "PosXyz", "POSITION", 0),
            new Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement("vNormalOs", "Normal", "NORMAL", 0),
            new Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement("vTexCoord", "TexCoord", "TEXCOORD", 0),
        };

        var def = new ResourceBuilder.MaterialDef
        {
            Name = "materials/mine/thing.vmat",
            Shader = "csgo_complex.vfx",
        };
        def.TextureParams["g_tColor"] = "materials/mine/thing_color.vtex";
        def.TextureParams["g_tNormal"] = "materials/mine/thing_normal.vtex";
        def.IntParams["F_TRANSLUCENT"] = 1;

        using var container = MaterialAuthor.NewContainer(signature, "materials/mine/thing.vmat");
        var bytes = ResourceBuilder.BuildMaterial(def, container);

        using var res = new Resource { FileName = "thing.vmat_c" };
        res.Read(new MemoryStream(bytes));

        // Stock block order, invariant across 200 stock and 320 community materials.
        Assert.Equal(ResourceType.Material, res.ResourceType);
        Assert.Equal(Source2ContainerAuthor.MaterialResourceVersion, res.Version);
        Assert.Equal([BlockType.RERL, BlockType.RED2, BlockType.DATA, BlockType.INSG],
            res.Blocks.Select(b => b.Type).ToArray());

        Assert.Contains(res.EditInfo!.SpecialDependencies,
            d => d.String == "Material Compiler Version" && d.Fingerprint == 25);

        // The parameters landed, and the texture refs are Resource-flagged with
        // matching RERL ids - the invariant a flagless ref breaks.
        var root = (res.DataBlock as KeyValuesOrNTRO)!.Data!;
        Assert.Equal("csgo_complex.vfx", root.GetStringProperty("m_shaderName"));

        var texParams = root["m_textureParams"]!.Values.ToList();
        Assert.Equal(2, texParams.Count);
        foreach (var p in texParams)
        {
            var value = p["m_pValue"]!;
            Assert.Equal(KVFlag.Resource, value.Flag);
            var path = (string)value;
            var rerl = res.ExternalReferences!.ResourceRefInfoList.SingleOrDefault(r => r.Name == path);
            Assert.NotNull(rerl);
            Assert.Equal(Source2ResourceId.ForPath(path), rerl!.Id);
        }

        // And the signature we asked for is the signature it carries.
        var insg = (BinaryKV3)res.GetBlockByType(BlockType.INSG)!;
        var elems = insg.Data!.Root!["m_elems"]!.Values.ToList();
        Assert.Equal(3, elems.Count);
        Assert.Equal("vPositionOs", elems[0].GetStringProperty("m_pName"));
        Assert.Equal("POSITION", elems[0].GetStringProperty("m_pD3DSemanticName"));
    }

    [Fact]
    public void Material_RefusesAnEmptyInputSignature()
    {
        // Not a limitation to paper over: no material in the game or in any
        // shipping community pack has an empty one, so silently writing one
        // would be inventing a file shape nothing has ever loaded.
        var ex = Assert.Throws<ArgumentException>(() => MaterialAuthor.NewContainer([]));
        Assert.Contains("input signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Material_InputSignatureRoundTripsThroughExtract()
    {
        var signature = new[]
        {
            new Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement("vPositionOs", "PosXyz", "POSITION", 0),
            new Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement("vBlendWeight", "BlendWeight", "BLENDWEIGHT", 1),
        };

        using var container = MaterialAuthor.NewContainer(signature);
        var bytes = ResourceBuilder.BuildMaterial(
            new ResourceBuilder.MaterialDef { Name = "materials/x.vmat", Shader = "csgo_simple.vfx" }, container);

        Assert.Equal(signature, MaterialAuthor.ExtractInputSignature(bytes));
    }
}
