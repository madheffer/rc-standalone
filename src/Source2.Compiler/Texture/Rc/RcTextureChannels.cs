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
    /// <summary>The texture's levels, whether any colour channel is sRGB, the most levels it keeps,
    /// and its reflectivity.</summary>
    internal sealed record Result(List<RcMipChain.Level> Levels, bool Srgb, int MaxMips, float[] Reflectivity);

    // The special dependencies each mip algorithm and image processor adds to a texture compile
    // (RC's children in RcReference/materials, the .vtex probes in RcReference/textures); the
    // names are the strings GenerateMips and the processors register (180142990, 1801443f0 ...).
    private static readonly Dictionary<string, (string Name, int Fingerprint)[]> AlgorithmDeps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Box"] = [("Mip Box", 1)],
        ["ArtistDefined"] = [("Mip Box", 1)],
        ["ArtistDefinedOrAlphaWeighted"] = [("Mip AlphaWeighted", 1)],
        // ArtistDefinedOrAlphaWeighted with no artist setting re-dispatches as AlphaWeighted
        // (180142990), which runs GenerateMips_AlphaWeighted (18014e680) directly.
        ["AlphaWeighted"] = [("Mip AlphaWeighted", 1)],
        ["HemiOctNormal"] = [("Mip HemiOctNormal", 1)],
        ["HemiOctIsoRoughness_RG_B"] = [("Mip HemiOctAnisoRoughness", 3), ("Mip HemiOctIsoRoughness_RG_B", 1)],
        // 180142990 registers its own entry for AnisoRoughness_RG, then the shared chain its (fp 3).
        ["AnisoRoughness_RG"] = [("Mip AnisoRoughness_RG", 1), ("Mip HemiOctAnisoRoughness", 3)],
        ["HemiOctAnisoRoughness"] = [("Mip HemiOctAnisoRoughness", 3)],
        // 180148100 registers its own entry, then calls the shared chain (180148350).
        ["HemiOctAnisoRoughness_RG_BA"] = [("Mip HemiOctAnisoRoughness_RG_BA", 1), ("Mip HemiOctAnisoRoughness", 3)],
        ["None"] = [("Mip None", 1)],
        ["BoxInverse"] = [("Mip Box Inverse", 1)],
        // MipNormal registers its own entry, then runs the Box chain, which registers Box's.
        ["MipNormal"] = [("Mip Normals", 1), ("Mip Box", 1)],
        ["PreserveCoverage"] = [("Mip PreserveCoverage", 5)],
        ["MultiplyBox"] = [("Mip Multiply", 1)],
        ["MultiplyNoMip"] = [("Mip Multiply", 1)],
        ["MipWidth"] = [("Mip MipSize", 1)],
        ["MipHeight"] = [("Mip MipSize", 1)],
        ["MipDepth"] = [("Mip MipSize", 1)],
        ["RGBM"] = [("Mip RGBM", 3)],
        ["NormalizeNormals"] = [("Image NormalizeNormals", 1)],
        ["Inverse"] = [("Image Inverse", 1)],
        ["Mod2XCenter"] = [("Image Mod2X", 1)],
        ["EncodeRGBM"] = [("Image Encode RGBM", 1)],
        ["ConvertToYCoCg"] = [("Image YCoCg Conversion", 1)],
        ["ScaleToPowerOfTwo"] = [("Image ScaleToPowerOfTwo", 1)],
        ["FillToPowerOfTwo"] = [("Image FillToPowerOfTwo", 1)],
        ["FillToMultipleOfFour"] = [("Image FillToPowerOfTwo", 1)],
        ["HeightToNormal"] = [("Image HeightToNormal", 1)],
    };

    // Box-family mip algorithms: the 2x2 box chain; MipNormal is the Box chain under another name
    // (180142990 calls 1801443f0 for it). "Box<N>" (V_sscanf "Box%d") is the chain cut to
    // min(N, the full count) levels (1801443f0's last argument; ao256: Box3 keeps 3).
    private static readonly HashSet<string> BoxAlgorithms = new(StringComparer.OrdinalIgnoreCase) { "Box", "ArtistDefined", "MipNormal" };

    /// <summary>The level cap of a "Box&lt;N&gt;" algorithm (N at least 1), else null.</summary>
    internal static int? BoxCap(string algorithm) =>
        algorithm.Length > 3 && algorithm.StartsWith("Box", StringComparison.Ordinal) && int.TryParse(algorithm.AsSpan(3), System.Globalization.NumberStyles.None, null, out var n) && n >= 1 ? n : null;

    // Mip algorithms with their own chain and the image processors.
    private static readonly HashSet<string> Algorithms = new(StringComparer.OrdinalIgnoreCase)
        {
            "HemiOctIsoRoughness_RG_B", "AnisoRoughness_RG", "HemiOctAnisoRoughness", "ArtistDefinedOrAlphaWeighted", "None", "BoxInverse", "PreserveCoverage",
            "MultiplyBox", "MultiplyNoMip", "MipWidth", "MipHeight", "MipDepth", "RGBM", "HemiOctAnisoRoughness_RG_BA", "AlphaWeighted", "HemiOctNormal",
        };
    private static readonly HashSet<string> Processors = new(StringComparer.OrdinalIgnoreCase) { "NormalizeNormals", "Inverse", "Mod2XCenter", "EncodeRGBM", "ConvertToYCoCg", "ScaleToPowerOfTwo", "FillToPowerOfTwo", "FillToMultipleOfFour", "HeightToNormal" };

    /// <summary>The special dependencies a texture's mip algorithms and image processors add.</summary>
    /// <param name="texture">Inputs and channel groups.</param>
    /// <param name="size">Each input file's size, when known: ScaleToPowerOfTwo registers its entry only
    /// when it resizes (18013f300 checks each frame first).</param>
    internal static IEnumerable<Source2ContainerAuthor.SpecialDep> Deps(GeneratedTexture texture, Func<string, (int Width, int Height)>? size = null)
    {
        static bool Pow2(int v) => v > 0 && (v & (v - 1)) == 0;
        foreach (var algorithm in texture.Channels.Select(c => c.MipAlgorithm).Concat(texture.Inputs.Select(i => i.ImageProcessor)).Where(a => a.Length > 0))
        {
            // The resizing processors register only when a frame needs them.
            Func<(int Width, int Height), bool>? untouched = algorithm.ToLowerInvariant() switch
            {
                "scaletopoweroftwo" or "filltopoweroftwo" => d => Pow2(d.Width) && Pow2(d.Height),
                "filltomultipleoffour" => d => d.Width % 4 == 0 && d.Height % 4 == 0,
                _ => null,
            };
            if (untouched is not null && size is not null
                && texture.Inputs.Where(i => i.ImageProcessor.Equals(algorithm, StringComparison.OrdinalIgnoreCase)).All(i => untouched(size(i.FileName))))
                continue;
            if (!AlgorithmDeps.TryGetValue(BoxCap(algorithm) is not null ? "Box" : algorithm, out var deps))
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
            if (!BoxAlgorithms.Contains(channel.MipAlgorithm) && !Algorithms.Contains(channel.MipAlgorithm) && BoxCap(channel.MipAlgorithm) is null)
                throw new InvalidOperationException($"Mip algorithm {channel.MipAlgorithm} ({label}) is not compiled here yet.");
        var caps = texture.Channels.Select(c => BoxCap(c.MipAlgorithm)).ToList();
        if (caps.Any(c => c is not null) && caps.Distinct().Count() > 1)
            throw new InvalidOperationException($"{label} mixes a capped Box<N> with other mip algorithms; not measured.");
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
        var cap = BoxCap(texture.Channels[0].MipAlgorithm) ?? int.MaxValue;

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
                    "encodergbm" => RcImageProcessors.EncodeRgbm(level),
                    "converttoycocg" => RcImageProcessors.ConvertToYCoCg(level),
                    "scaletopoweroftwo" => RcImageProcessors.ScaleToPowerOfTwo(level),
                    "filltopoweroftwo" => RcImageProcessors.FillToPowerOfTwo(level),
                    "filltomultipleoffour" => RcImageProcessors.FillToMultipleOfFour(level),
                    "heighttonormal" => RcImageProcessors.HeightToNormal(level),
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
                case "hemioctisoroughness_rg_b" or "anisoroughness_rg" or "hemioctanisoroughness" or "hemioctanisoroughness_rg_ba":
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
                        "hemioctanisoroughness_rg_ba" => RcNormalMaps.HemiOctAnisoRoughnessRgBa(n, r),
                        _ => RcNormalMaps.HemiOctAnisoRoughness(n, r),
                    };
                    break;
                }
                case "artistdefinedoralphaweighted" or "alphaweighted":
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
                case "multiplybox" or "multiplynomip":
                {
                    if (inputs.Count != 2)
                        throw new InvalidOperationException($"{label}: {channel.MipAlgorithm} reads two inputs (\"Expecting 2 input textures\").");
                    chain = RcMipAlgorithms.Multiply(Loaded(inputs[0]), Loaded(inputs[1]), mips: algorithm == "multiplybox");
                    break;
                }
                default:
                    // GenerateMips_Box, _BoxInverse, _PreserveCoverage, _MipSize and None read one input
                    // ("Expecting 1 input texture, found %d!").
                    if (inputs.Count != 1)
                        throw new InvalidOperationException($"A channel of {label} reads {inputs.Count} inputs with {channel.MipAlgorithm}; not measured.");
                    var input = Loaded(inputs[0]);
                    chain = algorithm switch
                    {
                        "none" => RcMipAlgorithms.None(input),
                        "boxinverse" => RcMipAlgorithms.BoxInverse(input),
                        "preservecoverage" => RcMipAlgorithms.PreserveCoverage(input),
                        "mipwidth" => RcMipAlgorithms.MipSize(input, 0),
                        "mipheight" => RcMipAlgorithms.MipSize(input, 1),
                        "mipdepth" => RcMipAlgorithms.MipSize(input, 2),
                        "rgbm" => RcMipAlgorithms.Rgbm(input),
                        "hemioctnormal" => RcNormalMaps.HemiOctNormal(input),
                        _ => RcMipChain.Chain(input),
                    };
                    break;
            }
            // MipSize's frames hold red alone; RC copies a group's channel with a memcpy of the
            // destination's size (181277690), so reading an absent channel runs past its 16-byte
            // constant into the heap (mipwidth_rgba: G and B equal R, A 0). Only red is defined.
            if (algorithm is "mipwidth" or "mipheight" or "mipdepth" && channel.SrcChannels.Any(c => c != 'r'))
                throw new InvalidOperationException($"{label}: {channel.MipAlgorithm} makes a red-only frame; RC's copy of its other channels reads past their storage.");
            // HeightToNormal leaves an RGB frame (180c3f140 mask 7): alpha has the same past-the-storage copy
            // (heighttonormal_rgba: alpha 0 in the stored levels, 1 in the reflectivity).
            if (inputs.Any(i => i.ImageProcessor.Equals("HeightToNormal", StringComparison.OrdinalIgnoreCase)) && channel.SrcChannels.Contains('a'))
                throw new InvalidOperationException($"{label}: HeightToNormal makes an RGB frame; RC's copy of its alpha reads past its storage.");
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
        // A shader's m_nMaxRes caps the output (180133da0 divides the size down); not measured yet.
        if (texture.OutputMaxDimension > 0 && Math.Max(width, height) > texture.OutputMaxDimension)
            throw new InvalidOperationException($"{label}: {width}x{height} is over the shader's maximum of {texture.OutputMaxDimension}; not measured.");
        // The first channel group makes the texture's levels: 180133da0 leaves its output loop
        // after level 0 when that group's algorithm made one level (None), so the texture keeps one
        // mip (none_box_rgba). A later group with one level fills every level from its level 0
        // (box_none_rgba, csgo_weapon's sticker normals). A later group larger than the first
        // upsizes the levels made so far (18013ce60) and regenerates their mips when it has more
        // than one level (character's 1x1 default metal before 16x16 masks); with only one-level
        // groups that case is not measured.
        bool firstIsLargest = chains[0].Chain[0].Width == width && chains[0].Chain[0].Height == height;
        bool oneLevel = chains[0].Chain.Count == 1 && firstIsLargest;
        if (chains[0].Chain.Count == 1 && !firstIsLargest && chains.All(c => c.Chain.Count == 1))
            throw new InvalidOperationException($"{label}: one-level channel groups of different sizes; not measured.");

        // Reflectivity, per destination channel, is the mean of its group's last level's source
        // channel (180133da0 averages each group's last mip, 180c3bb80, then adds avg[src] into
        // the destination's slot as it copies level 0): a None group's is its full image's mean.
        var reflectivity = new float[4];
        foreach (var (channel, chain) in chains)
        {
            var mean = VtexCompiler.Average(chain[Math.Min(chain.Count, cap) - 1]);
            for (int k = 0; k < channel.DstChannels.Length; k++)
                reflectivity["rgba".IndexOf(channel.DstChannels[k])] = mean["rgba".IndexOf(channel.SrcChannels[k])];
        }

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
            if ((w == 1 && h == 1) || oneLevel)
                break;
        }
        return new(levels, srgb, cap, reflectivity);
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
