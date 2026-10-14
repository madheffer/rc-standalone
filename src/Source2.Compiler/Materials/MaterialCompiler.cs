using System.IO.Hashing;
using System.Numerics;
using SkiaSharp;
using Source2.Compiler.Compiling;
using Source2.Compiler.Shaders;
using ValvePak;
using ValveKeyValue;

namespace Source2.Compiler.Materials;

/// <summary>A content tree under a search path name (<c>csgo</c>, <c>core</c>), searched after the request's own.</summary>
public sealed record ContentMount(string SearchPath, IContentSource Content);

/// <summary>Settings for <see cref="MaterialCompiler"/>.</summary>
public sealed record MaterialCompileSettings
{
    /// <summary>The game's <c>shaders_pc_dir.vpk</c>.</summary>
    public required string ShaderPackage { get; init; }

    /// <summary>
    /// The shader package path RC records in RED2 (<c>vpk:&lt;this&gt;/csgo_complex.vfx</c>): the
    /// absolute path on the machine RC ran on, with backslashes. Null derives it from
    /// <see cref="ShaderPackage"/> (<c>_dir</c> dropped, separators as backslashes).
    /// </summary>
    public string? RecordedShaderPackage { get; init; }

    /// <summary>Content searched after the request's own, in order: RC's is the addon, then
    /// <c>csgo</c>, then <c>core</c>.</summary>
    public IReadOnlyList<ContentMount> Mounts { get; init; } = [];

    /// <summary>
    /// Compile the child textures too (the default). A child this compiler cannot write exactly
    /// fails the compile, rather than leave a material without its texture; false writes the
    /// material alone.
    /// </summary>
    public bool CompileChildren { get; init; } = true;

    /// <summary>Whose BC7 the children reproduce.</summary>
    public RcTexture.Bc7Vendor Bc7Vendor { get; init; } = RcTexture.Bc7Vendor.Amd;
}

