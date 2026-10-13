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

    /// <summary>Compile one child.</summary>
    /// <param name="texture">The generated texture.</param>
    /// <param name="find">An image's search path and bytes, from the first content mount that has it.</param>
    /// <param name="firstSearchPath">The first mount's search path, where RC records an absent file.</param>
    /// <param name="vendor">Whose BC7 to reproduce.</param>
    public static byte[] Compile(GeneratedTexture texture, Func<string, (string SearchPath, byte[] Bytes)?> find,
        string firstSearchPath, Bc7Vendor vendor)
    {
        foreach (var input in texture.Inputs.Where(i => i.ImageProcessor.Length > 0))
            throw new InvalidOperationException($"Image processor {input.ImageProcessor} ({texture.ChildPath}) is not compiled here yet.");
        foreach (var channel in texture.Channels)
        {
            if (!BoxAlgorithms.Contains(channel.MipAlgorithm))
                throw new InvalidOperationException($"Mip algorithm {channel.MipAlgorithm} ({texture.ChildPath}) is not compiled here yet.");
            if (channel.Inputs.Length != 1)
                throw new InvalidOperationException($"A channel of {texture.ChildPath} reads {channel.Inputs.Length} inputs; one is compiled here so far.");
        }
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
            var txt = TxtPath(file);
            if (find(txt) is { } settings && !IsEmptySettings(settings.Bytes))
                throw new InvalidOperationException($"{txt} sets image settings, which this compiler has not measured yet.");
            decoded[file] = VtexCompiler.DecodeRgba(images[file].Bytes, file);
        }
        // Each input is loaded as RC loads it (its own colour space and width rule) and brought to
        // the largest input size with the wrap resampler; then packed: unset channels are 0, each
        // channel group copies its source channels to its destinations.
        int width = decoded.Values.Max(d => d.Width), height = decoded.Values.Max(d => d.Height);
        var loaded = new Dictionary<(string File, bool Srgb), RcMipChain.Level>();
        RcMipChain.Level Loaded(GeneratedTextureInput input)
        {
            if (!loaded.TryGetValue((input.FileName, input.Srgb), out var level))
            {
                var d = decoded[input.FileName];
                level = RcMipChain.Load(d.Rgba, d.Width, d.Height, input.Srgb);
                if (d.Width != width || d.Height != height)
                {
                    // Measured on constant images only (character's 1x1 default metal beside its 16x16 masks).
                    if (!IsConstant(d.Rgba))
                        throw new InvalidOperationException($"{input.FileName} is smaller than the other inputs of {texture.ChildPath}; RC's resampling of a varying image is not measured.");
                    level = RcMipChain.Resize(level, width, height, clamp: false);
                }
                loaded[(input.FileName, input.Srgb)] = level;
            }
            return level;
        }

        var packed = new float[width * height * 4];
        bool srgb = false;
        foreach (var channel in texture.Channels)
        {
            var input = texture.Inputs[channel.Inputs[0]];
            var src = Loaded(input).Rgba;
            for (int k = 0; k < channel.DstChannels.Length; k++)
            {
                int s = "rgba".IndexOf(channel.SrcChannels[k]), d = "rgba".IndexOf(channel.DstChannels[k]);
                if (channel.Srgb != input.Srgb || (channel.Srgb && (s == 3 || d == 3)))
                    throw new InvalidOperationException($"{texture.ChildPath}: colour space {(input.Srgb ? "srgb" : "linear")} to {(channel.Srgb ? "srgb" : "linear")} on channel {channel.DstChannels[k]} is not measured.");
                if (channel.Srgb)
                    srgb = true;
                else if (d < 3 && texture.Channels.Any(c => c.Srgb))
                    throw new InvalidOperationException($"{texture.ChildPath} mixes linear and sRGB colour channels; not measured.");
                for (int p = 0; p < width * height; p++)
                    packed[p * 4 + d] = src[p * 4 + s];
            }
        }

        var format = texture.OutputFormat;
        var first = decoded[texture.Inputs[0].FileName];
        var (data, stored, _) = VtexCompiler.BuildTexture(new RcMipChain.Level(width, height, packed), format, srgb,
            noLod: false, box3 ? 3 : int.MaxValue, vendor, (first.Width, first.Height));
        return VtexCompiler.WriteTexture(BuildRed2(texture, files, images, find, firstSearchPath), data, stored);
    }

    private static bool IsConstant(byte[] rgba)
    {
        for (int i = 4; i < rgba.Length; i++)
            if (rgba[i] != rgba[i & 3])
                return false;
        return true;
    }

    private static string TxtPath(string image) => Path.ChangeExtension(image, ".txt").Replace('\\', '/');

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
        var deps = new List<(string Path, string SearchPath, uint Crc, bool Optional, bool Exists)>();
        foreach (var file in files)
        {
            deps.Add((file, images[file].SearchPath, Crc32.HashToUInt32(images[file].Bytes), false, true));
            var txt = TxtPath(file);
            deps.Add(find(txt) is { } t ? (txt, t.SearchPath, Crc32.HashToUInt32(t.Bytes), true, true) : (txt, firstSearchPath, 0u, true, false));
        }
        var additional = KVObject.Array();
        foreach (var d in deps.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var o = KVObject.Collection();
            o.Add("m_RelativeFilename", new KVObject(d.Path));
            o.Add("m_SearchPath", new KVObject(d.SearchPath));
            o.Add("m_nFileCRC", new KVObject(d.Crc));
            o.Add("m_bOptional", new KVObject(d.Optional));
            o.Add("m_bFileExists", new KVObject(d.Exists));
            o.Add("m_bIsGameFile", new KVObject(false));
            additional.Add(o);
        }
        root.Add("m_AdditionalInputDependencies", additional);

        uint maxMip = BitConverter.SingleToUInt32Bits(1048576f);
        var args = new List<(string Name, string Type, uint Fingerprint, uint Default)>
        {
            ("___OverrideInputData___", "BinaryBlobArg", texture.LogicalCrc, 0), ("fast", "FloatArg", 0, 0),
            ("final", "FloatArg", 0, 0), ("hueShiftFixup", "FloatArg", 0, 0), ("lightmapMaxResolution", "FloatArg", 0, 0),
            ("maxmipsize", "FloatArg", maxMip, maxMip), ("minmipsize", "FloatArg", 0, 0),
        };
        foreach (var file in files)
        {
            args.Add((file, "ImageArg", 0, 0));
            args.Add((file, "BinaryBlobArg", 0, 0));
        }
        var argDeps = KVObject.Array();
        foreach (var (name, type, fingerprint, fingerprintDefault) in args.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var o = KVObject.Collection();
            o.Add("m_ParameterName", new KVObject(name));
            o.Add("m_ParameterType", new KVObject(type));
            o.Add("m_nFingerprint", fingerprint == 0 ? new KVObject(0) : new KVObject(fingerprint));
            o.Add("m_nFingerprintDefault", fingerprintDefault == 0 ? new KVObject(0) : new KVObject(fingerprintDefault));
            argDeps.Add(o);
        }
        root.Add("m_ArgumentDependencies", argDeps);

        var special = new SortedDictionary<string, (int Fingerprint, int UserData)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Texture Compiler Version"] = (11, 0),
            ["Texture Encode Quality"] = (1, 3),
        };
        foreach (var (name, fp) in MaterialCompiler.TextureDeps(texture))
            special[name] = (fp, 0);
        var specialDeps = KVObject.Array();
        foreach (var (name, (fp, user)) in special)
        {
            var o = KVObject.Collection();
            o.Add("m_String", new KVObject(name));
            o.Add("m_CompilerIdentifier", new KVObject("CompileTexture"));
            o.Add("m_nFingerprint", fp is 0 or 1 ? new KVObject((long)fp) : new KVObject((uint)fp));
            o.Add("m_nUserData", user is 0 or 1 ? new KVObject((long)user) : new KVObject((uint)user));
            specialDeps.Add(o);
        }
        root.Add("m_SpecialDependencies", specialDeps);
        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());
        root.Add("m_ChildResourceList", KVObject.Array());
        root.Add("m_WeakReferenceList", KVObject.Array());
        var searchable = KVObject.Collection();
        searchable.Add("IsChildResource", new KVObject(1));
        root.Add("m_SearchableUserData", searchable);
        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return Source2ContainerAuthor.NarrowIntegers(root);
    }
}
