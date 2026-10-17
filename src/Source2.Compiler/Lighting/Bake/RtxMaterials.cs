using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Shaders;
using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat.CompiledShader;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The material shaders' ray tracing programs (shaders/vfx/&lt;shader&gt;_vulkan_60_rtx.vcs and
/// its _features.vcs in shaders_vulkan_dir.vpk): the static combo a material
/// selects and the transpiled hit group for it (Glsl.Shaders).
/// </summary>
public sealed class RtxShaderLibrary(params string[] packages) : IDisposable
{
    public sealed record Program(string Shader, VfxProgramData Features, VfxProgramData Rtx);

    private readonly List<Package> _paks = packages.Where(File.Exists).Select(p =>
    {
        var package = new Package();
        package.Read(p);
        return package;
    }).ToList();
    private readonly Dictionary<string, Program?> _programs = new(StringComparer.OrdinalIgnoreCase);

    public Program? Get(string shader)
    {
        var name = Path.GetFileNameWithoutExtension(shader);
        lock (_programs)
        {
            if (_programs.TryGetValue(name, out var found))
                return found;
            VfxProgramData? Load(string suffix)
            {
                foreach (var pak in _paks)
                    if (pak.FindEntry($"shaders/vfx/{name}_vulkan_60_{suffix}.vcs") is { } entry)
                    {
                        pak.ReadEntry(entry, out var bytes);
                        var p = new VfxProgramData();
                        p.Read($"{name}_vulkan_60_{suffix}.vcs", new MemoryStream(bytes));
                        return p;
                    }
                return null;
            }
            var features = Load("features") ?? FeaturesOf(name);
            // A shader's RayTrace mode may name another shader (csgo_simple's is csgo_complex.vfx):
            // its rtx program and features then stand in, with the material's own parameters.
            if (features?.FeaturesHeader?.Modes.FirstOrDefault(m => m.Name == "RayTrace") is { ShaderFallback.Length: > 0 } mode
                && !string.Equals(Path.GetFileNameWithoutExtension(mode.ShaderFallback), name, StringComparison.OrdinalIgnoreCase))
            {
                _programs[name] = null;
                return _programs[name] = Get(mode.ShaderFallback);
            }
            var rtx = Load("rtx");
            return _programs[name] = features is null || rtx is null ? null : new Program(name, features, rtx);
        }
    }

    /// <summary>shaders/vfx/&lt;shader&gt;_vulkan_60_&lt;suffix&gt;.vcs (cs, rtx, features...), or null.</summary>
    public VfxProgramData? ProgramFile(string shader, string suffix) => ProgramFile50(shader, "60_" + suffix);

    /// <summary>shaders/vfx/&lt;shader&gt;_vulkan_&lt;level and suffix&gt;.vcs (such as 50_ps), or null.</summary>
    public VfxProgramData? ProgramFile50(string shader, string levelSuffix)
    {
        var name = Path.GetFileNameWithoutExtension(shader);
        var suffix = levelSuffix[3..];
        var level = levelSuffix[..2];
        foreach (var pak in _paks)
            if (pak.FindEntry($"shaders/vfx/{name}_vulkan_{level}_{suffix}.vcs") is { } entry)
            {
                pak.ReadEntry(entry, out var bytes);
                var p = new VfxProgramData();
                p.Read($"{name}_vulkan_{level}_{suffix}.vcs", new MemoryStream(bytes));
                return p;
            }
        return null;
    }

    /// <summary>A shader's vulkan features file (level 60, else 50): its modes name each mode's fallback shader.</summary>
    public VfxProgramData? FeaturesOf(string shader)
    {
        var name = Path.GetFileNameWithoutExtension(shader);
        foreach (var level in new[] { "60", "50" })
            foreach (var pak in _paks)
                if (pak.FindEntry($"shaders/vfx/{name}_vulkan_{level}_features.vcs") is { } entry)
                {
                    pak.ReadEntry(entry, out var bytes);
                    var p = new VfxProgramData();
                    p.Read($"{name}_vulkan_{level}_features.vcs", new MemoryStream(bytes));
                    return p;
                }
        return null;
    }

    /// <summary>The material's rtx static combo (ShaderDataProvider over its F_ parameters), or -1 without an rtx program.</summary>
    public (Program Program, long Combo)? Combo(SettleWorld.MaterialInfo info)
    {
        if (Get(info.Shader) is not { } p)
            return null;
        var f = info.Params.Where(x => x.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => (byte)x.Value);
        return (p, ValveResourceFormat.IO.ShaderDataProvider.GetStaticConfiguration_ForFeatureState(p.Features, p.Rtx, f).StaticComboId);
    }

    /// <summary>One shader attribute of a material's static combo: its constant or expression, and the variable it binds (with that variable's SrgbRead).</summary>
    public sealed record Attribute(string Name, uint Murmur, object? Constant, string? Expression, string? Binding, bool BindingSrgb);

