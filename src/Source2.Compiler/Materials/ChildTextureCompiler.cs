using System.IO.Hashing;
using Source2.Compiler.RcTexture;
using ValveKeyValue;

namespace Source2.Compiler.Materials;

/// <summary>
/// A material's generated texture to its <c>.vtex_c</c>, as resourcecompiler writes the child
/// (docs/MATERIALS.md): the inputs packed channel by channel into one image, the shared texture
/// build (<see cref="VtexCompiler"/>), and the RED2 of a child. Mip algorithms and image
/// processors not measured yet are refused.
/// </summary>
internal static class ChildTextureCompiler
{
    // Box-family mip algorithms: the 2x2 box chain; Box3 keeps at most three levels (ao256: 3).
    private static readonly HashSet<string> BoxAlgorithms = new(StringComparer.OrdinalIgnoreCase) { "Box", "Box3", "ArtistDefined" };

    // Mip algorithms with their own chain (RcNormalMaps, RcAlphaWeighted) and the image processors.
    private static readonly HashSet<string> Algorithms = new(StringComparer.OrdinalIgnoreCase) { "HemiOctIsoRoughness_RG_B", "ArtistDefinedOrAlphaWeighted" };
    private static readonly HashSet<string> Processors = new(StringComparer.OrdinalIgnoreCase) { "NormalizeNormals", "Inverse" };

