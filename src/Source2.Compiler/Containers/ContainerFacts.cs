namespace Source2.Compiler;

/// <summary>
/// The container facts that let the binary resource types be authored with no
/// donor file: what a stock compile of each type puts in its header, and which
/// RED2 special dependencies it always carries.
///
/// <para>These are measurements, not guesses. Each was taken by surveying stock
/// CS2 content with the probe described in <c>docs/RESOURCES.md</c>; the sample
/// size and the invariance are recorded next to each value. Re-derive them with
/// <c>tools/rc-oracle.ps1</c> after a game update rather than trusting the
/// numbers to stay true.</para>
/// </summary>
public static partial class Source2ContainerAuthor
{
    /// <summary>
    /// <c>.vtex_c</c> resource version. Invariant across 200 sampled stock
    /// textures (of 71,175 in <c>pak01</c>), all of which carry exactly the
    /// blocks <c>RED2 DATA</c> and no RERL.
    /// </summary>
    public const ushort TextureResourceVersion = 1;

    /// <summary>
    /// <c>.vsvg_c</c> resource version. Invariant across 200 sampled stock
    /// vector graphics (of 1,120), all <c>RED2 DATA</c>.
    /// </summary>
    public const ushort PanoramaVectorGraphicResourceVersion = 2;

    /// <summary>
    /// <c>.vsnd_c</c> resource version for the classic <c>RED2 DATA</c> layout.
    /// Stock ships both: of 200 sampled, 93 are version 4 with those two blocks
    /// and 107 are version 5, which adds a <c>CTRL</c> block.
    /// <c>ResourceBuilder.ModernizeVsnd</c> converts one to the other.
    /// </summary>
    public const ushort SoundResourceVersion = 4;

    /// <summary>
    /// The one dependency every stock <c>.vsnd_c</c> carries (200 of 200).
    /// </summary>
    public static readonly SpecialDep[] SoundDeps =
    [
        new("Sound Compiler Version", "CompileSound", 1),
    ];

    /// <summary>
    /// <c>.vmat_c</c> resource version. Invariant across 200 sampled stock
    /// materials (of 21,796), all carrying <c>RERL RED2 DATA INSG</c> - note the
    /// INSG, which is why a material cannot be authored quite as freely as the
    /// other two. See <see cref="MaterialAuthoring"/>.
    /// </summary>
    public const ushort MaterialResourceVersion = 1;

    /// <summary>
    /// The <c>GenerateSheetData</c> dependency a compiled sprite sheet declares.
    /// Present on every stock <c>.vtex_c</c> that carries a SHEET block and on no
    /// plain texture, so it is emitted exactly when a sheet payload is.
    /// </summary>
    public static readonly SpecialDep GenerateSheetDataDep =
        new("Texture Compiler Version GenerateSheetData", "CompileTexture", 5);

    /// <summary>
    /// The two texture dependencies every stock <c>.vtex_c</c> carries without
    /// exception (200 of 200). <c>BuildTextureEditInfo</c> falls back to this set
    /// plus a mip-algorithm entry when the caller states no encoding semantics.
    /// </summary>
    public static readonly SpecialDep[] TextureBaseDeps =
    [
        new("Texture Compiler Version", "CompileTexture", 11),
        new("Texture Encode Quality", "CompileTexture", 1, 3),
    ];

    /// <summary>
    /// The one dependency every stock <c>.vsvg_c</c> carries (200 of 200).
    /// </summary>
    public static readonly SpecialDep[] VectorGraphicDeps =
    [
        new("Vector Graphic Version", "CompileVectorGraphic", 2),
    ];

    /// <summary>
    /// The material dependencies every stock <c>.vmat_c</c> carries (200 of 200).
    /// A real compile adds one named for the shader itself (e.g.
    /// <c>"csgo_core/csgo_character.vfx" / CompileMaterial / fp=8</c>) plus one
    /// per texture-processing mode any of its textures used.
    /// </summary>
    public static readonly SpecialDep[] MaterialBaseDeps =
    [
        new("Material Compiler Version", "CompileMaterial", 25),
        new("Texture Compiler Version", "CompileTexture", 11),
        new("Texture Encode Quality", "CompileTexture", 1, 3),
    ];

    /// <summary>
    /// Why a <c>.vmat_c</c> is the one type that cannot be authored end to end
    /// from nothing.
    ///
    /// <para>Every material sampled, 200 stock and 320 community, carries a non-empty
    /// INSG block: the vertex input signature its shader consumes. It is not derivable
    /// from the shader NAME, because it follows the enabled feature combo - 600 sampled
    /// materials gave 22 distinct payloads across 14 shaders, one shader alone
    /// accounting for seven. Producing one from first principles means resolving the
    /// compiled shader, which is a different compiler.</para>
    ///
    /// <para>So everything else about a material is authored here and the signature has
    /// to be stated: hand it in, or lift it with
    /// <c>MaterialAuthor.ExtractInputSignature</c> from a material using the shader and
    /// features you want. That replaces the silent version of the same dependency,
    /// where a donor material was taken wholesale and its signature inherited by
    /// accident.</para>
    /// </summary>
    public static class MaterialAuthoring
    {
        /// <summary>Shape of one INSG element.</summary>
        public sealed record InputSignatureElement(
            string Name, string Semantic, string D3DSemanticName, int D3DSemanticIndex);
    }
}
