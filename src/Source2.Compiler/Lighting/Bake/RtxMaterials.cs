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
            var features = Load("features");
            var rtx = Load("rtx");
            return _programs[name] = features is null || rtx is null ? null : new Program(name, features, rtx);
        }
    }

    /// <summary>The material's rtx static combo (ShaderDataProvider over its F_ parameters), or -1 without an rtx program.</summary>
    public (Program Program, long Combo)? Combo(SettleWorld.MaterialInfo info)
    {
        if (Get(info.Shader) is not { } p)
            return null;
        var f = info.Params.Where(x => x.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => (byte)x.Value);
        return (p, ValveResourceFormat.IO.ShaderDataProvider.GetStaticConfiguration_ForFeatureState(p.Features, p.Rtx, f).StaticComboId);
    }

    public void Dispose()
    {
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
                if (a[0].Take(3).All(x => x == 1f))
                    return Identity();
                UnportedColorMatrices++;
                return Identity();
            case "MatrixColorCorrect2":
                if (a[0].Take(3).All(x => x == 1f))
                    return Identity();
                UnportedColorMatrices++;
                return Identity();
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

    // Row-major 4x4 product a * b.
    private static float[] Multiply(float[] a, float[] b)
    {
        if (a.Length != 16 || b.Length != 16)
            return Identity();
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j];
        return r;
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
/// vertex and index buffers (scene_meshes.kv3's float vertices: a field of
/// size 2, 3 or 4 is R32G32 / R32G32B32 / R32G32B32A32_FLOAT, offsets times
/// 4; 32-bit indices), per instance the PackedRtxInstanceData_t, per material
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

    private RtxScene(BakeScene scene, HitShader?[] shaders, Func<HitContext> factory, IReadOnlyList<string> notes)
    {
        _scene = scene;
        _shaders = shaders;
        _contexts = new ThreadLocal<HitContext>(factory);
        Notes = notes;
    }

    public bool Has(int instance) => _shaders[instance] != null;

    public static RtxScene Build(BakeScene scene, Func<string, SettleWorld.MaterialInfo?> materials, RtxShaderLibrary shaders, Func<string, byte[]?> read)
    {
        var notes = new List<string>();
        var meshes = scene.Meshes;
        var geometries = new GeometryInfo_t_natural[meshes.Count];
        var vf = new float[meshes.Count][];
        var vu = new uint[meshes.Count][];
        var ib = new uint[meshes.Count][];
        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            vf[m] = mesh.Vertices;
            vu[m] = System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(mesh.Vertices).ToArray();
            ib[m] = [.. mesh.Indices.Select(i => (uint)i)];
            geometries[m] = Geometry(mesh, m);
        }
        var textures = new List<BindlessTexture>();
        var textureIndex = new Dictionary<(string, bool), int>();
        int Texture(string? path, bool srgb, bool normal)
        {
            var key = (path ?? (normal ? "<flat normal>" : "<white>"), srgb);
            if (textureIndex.TryGetValue(key, out var i))
                return i;
            var t = path is null ? null : LoadTexture(read(path + "_c"), srgb);
            t ??= normal ? new BindlessTexture(1, 1, [[new vec4(0.5f, 0.5f, 1f, 1f)]]) : new BindlessTexture(1, 1, [[new vec4(1f)]]);
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
            if (materials(material) is { } info && shaders.Combo(info) is var (program, combo))
            {
                var shader = HitShaders.All.FirstOrDefault(h => h.Name == $"{program.Shader}_vulkan_60_rtx" && h.Combo == combo);
                if (shader == null)
                    notes.Add($"{material}: {program.Shader} rtx combo {combo} is not transpiled; the default hit group stands in");
                else
                {
                    var bytes = MaterialBlock(program, combo, info, Texture, Sampler);
                    while (data.Count % 16 != 0)
                        data.Add(0);
                    var offset = (uint)data.Count;
                    data.AddRange(bytes);
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
            notes.Add($"{VfxEval.UnportedColorMatrices} non-default colour matrices taken as identity (MatrixColorTint2 / MatrixColorCorrect2 not ported)");
        return new RtxScene(scene, hit, Factory, notes);
    }

    // GeometryInfo_t for one scene mesh: one stream, its float fields (scenesystem 180093f10's formats).
    private static GeometryInfo_t_natural Geometry(BakeScene.Mesh mesh, int index)
    {
        static int Format(int size) => size switch { 1 => 41, 2 => 16, 3 => 6, _ => 2 };
        var g = new GeometryInfo_t_natural
        {
            m_nVertexAndIndexBuffers = new ivec4(index, index, index, index),
            m_nBufferBindOffsetBytes = new ivec4(0),
            m_nBufferStrideInBytes = new ivec4(mesh.Stride * 4, 0, 0, 4),
            m_vTexCoordFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
            m_vColorFieldInfo = new _Array_natural_vector_int_4_8 { data = new ivec4[8] },
        };
        foreach (var f in mesh.Fields)
        {
            var info = new ivec4(f.Offset * 4, Format(f.Size), 0, 0);
            switch (f.Name.ToLowerInvariant())
            {
                case "normal" when f.SemanticIndex == 0 && f.Size == 3:
                    g.m_vNormalFieldInfo = info;
                    break;
                case "tangent" when f.SemanticIndex == 0 && f.Size == 4:
                    g.m_vTangentFieldInfo = info;
                    break;
                case "texcoord" when f.SemanticIndex is >= 0 and < 8:
                    g.m_vTexCoordFieldInfo.data[f.SemanticIndex] = info;
                    break;
                case "color" when f.SemanticIndex is >= 0 and < 8 && f.Size == 4:
                    g.m_vColorFieldInfo.data[f.SemanticIndex] = info;
                    break;
            }
        }
        return g;
    }

    // The material's _RTX_MATERIAL_DATA_ block: each field from its rtx variable (artist value, default or expression).
    private static byte[] MaterialBlock(RtxShaderLibrary.Program program, long combo, SettleWorld.MaterialInfo info,
                                        Func<string?, bool, bool, int> texture, Func<BindlessSampler, int> sampler)
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
        var writes = program.Rtx.GetStaticCombo(combo).AllVariables.Fields.Where(f => f.RegisterOffset >= 0).ToList();
        var size = writes.Count == 0 ? 16 : writes.Max(f => f.RegisterOffset * 4 + Size(vars[f.VariableIndex].VfxType));
        var bytes = new byte[(size + 15) & ~15];
        foreach (var w in writes)
        {
            var desc = vars[w.VariableIndex];
            var at = w.RegisterOffset * 4;
            if (desc.VfxType == VfxVariableType.Texture2DIndex)
            {
                info.Textures.TryGetValue(desc.Name, out var path);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), texture(path, desc.SrgbRead, desc.Name.Contains("Normal", StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (desc.VfxType == VfxVariableType.SamplerStateIndex)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), sampler(SamplerFor(desc.Name, Value)));
                continue;
            }
            Write(bytes, at, desc.VfxType, Value(desc.Name) ?? [0f]);
        }
        return bytes;
    }

    /// <summary>
    /// The sampler states by name (the named samplers' fixed states; the user
    /// ones from g_nTextureAddressModeU/V, 0 wrap, 1 mirror, 2 clamp...). Not
    /// read from the render state literals yet.
    /// </summary>
    private static BindlessSampler SamplerFor(string name, Func<string, string?, float[]?, float[]?> value)
    {
        int Mode(string v) => value(v, null, null) is { } m ? (int)m[0] : 0;
        return name switch
        {
            "g_sPointClamp" => new BindlessSampler(2, 2, false, false),
            "g_sBilinearClamp" => new BindlessSampler(2, 2, true, false),
            "g_sTrilinearClamp" => new BindlessSampler(2, 2, true, true),
            "g_sTrilinearWrap" or "g_sAniso" => new BindlessSampler(0, 0, true, true),
            _ => new BindlessSampler(Mode("g_nTextureAddressModeU"), Mode("g_nTextureAddressModeV"), true, true),
        };
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
        for (var i = 0; i < n && at + i * 4 + 4 <= bytes.Length; i++)
        {
            var c = i < v.Length ? v[i] : 0f;
            if (integer)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + i * 4), (int)c);
            else
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), c);
        }
    }

    /// <summary>Every mip of a texture as linear floats (8-bit sRGB through the sRGB curve when the shader reads it sRGB).</summary>
    public static BindlessTexture? LoadTexture(byte[]? bytes, bool srgb)
    {
        if (bytes is null)
            return null;
        using var res = new ValveResourceFormat.Resource();
        res.Read(new MemoryStream(bytes));
        if (res.DataBlock is not ValveResourceFormat.ResourceTypes.Texture tex)
            return null;
        var mips = new vec4[Math.Max(1, (int)tex.NumMipLevels)][];
        for (var m = 0; m < mips.Length; m++)
        {
            using var bmp = tex.GenerateBitmap(0, 0, (uint)m, ValveResourceFormat.TextureDecoders.TextureCodec.None);
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
        return new BindlessTexture(tex.Width, tex.Height, mips);
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
    private static mat4x3 Mat(Matrix4x4 m) => new(new vec3(m.M11, m.M12, m.M13), new vec3(m.M21, m.M22, m.M23), new vec3(m.M31, m.M32, m.M33), new vec3(m.M41, m.M42, m.M43));
}
