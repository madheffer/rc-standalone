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

    /// <summary>A minimal PCM16 mono WAV, so the sound tests need no game file.</summary>
    private static byte[] SineWav(int seconds = 1, int rate = 22050)
    {
        var frames = rate * seconds;
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / rate));
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((ushort)2);
            w.Write((ushort)16);
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        return ms.ToArray();
    }

    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\" width=\"64\" height=\"64\">"
      + "<path fill=\"#ffffff\" d=\"M8 8 L56 8 L56 56 L8 56 Z\"/></svg>";

    [Fact]
    public void VectorGraphic_AuthoredWithNoTemplate_MatchesStockContainerShape()
    {
        var bytes = ResourceBuilder.BuildPanoramaSvg(
            System.Text.Encoding.UTF8.GetBytes(Svg), "panorama/images/icons/equipment/mine.svg");

        using var res = new Resource { FileName = "mine.vsvg_c" };
        res.Read(new MemoryStream(bytes));

        // 400 of 400 sampled stock vector graphics: version 2, RED2 + DATA,
        // and an EMPTY name table — which is the only thing a template was
        // contributing here.
        Assert.Equal(ResourceType.PanoramaVectorGraphic, res.ResourceType);
        Assert.Equal(Source2ContainerAuthor.PanoramaVectorGraphicResourceVersion, res.Version);
        Assert.Equal([BlockType.RED2, BlockType.DATA], res.Blocks.Select(b => b.Type).ToArray());

        var pan = (Panorama)res.DataBlock!;
        Assert.Empty(pan.Names);
        // Read() validates CRC32 against the payload, so getting here proves it.
        Assert.Contains("M8 8", System.Text.Encoding.UTF8.GetString(pan.Data));

        Assert.Contains(res.EditInfo!.SpecialDependencies,
            d => d.String == "Vector Graphic Version" && d.CompilerIdentifier == "CompileVectorGraphic" && d.Fingerprint == 2);
        Assert.Contains(res.EditInfo.InputDependencies,
            d => d.ContentRelativeFilename == "panorama/images/icons/equipment/mine.svg");
    }

    [Fact]
    public void Sound_AuthoredWithNoTemplate_MatchesStockContainerShape()
    {
        var bytes = ResourceBuilder.BuildSound(SineWav(), "sounds/mine/beep.wav");

        using var res = new Resource { FileName = "beep.vsnd_c" };
        res.Read(new MemoryStream(bytes));

        // Stock ships v4 as RED2 + DATA (93 of 200 sampled; the rest are v5,
        // which ModernizeVsnd produces from this).
        Assert.Equal(ResourceType.Sound, res.ResourceType);
        Assert.Equal(Source2ContainerAuthor.SoundResourceVersion, res.Version);
        Assert.Equal([BlockType.RED2, BlockType.DATA], res.Blocks.Select(b => b.Type).ToArray());
        Assert.Null(res.GetBlockByType(BlockType.RERL));

        Assert.Contains(res.EditInfo!.SpecialDependencies,
            d => d.String == "Sound Compiler Version" && d.CompilerIdentifier == "CompileSound" && d.Fingerprint == 1);
        Assert.Contains(res.EditInfo.InputDependencies,
            d => d.ContentRelativeFilename == "sounds/mine/beep.wav");

        var snd = (Sound)res.DataBlock!;
        Assert.Equal(22050u, snd.SampleRate);
        Assert.True(snd.SampleCount > 0);
    }

    [Fact]
    public void Sound_AuthoredStillModernizesToV5()
    {
        // The v5 layout is what the override path actually ships, so the
        // authored container has to survive the conversion the same way a
        // templated one does.
        var v4 = ResourceBuilder.BuildSound(SineWav(), "sounds/mine/beep.wav");
        var v5 = ResourceBuilder.ModernizeVsnd(v4);

        using var res = new Resource { FileName = "beep.vsnd_c" };
        res.Read(new MemoryStream(v5));
        Assert.Equal(5, res.Version);
        Assert.NotNull(res.GetBlockByType(BlockType.CTRL));
        Assert.Contains(res.EditInfo!.SpecialDependencies, d => d.String == "Sound Compiler Version");
    }

    [Fact]
    public void VectorGraphic_AuthoredAndTemplated_AgreeByteForByte()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes(Svg);
        var authored = ResourceBuilder.BuildPanoramaSvg(svg, "panorama/images/icons/equipment/mine.svg");
        var templated = ResourceBuilder.BuildPanoramaSvg(authored, svg, "panorama/images/icons/equipment/mine.svg");
        Assert.Equal(authored, templated);
    }

    [Fact]
    public void Texture_RefusesARawBufferThatDoesNotMatchItsStatedSize()
    {
        // RawRgba carries its dimensions separately from the buffer, so the two
        // can disagree. The copy into the bitmap does not say so when the buffer
        // is SHORT: the tail stays transparent black and a texture that is wrong
        // from some row down ships with nothing raised anywhere.
        var ex = Assert.Throws<ArgumentException>(() => ResourceBuilder.BuildTexture(
            new ResourceBuilder.TextureDef
            {
                RawRgba = new byte[10],
                RawWidth = 64,
                RawHeight = 64,
                Compression = ResourceBuilder.TextureCompression.None,
            }));
        Assert.Contains("16384", ex.Message);

        // And the matching buffer still builds.
        ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef
        {
            RawRgba = new byte[64 * 64 * 4],
            RawWidth = 64,
            RawHeight = 64,
            Compression = ResourceBuilder.TextureCompression.None,
        });
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("just some text", "not an SVG")]
    [InlineData("PNG-not-svg", "not an SVG")]
    public void VectorGraphic_RefusesAPayloadThatIsNotSvg(string body, string expected)
    {
        // The sanitizer passes anything it does not recognise through untouched,
        // so without this a non-SVG upload compiles into a well-formed container
        // holding bytes the engine cannot draw: an icon simply missing in game,
        // with no failure anywhere upstream.
        var ex = Assert.Throws<InvalidDataException>(
            () => ResourceBuilder.BuildPanoramaSvg(System.Text.Encoding.UTF8.GetBytes(body)));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kv3Authoring_RefusesADocumentWithNoFormatHeader()
    {
        // AuthorKv3Resource stamps the document's own format GUID into DATA, so
        // a header-less document has nothing to stamp. Kv3SourceCompiler already
        // refuses that input; this entry point is public and reachable directly.
        var root = new KVObject("root").ToKV3Document().Root;
        var doc = new ValveKeyValue.KVDocument(header: null, name: null, root);
        var ex = Assert.Throws<InvalidOperationException>(
            () => Source2ContainerAuthor.AuthorKv3Resource(doc, ".vdata", [1, 2, 3]));
        Assert.Contains("Format header", ex.Message);
    }
}