    private readonly Dictionary<string, (VfxProgramData Features, VfxProgramData[] Programs)?> _sets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The attributes a material's shader declares for the static combo its F_ parameters select, from
    /// its vulkan vs and ps programs (as MaterialAttributes reads the pc ones), first declaration of a name kept.
    /// </summary>
    public IReadOnlyList<Attribute> Attributes(SettleWorld.MaterialInfo info)
    {
        var name = Path.GetFileNameWithoutExtension(info.Shader ?? "");
        (VfxProgramData Features, VfxProgramData[] Programs)? set;
        lock (_sets)
        {
            if (!_sets.TryGetValue(name, out set))
            {
                VfxProgramData? Load(string level, string suffix)
                {
                    foreach (var pak in _paks)
                        if (pak.FindEntry($"shaders/vfx/{name}_vulkan_{level}_{suffix}.vcs") is { } entry)
                        {
                            pak.ReadEntry(entry, out var bytes);
                            var p = new VfxProgramData();
                            p.Read($"{name}_vulkan_{level}_{suffix}.vcs", new MemoryStream(bytes));
                            return p;
                        }
                    return null;
                }
                set = null;
                foreach (var level in new[] { "60", "50" })
                    if (Load(level, "features") is { } features)
                    {
                        set = (features, new[] { Load(level, "vs"), Load(level, "ps") }.OfType<VfxProgramData>().ToArray());
                        break;
                    }
                _sets[name] = set;
            }
        }
        if (set is not var (feats, programs))
            return [];
        var f = info.Params.Where(x => x.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => (byte)x.Value);
        var result = new List<Attribute>();
        foreach (var program in programs)
        {
            var (config, id) = ValveResourceFormat.IO.ShaderDataProvider.GetStaticConfiguration_ForFeatureState(feats, program, f);
            if (!program.StaticComboEntries.ContainsKey(id) && ValveResourceFormat.IO.ShaderDataProvider.TryReduceStaticConfiguration(program, config, out var reduced))
                id = new ValveResourceFormat.CompiledShader.ComboConfigMapping(program).CalcComboIdFromValues(reduced);
            if (!program.StaticComboEntries.ContainsKey(id))
                continue;
            foreach (var a in program.GetStaticCombo(id).Attributes)
            {
                if (result.Exists(r => r.Murmur == a.Murmur32))
                    continue;
                var desc = a.VariableBinding >= 0 && a.VariableBinding < program.VariableDescriptions.Length ? program.VariableDescriptions[a.VariableBinding] : null;
                result.Add(new Attribute(a.Name, a.Murmur32, a.ConstValue,
                    a.DynExpression is { } e ? ValveResourceFormat.CompiledShader.ShaderUtilHelpers.ParseDynamicExpression(e) : null, desc?.Name, desc?.SrgbRead ?? false));
            }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var (features, programs) in _sets.Values.OfType<(VfxProgramData, VfxProgramData[])>())
        {
            features.Dispose();
            foreach (var program in programs)
                program.Dispose();
        }
        foreach (var p in _programs.Values)
            if (p != null)
            {
                p.Features.Dispose();
                p.Rtx.Dispose();
            }
        foreach (var pak in _paks)
            pak.Dispose();
    }
}

/// <summary>
/// VfxEval as VRF prints it (ShaderUtilHelpers.ParseDynamicExpression): locals
/// (<c>v0 = ...;</c>), FEAT[i], variables, numbers, unary ! and -, * / + -,
/// comparisons, &amp;&amp; ||, ?:, swizzles, float2-4 constructors and
/// materialsystem2's functions (the table at 1800fa000: SrgbGammaToLinear
/// 18009b580, MatrixMultiply 18009bf90, MatrixColorTint2 18009c3c0,
/// MatrixColorCorrect2 18009c090, TextureAverageColor 18009bb70). Values are
/// float vectors of 1 to 4 components or 4x4 matrices (16, row-major).
/// </summary>
public sealed class VfxEval
{
    private readonly string _text;
    private int _pos;
    private readonly Func<string, float[]?> _lookup;
    private readonly int[] _features;
    private readonly Dictionary<string, float[]> _locals = [];

    private VfxEval(string text, Func<string, float[]?> lookup, int[] features)
    {
        _text = text;
        _lookup = lookup;
        _features = features;
    }

    /// <summary>Unported colour matrix arguments met (non-default colour correction or tint): the result was identity.</summary>
    public static int UnportedColorMatrices;

    public static float[] Evaluate(string text, Func<string, float[]?> lookup, int[] features)
    {
        var e = new VfxEval(text.Trim(), lookup, features);
        if (e._text.StartsWith("return ", StringComparison.Ordinal))
            e._pos = 7;
        float[] last = [0f];
        while (true)
        {
            e.Skip();
            if (e._pos >= e._text.Length)
                return last;
            var save = e._pos;
            if (e.Identifier() is { } id && e.Peek() == '=' && e.PeekAt(1) != '=')
            {
                e._pos++;
                e._locals[id] = e.Ternary();
            }
            else
            {
                e._pos = save;
                last = e.Ternary();
            }
            e.Skip();
            if (e.Peek() == ';')
                e._pos++;
        }
    }

