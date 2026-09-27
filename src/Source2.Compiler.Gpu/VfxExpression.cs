using ValveResourceFormat.Utils;

namespace Source2.Compiler.Gpu;

/// <summary>
/// Evaluates a shader variable's compiled expression (the bytecode VRF's
/// VfxEval decompiles to text): a stack machine over float values of one to
/// four components (sixteen for a matrix). Operations are single precision,
/// component by component, a scalar spreading to the other operand's width.
/// Matrix builders return the identity and TextureAverageColor zero: in the
/// material sampler's programs they only reach colour adjustment, never the
/// layer weights.
/// </summary>
public static class VfxExpression
{
    /// <summary>A value: its components (1 to 4, or 16 for a matrix).</summary>
    public readonly record struct Value(float[] C)
    {
        public static Value Of(params float[] c) => new(c);
        public float this[int i] => C[Math.Min(i, C.Length - 1)];
        public int Width => C.Length;
    }

    /// <summary>Where names resolve: material parameters and render attributes, features by index.</summary>
    public interface IScope
    {
        Value? Parameter(string name);
        Value? Attribute(string name);
        int Feature(int index);
    }

    public static Value Evaluate(byte[] code, IScope scope)
    {
        var stack = new Stack<Value>();
        var locals = new Dictionary<byte, Value>();
        var at = 0;
        byte U8() => code[at++];
        ushort U16() { var v = BitConverter.ToUInt16(code, at); at += 2; return v; }
        uint U32() { var v = BitConverter.ToUInt32(code, at); at += 4; return v; }
        float F32() { var v = BitConverter.ToSingle(code, at); at += 4; return v; }
        string Token(uint t) => StringToken.InvertedTable.GetValueOrDefault(t, $"{t:x8}");
        for (var guard = 0; guard < 100000; guard++)
        {
            var op = U8();
            switch (op)
            {
                case 0x00: // RETURN
                    return stack.Pop();
                case 0x01: // NOP
                    break;
                case 0x02: // JUMP
                    at = U16();
                    break;
                case 0x04: // BRANCH
                {
                    var whenTrue = U16();
                    var whenFalse = U16();
                    at = stack.Pop()[0] != 0f ? whenTrue : whenFalse;
                    break;
                }
                case 0x06: // FUNC
                {
                    var id = U8();
                    U8();
                    stack.Push(Call(id, stack));
                    break;
                }
                case 0x07: // FLOAT
                    stack.Push(Value.Of(F32()));
                    break;
                case 0x08: // STORE
                    locals[U8()] = stack.Pop();
                    break;
                case 0x09: // LOAD
                    stack.Push(locals[U8()]);
                    break;
                case 0x0C: // NOT
                    stack.Push(Map(stack.Pop(), x => x == 0f ? 1f : 0f));
                    break;
                case 0x18: // NEGATE
                    stack.Push(Map(stack.Pop(), x => -x));
                    break;
                case >= 0x0D and <= 0x17:
                {
                    var b = stack.Pop();
                    var a = stack.Pop();
                    stack.Push(op switch
                    {
                        0x0D => Zip(a, b, (x, y) => x == y ? 1f : 0f),
                        0x0E => Zip(a, b, (x, y) => x != y ? 1f : 0f),
                        0x0F => Zip(a, b, (x, y) => x > y ? 1f : 0f),
                        0x10 => Zip(a, b, (x, y) => x >= y ? 1f : 0f),
                        0x11 => Zip(a, b, (x, y) => x < y ? 1f : 0f),
                        0x12 => Zip(a, b, (x, y) => x <= y ? 1f : 0f),
                        0x13 => Zip(a, b, (x, y) => x + y),
                        0x14 => Zip(a, b, (x, y) => x - y),
                        0x15 => Zip(a, b, (x, y) => x * y),
                        0x16 => Zip(a, b, (x, y) => x / y),
                        _ => Zip(a, b, (x, y) => x % y),
                    });
                    break;
                }
                case 0x19: // ATTRIBUTE
                {
                    var name = Token(U32());
                    stack.Push(scope.Attribute(name) ?? Value.Of(0f));
                    break;
                }
                case 0x1A: // FEATURE
                    stack.Push(Value.Of(scope.Feature(U8())));
                    break;
                case 0x1D: // MATERIAL_PARAM
                {
                    var name = Token(U32());
                    stack.Push(scope.Parameter(name) ?? Value.Of(0f));
                    break;
                }
                case 0x1E: // SWIZZLE
                {
                    var v = stack.Pop();
                    var s = U8();
                    stack.Push(Value.Of(v[s & 3], v[(s >> 2) & 3], v[(s >> 4) & 3], v[(s >> 6) & 3]));
                    break;
                }
                case 0x1F: // EXISTS
                {
                    var name = Token(U32());
                    stack.Push(Value.Of(scope.Attribute(name) != null ? 1f : 0f));
                    break;
                }
                case 0x22: // SYSTEM_VALUE
                    U8();
                    stack.Push(Value.Of(0f));
                    break;
                default:
                    throw new NotSupportedException($"expression opcode 0x{op:x2} at {at - 1}");
            }
        }
        throw new InvalidDataException("expression did not return");
    }

