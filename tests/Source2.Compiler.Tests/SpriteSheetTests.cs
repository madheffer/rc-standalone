using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the SHEET writer to Valve's own bytes.
///
/// <para>The strong test is <see cref="StockSheet_RoundTripsByteForByte"/>: read a
/// real animated texture out of the game, decode its SHEET with VRF, hand the
/// decoded sequences straight back to <see cref="SpriteSheet.Write"/>, and
/// require the result to be byte-identical. Every offset, the alignment after
/// each name, the order the per-sequence blocks are laid out in and the
/// convention that <c>totalTime</c> is the sum of the frame display times are
/// all pinned at once by that one comparison - and none of them could be
/// confirmed by writing a file and reading it back with the same code.</para>
/// </summary>
public class SpriteSheetTests
{
    /// <summary>
    /// A stock texture that carries a sprite sheet. Sheets are rare among the
    /// game's 71,000 textures and effectively all of them are particle art, so
    /// the search is narrowed to that subtree rather than decoding its way
    /// alphabetically through everything else first.
    /// </summary>
    private static byte[]? SheetTexture() => CS2Fixtures.TemplatePathWhere(
        "vtex_with_sheet", ".vtex_c", HasSheet,
        pathFilter: p => p.Replace('\\', '/').StartsWith("materials/particle/", StringComparison.OrdinalIgnoreCase))
        is { } path ? File.ReadAllBytes(path) : null;

    private static bool HasSheet(byte[] vtexC)
    {
        using var res = new Resource();
        res.Read(new MemoryStream(vtexC));
        return res.DataBlock is Texture t && t.ExtraData.ContainsKey(VTexExtraData.SHEET);
    }

    [Fact]
    public void StockSheet_RoundTripsByteForByte()
    {
        var bytes = SheetTexture();
        if (bytes is null)
        { CS2Fixtures.Skip("a compiled .vtex_c carrying a SHEET"); return; }

        using var res = new Resource();
        res.Read(new MemoryStream(bytes));
        var tex = (Texture)res.DataBlock!;
        var expected = tex.ExtraData[VTexExtraData.SHEET];
        var decoded = tex.GetSpriteSheetData()!;

        var rewritten = SpriteSheet.Write(decoded.Sequences.Select(s => new SpriteSheet.Sequence(
            Frames: s.Frames.Select(f => new SpriteSheet.Frame(
                f.DisplayTime,
                Min: (f.Images[0].UncroppedMin.X, f.Images[0].UncroppedMin.Y),
                Max: (f.Images[0].UncroppedMax.X, f.Images[0].UncroppedMax.Y),
                CroppedMin: (f.Images[0].CroppedMin.X, f.Images[0].CroppedMin.Y),
                CroppedMax: (f.Images[0].CroppedMax.X, f.Images[0].CroppedMax.Y))).ToList(),
            Clamp: s.Clamp,
            AlphaCrop: s.AlphaCrop,
            NoColor: s.NoColor,
            NoAlpha: s.NoAlpha,
            Name: s.Name)).ToList());

        // Ids are not passed back in because VRF's decoder does not surface them,
        // so the writer numbers by position here - and the payload still matching
        // byte for byte is itself the evidence that this stock sheet's ids are
        // 0..n-1. MksSequenceNumber_ReachesTheSheetId covers the case where they
        // are not.
        Assert.Equal(expected.Length, rewritten.Length);
        Assert.True(expected.AsSpan().SequenceEqual(rewritten),
            "SHEET payload differs from the stock bytes it was decoded from.");
    }