    private void Skip()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            _pos++;
    }

    private char Peek()
    {
        Skip();
        return _pos < _text.Length ? _text[_pos] : '\0';
    }

    private char PeekAt(int k) => _pos + k < _text.Length ? _text[_pos + k] : '\0';

    private bool Eat(string s)
    {
        Skip();
        if (string.CompareOrdinal(_text, _pos, s, 0, s.Length) != 0)
            return false;
        _pos += s.Length;
        return true;
    }

    private string? Identifier()
    {
        Skip();
        var start = _pos;
        while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            _pos++;
        if (_pos == start || char.IsDigit(_text[start]))
        {
            _pos = start;
            return null;
        }
        return _text[start.._pos];
    }

    private float[] Ternary()
    {
        var c = Or();
        if (!Eat("?"))
            return c;
        var a = Ternary();
        Eat(":");
        var b = Ternary();
        return c[0] != 0f ? a : b;
    }

    private float[] Or()
    {
        var a = And();
        while (Eat("||"))
        {
            var b = And();
            a = [a[0] != 0f || b[0] != 0f ? 1f : 0f];
        }
        return a;
    }

    private float[] And()
    {
        var a = Compare();
        while (Eat("&&"))
        {
            var b = Compare();
            a = [a[0] != 0f && b[0] != 0f ? 1f : 0f];
        }
        return a;
    }

    private float[] Compare()
    {
        var a = Sum();
        while (true)
        {
            string? op = null;
            foreach (var o in new[] { "==", "!=", "<=", ">=", "<", ">" })
                if (Eat(o))
                {
                    op = o;
                    break;
                }
            if (op == null)
                return a;
            var b = Sum();
            var r = op switch
            {
                "==" => a[0] == b[0], "!=" => a[0] != b[0], "<=" => a[0] <= b[0], ">=" => a[0] >= b[0], "<" => a[0] < b[0], _ => a[0] > b[0],
            };
            a = [r ? 1f : 0f];
        }
    }

    private float[] Sum()
    {
        var a = Product();
        while (true)
        {
            if (Eat("+"))
                a = Zip(a, Product(), (x, y) => x + y);
            else if (Peek() == '-' )
            {
                _pos++;
                a = Zip(a, Product(), (x, y) => x - y);
            }
            else
                return a;
        }
    }

    private float[] Product()
    {
        var a = Unary();
        while (true)
        {
            if (Eat("*"))
                a = Zip(a, Unary(), (x, y) => x * y);
            else if (Eat("/"))
                a = Zip(a, Unary(), (x, y) => x / y);
            else
                return a;
        }
    }

    private float[] Unary()
    {
        if (Eat("!"))
            return [Unary()[0] == 0f ? 1f : 0f];
        if (Peek() == '-')
        {
            _pos++;
            return [.. Unary().Select(x => -x)];
        }
        return Postfix(Primary());
    }

    private float[] Postfix(float[] v)
    {
        while (Peek() == '.' && _pos + 1 < _text.Length && char.IsLetter(_text[_pos + 1]))
        {
            _pos++;
            var sw = Identifier()!;
            v = [.. sw.Select(c => v[Math.Min(v.Length - 1, "xyzwrgba".IndexOf(c) % 4)])];
        }
        return v;
    }

    private float[] Primary()
    {
        if (Eat("("))
        {
            var v = Ternary();
            Eat(")");
            return v;
        }
        Skip();
        var start = _pos;
        if (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
        {
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == 'e' || ((_text[_pos] == '-' || _text[_pos] == '+') && _text[_pos - 1] == 'e')))
                _pos++;
            return [float.Parse(_text[start.._pos], CultureInfo.InvariantCulture)];
        }
        var id = Identifier() ?? throw new FormatException($"VfxEval: unexpected '{_text[_pos..]}' in '{_text}'");
        if (id == "FEAT" && Eat("["))
        {
            var i = (int)Ternary()[0];
            Eat("]");
            return [i >= 0 && i < _features.Length ? _features[i] : 0];
        }
        if (Eat("("))
        {
            var args = new List<float[]>();
            if (!Eat(")"))
            {
                do
                    args.Add(Ternary());
                while (Eat(","));
                Eat(")");
            }
            return Call(id, args);
        }
        if (id is "true")
            return [1f];
        if (id is "false")
            return [0f];
        if (_locals.TryGetValue(id, out var local))
            return local;
        return _lookup(id) ?? [0f];
    }

    private float[] Call(string fn, List<float[]> a)
    {
        switch (fn)
        {
            case "float" or "float1" or "int" or "uint" or "bool":
                return [a[0][0]];
            case "float2" or "float3" or "float4":
            {
                var n = fn[^1] - '0';
                var flat = a.SelectMany(x => x).ToList();
                if (flat.Count == 1)
                    return [.. Enumerable.Repeat(flat[0], n)];
                return [.. flat.Take(n)];
            }
            case "sin":
                return Map(a[0], MathF.Sin);
            case "cos":
                return Map(a[0], MathF.Cos);
            case "sqrt":
                return Map(a[0], MathF.Sqrt);
            case "abs":
                return Map(a[0], MathF.Abs);
            case "pow":
                return Zip(a[0], a[1], MathF.Pow);
            case "min":
                return Zip(a[0], a[1], MathF.Min);
            case "max":
                return Zip(a[0], a[1], MathF.Max);
            case "saturate":
                return Map(a[0], x => Math.Clamp(x, 0f, 1f));
            case "lerp":
                return Zip(Zip(a[0], a[1], (x, y) => y - x), a[2], (d, t) => d * t) is var d2 ? Zip(a[0], d2, (x, y) => x + y) : a[0];
            case "dot":
                return [a[0].Zip(a[1], (x, y) => x * y).Sum()];
            case "SrgbGammaToLinear":
            {
                var v = (float[])a[0].Clone();
                for (var i = 0; i < Math.Min(3, v.Length); i++)
                    v[i] = SrgbGammaToLinear(v[i]);
                return v;
            }
            case "MatrixMultiply":
                return Multiply(a[0], a[1]);
            case "MatrixColorTint2":
                // 1800818d0 builds D^-1 R^T (tint terms) R D like ColorCorrect; with a white tint the tint
                // terms are identity, and its output equals ColorCorrect(1, 1, 1) word for word
                // (tools/vrad3/capture_colormatrix.py). Coloured tints are not ported.
                if (!a[0].Take(3).All(x => x == 1f))
                    UnportedColorMatrices++;
                return ColorCorrect(1f, 1f, 1f, [0f, 0f, 0f]);
            case "MatrixColorCorrect2":
                return ColorCorrect(a[0][0], a[0][1], a[0][2], a.Count > 1 ? a[1] : [0f, 0f, 0f]);
            case "TextureAverageColor":
                return [0f, 0f, 0f, 1f];
            default:
                throw new FormatException($"VfxEval: unknown function {fn}");
        }
    }

    /// <summary>18009b580 through 180082e60: clamp to 0..1, x / 12.92 up to 0.04045, else powf((x + 0.055) / 1.055, 2.4).</summary>
    public static float SrgbGammaToLinear(float x)
    {
        var c = 0f;
        if (0f <= x)
            c = x <= 1f ? x : 1f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    // Row-major 4x4 product a * b (materialsystem2 180081100: each element a dpps of a row and a column,
    // (a0 b0 + a1 b1) + (a2 b2 + a3 b3)).
    private static float[] Multiply(float[] a, float[] b)
    {
        if (a.Length != 16 || b.Length != 16)
            return Identity();
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = (a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j]) + (a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j]);
        return r;
    }

    private static float[] Diagonal(float x, float y, float z) => [x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1];

    /// <summary>
    /// MatrixColorCorrect2 (materialsystem2 18009c090 / 180081260) of (contrast, saturation,
    /// brightness) about the average colour a: ((((((D^-1 R^T) S) R) D) B) C), R the rotation
    /// (1800828c0) taking the normalised luminance axis (0.2125, 0.7154, 0.0721) (180080f40) to z,
    /// D that axis' components, S diag(saturation, saturation, 1), B diag(brightness),
    /// C = (T(a) diag(contrast)) T(-a).
    /// </summary>
    public static float[] ColorCorrect(float contrast, float saturation, float brightness, float[] a)
    {
        float[] t = [1, 0, 0, a[0], 0, 1, 0, a[1], 0, 0, 1, a[2], 0, 0, 0, 1];
        float[] tn = [1, 0, 0, -a[0], 0, 1, 0, -a[1], 0, 0, 1, -a[2], 0, 0, 0, 1];
        var c = Multiply(Multiply(t, Diagonal(contrast, contrast, contrast)), tn);
        // 180080f40: length from (y y + z z) + x x, scaled by its reciprocal.
        float lx = 0.2125f, ly = 0.7154f, lz = 0.0721f;
        var len = MathF.Sqrt(ly * ly + lz * lz + lx * lx);
        if (len >= 1e-17f && len <= 1e17f)
        {
            var inv = 1f / len;
            (lx, ly, lz) = (inv * lx, inv * ly, inv * lz);
        }
        var r = RotationBetween(lx, ly, lz, 0f, 0f, 1f);
        float[] rt =
        [
            r[0], r[4], r[8], r[12], r[1], r[5], r[9], r[13], r[2], r[6], r[10], r[14], r[3], r[7], r[11], r[15],
        ];
        var m = Multiply(Diagonal(1f / lx, 1f / ly, 1f / lz), rt);
        m = Multiply(m, Diagonal(saturation, saturation, 1f));
        m = Multiply(m, r);
        m = Multiply(m, Diagonal(lx, ly, lz));
        m = Multiply(m, Diagonal(brightness, brightness, brightness));
        return Multiply(m, c);
    }

    // 1800828c0: the rotation taking unit vector f to unit vector t about their cross product, with
    // |cross| as the sine (identity when it is 0); the decompile's operation order.
    private static float[] RotationBetween(float fx, float fy, float fz, float tx, float ty, float tz)
    {
        var cy = fz * tx - tz * fx;
        var cx = tz * fy - fz * ty;
        var cz = ty * fx - fy * tx;
        var len = MathF.Sqrt(cz * cz + cy * cy + cx * cx);
        var cos = fz * tz + ty * fy + fx * tx;
        var k = 1f - cos;
        var m = new float[16];
        m[15] = 1f;
        if (!(len > 0f))
        {
            m[0] = m[5] = m[10] = 1f;
            return m;
        }
        var inv = 1f / len;
        var ax = inv * cx;
        var ay = inv * cy;
        var az = inv * cz;
        var t = ax * k;
        m[0] = ax * t + cos;
        m[1] = ay * t - az * len;
        m[4] = ay * t + az * len;
        m[5] = ay * ay * k + cos;
        var u = az * ay * k;
        m[2] = az * t + ay * len;
        m[8] = az * t - ay * len;
        m[9] = u + ax * len;
        m[6] = u - ax * len;
        m[10] = az * k * az + cos;
        return m;
    }

    private static float[] Map(float[] a, Func<float, float> f) => [.. a.Select(f)];

    private static float[] Zip(float[] a, float[] b, Func<float, float, float> f)
    {
        var n = Math.Max(a.Length, b.Length);
        var r = new float[n];
        for (var i = 0; i < n; i++)
            r[i] = f(a[a.Length == 1 ? 0 : Math.Min(i, a.Length - 1)], b[b.Length == 1 ? 0 : Math.Min(i, b.Length - 1)]);
        return r;
    }
}