/// <summary>
/// <c>.vmat</c> to <c>.vmat_c</c> and its child textures, as resourcecompiler writes them
/// (docs/MATERIALS.md). Anything not measured against RC is refused.
/// </summary>
public sealed class MaterialCompiler : IResourceCompiler
{
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".vmat"];

    public string OutputPathFor(string sourcePath) => sourcePath + "_c";

    // The special dependencies each mip algorithm and image processor adds to a texture compile
    // (RC's children in RcReference/materials).
    private static readonly Dictionary<string, (string Name, int Fingerprint)[]> AlgorithmDeps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Box"] = [("Mip Box", 1)],
        ["Box3"] = [("Mip Box", 1)],
        ["ArtistDefined"] = [("Mip Box", 1)],
        ["ArtistDefinedOrAlphaWeighted"] = [("Mip AlphaWeighted", 1)],
        ["HemiOctIsoRoughness_RG_B"] = [("Mip HemiOctAnisoRoughness", 3), ("Mip HemiOctIsoRoughness_RG_B", 1)],
        ["NormalizeNormals"] = [("Image NormalizeNormals", 1)],
        ["Inverse"] = [("Image Inverse", 1)],
    };

    // Attribute value types that land in m_SearchableUserData.
    private const int AttrFloat = 1, AttrInt = 5, AttrBool = 9, AttrTexture = 14;

    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
    {
        var settings = request.Settings as MaterialCompileSettings ?? throw new InvalidOperationException(
            "A .vmat compile needs MaterialCompileSettings: the shader package and the content mounts.");
        var content = request.Content ?? throw new InvalidOperationException($"{request.SourcePath} needs a content source for its images.");
        var mounts = new List<ContentMount> { new(request.SearchPath, content) };
        mounts.AddRange(settings.Mounts);

        var vmat = VmatSource.Parse(request.Source, request.SourcePath);
        var values = vmat.ByName;
        var shaders = new ShaderSet(settings.ShaderPackage, vmat.Shader);
        int Feature(int index) => MaterialTextures.FeatureValue(shaders.Features, index, values);

        var textures = MaterialTextures.Resolve(shaders.Features, shaders.Pixel, values,
            path => mounts.Any(m => m.Content.Read(path) is not null));

        // Every image the children read, found in the first mount that has it.
        var images = new SortedDictionary<string, (string SearchPath, byte[] Bytes)>(StringComparer.Ordinal);
        foreach (var input in textures.Where(t => t.Generated is not null).SelectMany(t => t.Generated!.Inputs))
            if (!images.ContainsKey(input.FileName))
                images[input.FileName] = Find(mounts, input.FileName)
                    ?? throw new InvalidOperationException($"{input.FileName} (for {request.SourcePath}) was not found in any content mount.");

        var data = BuildData(request.SourcePath, vmat, shaders, textures, images);
        var red2 = BuildRed2(request, settings, shaders, textures, images, mounts, Feature, values);

        var container = new CompiledResource(new ResourceFile { Version = Source2ContainerAuthor.MaterialResourceVersion });
        container.SetKv3("RED2", red2, Kv3Formats.Get("generic"));
        container.SetKv3("DATA", data, Kv3Formats.Get("generic"));
        container.SetKv3("INSG", BuildInsg(shaders, values), Kv3Formats.Get("generic"));
        container.SetReferences(textures.Select(t => t.ResourcePath).Distinct().Order(StringComparer.Ordinal));

        var children = new List<CompiledFile>();
        if (settings.CompileChildren)
            foreach (var g in textures.Where(t => t.Generated is not null).Select(t => t.Generated!).DistinctBy(g => g.ChildPath))
                children.Add(new(g.ChildPath + "_c", ChildTextureCompiler.Compile(g, path => Find(mounts, path), request.SearchPath, settings.Bc7Vendor)));
        return new(container.Write(), children);
    }

    /// <summary>The special dependencies a generated texture's mip algorithms and image processors add.</summary>
    internal static IEnumerable<Source2ContainerAuthor.SpecialDep> TextureDeps(GeneratedTexture texture)
    {
        foreach (var algorithm in texture.Channels.Select(c => c.MipAlgorithm).Concat(texture.Inputs.Select(i => i.ImageProcessor)).Where(a => a.Length > 0))
        {
            if (!AlgorithmDeps.TryGetValue(algorithm, out var deps))
                throw new InvalidOperationException($"Texture algorithm {algorithm} is not measured yet.");
            foreach (var (name, fp) in deps)
                yield return new("Texture Compiler Version " + name, "CompileTexture", fp);
        }
    }

    internal static (string SearchPath, byte[] Bytes)? Find(IEnumerable<ContentMount> mounts, string path)
    {
        foreach (var m in mounts)
            if (m.Content.Read(path) is { } bytes)
                return (m.SearchPath, bytes);
        return null;
    }

    private static KVObject BuildData(string materialPath, VmatSource vmat, ShaderSet shaders,
        IReadOnlyList<MaterialTexture> textures, IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images)
    {
        // Params as the .vmat sets them, typed by their name's prefix (RC keeps names the shader
        // does not declare: cx_order's F_SPECULAR, g_flFadeExponent), each kind sorted by name.
        var ints = new List<(string Name, int Value)>();
        var floats = new List<(string Name, float Value)>();
        var vectors = new List<(string Name, float[] Value)>();
        var textureInputs = shaders.Features.Variables.Where(v => v.IsMaterialTexture).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in vmat.Values)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (textureInputs.Contains(key))
                continue;
            if (key.StartsWith("F_", StringComparison.Ordinal) || key.StartsWith("g_n", StringComparison.Ordinal))
                ints.Add((key, int.Parse(value, c)));
            else if (key.StartsWith("g_fl", StringComparison.Ordinal))
                floats.Add((key, float.Parse(value, c)));
            else if (key.StartsWith("g_v", StringComparison.Ordinal))
                vectors.Add((key, ParseVector(value, materialPath, key)));
            else
                throw new InvalidOperationException($"{materialPath}: '{key}' has a name RC's typing is not measured on.");
        }
        ints.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        floats.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        vectors.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var root = KVObject.Collection();
        root.Add("m_materialName", new KVObject(materialPath));
        root.Add("m_shaderName", new KVObject(vmat.Shader));
        root.Add("m_intParams", VmatParams.NamedArray(ints, "m_nValue", v => new KVObject(v)));
        root.Add("m_floatParams", VmatParams.NamedArray(floats, "m_flValue", v => new KVObject((double)v)));
        root.Add("m_vectorParams", VmatParams.NamedArray(vectors, "m_value", v =>
        {
            var a = KVObject.Array();
            foreach (var f in v) a.Add(new KVObject((double)f));
            return a;
        }));
        root.Add("m_textureParams", VmatParams.NamedArray(textures.Select(t => (t.Name, t.ResourcePath)), "m_pValue",
            v => new KVObject(v) { Flag = KVFlag.Resource }));
        root.Add("m_dynamicParams", KVObject.Array());
        root.Add("m_dynamicTextureParams", KVObject.Array());
        var (w, h) = RepresentativeSize(textures, images);
        root.Add("m_intAttributes", VmatParams.NamedArray([("RepresentativeTextureHeight", h), ("RepresentativeTextureWidth", w)], "m_nValue", v => new KVObject(v)));
        foreach (var key in new[] { "m_floatAttributes", "m_vectorAttributes", "m_textureAttributes", "m_stringAttributes", "m_renderAttributesUsed" })
            root.Add(key, KVObject.Array());
        return Source2ContainerAuthor.NarrowIntegers(root);
    }

    // The colour texture's first input image size; a .vtex named directly reads as 1x1 (cx_vtex).
    private static (int W, int H) RepresentativeSize(IReadOnlyList<MaterialTexture> textures,
        IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images)
    {
        var color = textures.FirstOrDefault(t => t.Name == "g_tColor")
            ?? throw new InvalidOperationException("The material has no g_tColor; its representative texture is not measured.");
        if (color.Generated is null)
            return (1, 1);
        using var data = SKData.CreateCopy(images[color.Generated.Inputs[0].FileName].Bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException($"{color.Generated.Inputs[0].FileName} is not an image this compiler reads.");
        return (codec.Info.Width, codec.Info.Height);
    }

    private static float[] ParseVector(string value, string path, string key)
    {
        var parts = value.Trim().TrimStart('[').TrimEnd(']').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
            throw new InvalidOperationException($"{path}: '{key}' is not a vector: {value}");
        var v = new float[4];
        for (int i = 0; i < parts.Length; i++)
            v[i] = float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        return v;
    }

    private static KVObject BuildRed2(CompileRequest request, MaterialCompileSettings settings, ShaderSet shaders,
        IReadOnlyList<MaterialTexture> textures, IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images,
        IReadOnlyList<ContentMount> mounts, Func<int, int> feature, IReadOnlyDictionary<string, string> values)
    {
        var root = KVObject.Collection();

        // Input dependencies: the .vmat, each image and each image's optional .txt (recorded in
        // the first mount when absent), by path.
        var inputs = new List<EditInfo.Input> { EditInfo.Input.Read(request.SourcePath, request.SearchPath, request.Source) };
        foreach (var (path, (searchPath, bytes)) in images)
        {
            inputs.Add(EditInfo.Input.Read(path, searchPath, bytes));
            inputs.Add(SettingsInput(mounts, path, request.SearchPath));
        }
        root.Add("m_InputDependencies", EditInfo.Inputs(EditInfo.ByPath(inputs)));
        root.Add("m_AdditionalInputDependencies", KVObject.Array());

        var args = new List<EditInfo.Argument>();
        if (images.Count > 0 || textures.Any(t => t.Generated is null))
            args.AddRange(EditInfo.TextureArguments(images.Keys));
        // A .vtex the material names directly is an image argument only (cx_vtex).
        foreach (var path in textures.Where(t => t.Generated is null).Select(t => t.ResourcePath).Distinct())
            args.Add(new(path, "ImageArg"));
        root.Add("m_ArgumentDependencies", EditInfo.Arguments(args));

        var special = new Dictionary<string, Source2ContainerAuthor.SpecialDep>(StringComparer.Ordinal);
        void Special(Source2ContainerAuthor.SpecialDep dep) => special[dep.Name] = dep;
        Special(Source2ContainerAuthor.MaterialBaseDeps[0]);
        var generated = textures.Where(t => t.Generated is not null).Select(t => t.Generated!).ToList();
        if (generated.Count > 0)
            foreach (var dep in Source2ContainerAuthor.TextureBaseDeps)
                Special(dep);
        foreach (var g in generated)
            foreach (var dep in TextureDeps(g))
                Special(dep);
        var recorded = settings.RecordedShaderPackage
            ?? settings.ShaderPackage.Replace("_dir.vpk", ".vpk", StringComparison.OrdinalIgnoreCase).Replace('/', '\\');
        Special(new($"vpk:{recorded}/{shaders.Name}", "CompileMaterial", shaders.Features.Version));
        foreach (var mode in shaders.Features.Modes)
            if (mode.Name == "Depth" && mode.Fallback.Length > 0)
                Special(new($"vpk:{recorded}/{mode.Fallback}", "CompileMaterial", shaders.Fallback(mode.Fallback).Features.Version));
        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(special.Values));

        var children = KVObject.Array();
        foreach (var path in generated.Select(g => g.ChildPath).Distinct().Order(StringComparer.Ordinal))
            children.Add(new KVObject(path));
        EditInfo.AddTail(root, Searchable(shaders, textures, images, feature, values), children);
        return Source2ContainerAuthor.NarrowIntegers(root);
    }

    /// <summary>An image's <c>.txt</c> settings file as RC records it: read from the first mount that
    /// has it, else absent in <paramref name="absentSearchPath"/>.</summary>
    internal static EditInfo.Input SettingsInput(IEnumerable<ContentMount> mounts, string image, string absentSearchPath)
    {
        var txt = EditInfo.SettingsFile(image);
        return Find(mounts, txt) is { } found
            ? new EditInfo.Input(txt, found.SearchPath, System.IO.Hashing.Crc32.HashToUInt32(found.Bytes), true, true)
            : EditInfo.Input.Absent(txt, absentSearchPath);
    }

    // The shader's attributes for the material's combo in every program, evaluated, with the F_
    // values the material sets and RC's own keys; sorted ignoring case.
    private static KVObject Searchable(ShaderSet shaders, IReadOnlyList<MaterialTexture> textures,
        IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images, Func<int, int> feature,
        IReadOnlyDictionary<string, string> values)
    {
        var entries = new Dictionary<string, KVObject>(StringComparer.Ordinal);
        var byHash = shaders.Features.Variables.GroupBy(v => ShaderExpression.ParamHash(v.Name)).ToDictionary(g => g.Key, g => g.First());
        Vector4 Param(uint hash)
        {
            if (!byHash.TryGetValue(hash, out var v))
                throw new InvalidOperationException($"A shader expression reads parameter {hash:X8}, which the shader does not declare.");
            if (values.TryGetValue(v.Name, out var s))
            {
                var f = v.Type == 3 || v.Type == 4 ? ParseVector(s, shaders.Name, v.Name) : [float.Parse(s, System.Globalization.CultureInfo.InvariantCulture), 0, 0, 0];
                return new Vector4(f[0], f[1], f[2], f[3]);
            }
            return v.Type == ShaderVariable.Int
                ? new Vector4(v.IntDefault[0], v.IntDefault[1], v.IntDefault[2], v.IntDefault[3])
                : new Vector4(v.FloatDefault[0], v.FloatDefault[1], v.FloatDefault[2], v.FloatDefault[3]);
        }

        // The combo of every mode the shader runs itself (no fallback): the default one, and the
        // ones a mode fixes (unlit's Depth mode, S_MODE_DEPTH 1, adds DepthPassBatchID). Which
        // mode wins when two disagree on a value is not measured; the first one seen does here.
        var modeSettings = shaders.Features.Modes.Where(m => m.Fallback.Length == 0).Select(m => m.StaticComboSettings)
            .Prepend(new Dictionary<string, int>()).ToList();
        foreach (var program in shaders.Programs)
        foreach (var modeCombo in modeSettings)
        {
            long id = program.StaticComboId(feature, modeCombo);
            foreach (int index in program.AttributesOf(id) ?? [])
            {
                var a = program.Attributes[index];
                KVObject? value = a.Type switch
                {
                    AttrTexture => a.VariableBinding >= 0
                        ? textures.FirstOrDefault(t => t.Name == program.Variables[a.VariableBinding].Name) is { } t ? new KVObject(t.ResourcePath) : null
                        : null,
                    AttrBool or AttrInt or AttrFloat => Evaluate(a, feature, Param),
                    _ => null,
                };
                if (value is not null)
                    entries.TryAdd(a.Name, value);
            }
        }
        foreach (var combo in shaders.Features.StaticCombos)
            if (values.TryGetValue(combo.Name, out var v))
                entries[combo.Name] = new KVObject(int.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        entries["IsChildResource"] = new KVObject(0);
        entries["shader"] = new KVObject(shaders.Name);
        if (entries.ContainsKey("RepresentativeTexture"))
        {
            var (w, h) = RepresentativeSize(textures, images);
            entries["RepresentativeTextureHeight"] = new KVObject(h);
            entries["RepresentativeTextureWidth"] = new KVObject(w);
        }

        var root = KVObject.Collection();
        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            root.Add(key, value);
        return root;
    }

    private static KVObject Evaluate(ShaderAttribute a, Func<int, int> feature, Func<uint, Vector4> param)
    {
        float x;
        if (a.Expression is { } code)
            x = ShaderExpression.Evaluate(code, feature, param).X;
        else if (a.Value is { } v)
            x = v.ValueType == KVValueType.Boolean ? ((bool)v ? 1 : 0) : (float)v;
        else
            throw new InvalidDataException($"Shader attribute {a.Name} has neither a value nor an expression.");
        return a.Type switch
        {
            AttrBool => new KVObject(x != 0 ? 1 : 0),
            AttrInt => new KVObject((int)x),
            _ => new KVObject((double)x),
        };
    }

    // m_elems: the union of the input signatures of the VS static combo, sorted by D3D semantic
    // name, index and element name; m_depth_elems the same from the Depth mode's fallback shader.
    private static KVObject BuildInsg(ShaderSet shaders, IReadOnlyDictionary<string, string> values)
    {
        var root = KVObject.Collection();
        root.Add("m_elems", Signature(shaders.Features, shaders.Vertex, values, null));
        if (shaders.Features.Modes.FirstOrDefault(m => m.Name == "Depth") is { } depth)
        {
            if (depth.Fallback.Length > 0)
            {
                var fallback = shaders.Fallback(depth.Fallback);
                root.Add("m_depth_elems", Signature(fallback.Features, fallback.Vertex, values, null));
            }
            else
                root.Add("m_depth_elems", Signature(shaders.Features, shaders.Vertex, values, depth.StaticComboSettings));
        }
        return Source2ContainerAuthor.NarrowIntegers(root);
    }

    private static KVObject Signature(ShaderProgram features, ShaderProgram vertex, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, int>? modeSettings)
    {
        long id = vertex.StaticComboId(i => MaterialTextures.FeatureValue(features, i, values), modeSettings);
        var indices = vertex.InputSignaturesOf(id) ?? throw new InvalidOperationException($"The vertex shader has no static combo {id}.");
        return MaterialAuthor.WriteSignature(indices.Distinct().SelectMany(i => vertex.InputSignatures[i]).Distinct()
            .OrderBy(e => e.D3DSemanticName, StringComparer.Ordinal).ThenBy(e => e.D3DSemanticIndex).ThenBy(e => e.Name, StringComparer.Ordinal));
    }

    /// <summary>A shader's programs from the shader package, read once per compile.</summary>
    private sealed class ShaderSet
    {
        private readonly string _package;
        private readonly Dictionary<string, ShaderSet> _fallbacks = [];

        public string Name { get; }
        public ShaderProgram Features { get; }
        public ShaderProgram Vertex { get; }
        public ShaderProgram Pixel { get; }
        public IReadOnlyList<ShaderProgram> Programs { get; }

        public ShaderSet(string package, string shader)
        {
            _package = package;
            Name = shader;
            using var pak = new Package();
            pak.Read(package);
            var stem = Path.GetFileNameWithoutExtension(shader);
            ShaderProgram? Load(string kind)
            {
                var entry = pak.FindEntry($"shaders/vfx/{stem}_pc_50_{kind}.vcs");
                if (entry is null)
                    return null;
                pak.ReadEntry(entry, out var bytes);
                return ShaderProgram.Read(bytes);
            }
            Features = Load("features") ?? throw new InvalidOperationException($"{shader} is not in {package}.");
            Vertex = Load("vs") ?? throw new InvalidOperationException($"{shader} has no vertex program.");
            Pixel = Load("ps") ?? throw new InvalidOperationException($"{shader} has no pixel program.");
            Programs = new[] { Vertex, Pixel, Load("psrs") }.OfType<ShaderProgram>().ToList();
        }

        public ShaderSet Fallback(string shader)
        {
            if (!_fallbacks.TryGetValue(shader, out var set))
                _fallbacks[shader] = set = new ShaderSet(_package, shader);
            return set;
        }
    }
}
