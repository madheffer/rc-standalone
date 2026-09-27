namespace Source2.Compiler.Gpu;

/// <summary>
/// Just enough SPIR-V reflection to drive Valve's compiled programs from
/// Vulkan: each resource variable's descriptor set, binding and kind (with
/// uniform blocks' members by name and offset), and the entry point's inputs
/// by location. Parsed from the module's own decorations, names and types.
/// </summary>
public sealed class SpirvReflection
{
    /// <summary>What a descriptor binding holds.</summary>
    public enum Kind { UniformBuffer, StorageBuffer, SampledImage, Sampler, CombinedImageSampler, Other }

    /// <summary>A member of a uniform block: its name, byte offset, byte size, and type description.</summary>
    public sealed record Member(string Name, int Offset, int Size, string Type);

    /// <summary>
    /// A resource variable: set, binding, kind, array length (0 for a runtime
    /// array, 1 for none), the block's size and members for a buffer, the
    /// variable and type names, and the image dimension for an image.
    /// </summary>
    public sealed record Resource(int Set, int Binding, Kind Kind, int ArrayLength, int BlockSize, IReadOnlyList<Member> Members, string Name, string TypeName, string Dim);

    /// <summary>An entry point input: location, name, component type and count.</summary>
    public sealed record Input(int Location, string Name, string Type, int Components);

    public string EntryPoint { get; private set; } = "";
    public List<Resource> Resources { get; } = [];
    public List<Input> Inputs { get; } = [];
    public List<int> Capabilities { get; } = [];

    private readonly Dictionary<uint, string> _names = [];
    private readonly Dictionary<(uint, uint), string> _memberNames = [];
    private readonly Dictionary<uint, Dictionary<uint, uint>> _decorations = [];
    private readonly Dictionary<(uint, uint), Dictionary<uint, uint>> _memberDecorations = [];
    private readonly Dictionary<uint, uint[]> _types = []; // id -> instruction words (opcode first)
    private readonly Dictionary<uint, ulong> _constants = [];

