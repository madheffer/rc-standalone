using Source2.Compiler.Materials;

namespace Source2.Compiler.RcTexture;

/// <summary>
/// How <c>CResourceCompilerTexture::CompileResourceInternal</c> (180133da0) turns a texture's
/// inputs into float levels: each output channel group runs its mip algorithm
/// (<c>CTextureFrame::GenerateMips</c>, 180142990) on its inputs' whole images, and every level of
/// the texture copies each group's source channels from that group's chain to its destination
/// channels (181277690). Shared by <c>.vtex</c> sources (<see cref="VtexCompiler"/>) and a
/// material's generated children (<c>ChildTextureCompiler</c>); docs/TEXTURES.md and
/// docs/MATERIALS.md. Algorithms and processors not measured are refused.
/// </summary>
internal static class RcTextureChannels
{
    /// <summary>The texture's levels, whether any colour channel is sRGB, and the most levels it keeps.</summary>
    internal sealed record Result(List<RcMipChain.Level> Levels, bool Srgb, int MaxMips);

    // The special dependencies each mip algorithm and image processor adds to a texture compile
    // (RC's children in RcReference/materials, the .vtex probes in RcReference/textures); the
    // names are the strings GenerateMips and the processors register (180142990, 1801443f0 ...).
    private static readonly Dictionary<string, (string Name, int Fingerprint)[]> AlgorithmDeps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Box"] = [("Mip Box", 1)],
        ["Box3"] = [("Mip Box", 1)],
        ["ArtistDefined"] = [("Mip Box", 1)],
        ["ArtistDefinedOrAlphaWeighted"] = [("Mip AlphaWeighted", 1)],
        ["HemiOctIsoRoughness_RG_B"] = [("Mip HemiOctAnisoRoughness", 3), ("Mip HemiOctIsoRoughness_RG_B", 1)],
        // 180142990 registers its own entry for AnisoRoughness_RG, then the shared chain its (fp 3).
        ["AnisoRoughness_RG"] = [("Mip AnisoRoughness_RG", 1), ("Mip HemiOctAnisoRoughness", 3)],
        ["HemiOctAnisoRoughness"] = [("Mip HemiOctAnisoRoughness", 3)],
        ["None"] = [("Mip None", 1)],
        ["BoxInverse"] = [("Mip Box Inverse", 1)],
        // MipNormal registers its own entry, then runs the Box chain, which registers Box's.
        ["MipNormal"] = [("Mip Normals", 1), ("Mip Box", 1)],
        ["PreserveCoverage"] = [("Mip PreserveCoverage", 5)],
        ["NormalizeNormals"] = [("Image NormalizeNormals", 1)],
        ["Inverse"] = [("Image Inverse", 1)],
        ["Mod2XCenter"] = [("Image Mod2X", 1)],
    };

    // Box-family mip algorithms: the 2x2 box chain; Box3 keeps at most three levels (ao256: 3);
    // MipNormal is the Box chain under another name (180142990 calls 1801443f0 for it).
    private static readonly HashSet<string> BoxAlgorithms = new(StringComparer.OrdinalIgnoreCase) { "Box", "Box3", "ArtistDefined", "MipNormal" };

    // Mip algorithms with their own chain and the image processors.
    private static readonly HashSet<string> Algorithms = new(StringComparer.OrdinalIgnoreCase)
        { "HemiOctIsoRoughness_RG_B", "AnisoRoughness_RG", "HemiOctAnisoRoughness", "ArtistDefinedOrAlphaWeighted", "None", "BoxInverse", "PreserveCoverage" };
    private static readonly HashSet<string> Processors = new(StringComparer.OrdinalIgnoreCase) { "NormalizeNormals", "Inverse", "Mod2XCenter" };

    /// <summary>The special dependencies a texture's mip algorithms and image processors add.</summary>
    internal static IEnumerable<Source2ContainerAuthor.SpecialDep> Deps(GeneratedTexture texture)
    {
        foreach (var algorithm in texture.Channels.Select(c => c.MipAlgorithm).Concat(texture.Inputs.Select(i => i.ImageProcessor)).Where(a => a.Length > 0))
        {
            if (!AlgorithmDeps.TryGetValue(algorithm, out var deps))
                throw new InvalidOperationException($"Texture algorithm {algorithm} is not measured yet.");
            foreach (var (name, fp) in deps)
                yield return new("Texture Compiler Version " + name, "CompileTexture", fp);
        }
    }

    /// <summary>Refuse a texture whose algorithms or processors are not ported.</summary>
    internal static void Check(GeneratedTexture texture, string label)
    {
        foreach (var input in texture.Inputs.Where(i => i.ImageProcessor.Length > 0))
            if (!Processors.Contains(input.ImageProcessor))
                throw new InvalidOperationException($"Image processor {input.ImageProcessor} ({label}) is not compiled here yet.");
        foreach (var channel in texture.Channels)
            if (!BoxAlgorithms.Contains(channel.MipAlgorithm) && !Algorithms.Contains(channel.MipAlgorithm))
                throw new InvalidOperationException($"Mip algorithm {channel.MipAlgorithm} ({label}) is not compiled here yet.");
        bool box3 = texture.Channels.Any(c => c.MipAlgorithm.Equals("Box3", StringComparison.OrdinalIgnoreCase));
        if (box3 && texture.Channels.Any(c => !c.MipAlgorithm.Equals("Box3", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{label} mixes Box3 with other mip algorithms; not measured.");
        bool none = texture.Channels.Any(c => c.MipAlgorithm.Equals("None", StringComparison.OrdinalIgnoreCase));
        if (none && texture.Channels.Any(c => !c.MipAlgorithm.Equals("None", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{label} mixes None with other mip algorithms; not measured.");
    }

    /// <summary>The texture's float levels from its decoded images.</summary>
    /// <param name="texture">Inputs and channel groups.</param>
    /// <param name="decoded">Each distinct input file, decoded to 8-bit RGBA.</param>
    /// <param name="vendor">Whose <c>rsqrtps</c> the normal processor reproduces.</param>
    /// <param name="label">Names the texture in errors.</param>
    internal static Result Build(GeneratedTexture texture, IReadOnlyDictionary<string, (byte[] Rgba, int Width, int Height)> decoded,
        Bc7Vendor vendor, string label)
    {
        Check(texture, label);
        bool box3 = texture.Channels.Any(c => c.MipAlgorithm.Equals("Box3", StringComparison.OrdinalIgnoreCase));

        // Each input is loaded as RC loads it (its own colour space and width rule) at its own size
        // and run through its image processor.
        var loaded = new Dictionary<(string File, bool Srgb, string Processor), RcMipChain.Level>();
        RcMipChain.Level Loaded(GeneratedTextureInput input)
        {
            var key = (input.FileName, input.Srgb, input.ImageProcessor.ToLowerInvariant());
            if (!loaded.TryGetValue(key, out var level))
            {
                var d = decoded[input.FileName];
                level = RcMipChain.Load(d.Rgba, d.Width, d.Height, input.Srgb);
                level = key.Item3 switch
                {
                    "normalizenormals" => RcNormalMaps.NormalizeNormals(level, vendor),
                    "inverse" => RcNormalMaps.Inverse(level),
                    "mod2xcenter" => RcImageProcessors.Mod2XCenter(level),
                    _ => level,
                };
                loaded[key] = level;
            }
            return level;
        }

        // Inside one mip algorithm, a second input of another size is resampled to the larger with
        // clamping (180c43790 with clamp, as the normal-map and alpha-weighting generators call it).
        static RcMipChain.Level Fit(RcMipChain.Level level, int w, int h) =>
            level.Width == w && level.Height == h ? level : RcMipChain.Resize(level, w, h, clamp: true);

        // Each channel group makes its own float chain with its mip algorithm; the texture's
        // levels take each destination channel from its group's chain.
        var chains = new List<(GeneratedTextureChannel Channel, List<RcMipChain.Level> Chain)>();
        bool srgb = false;
        foreach (var channel in texture.Channels)
        {
            var inputs = channel.Inputs.Select(i => texture.Inputs[i]).ToList();
            var algorithm = channel.MipAlgorithm.ToLowerInvariant();
            List<RcMipChain.Level> chain;
            switch (algorithm)
            {
                case "hemioctisoroughness_rg_b" or "anisoroughness_rg" or "hemioctanisoroughness":
                {
                    if (inputs.Count is < 1 or > 2 || inputs.Any(i => i.Srgb) || channel.Srgb)
                        throw new InvalidOperationException($"{label}: a normal map takes a linear normal and an optional linear roughness.");
                    var normal = Loaded(inputs[0]);
                    var rough = inputs.Count == 2 ? Loaded(inputs[1]) : null;
                    int w = Math.Max(normal.Width, rough?.Width ?? 0), h = Math.Max(normal.Height, rough?.Height ?? 0);
                    var (n, r) = (Fit(normal, w, h), rough is null ? null : Fit(rough, w, h));
                    chain = algorithm switch
                    {
                        "hemioctisoroughness_rg_b" => RcNormalMaps.HemiOctIsoRoughness(n, r),
                        "anisoroughness_rg" => RcNormalMaps.AnisoRoughness(n, r),
                        _ => RcNormalMaps.HemiOctAnisoRoughness(n, r),
                    };
                    break;
                }
                case "artistdefinedoralphaweighted":
                {
                    if (inputs.Count != 2)
                        throw new InvalidOperationException($"{label}: alpha weighting reads a colour and a weight input.");
                    // The weighting input's channel 0 becomes the working image's alpha.
                    var color = Loaded(inputs[0]);
                    var weight = Fit(Loaded(inputs[1]), color.Width, color.Height);
                    var work = (float[])color.Rgba.Clone();
                    for (int p = 0; p < color.Width * color.Height; p++)
                        work[p * 4 + 3] = weight.Rgba[p * 4];
                    chain = RcAlphaWeighted.Mips(new RcMipChain.Level(color.Width, color.Height, work));
                    break;
                }
                default:
                    // GenerateMips_Box, _BoxInverse, _PreserveCoverage and None read one input
                    // ("Expecting 1 input texture, found %d!").
                    if (inputs.Count != 1)
                        throw new InvalidOperationException($"A channel of {label} reads {inputs.Count} inputs with {channel.MipAlgorithm}; not measured.");
                    var input = Loaded(inputs[0]);
                    chain = algorithm switch
                    {
                        "none" => RcMipAlgorithms.None(input),
                        "boxinverse" => RcMipAlgorithms.BoxInverse(input),
                        "preservecoverage" => RcMipAlgorithms.PreserveCoverage(input),
                        _ => RcMipChain.Chain(input),
                    };
                    break;
            }
            for (int k = 0; k < channel.DstChannels.Length; k++)
            {
                int s = "rgba".IndexOf(channel.SrcChannels[k]), d = "rgba".IndexOf(channel.DstChannels[k]);
                // An sRGB group keeps alpha linear end to end (the rgba .vtex probes); alpha to a
                // colour channel or back is not measured.
                if (channel.Srgb != inputs[0].Srgb || (channel.Srgb && (s == 3) != (d == 3)))
                    throw new InvalidOperationException($"{label}: colour space {(inputs[0].Srgb ? "srgb" : "linear")} to {(channel.Srgb ? "srgb" : "linear")} on channel {channel.DstChannels[k]} is not measured.");
                if (channel.Srgb)
                    srgb = true;
                else if (d < 3 && texture.Channels.Any(c => c.Srgb))
                    throw new InvalidOperationException($"{label} mixes linear and sRGB colour channels; not measured.");
            }
            chains.Add((channel, chain));
        }

        // The texture is the largest group's size. Each of its levels takes, from every group's
        // chain, the smallest level at least that size (the first when none is) resampled to it
        // with wrapping (18013ce60; the 1x1 default metal beside 16x16 masks, a 64x64 metalness
        // beside a 128x64 colour).
        int width = chains.Max(c => c.Chain[0].Width), height = chains.Max(c => c.Chain[0].Height);
        var levels = new List<RcMipChain.Level>();
        for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            var o = new float[w * h * 4];
            foreach (var (channel, chain) in chains)
            {
                var source = chain[0];
                foreach (var l in chain)
                {
                    if (l.Width < w || l.Height < h)
                        break;
                    source = l;
                }
                var src = (source.Width == w && source.Height == h ? source : RcMipChain.Resize(source, w, h, clamp: false)).Rgba;
                for (int k = 0; k < channel.DstChannels.Length; k++)
                {
                    int s = "rgba".IndexOf(channel.SrcChannels[k]), d = "rgba".IndexOf(channel.DstChannels[k]);
                    for (int p = 0; p < w * h; p++)
                        o[p * 4 + d] = src[p * 4 + s];
                }
            }
            levels.Add(new RcMipChain.Level(w, h, o));
            // A group whose algorithm made one level (None) writes the texture's first level only
            // (180133da0 leaves the output loop when the group's mip count is 1); Check keeps
            // None from sharing a texture with other algorithms.
            if ((w == 1 && h == 1) || chains.All(c => c.Chain.Count == 1))
                break;
        }
        return new(levels, srgb, box3 ? 3 : int.MaxValue);
    }

    /// <summary>
    /// Every level's alpha is exactly 1 (180133da0 checks plane 3 of every mip of every frame
    /// before "Found DXT5 texture with solid 255 alpha! Saving as a DXT1!").
    /// </summary>
    internal static bool AlphaIsSolid(IEnumerable<RcMipChain.Level> levels)
    {
        foreach (var level in levels)
            for (int i = 3; i < level.Rgba.Length; i += 4)
                if (level.Rgba[i] != 1f)
                    return false;
        return true;
    }
}