    /// <summary>Compile one child.</summary>
    /// <param name="texture">The generated texture.</param>
    /// <param name="find">An image's search path and bytes, from the first content mount that has it.</param>
    /// <param name="firstSearchPath">The first mount's search path, where RC records an absent file.</param>
    /// <param name="vendor">Whose BC7 to reproduce.</param>
    public static byte[] Compile(GeneratedTexture texture, Func<string, (string SearchPath, byte[] Bytes)?> find,
        string firstSearchPath, Bc7Vendor vendor)
    {
        foreach (var input in texture.Inputs.Where(i => i.ImageProcessor.Length > 0))
            if (!Processors.Contains(input.ImageProcessor))
                throw new InvalidOperationException($"Image processor {input.ImageProcessor} ({texture.ChildPath}) is not compiled here yet.");
        foreach (var channel in texture.Channels)
            if (!BoxAlgorithms.Contains(channel.MipAlgorithm) && !Algorithms.Contains(channel.MipAlgorithm))
                throw new InvalidOperationException($"Mip algorithm {channel.MipAlgorithm} ({texture.ChildPath}) is not compiled here yet.");
        bool box3 = texture.Channels.Any(c => c.MipAlgorithm.Equals("Box3", StringComparison.OrdinalIgnoreCase));
        if (box3 && texture.Channels.Any(c => !c.MipAlgorithm.Equals("Box3", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{texture.ChildPath} mixes Box3 with other mip algorithms; not measured.");

        // Every distinct image, with its .txt sibling (RC's image settings).
        var files = texture.Inputs.Select(i => i.FileName).Distinct().ToList();
        var images = new Dictionary<string, (string SearchPath, byte[] Bytes)>(StringComparer.Ordinal);
        var decoded = new Dictionary<string, (byte[] Rgba, int Width, int Height)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            images[file] = find(file) ?? throw new InvalidOperationException($"{file} (for {texture.ChildPath}) was not found.");
            var txt = EditInfo.SettingsFile(file);
            if (find(txt) is { } settings && !IsEmptySettings(settings.Bytes))
                throw new InvalidOperationException($"{txt} sets image settings, which this compiler has not measured yet.");
            decoded[file] = VtexCompiler.DecodeRgba(images[file].Bytes, file);
        }

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
                case "hemioctisoroughness_rg_b":
                {
                    if (inputs.Count is < 1 or > 2 || inputs.Any(i => i.Srgb) || channel.Srgb)
                        throw new InvalidOperationException($"{texture.ChildPath}: a normal map takes a linear normal and an optional linear roughness.");
                    var normal = Loaded(inputs[0]);
                    var rough = inputs.Count == 2 ? Loaded(inputs[1]) : null;
                    int w = Math.Max(normal.Width, rough?.Width ?? 0), h = Math.Max(normal.Height, rough?.Height ?? 0);
                    chain = RcNormalMaps.HemiOctIsoRoughness(Fit(normal, w, h), rough is null ? null : Fit(rough, w, h));
                    break;
                }
                case "artistdefinedoralphaweighted":
                {
                    if (inputs.Count != 2)
                        throw new InvalidOperationException($"{texture.ChildPath}: alpha weighting reads a colour and a weight input.");
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
                    if (inputs.Count != 1)
                        throw new InvalidOperationException($"A channel of {texture.ChildPath} reads {inputs.Count} inputs with {channel.MipAlgorithm}; not measured.");
                    chain = RcMipChain.Chain(Loaded(inputs[0]));
                    break;
            }
            for (int k = 0; k < channel.DstChannels.Length; k++)
            {
                int s = "rgba".IndexOf(channel.SrcChannels[k]), d = "rgba".IndexOf(channel.DstChannels[k]);
                if (channel.Srgb != inputs[0].Srgb || (channel.Srgb && (s == 3 || d == 3)))
                    throw new InvalidOperationException($"{texture.ChildPath}: colour space {(inputs[0].Srgb ? "srgb" : "linear")} to {(channel.Srgb ? "srgb" : "linear")} on channel {channel.DstChannels[k]} is not measured.");
                if (channel.Srgb)
                    srgb = true;
                else if (d < 3 && texture.Channels.Any(c => c.Srgb))
                    throw new InvalidOperationException($"{texture.ChildPath} mixes linear and sRGB colour channels; not measured.");
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
            if (w == 1 && h == 1)
                break;
        }

        var format = texture.OutputFormat;
        var first = decoded[texture.Inputs[0].FileName];
        var (data, stored, _) = VtexCompiler.BuildTexture(levels, format, srgb,
            noLod: false, box3 ? 3 : int.MaxValue, vendor, (first.Width, first.Height));
        return VtexCompiler.WriteTexture(BuildRed2(texture, files, images, find, firstSearchPath), data, stored);
    }

    // core's default_normal.txt: "settings" { }.
    private static bool IsEmptySettings(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"", "").Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return text is ["settings", "{", "}"];
    }

    // A child's RED2: no input dependencies of its own; the images and their .txt as additional
    // ones; the texture compiler's arguments with the logical CRC as the override fingerprint.
    private static KVObject BuildRed2(GeneratedTexture texture, IReadOnlyList<string> files,
        IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images,
        Func<string, (string SearchPath, byte[] Bytes)?> find, string firstSearchPath)
    {
        var root = KVObject.Collection();
        root.Add("m_InputDependencies", KVObject.Array());
        var deps = new List<EditInfo.Input>();
        foreach (var file in files)
        {
            deps.Add(EditInfo.Input.Read(file, images[file].SearchPath, images[file].Bytes));
            var txt = EditInfo.SettingsFile(file);
            deps.Add(find(txt) is { } t
                ? new EditInfo.Input(txt, t.SearchPath, System.IO.Hashing.Crc32.HashToUInt32(t.Bytes), true, true)
                : EditInfo.Input.Absent(txt, firstSearchPath));
        }
        root.Add("m_AdditionalInputDependencies", EditInfo.Inputs(EditInfo.ByPath(deps)));
        root.Add("m_ArgumentDependencies", EditInfo.Arguments(EditInfo.TextureArguments(files, texture.LogicalCrc)));

        var special = new Dictionary<string, Source2ContainerAuthor.SpecialDep>(StringComparer.OrdinalIgnoreCase);
        foreach (var dep in Source2ContainerAuthor.TextureBaseDeps.Concat(MaterialCompiler.TextureDeps(texture)))
            special[dep.Name] = dep;
        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(special.Values));
        EditInfo.AddTail(root, EditInfo.Searchable(child: true));
        return Source2ContainerAuthor.NarrowIntegers(root);
    }

}
