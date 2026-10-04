using Source2.Compiler.Io;
using System.Globalization;
using ValvePak;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;

namespace Source2.Compiler.Maps;

/// <summary>
/// A loaded material's attributes as the builders ask for them by murmur
/// key: the vmat_c's own attributes, then those its shader declares for the
/// static combo the material's F_ parameters select (vs and ps programs,
/// VRF's GetStaticConfiguration_ForFeatureState), constant or a dynamic
/// expression over the features (FEAT[i]) and the material's parameters.
/// </summary>
public sealed class MaterialAttributes : MeshEntryFlags.IAttributes
{
    private readonly Dictionary<uint, object> values = [];

    public (int Width, int Height) TextureSize { get; private set; } = (-1, -1);

    /// <summary>The texture parameter the shader's RepresentativeTexture attribute binds, when declared.</summary>
    public string? RepresentativeTexture { get; private set; }

    public bool? Bool(uint key) => values.TryGetValue(key, out var v) ? Truthy(v) : null;

    public int? Int(uint key) => values.TryGetValue(key, out var v) ? (int)Number(v) : null;

    public string? String(uint key) => values.TryGetValue(key, out var v) ? v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// A material the content does not hold: atixref's four such entries carry
    /// only their record's flags, which is what no attributes give (the
    /// builder's "Cannot load" path would set bit 0).
    /// </summary>
    public static MaterialAttributes Empty => new();

    /// <summary>
    /// The attributes of <paramref name="material"/> (GameContent's record) with
    /// its shader's from <paramref name="shaders"/>. The texture size (vf 0xa8)
    /// is the representative texture's own (its vtex_c, via
    /// <paramref name="textureSize"/>), not the material's nominal
    /// RepresentativeTextureWidth/Height.
    /// </summary>
    public static MaterialAttributes Of(SettleWorld.MaterialInfo material, ShaderLibrary? shaders, Func<string, (int Width, int Height)?>? textureSize = null)
    {
        var result = new MaterialAttributes();
        if (shaders?.Collection(material.Shader) is { } collection)
            result.AddShader(collection, material);
        var parameter = result.RepresentativeTexture;
        if (parameter != null && material.Textures.TryGetValue(parameter, out var texture) && textureSize?.Invoke(texture) is { } size)
            result.TextureSize = size;
        foreach (var (name, value) in material.Strings)
            result.values[MeshEntryFlags.Key(name)] = value;
        foreach (var (name, value) in material.Ints)
            result.values[MeshEntryFlags.Key(name)] = value;
        // Open (GROUND_TRUTH 40): CMaterial2's lookup (materialsystem2 vf 0x48)
        // falls back through a parent attribute set not identified; measured on
        // atixref, probe01 and cardtest, a shader that does not declare
        // AllowBackfaceCulling answers true, but csgo_water_fancy false.
        var cull = MeshEntryFlags.Key("AllowBackfaceCulling");
        if (!result.values.ContainsKey(cull) && !NoCullDefault.Contains(Path.GetFileNameWithoutExtension(material.Shader ?? "")))
            result.values[cull] = true;
        return result;
    }

    private static readonly HashSet<string> NoCullDefault = new(Tier0Strings.IgnoreCase) { "csgo_water_fancy" };