    public static SpirvReflection Parse(byte[] code)
    {
        var words = new uint[code.Length / 4];
        Buffer.BlockCopy(code, 0, words, 0, words.Length * 4);
        if (words[0] != 0x07230203)
            throw new InvalidDataException("not SPIR-V");
        var r = new SpirvReflection();
        var variables = new List<(uint Type, uint Id, uint Storage)>();
        var interfaceIds = new HashSet<uint>();
        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            var op = words[i] & 0xffff;
            if (count == 0)
                break;
            var w = words.AsSpan(i, count);
            switch (op)
            {
                case 17: // OpCapability
                    r.Capabilities.Add((int)w[1]);
                    break;
                case 15: // OpEntryPoint
                {
                    var (name, used) = Str(w[3..]);
                    r.EntryPoint = name;
                    foreach (var id in w[(3 + used)..])
                        interfaceIds.Add(id);
                    break;
                }
                case 5: // OpName
                    r._names[w[1]] = Str(w[2..]).Text;
                    break;
                case 6: // OpMemberName
                    r._memberNames[(w[1], w[2])] = Str(w[3..]).Text;
                    break;
                case 71: // OpDecorate
                    if (!r._decorations.TryGetValue(w[1], out var d))
                        r._decorations[w[1]] = d = [];
                    d[w[2]] = w.Length > 3 ? w[3] : 1;
                    break;
                case 72: // OpMemberDecorate
                    if (!r._memberDecorations.TryGetValue((w[1], w[2]), out var md))
                        r._memberDecorations[(w[1], w[2])] = md = [];
                    md[w[3]] = w.Length > 4 ? w[4] : 1;
                    break;
                case 43: // OpConstant
                    r._constants[w[2]] = w.Length > 4 ? w[3] | ((ulong)w[4] << 32) : w[3];
                    break;
                case 59: // OpVariable
                    variables.Add((w[1], w[2], w[3]));
                    break;
                default:
                    if (op is >= 19 and <= 39 or 4472) // type declarations
                        r._types[w[1]] = w.ToArray();
                    break;
            }
            i += count;
        }
        foreach (var (typeId, id, storage) in variables)
        {
            var pointee = r._types[typeId][3];
            var name = r._names.GetValueOrDefault(id, $"_{id}");
            if (storage == 1 && interfaceIds.Contains(id)) // Input
            {
                if (r.Dec(id, 30) is { } location) // Location
                {
                    var (baseType, components) = r.Scalar(pointee);
                    r.Inputs.Add(new Input((int)location, name, baseType, components));
                }
                continue;
            }
            if (r.Dec(id, 34) is not { } set || r.Dec(id, 33) is not { } binding) // DescriptorSet, Binding
                continue;
            var (element, length) = r.Unarray(pointee);
            var t = r._types[element];
            var kind = Kind.Other;
            var members = new List<Member>();
            var size = 0;
            var dim = "";
            switch (t[0] & 0xffff)
            {
                case 30: // OpTypeStruct
                    kind = storage == 12 || r.Dec(element, 3) != null ? Kind.StorageBuffer : Kind.UniformBuffer; // StorageBuffer class, BufferBlock
                    for (uint m = 0; m < t.Length - 2; m++)
                    {
                        var offset = (int)(r.MemberDec(element, m, 35) ?? 0); // Offset
                        var msize = r.Size(t[2 + m], r.MemberDec(element, m, 6), r.MemberDec(element, m, 7));
                        members.Add(new Member(r._memberNames.GetValueOrDefault((element, m), $"_m{m}"), offset, msize, r.Describe(t[2 + m])));
                        size = Math.Max(size, offset + msize);
                    }
                    break;
                case 25: // OpTypeImage
                    kind = Kind.SampledImage;
                    dim = t[3] switch { 0 => "1D", 1 => "2D", 2 => "3D", 3 => "Cube", 5 => "Buffer", _ => $"dim{t[3]}" } + (t[5] == 1 ? "Array" : "");
                    break;
                case 26: // OpTypeSampler
                    kind = Kind.Sampler;
                    break;
                case 27: // OpTypeSampledImage
                    kind = Kind.CombinedImageSampler;
                    break;
            }
            r.Resources.Add(new Resource((int)set, (int)binding, kind, length, size, members, name, r._names.GetValueOrDefault(element, $"_{element}"), dim));
        }
        r.Resources.Sort((a, b) => a.Set != b.Set ? a.Set - b.Set : a.Binding - b.Binding);
        r.Inputs.Sort((a, b) => a.Location - b.Location);
        return r;
    }

    private uint? Dec(uint id, uint decoration)
        => _decorations.TryGetValue(id, out var d) && d.TryGetValue(decoration, out var v) ? v : null;

    private uint? MemberDec(uint id, uint member, uint decoration)
        => _memberDecorations.TryGetValue((id, member), out var d) && d.TryGetValue(decoration, out var v) ? v : null;

    // An array's element type and length (0 for a runtime array, 1 for no array).
    private (uint Element, int Length) Unarray(uint id)
    {
        var t = _types[id];
        return (t[0] & 0xffff) switch
        {
            28 => (t[2], (int)_constants.GetValueOrDefault(t[3])), // OpTypeArray
            29 => (t[2], 0), // OpTypeRuntimeArray
            _ => (id, 1),
        };
    }

    private (string Type, int Components) Scalar(uint id)
    {
        var t = _types[id];
        return (t[0] & 0xffff) switch
        {
            21 => (t[3] == 1 ? "int" : "uint", 1),
            22 => ("float", 1),
            23 => (Scalar(t[2]).Type, (int)t[3]),
            _ => ("?", 0),
        };
    }

    private string Describe(uint id)
    {
        var t = _types[id];
        return (t[0] & 0xffff) switch
        {
            21 or 22 or 23 => Scalar(id) is var (s, n) && n > 1 ? $"{s}{n}" : Scalar(id).Type,
            24 => $"mat{t[3]}x{Scalar(t[2]).Components}",
            28 => $"{Describe(t[2])}[{_constants.GetValueOrDefault(t[3])}]",
            29 => $"{Describe(t[2])}[]",
            30 => _names.GetValueOrDefault(id, $"struct{id}"),
            _ => $"op{t[0] & 0xffff}",
        };
    }

    // A member's byte size: scalars 4, vectors 4n, matrices columns * MatrixStride,
    // arrays length * ArrayStride, structs their last member's end.
    private int Size(uint id, uint? colMajor, uint? matrixStride)
    {
        var t = _types[id];
        switch (t[0] & 0xffff)
        {
            case 21:
            case 22:
                return (int)t[2] / 8;
            case 23:
                return (int)t[3] * Size(t[2], null, null);
            case 24:
                return (int)t[3] * (int)(matrixStride ?? 16);
            case 28:
                return (int)_constants.GetValueOrDefault(t[3]) * (int)(Dec(id, 6) ?? 16); // ArrayStride
            case 29:
                return 0;
            case 30:
            {
                var end = 0;
                for (uint m = 0; m < t.Length - 2; m++)
                    end = Math.Max(end, (int)(MemberDec(id, m, 35) ?? 0) + Size(t[2 + m], MemberDec(id, m, 6), MemberDec(id, m, 7)));
                return end;
            }
            default:
                return 0;
        }
    }

    private static (string Text, int Words) Str(ReadOnlySpan<uint> w)
    {
        var bytes = new List<byte>();
        var used = 0;
        foreach (var x in w)
        {
            used++;
            for (var k = 0; k < 4; k++)
            {
                var c = (byte)(x >> (k * 8));
                if (c == 0)
                    return (System.Text.Encoding.UTF8.GetString([.. bytes]), used);
                bytes.Add(c);
            }
        }
        return (System.Text.Encoding.UTF8.GetString([.. bytes]), used);
    }
}