/// <summary>
/// The scene as vrad3's hit shaders read it: per mesh a GeometryInfo_t and its
/// vertex and index buffers (scenesystem's packed layout from the mesh's
/// material input signature, <see cref="SceneVertexLayout"/>; 32-bit
/// indices), per instance the PackedRtxInstanceData_t, per material
/// its block for the selected hit group (every variable at the RegisterOffset
/// of the static combo's write sequence, in 4-byte words), and the bindless
/// textures and samplers. Instances whose material has no transpiled hit
/// group use the default one (BakeHits).
/// </summary>
public sealed class RtxScene
{
    private readonly BakeScene _scene;
    private readonly HitShader?[] _shaders;
    private readonly ThreadLocal<HitContext> _contexts;

    public IReadOnlyList<string> Notes { get; }

    private readonly Func<HitContext> _factory;

    private RtxScene(BakeScene scene, HitShader?[] shaders, Func<HitContext> factory, IReadOnlyList<string> notes, IReadOnlyList<TextureSource> textureSources)
    {
        _scene = scene;
        _shaders = shaders;
        _factory = factory;
        _contexts = new ThreadLocal<HitContext>(factory);
        Notes = notes;
        TextureSources = textureSources;
    }

    /// <summary>Where a bindless texture came from: its path (null for a stand-in) and whether it is read sRGB, or the stand-in's value.</summary>
    public readonly record struct TextureSource(string? Path, bool Srgb, vec4 Constant);

    /// <summary>Each bindless texture's source, by index (the GPU oracle uploads the same files).</summary>
    public IReadOnlyList<TextureSource> TextureSources { get; }

    /// <summary>The instance's hit group, or null (the caller shades it).</summary>
    public HitShader? ShaderOf(int instance) => _shaders[instance];

    /// <summary>The content reader the scene was built with (the GPU oracle uploads the same files).</summary>
    public Func<string, byte[]?>? ContentRead { get; private init; }

    /// <summary>A fresh hit context over the scene's buffers (the GPU oracle's CPU side).</summary>
    public HitContext NewContext() => _factory();

    public bool Has(int instance) => _shaders[instance] != null;