    [Fact]
    public void WrittenSheet_ReadsBackThroughVrf()
    {
        var sheet = SpriteSheet.Write([
            new SpriteSheet.Sequence(
                Frames: [
                    new SpriteSheet.Frame(1f, (0f, 0f), (0.5f, 0.5f)),
                    new SpriteSheet.Frame(2.5f, (0.5f, 0f), (1f, 0.5f)),
                ],
                Clamp: false),
            new SpriteSheet.Sequence(
                Frames: [new SpriteSheet.Frame(1f, (0f, 0.5f), (1f, 1f))],
                NoAlpha: true,
                Name: "colour_only"),
        ]);

        // Feed it through a real texture so VRF's own reader parses it, rather
        // than testing the writer against a second copy of the writer's logic.
        var vtex = ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef
        {
            RawRgba = new byte[16 * 16 * 4],
            RawWidth = 16,
            RawHeight = 16,
            GenerateMipmaps = false,
            Compression = ResourceBuilder.TextureCompression.None,
            SheetData = sheet,
            Flags = ResourceBuilder.VTexFlags.NO_LOD,
        });

        using var res = new Resource();
        res.Read(new MemoryStream(vtex));
        var tex = (Texture)res.DataBlock!;

        Assert.Equal(ResourceBuilder.VTexFlags.NO_LOD, (ushort)tex.Flags);
        Assert.True(tex.ExtraData.ContainsKey(VTexExtraData.SHEET));

        var back = tex.GetSpriteSheetData()!;
        Assert.Equal(2, back.Sequences.Length);

        Assert.False(back.Sequences[0].Clamp);
        Assert.Equal(2, back.Sequences[0].Frames.Length);
        Assert.Equal(3.5f, back.Sequences[0].FramesPerSecond);      // total time == sum of display times
        Assert.Equal(2.5f, back.Sequences[0].Frames[1].DisplayTime);
        Assert.Equal(0.5f, back.Sequences[0].Frames[1].Images[0].UncroppedMin.X);

        Assert.Equal("colour_only", back.Sequences[1].Name);
        Assert.True(back.Sequences[1].NoAlpha);
        Assert.Single(back.Sequences[1].Frames);