    private static Value Map(Value a, Func<float, float> f) => new([.. a.C.Select(f)]);

    private static Value Zip(Value a, Value b, Func<float, float, float> f)
    {
        var n = Math.Max(a.Width, b.Width);
        var c = new float[n];
        for (var i = 0; i < n; i++)
            c[i] = f(a.Width == 1 ? a[0] : a[i], b.Width == 1 ? b[0] : b[i]);
        return new Value(c);
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    // FUNCTION_REF in VfxEval: id, argument count.
    private static readonly int[] Arguments =
    [
        1, 1, 1, 1, 1, 1, 1, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 3, 4, 3, 2, 0, 2, 2, 1, 1, 2, 1, 1, 1, 1, 2, 1, 1, 1,
        0, 1, 1, 1, 2, 2, 1, 2, 1, 1, 1, 1, 1, 2, 3, 5, 5,
    ];

    private static Value Call(byte id, Stack<Value> stack)
    {
        var n = id < Arguments.Length ? Arguments[id] : throw new NotSupportedException($"expression function 0x{id:x2}");
        var a = new Value[n];
        for (var i = n - 1; i >= 0; i--)
            a[i] = stack.Pop();
        static float Srgb2Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        static float Lin2Srgb(float c) => c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
        float Dot(int k) { var s = 0f; for (var i = 0; i < k; i++) s += a[0][i] * a[1][i]; return s; }
        return id switch
        {
            0x00 => Map(a[0], MathF.Sin),
            0x01 => Map(a[0], MathF.Cos),
            0x02 => Map(a[0], MathF.Tan),
            0x03 => Map(a[0], x => x - MathF.Floor(x)),
            0x04 => Map(a[0], MathF.Floor),
            0x05 => Map(a[0], MathF.Ceiling),
            0x06 => Map(a[0], x => Math.Clamp(x, 0f, 1f)),
            0x07 => Zip(Zip(a[0], a[1], MathF.Max), a[2], MathF.Min),
            0x08 => Zip(a[0], Zip(Zip(a[1], a[0], (x, y) => x - y), a[2], (x, t) => x * t), (x, y) => x + y),
            0x09 => Value.Of(Dot(4)),
            0x0A => Value.Of(Dot(3)),
            0x0B => Value.Of(Dot(2)),
            0x0C => Map(a[0], MathF.Log),
            0x0D => Map(a[0], MathF.Log2),
            0x0E => Map(a[0], MathF.Log10),
            0x0F => Map(a[0], MathF.Exp),
            0x10 => Map(a[0], x => MathF.Pow(2f, x)),
            0x11 => Map(a[0], MathF.Sqrt),
            0x12 => Map(a[0], x => 1f / MathF.Sqrt(x)),
            0x13 => Map(a[0], x => MathF.Sign(x)),
            0x14 => Map(a[0], MathF.Abs),
            0x15 => Zip(a[0], a[1], MathF.Pow),
            0x16 => Zip(a[0], a[1], (e, x) => x >= e ? 1f : 0f),
            0x17 => Zip(Zip(a[0], a[1], (e0, e1) => e1 - e0), Zip(a[2], a[0], (x, e0) => x - e0), (d, x) => { var t = Math.Clamp(x / d, 0f, 1f); return t * t * (3f - (2f * t)); }),
            0x18 => Value.Of(a[0][0], a[1][0], a[2][0], a[3][0]),
            0x19 => Value.Of(a[0][0], a[1][0], a[2][0]),
            0x1A => Value.Of(a[0][0], a[1][0]),
            0x1B => Value.Of(0f),
            0x1C => Zip(a[0], a[1], MathF.Min),
            0x1D => Zip(a[0], a[1], MathF.Max),
            0x1E => Map(a[0], Lin2Srgb),
            0x1F => Map(a[0], Srgb2Lin),
            0x21 or 0x32 => Normalize(a[0]),
            0x22 => Value.Of(MathF.Sqrt(a[0].C.Sum(x => x * x))),
            0x23 => Map(a[0], x => x * x),
            0x34 => Map(a[0], x => x * (MathF.PI / 180f)),
            0x35 => Map(a[0], x => x * (180f / MathF.PI)),
            // TextureAverageColor: only an argument of the colour matrices below.
            0x28 => Value.Of(0f, 0f, 0f, 0f),
            0x29 or 0x2A or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x36 or 0x37 => new Value([.. Identity]),
            _ => throw new NotSupportedException($"expression function 0x{id:x2} not evaluated"),
        };
    }

    private static Value Normalize(Value v)
    {
        var l = MathF.Sqrt(v.C.Sum(x => x * x));
        return l == 0f ? v : Map(v, x => x / l);
    }
}
