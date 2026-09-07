using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

using InputSignatureElement = Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement;

/// <summary>
/// Authors the container of a compiled material, so <c>BuildMaterial</c> can run
/// with no donor file.
///
/// <para>A stock <c>.vmat_c</c> is <c>Version 1</c> with four blocks, in this
/// order: <c>RERL RED2 DATA INSG</c>. Measured invariant across 200 sampled stock
/// materials and all 320 materials in three shipping community packs. Three of
/// the four this compiler can write outright - the RERL is derived from the
/// texture parameters, the RED2 describes this compile, and the DATA tree is
/// built from the caller's <see cref="ResourceBuilder.MaterialDef"/> by
/// <c>ApplyMaterial</c>, which already clears whatever the template held.</para>
///
/// <para><b>INSG is the one that cannot be invented.</b> It is the vertex input
/// signature the material's shader consumes, and it follows the shader's enabled
/// feature combo rather than its name: 600 sampled materials produced 22 distinct
/// signatures across 14 shaders, <c>csgo_character.vfx</c> alone accounting for
/// seven. Deriving one means resolving the compiled shader, which is a different
/// compiler than this. So it is a required input here.</para>
///
/// <para>That is not a step backwards from the template flow, it is the same
/// dependency made visible. Passing a donor <c>.vmat_c</c> to
/// <c>BuildMaterial</c> silently adopts whatever signature that file carried,
/// which is only correct when the donor used the same shader and features.
/// <see cref="ExtractInputSignature"/> lifts one deliberately, from a material
/// you have chosen because it matches.</para>
/// </summary>
public static class MaterialAuthor
{
    /// <summary>
    /// Read the input signature out of a compiled material, to hand to
    /// <see cref="NewContainer"/>. Throws when the file carries no INSG block.
    /// </summary>
    public static IReadOnlyList<InputSignatureElement> ExtractInputSignature(byte[] vmatC)
    {
        ArgumentNullException.ThrowIfNull(vmatC);
        using var res = new Resource();
        res.Read(new MemoryStream(vmatC, writable: false));

        if (res.GetBlockByType(BlockType.INSG) is not BinaryKV3 insg || insg.Data?.Root is not { } root)
            throw new InvalidOperationException(
                "That material has no INSG block, so it carries no input signature to copy.");

        var elems = root["m_elems"];
        if (elems is null || !elems.IsArray)
            throw new InvalidOperationException("The INSG block has no m_elems array.");

        return elems.Values.Select(e => new InputSignatureElement(
            e.GetStringProperty("m_pName") ?? "",
            e.GetStringProperty("m_pSemantic") ?? "",
            e.GetStringProperty("m_pD3DSemanticName") ?? "",
            (int)(e.GetInt32Property("m_nD3DSemanticIndex")))).ToList();
    }

    /// <summary>
    /// A fresh, empty material container ready for <c>ApplyMaterial</c> to fill:
    /// the right resource version and the four blocks in stock order.
    /// </summary>
    /// <param name="inputSignature">
    /// The shader's vertex input signature. See the class summary for why this
    /// cannot be defaulted.
    /// </param>
    /// <param name="sourceName">
    /// Content-relative source path recorded in RED2 (e.g.
    /// <c>"materials/mine/thing.vmat"</c>).
    /// </param>
    /// <param name="sourceBytes">
    /// The source text this compile came from, for the RED2 CRC. Pass the
    /// <c>.vmat</c> source when there is one; otherwise anything stable that
    /// identifies the input.
    /// </param>
    /// <param name="extraSpecialDependencies">
    /// Dependencies beyond the three every stock material carries: the
    /// shader's own entry (e.g. <c>"csgo_core/csgo_character.vfx" /
    /// CompileMaterial / fp=8</c>) and one per texture-processing mode the
    /// referenced textures used. Their fingerprints are per-shader values only a
    /// real compile knows, so they are stated rather than guessed.
    /// </param>
    public static Resource NewContainer(
        IReadOnlyList<InputSignatureElement> inputSignature,
        string? sourceName = null,
        ReadOnlySpan<byte> sourceBytes = default,
        IReadOnlyList<Source2ContainerAuthor.SpecialDep>? extraSpecialDependencies = null)
    {
        ArgumentNullException.ThrowIfNull(inputSignature);
        if (inputSignature.Count == 0)
            throw new ArgumentException(
                "A material needs a vertex input signature; every material in the game and in every " +
                "shipping community pack has a non-empty one. Lift it from a material using the same " +
                "shader and feature set with ExtractInputSignature.", nameof(inputSignature));

        var res = new Resource { Version = Source2ContainerAuthor.MaterialResourceVersion };

        var deps = Source2ContainerAuthor.MaterialBaseDeps
            .Concat(extraSpecialDependencies ?? [])
            .ToList();

        var editInfo = Source2ContainerAuthor.BuildBinaryEditInfo(
            string.IsNullOrWhiteSpace(sourceName) ? "materials/vpkeditor/material.vmat" : sourceName!,
            sourceBytes, deps, null);

        // Stock block order: RERL RED2 DATA INSG.
        res.Blocks.Add(new ResourceExtRefList { Resource = res });
        res.Blocks.Add(new BinaryKV3(editInfo, KV3IDLookup.Get("generic"), BlockType.RED2) { Resource = res });
        res.Blocks.Add(new BinaryKV3(KVObject.Collection(), KV3IDLookup.Get("generic"), BlockType.DATA) { Resource = res });
        res.Blocks.Add(new BinaryKV3(BuildInsg(inputSignature), KV3IDLookup.Get("generic"), BlockType.INSG) { Resource = res });

        return res;
    }

    private static KVObject BuildInsg(IReadOnlyList<InputSignatureElement> elements)
    {
        var elems = KVObject.Array();
        foreach (var e in elements)
        {
            var o = KVObject.Collection();
            o.Add("m_pName", new KVObject(e.Name));
            o.Add("m_pSemantic", new KVObject(e.Semantic));
            o.Add("m_pD3DSemanticName", new KVObject(e.D3DSemanticName));
            o.Add("m_nD3DSemanticIndex", new KVObject(e.D3DSemanticIndex));
            elems.Add(o);
        }
        var root = KVObject.Collection();
        root.Add("m_elems", elems);
        return root;
    }
}