        // A sheet texture states that it is one, without losing the two
        // dependencies every stock texture carries.
        var deps = res.EditInfo!.SpecialDependencies;
        Assert.Contains(deps, d => d.String == "Texture Compiler Version GenerateSheetData" && d.Fingerprint == 5);
        Assert.Contains(deps, d => d.String == "Texture Compiler Version" && d.Fingerprint == 11);
        Assert.Contains(deps, d => d.String == "Texture Encode Quality");
    }

    [Fact]
    public void PlainTexture_DeclaresNoSheet()
    {
        var vtex = ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef
        {
            RawRgba = new byte[16 * 16 * 4],
            RawWidth = 16,
            RawHeight = 16,
            GenerateMipmaps = false,
            Compression = ResourceBuilder.TextureCompression.None,
        });

        using var res = new Resource();
        res.Read(new MemoryStream(vtex));
        var tex = (Texture)res.DataBlock!;

        Assert.False(tex.ExtraData.ContainsKey(VTexExtraData.SHEET));
        Assert.Null(tex.GetSpriteSheetData());
        Assert.DoesNotContain(res.EditInfo!.SpecialDependencies,
            d => d.String == "Texture Compiler Version GenerateSheetData");
    }

    [Fact]
    public void MksSource_ParsesTheGrammarVrfEmits()
    {
        // Shaped exactly like TextureExtract.TryGetMksData's output.
        var script = MksSource.Parse("""
            // Reconstructed with Source 2 Viewer 20.0.0.0

            packmode rgb+a

            sequence 0
            frame flame_seq0_0.png 1
            frame flame_seq0_1.png 17

            sequence-rgb 1
            LOOP
            frame flame_seq1_0.png 1

            sequence-a 2
            frame flame_seq2_0.png 1
            """);

        Assert.True(script.PackModeRgbA);
        Assert.Equal(3, script.Sequences.Count);

        Assert.True(script.Sequences[0].Clamp);
        Assert.False(script.Sequences[0].NoColor);
        Assert.False(script.Sequences[0].NoAlpha);
        Assert.Equal(17f, script.Sequences[0].Frames[1].DisplayTime);

        Assert.False(script.Sequences[1].Clamp);        // LOOP
        Assert.True(script.Sequences[1].NoAlpha);       // sequence-rgb

        Assert.True(script.Sequences[2].NoColor);       // sequence-a

        Assert.Equal(4, script.ImagePaths.Count);
    }

    [Theory]
    [InlineData("frame nope.png 1", "frame before any sequence")]
    [InlineData("sequence 0\nwobble", "unrecognised directive")]
    [InlineData("sequence 0\nframe a.png b.png 1", "multi-image frames")]
    [InlineData("sequence 0", "declares no frames")]
    [InlineData("packmode sideways", "unknown packmode")]
    public void MksSource_RejectsRatherThanGuesses(string text, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MksSource.Parse(text));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Atlas_PacksFramesAndInsetsUvsByHalfATexel()
    {
        var script = MksSource.Parse("""
            sequence 0
            frame a.png 1
            frame b.png 1
            """);

        using var packed = SheetAtlas.Pack(script, name => Png(32, 32, (byte)(name == "a.png" ? 255 : 0)));

        // Two 32x32 frames pack into a 64x32 shelf, rounded to 64x32 (both pow2).
        Assert.Equal(64, packed.Atlas.Width);
        Assert.Equal(32, packed.Atlas.Height);

        var frames = packed.Sequences[0].Frames;
        Assert.Equal(2, frames.Count);

        // Half-texel inset on every edge, the convention Valve's own sheets use.
        Assert.Equal(0.5f / 64f, frames[0].Min.X, 6f / 1000000f);
        Assert.Equal(0.5f / 32f, frames[0].Min.Y, 6f / 1000000f);
        Assert.Equal(31.5f / 64f, frames[0].Max.X, 6f / 1000000f);
        Assert.Equal(31.5f / 32f, frames[0].Max.Y, 6f / 1000000f);

        // The frames do not overlap in UV space.
        Assert.True(frames[1].Min.X > frames[0].Max.X);
    }

    [Fact]
    public void MksSequenceNumber_ReachesTheSheetId()
    {
        // A particle addresses a sequence by the number the .mks names, not by
        // where it sits in the file. A script declaring only "sequence 3" is
        // addressing sequence 3, so a sheet that renumbered it to 0 would leave
        // the stock effect selecting a sequence that is not there.
        var script = MksSource.Parse("""
            sequence 3
            frame a.png 1
            """);

        using var packed = SheetAtlas.Pack(script, _ => Png(16, 16, 200));
        Assert.Equal(3, packed.Sequences[0].Id);

        var sheet = SpriteSheet.Write(packed.Sequences);
        Assert.Equal(3u, BitConverter.ToUInt32(sheet, 8));   // first sequence header, id field
    }

    [Fact]
    public void SheetWriter_RefusesDuplicateSequenceIds()
    {
        var frames = new[] { new SpriteSheet.Frame(1f, (0f, 0f), (1f, 1f)) };
        var ex = Assert.Throws<ArgumentException>(() => SpriteSheet.Write([
            new SpriteSheet.Sequence(frames, Id: 2),
            new SpriteSheet.Sequence(frames, Id: 2),
        ]));
        Assert.Contains("share id 2", ex.Message);
    }

    [Fact]
    public void MksSource_AcceptsAUtf8Bom()
    {
        // U+FEFF is not whitespace, so an editor that saves with a BOM would
        // otherwise glue it onto the first directive.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("sequence 0\nframe a.png 1")).ToArray();
        var script = MksSource.Parse(bytes);
        Assert.Single(script.Sequences);
    }

    [Theory]
    [InlineData("sequence 0\nframe a.png 1\nsequence 0\nframe b.png 1", "declared more than once")]
    [InlineData("sequence 0\nframe a.png 0", "positive number")]
    [InlineData("sequence 0\nframe a.png -2", "positive number")]
    public void MksSource_RejectsScriptsThatWouldAnimateUndefined(string text, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MksSource.Parse(text));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Png(int w, int h, byte red)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
            canvas.Clear(new SkiaSharp.SKColor(red, 0, 0, 255));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