    public static RtxScene Build(BakeScene scene, Func<string, SettleWorld.MaterialInfo?> materials, RtxShaderLibrary shaders, Func<string, byte[]?> read)
    {
        var notes = new List<string>();
        var meshes = scene.Meshes;
        var geometries = new GeometryInfo_t_natural[meshes.Count];
        var vf = new float[meshes.Count][];
        var vu = new uint[meshes.Count][];
        var ib = new uint[meshes.Count][];
        // Each mesh's vertex buffer and layout as scenesystem builds them from its own material's input signature.
        var signatures = new Dictionary<string, IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement>?>(StringComparer.OrdinalIgnoreCase);
        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            if (!signatures.TryGetValue(mesh.Material, out var signature))
            {
                signature = null;
                if (read(mesh.Material + "_c") is { } vmat)
                {
                    try { signature = MaterialAuthor.ExtractInputSignature(vmat); }
                    catch (InvalidOperationException) { }
                }
                signatures[mesh.Material] = signature;
            }
            ib[m] = [.. mesh.Indices.Select(i => (uint)i)];
            if (signature is null)
            {
                // 1800912d0 makes no vertex buffer for a mesh whose material it cannot load (180092bff);
                // what the GPU then reads for its hits is not ported: no vertex fields here.
                notes.Add($"{mesh.Material}: no material input signature; its meshes get no vertex buffer");
                vu[m] = [];
                vf[m] = [];
                geometries[m] = new GeometryInfo_t_natural
                {
                    m_nVertexAndIndexBuffers = new ivec4(m, m, m, m), m_nBufferStrideInBytes = new ivec4(0, 0, 0, 4),
                    m_vTexCoordFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
                    m_vColorFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
                };
                continue;
            }
            var layout = SceneVertexLayout.Build(mesh, m, signature);
            vu[m] = layout.Words;
            vf[m] = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, float>(layout.Words).ToArray();
            geometries[m] = layout.Geometry;
            foreach (var note in layout.Notes)
                if (!notes.Contains(note))
                    notes.Add(note);
        }
        var textures = new List<BindlessTexture>();
        var textureSources = new List<TextureSource>();
        var textureIndex = new Dictionary<(string, bool), int>();
        int Texture(string? path, bool srgb, bool normal)
        {
            // CPUBAKE_FLATNORMALS=1 (a check): every normal map replaced by the flat default.
            if (normal && Environment.GetEnvironmentVariable("CPUBAKE_FLATNORMALS") == "1")
                path = null;
            var key = (path ?? (normal ? "<flat normal>" : "<white>"), srgb);
            if (textureIndex.TryGetValue(key, out var i))
                return i;
            var t = path is null ? null : LoadTexture(read(path + "_c"), srgb);
            var constant = normal ? new vec4(0.5f, 0.5f, 1f, 1f) : new vec4(1f);
            textureSources.Add(t is null ? new TextureSource(null, srgb, constant) : new TextureSource(path, srgb, default));
            t ??= new BindlessTexture(1, 1, [[constant]]);
            textureIndex[key] = textures.Count;
            textures.Add(t);
            return textures.Count - 1;
        }
        var samplers = new List<BindlessSampler>();
        var samplerIndex = new Dictionary<BindlessSampler, int>();
        int Sampler(BindlessSampler s)
        {
            if (!samplerIndex.TryGetValue(s, out var i))
            {
                samplerIndex[s] = i = samplers.Count;
                samplers.Add(s);
            }
            return i;
        }
        var data = new List<byte>();
        var blocks = new Dictionary<string, (HitShader Shader, uint Offset)?>(StringComparer.OrdinalIgnoreCase);
        (HitShader, uint)? Block(string material)
        {
            if (blocks.TryGetValue(material, out var b))
                return b;
            b = null;
            var info = materials(material);
            if (info != null && shaders.Get(info.Shader) == null)
            {
                // No ray tracing program: rtx_default_hitgroup (180098cd0's block).
                var flags = scene.Instances.First(x => string.Equals(x.Material.Name, material, StringComparison.OrdinalIgnoreCase)).Material.Flags;
                var shader = HitShaders.All.First(h => h.Name == "rtx_default_hitgroup_vulkan_60_rtx" && h.Combo == ((flags & 2) != 0 ? 1 : 0));
                var bytes = DefaultBlock(material, info, flags, shaders.Attributes(info), Texture);
                while (data.Count % 16 != 0)
                    data.Add(0);
                var offset = (uint)data.Count;
                data.AddRange(bytes);
                b = (shader, offset);
            }
            else if (info != null && shaders.Combo(info) is var (program, combo))
            {
                var shader = HitShaders.All.FirstOrDefault(h => h.Name == $"{program.Shader}_vulkan_60_rtx" && h.Combo == combo);
                if (shader == null)
                    notes.Add($"{material}: {program.Shader} rtx combo {combo} is not transpiled; the default hit group stands in");
                else
                {
                    // CPUBAKE_MATDUMP=1: every variable the block writes, as a note (a check against the vmat).
                    var bytes = MaterialBlock(program, combo, info, Texture, Sampler,
                        Environment.GetEnvironmentVariable("CPUBAKE_MATDUMP") == "1" ? line => notes.Add($"  {material} {line}") : null);
                    while (data.Count % 16 != 0)
                        data.Add(0);
                    var offset = (uint)data.Count;
                    data.AddRange(bytes);
                    // The hit group loads the whole _RTX_MATERIAL_DATA_ struct, trailing pads included
                    // (csgo_environment reads its last pad at 748): zeros past what the combo writes.
                    while (data.Count < offset + StructSize(shader.Layout))
                        data.Add(0);
                    b = (shader, offset);
                }
            }
            return blocks[material] = b;
        }
        var instances = new PackedRtxInstanceData_t_natural[scene.Instances.Count];
        var hit = new HitShader?[scene.Instances.Count];
        for (var i = 0; i < instances.Length; i++)
        {
            var inst = scene.Instances[i];
            var block = Block(inst.Material.Name);
            hit[i] = block?.Item1;
            instances[i] = new PackedRtxInstanceData_t_natural
            {
                nSrgbColor = inst.SrgbColor, nTriangleGeometry = (uint)inst.Mesh, nMaterialData = block?.Item2 ?? 0,
                nMaterialFlags_nInstanceFlags = inst.Packed, fEmissiveBoost = inst.EmissiveBoost, nInstanceFlags = inst.InstanceFlags,
            };
        }
        while (data.Count % 4 != 0)
            data.Add(0);
        var raw = data.ToArray();
        var matF = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw).ToArray();
        var matI = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(raw).ToArray();
        var matU = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(raw).ToArray();
        var tex = textures.ToArray();
        var smp = samplers.ToArray();
        HitContext Factory() => new()
        {
            Instances = instances, Geometries = geometries, VerticesF = vf, VerticesU = vu, Indices = ib,
            MatF = matF, MatI = matI, MatU = matU, Textures = tex, Samplers = smp,
        };
        if (VfxEval.UnportedColorMatrices > 0)
            notes.Add($"{VfxEval.UnportedColorMatrices} coloured MatrixColorTint2 tints taken as white (1800818d0 is ported for white only)");
        return new RtxScene(scene, hit, Factory, notes, textureSources) { ContentRead = read };
    }

    /// <summary>
    /// 180098cd0's 0x20-byte block for rtx_default_hitgroup (combo 0 from
    /// materials/dev/rtx/hitgroup_default_opaque.vmat, 1 from _transparent.vmat
    /// with F_TRANSLUCENT, picked by material flag 2): the albedo (attribute
    /// LightSim_Albedo, each component clamped to 0..0.99; (0, 0, 0) without
    /// it), the opacity at +12 (1), the colour texture's bindless index at +16
    /// when there is no LightSim_Albedo (attribute LightSim_DiffuseAlbedoTexture,
    /// else RepresentativeTexture; -1 without), the opacity texture at +20
    /// (-1), the material flags at +24 (180098660's 1, 2, 5), zero at +28. The
    /// shader reads the albedo, the colour texture times the instance colour.
    /// Not ported (they throw): LightSim_Albedo (vf 0x70's width not read) and
    /// a translucent material's opacity (texture 0xfa77b522, not named, else
    /// LightSim_Opacity).
    /// </summary>
    private static byte[] DefaultBlock(string material, SettleWorld.MaterialInfo info, uint flags, IReadOnlyList<RtxShaderLibrary.Attribute> attributes,
                                       Func<string?, bool, bool, int> texture)
    {
        var bytes = new byte[0x20];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 1f);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), -1);
        if (attributes.Any(a => a.Name == "LightSim_Albedo"))
            throw new NotSupportedException($"{material}: LightSim_Albedo in the default hit group block is not ported");
        var colour = attributes.FirstOrDefault(a => a.Name == "LightSim_DiffuseAlbedoTexture") ?? attributes.FirstOrDefault(a => a.Name == "RepresentativeTexture");
        if (colour?.Binding is { } parameter)
        {
            if (!info.Textures.TryGetValue(parameter, out var path))
                throw new NotSupportedException($"{material}: {colour.Name} binds {parameter}, which the material does not set (the shader default is not ported)");
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), texture(path, colour.BindingSrgb, false));
        }
        if ((flags & 2) != 0)
            throw new NotSupportedException($"{material}: a translucent material's default hit group opacity (texture 0xfa77b522 or LightSim_Opacity) is not ported");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), flags);
        return bytes;
    }

    // std430 size of a hit group's _RTX_MATERIAL_DATA_: the last field's end, rounded to 16.
    private static int StructSize(MaterialField[] layout)
    {
        var end = 0;
        foreach (var f in layout)
            end = Math.Max(end, f.Offset + f.Type switch { "vec2" or "ivec2" or "uvec2" => 8, "vec3" or "ivec3" or "uvec3" => 12, "vec4" or "ivec4" or "uvec4" => 16, "mat4" => 64, _ => 4 });
        return (end + 15) & ~15;
    }

    // The material's _RTX_MATERIAL_DATA_ block: each field from its rtx variable (artist value, default or expression).
    private static byte[] MaterialBlock(RtxShaderLibrary.Program program, long combo, SettleWorld.MaterialInfo info,
                                        Func<string?, bool, bool, int> texture, Func<BindlessSampler, int> sampler, Action<string>? log = null)
    {
        var vars = program.Rtx.VariableDescriptions;
        var features = program.Features.StaticComboArray.Select(f => info.Params.TryGetValue(f.Name, out var v) ? (int)v : 0).ToArray();
        var cache = new Dictionary<string, float[]?>();
        float[]? Artist(VfxVariableDescription v)
        {
            if (info.Floats.TryGetValue(v.Name, out var f))
                return [f];
            if (info.Params.TryGetValue(v.Name, out var i))
                return [i];
            if (info.Vectors.TryGetValue(v.Name, out var vec))
                return [vec.X, vec.Y, vec.Z, vec.W];
            return null;
        }
        float[] Default(VfxVariableDescription v) => v.VfxType is VfxVariableType.Int or VfxVariableType.Int2 or VfxVariableType.Int3 or VfxVariableType.Int4 or VfxVariableType.Bool
            ? [.. v.IntDefs.Select(x => (float)x)] : [.. v.FloatDefs];
        float[]? Value(string name, string? self = null, float[]? selfValue = null)
        {
            if (name == self)
                return selfValue;
            if (cache.TryGetValue(name, out var c))
                return c;
            cache[name] = null;
            var candidates = vars.Where(v => v.Name == name).ToList();
            if (candidates.Count == 0)
                return null;
            var d = candidates.FirstOrDefault(v => v.CompiledExpression.Length > 0 && v.VariableSource is VfxVariableSourceType.__Expression__ or VfxVariableSourceType.__SetByArtistAndExpression__) ?? candidates[0];
            float[]? r;
            switch (d.VariableSource)
            {
                case VfxVariableSourceType.__Expression__:
                    r = VfxEval.Evaluate(ShaderUtilHelpers.ParseDynamicExpression(d.CompiledExpression), n => Value(n), features);
                    break;
                case VfxVariableSourceType.__SetByArtistAndExpression__:
                {
                    var artist = Artist(d) ?? Default(d);
                    r = VfxEval.Evaluate(ShaderUtilHelpers.ParseDynamicExpression(d.CompiledExpression), n => Value(n, name, artist), features);
                    break;
                }
                default:
                    r = Artist(d) ?? Default(d);
                    break;
            }
            return cache[name] = r;
        }
        // The static combo's write sequence places every variable it uses: RegisterOffset in 4-byte words of the block.
        var all = program.Rtx.GetStaticCombo(combo).AllVariables.Fields.Where(f => f.RegisterOffset >= 0).ToList();
        // Sampler state descriptions (RegisterType SamplerState, RegisterOffset the state's number): not block words.
        var states = new Dictionary<int, Dictionary<string, int>>();
        foreach (var f in all.Where(f => vars[f.VariableIndex].RegisterType == VfxRegisterType.SamplerState))
        {
            var d = vars[f.VariableIndex];
            var value = d.VariableSource == VfxVariableSourceType.__Expression__ && d.CompiledExpression.Length > 0
                ? (int)(VfxEval.Evaluate(ShaderUtilHelpers.ParseDynamicExpression(d.CompiledExpression), n => Value(n), features)?[0] ?? 0f)
                : d.IntDefs[0];
            if (!states.TryGetValue(f.RegisterOffset, out var st))
                states[f.RegisterOffset] = st = [];
            st[d.Name] = value;
        }
        var writes = all.Where(f => vars[f.VariableIndex].RegisterType != VfxRegisterType.SamplerState).ToList();
        var size = writes.Count == 0 ? 16 : writes.Max(f => f.RegisterOffset * 4 + Size(vars[f.VariableIndex].VfxType));
        // The block ends at the next multiple of 16 past the last write (measured: 348 gives 352, 432 gives 448).
        var bytes = new byte[(size + 16) & ~15];
        foreach (var w in writes)
        {
            var desc = vars[w.VariableIndex];
            var at = w.RegisterOffset * 4;
            if (desc.VfxType == VfxVariableType.Texture2DIndex)
            {
                info.Textures.TryGetValue(desc.Name, out var path);
                var index = texture(path, desc.SrgbRead, desc.Name.Contains("Normal", StringComparison.OrdinalIgnoreCase));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), index);
                log?.Invoke($"{desc.Name} @{at}: texture {index} {path ?? "(none)"}{(desc.SrgbRead ? " srgb" : "")}");
                continue;
            }
            if (desc.VfxType == VfxVariableType.SamplerStateIndex)
            {
                var state = SamplerFor(states.GetValueOrDefault(desc.TypeSpecificBits) ?? []);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), sampler(state));
                log?.Invoke($"{desc.Name} @{at}: sampler {state}");
                continue;
            }
            var v = Value(desc.Name) ?? [0f];
            Write(bytes, at, desc.VfxType, v);
            log?.Invoke($"{desc.Name} @{at}: {desc.VfxType} {string.Join(" ", v.Select(x => x.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)))}");
        }
        return bytes;
    }

    /// <summary>
    /// A sampler state description as rendersystem turns it into a VkSampler (matched slot by slot
    /// against vrad3's bindless table, tools/vrad3/capture_samplers.py): Filter is the D3D11 enum
    /// (0x55 anisotropic, mip / mag / min linear bits 0x1 / 0x4 / 0x10; 255 the default, trilinear),
    /// MaxAniso only with the anisotropic filter, unset addresses wrap.
    /// </summary>
    private static BindlessSampler SamplerFor(IReadOnlyDictionary<string, int> state)
    {
        var filter = state.GetValueOrDefault("Filter", 255);
        var aniso = filter == 0x55 ? Math.Max(1, state.GetValueOrDefault("MaxAniso", 1)) : 0;
        var linear = filter is 0x55 or 255 || (filter & 0x14) != 0;
        var mip = filter is 0x55 or 255 || (filter & 1) != 0;
        return new BindlessSampler(state.GetValueOrDefault("AddressU"), state.GetValueOrDefault("AddressV"), linear, mip, state.GetValueOrDefault("AddressW"), aniso);
    }

    private static int Size(VfxVariableType t) => t switch
    {
        VfxVariableType.Float4x4 => 64, VfxVariableType.Float4x3 => 48, VfxVariableType.Float3x3 => 36,
        VfxVariableType.Float4 or VfxVariableType.Int4 or VfxVariableType.Bool4 => 16,
        VfxVariableType.Float3 or VfxVariableType.Int3 or VfxVariableType.Bool3 => 12,
        VfxVariableType.Float2 or VfxVariableType.Int2 or VfxVariableType.Bool2 => 8,
        _ => 4,
    };

    // A value at a byte offset by its VCS type: floats as floats, ints and bools as 32-bit integers, matrices row-major.
    private static void Write(byte[] bytes, int at, VfxVariableType t, float[] v)
    {
        var n = Size(t) / 4;
        var integer = t is VfxVariableType.Int or VfxVariableType.Int2 or VfxVariableType.Int3 or VfxVariableType.Int4
            or VfxVariableType.Bool or VfxVariableType.Bool2 or VfxVariableType.Bool3 or VfxVariableType.Bool4;
        if (t == VfxVariableType.Float4x4 && v.Length < 16)
            v = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        // A 4x4 goes into the block transposed (measured: vrad3's g_mTextureColorAdjust1 holds the
        // expression's matrix by columns).
        if (t == VfxVariableType.Float4x4)
            v = [v[0], v[4], v[8], v[12], v[1], v[5], v[9], v[13], v[2], v[6], v[10], v[14], v[3], v[7], v[11], v[15]];
        for (var i = 0; i < n && at + i * 4 + 4 <= bytes.Length; i++)
        {
            var c = i < v.Length ? v[i] : 0f;
            if (integer)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + i * 4), (int)c);
            else
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), c);
        }
    }

    /// <summary>
    /// The render system's preload cap (RenderSystem/MaxPreloadTextureResolution, 512; rendersystemvulkan
    /// 1800f7ccc / 180104cd0, settled for physicsbuilder's sampler, Source2.Compiler.Gpu.GpuTextures):
    /// a texture is resident from the first mip whose longer side is at most this, and vrad3 streams in no
    /// more (scenesystem's rtx_texture_resolution, 1800058f0, defaults to the same 512), so the hit
    /// shaders' textureLod(..., 0) reads that mip. 0 lifts the cap (CPUBAKE_TEXCAP, a check).
    /// </summary>
    public static int TextureCap { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("CPUBAKE_TEXCAP"), out var cap) ? cap : 512;

    /// <summary>The first mip a texture is resident from under <see cref="TextureCap"/>.</summary>
    public static int ResidentLevel(int width, int height, int mips)
    {
        var level = 0;
        while (TextureCap > 0 && level < mips - 1 && Math.Max(width >> level, height >> level) > TextureCap)
            level++;
        return level;
    }

    /// <summary>The resident mips of a texture as linear floats (8-bit sRGB through the sRGB curve when the shader reads it sRGB).</summary>
    public static BindlessTexture? LoadTexture(byte[]? bytes, bool srgb)
    {
        if (bytes is null)
            return null;
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(bytes));
        if (res.DataBlock is not ValveResourceFormat.ResourceTypes.Texture tex)
            return null;
        var count = Math.Max(1, (int)tex.NumMipLevels);
        var first = ResidentLevel(tex.Width, tex.Height, count);
        var mips = new vec4[count - first][];
        for (var m = 0; m < mips.Length; m++)
        {
            using var bmp = tex.GenerateBitmap(0, 0, (uint)(m + first), ValveResourceFormat.TextureDecoders.TextureCodec.None);
            var texels = new vec4[bmp.Width * bmp.Height];
            if (bmp.ColorType is SkiaSharp.SKColorType.RgbaF32)
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bmp.GetPixelSpan());
                for (var i = 0; i < texels.Length; i++)
                    texels[i] = new vec4(s[i * 4], s[i * 4 + 1], s[i * 4 + 2], s[i * 4 + 3]);
            }
            else if (bmp.ColorType is SkiaSharp.SKColorType.RgbaF16)
            {
                var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bmp.GetPixelSpan());
                for (var i = 0; i < texels.Length; i++)
                    texels[i] = new vec4((float)s[i * 4], (float)s[i * 4 + 1], (float)s[i * 4 + 2], (float)s[i * 4 + 3]);
            }
            else
            {
                // 8-bit: read the bitmap's own byte order (Skia's CopyTo between colour types can fail and leave zeros).
                using var rgba = bmp.ColorType is SkiaSharp.SKColorType.Bgra8888 or SkiaSharp.SKColorType.Rgba8888 ? null : bmp.Copy(SkiaSharp.SKColorType.Rgba8888);
                var src = rgba ?? bmp;
                var bgra = src.ColorType == SkiaSharp.SKColorType.Bgra8888;
                var s = src.GetPixelSpan();
                for (var i = 0; i < texels.Length; i++)
                {
                    float r = s[i * 4 + (bgra ? 2 : 0)] / 255f, g = s[i * 4 + 1] / 255f, b = s[i * 4 + (bgra ? 0 : 2)] / 255f;
                    if (srgb)
                        (r, g, b) = (BakeTexture.SrgbToLinear(r), BakeTexture.SrgbToLinear(g), BakeTexture.SrgbToLinear(b));
                    texels[i] = new vec4(r, g, b, s[i * 4 + 3] / 255f);
                }
            }
            mips[m] = texels;
        }
        return new BindlessTexture(Math.Max(1, tex.Width >> first), Math.Max(1, tex.Height >> first), mips);
    }

    /// <summary>Runs the instance's hit group for an accepted hit; false when the instance has none (the caller shades it).</summary>
    public bool Shade(ref Payload p, in RayScene.Hit hit, int instance, int primitive, Vector3 origin, Vector3 direction)
    {
        var shader = _shaders[instance];
        if (shader == null)
            return false;
        var ctx = _contexts.Value!;
        var inst = _scene.Instances[instance];
        ctx.Payload = new Payload_t(new vec3(p.SmoothPosition.X, p.SmoothPosition.Y, p.SmoothPosition.Z), default, default, p.HitT, p.OpacityRng, p.Instance, p.Flipped,
            new MaterialDataMinimal_t(default, 0f, new vec3(1f, 0f, 1f), 1f, default, 0.040008545f, 0u, default));
        ctx.Ignored = false;
        ctx.InstanceCustomIndex = inst.Mesh;
        ctx.InstanceID = instance;
        ctx.PrimitiveID = primitive;
        ctx.ObjectToWorld = Mat(inst.ObjectToWorld);
        ctx.WorldToObject = Mat(inst.WorldToObject);
        ctx.RayTmax = hit.T;
        ctx.HitKind = hit.FrontFacing ? 0xFEu : 0xFFu;
        ctx.Barycentrics = new vec2(hit.U, hit.V);
        ctx.WorldRayOrigin = new vec3(origin.X, origin.Y, origin.Z);
        ctx.WorldRayDirection = new vec3(direction.X, direction.Y, direction.Z);
        shader.ClosestHit(ctx);
        var r = ctx.Payload;
        static Vector3 V(vec3 v) => new(v.x, v.y, v.z);
        p.SmoothPosition = V(r.vSmoothPositionWs);
        p.FaceNormal = V(r.vFaceNormalWs);
        p.InterpolatedNormal = V(r.vInterpolatedNormalWs);
        p.HitT = r.flHitT;
        p.OpacityRng = r.flOpacityRng;
        p.Instance = r.nInstance;
        p.Flipped = r.bFlippedNormal;
        p.Normal = V(r.material.vNormalWs);
        p.Albedo = V(r.material.vAlbedo);
        p.Emissive = V(r.material.vEmissive);
        p.Metalness = r.material.flMetalness;
        p.Roughness = r.material.flRoughness;
        p.Reflectance = r.material.flReflectance;
        p.MaterialTypeAndFlags = r.material.nMaterialTypeAndFlags;
        p.MaterialBits = new Vector4(r.material.vMaterialBits.x, r.material.vMaterialBits.y, r.material.vMaterialBits.z, r.material.vMaterialBits.w);
        return true;
    }

    // System.Numerics' row-vector matrix as GLSL's mat4x3 (columns: the basis images and the translation).
    internal static mat4x3 Mat(Matrix4x4 m) => new(new vec3(m.M11, m.M12, m.M13), new vec3(m.M21, m.M22, m.M23), new vec3(m.M31, m.M32, m.M33), new vec3(m.M41, m.M42, m.M43));
}