    private void AddShader(ShaderLibrary.ShaderSet set, SettleWorld.MaterialInfo material)
    {
        var features = (material.Params ?? new Dictionary<string, long>())
            .Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => (byte)p.Value);
        var featureValues = set.Features.StaticComboArray.Select(f => features.TryGetValue(f.Name, out var v) ? (int)v : 0).ToArray();
        foreach (var program in set.Programs)
        {
            var (config, id) = ShaderDataProvider.GetStaticConfiguration_ForFeatureState(set.Features, program, features);
            if (!program.StaticComboEntries.ContainsKey(id) && ShaderDataProvider.TryReduceStaticConfiguration(program, config, out var reduced))
                id = new ConfigMappingIds(program).Id(reduced);
            if (!program.StaticComboEntries.ContainsKey(id))
                continue;
            foreach (var attribute in program.GetStaticCombo(id).Attributes)
            {
                if (attribute.Name == "RepresentativeTexture" && RepresentativeTexture == null
                    && attribute.VariableBinding >= 0 && attribute.VariableBinding < program.VariableDescriptions.Length)
                    RepresentativeTexture = program.VariableDescriptions[attribute.VariableBinding].Name;
                if (values.ContainsKey(attribute.Murmur32))
                    continue;
                var value = attribute.DynExpression is { } expression
                    ? Evaluate(ShaderUtilHelpers.ParseDynamicExpression(expression), featureValues, material)
                    : attribute.ConstValue;
                if (value != null)
                    values[attribute.Murmur32] = value;
            }
        }
    }

    private sealed class ConfigMappingIds(VfxProgramData program)
    {
        private readonly ComboConfigMapping mapping = new(program);

        public long Id(int[] config) => mapping.CalcComboIdFromValues(config);
    }

    private static bool Truthy(object v) => v switch
    {
        bool b => b,
        string s => s.Length > 0,
        _ => Number(v) != 0,
    };

    private static double Number(object v) => v switch
    {
        bool b => b ? 1 : 0,
        string => 0,
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    /// <summary>A dynamic expression as VfxEval prints it: FEAT[i], parameters, numbers, ! - * / + - &lt; &gt; == != &amp;&amp; || and ?:.</summary>
    internal static object? Evaluate(string text, int[] features, SettleWorld.MaterialInfo material)
    {
        var parser = new Parser(text.Trim().TrimEnd(';'), features, material);
        if (parser.Text.StartsWith("return ", StringComparison.Ordinal))
            parser.Position = 7;
        try
        {
            var value = parser.Ternary();
            return parser.Position >= parser.Text.Length ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Parser(string text, int[] features, SettleWorld.MaterialInfo material)
    {
        public string Text { get; } = text;
        public int Position { get; set; }

        private void Skip()
        {
            while (Position < Text.Length && char.IsWhiteSpace(Text[Position]))
                Position++;
        }

        private bool Take(string token)
        {
            Skip();
            if (string.CompareOrdinal(Text, Position, token, 0, token.Length) != 0)
                return false;
            Position += token.Length;
            return true;
        }

        public double Ternary()
        {
            var condition = Or();
            if (!Take("?"))
                return condition;
            var a = Ternary();
            if (!Take(":"))
                throw new FormatException();
            var b = Ternary();
            return condition != 0 ? a : b;
        }

        private double Or()
        {
            var v = And();
            while (Take("||"))
            {
                var r = And();
                v = (v != 0 || r != 0) ? 1 : 0;
            }
            return v;
        }

        private double And()
        {
            var v = Compare();
            while (Take("&&"))
            {
                var r = Compare();
                v = (v != 0 && r != 0) ? 1 : 0;
            }
            return v;
        }

        private double Compare()
        {
            var v = Sum();
            while (true)
            {
                if (Take("=="))
                    v = v == Sum() ? 1 : 0;
                else if (Take("!="))
                    v = v != Sum() ? 1 : 0;
                else if (Take("<="))
                    v = v <= Sum() ? 1 : 0;
                else if (Take(">="))
                    v = v >= Sum() ? 1 : 0;
                else if (Take("<"))
                    v = v < Sum() ? 1 : 0;
                else if (Take(">"))
                    v = v > Sum() ? 1 : 0;
                else
                    return v;
            }
        }

        private double Sum()
        {
            var v = Product();
            while (true)
            {
                if (Take("+"))
                    v += Product();
                else if (Take("-"))
                    v -= Product();
                else
                    return v;
            }
        }

        private double Product()
        {
            var v = Unary();
            while (true)
            {
                if (Take("*"))
                    v *= Unary();
                else if (Take("/"))
                    v /= Unary();
                else
                    return v;
            }
        }

        private double Unary()
        {
            if (Take("!"))
                return Unary() == 0 ? 1 : 0;
            if (Take("-"))
                return -Unary();
            if (Take("("))
            {
                var v = Ternary();
                if (!Take(")"))
                    throw new FormatException();
                return v;
            }
            Skip();
            if (Take("FEAT["))
            {
                var start = Position;
                while (Position < Text.Length && char.IsDigit(Text[Position]))
                    Position++;
                var index = int.Parse(Text.AsSpan(start, Position - start), CultureInfo.InvariantCulture);
                if (!Take("]"))
                    throw new FormatException();
                return index < features.Length ? features[index] : 0;
            }
            var begin = Position;
            while (Position < Text.Length && (char.IsLetterOrDigit(Text[Position]) || Text[Position] is '_' or '.'))
                Position++;
            var word = Text[begin..Position];
            if (word.Length == 0)
                throw new FormatException();
            if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            if (word is "true" or "True")
                return 1;
            if (word is "false" or "False")
                return 0;
            if (material.Params?.TryGetValue(word, out var i) == true)
                return i;
            if (material.Floats?.TryGetValue(word, out var f) == true)
                return f;
            return 0;
        }
    }
}

/// <summary>Compiled shaders (shaders_pc_dir.vpk of csgo, then csgo_core) by shader name.</summary>
public sealed class ShaderLibrary(params string[] packages) : IDisposable
{
    public sealed record ShaderSet(VfxProgramData Features, VfxProgramData[] Programs);

    private readonly List<Package> paks = packages.Where(File.Exists).Select(p =>
    {
        var package = new Package();
        package.Read(p);
        return package;
    }).ToList();
    private readonly Dictionary<string, ShaderSet?> sets = new(Tier0Strings.IgnoreCase);

    public ShaderSet? Collection(string? shader)
    {
        if (string.IsNullOrEmpty(shader))
            return null;
        var name = Path.GetFileNameWithoutExtension(shader);
        if (sets.TryGetValue(name, out var found))
            return found;
        VfxProgramData? Load(string suffix)
        {
            foreach (var pak in paks)
                if (pak.FindEntry($"shaders/vfx/{name}_pc_50_{suffix}.vcs") is { } entry)
                {
                    pak.ReadEntry(entry, out var bytes);
                    var program = new VfxProgramData();
                    program.Read($"{name}_pc_50_{suffix}.vcs", new MemoryStream(bytes));
                    return program;
                }
            return null;
        }
        var features = Load("features");
        var programs = new[] { Load("vs"), Load("ps"), Load("psrs") }.OfType<VfxProgramData>().ToArray();
        return sets[name] = features == null ? null : new ShaderSet(features, programs);
    }

    public void Dispose()
    {
        foreach (var set in sets.Values)
            if (set != null)
                foreach (var p in set.Programs.Append(set.Features))
                    p.Dispose();
        foreach (var pak in paks)
            pak.Dispose();
    }
}
