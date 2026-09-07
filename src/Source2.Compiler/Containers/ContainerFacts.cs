namespace Source2.Compiler;

/// <summary>
/// The container facts that let the binary resource types be authored with no
/// donor file: what a stock compile of each type puts in its header, and which
/// RED2 special dependencies it always carries.
///
/// <para>These are measurements, not guesses. Each was taken by surveying stock
/// CS2 content with the probe described in <c>docs/AUTHORING.md</c>; the sample
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
    /// <c>.vmat_c</c> resource version. Invariant across 200 sampled stock
    /// materials (of 21,796), all carrying <c>RERL RED2 DATA INSG</c> — note the
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
    /// <para>Every stock material (200 of 200) and every material in the three
    /// shipping community packs surveyed (320 of 320) carries an <b>INSG</b>
    /// block: the vertex input signature the material's shader consumes, as a
    /// list of <c>{m_pName, m_pSemantic, m_pD3DSemanticName, m_nD3DSemanticIndex}</c>.
    /// None is empty.</para>
    ///
    /// <para>It is not derivable from the shader name. Across 600 sampled
    /// materials there were 22 distinct INSG payloads for 14 shaders:
    /// <c>csgo_character.vfx</c> alone has 7, because the signature follows the
    /// shader's enabled feature combo (blend weights, per-vertex lighting, and so
    /// on), not just which shader it is. Producing one from first principles
    /// means resolving the compiled shader, which is a different compiler.</para>
    ///
    /// <para>So the honest boundary is: everything else about a material — the
    /// header, RERL, RED2, the whole parameter tree — is authored here, and the
    /// input signature has to be stated. Either hand it in, or lift it from a
    /// material that already uses the shader and feature set you want, with
    /// <c>MaterialAuthor.ExtractInputSignature</c>. What this replaces is the
    /// silent version of the same dependency: taking a donor material wholesale
    /// and inheriting whatever signature it happened to carry.</para>
    /// </summary>
    public static class MaterialAuthoring
    {
        /// <summary>Shape of one INSG element.</summary>
        public sealed record InputSignatureElement(
            string Name, string Semantic, string D3DSemanticName, int D3DSemanticIndex);
    }
}
