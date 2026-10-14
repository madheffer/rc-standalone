using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Source2.Compiler.Shaders;

/// <summary>
/// Evaluates a shader's compiled dynamic expression (a shader attribute's <c>m_expr</c>) against
/// a material: a stack machine over float4 values whose opcodes are the VFX expression set
/// (00 return, 02 jump, 04 branch, 07 float, 08/09 store/load, 0A..18 operators, 1A feature,
/// 1D material parameter by MurmurHash2 of its lowercased name, seed 0x31415926, 1E swizzle).
/// Opcodes and functions a compile has not needed yet are refused, not guessed.
/// </summary>
public static class ShaderExpression
{
    /// <summary>The seed of a material parameter's name hash.</summary>
    public const uint ParamHashSeed = 0x31415926;

    /// <summary>The hash a <c>1D</c> opcode names a material parameter by.</summary>
    public static uint ParamHash(string name) =>
        MurmurHash2.Hash32(Encoding.UTF8.GetBytes(name.ToLowerInvariant()), ParamHashSeed);

    /// <summary>Evaluate an expression.</summary>
    /// <param name="code">The bytecode.</param>
    /// <param name="feature">A feature combo's value by its index in the features program.</param>
    /// <param name="param">A material parameter's value by name hash.</param>
    /// <exception cref="InvalidDataException">The bytecode is malformed, loops without end or uses an opcode not measured.</exception>
    public static Vector4 Evaluate(ReadOnlySpan<byte> code, Func<int, int> feature, Func<uint, Vector4> param)
    {
        try
        {
            return Run(code, feature, param);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidDataException("A shader expression is malformed: " + ex.Message, ex);
        }
    }

    // Expressions are a few dozen opcodes; a jump that never lands is not one.
    private const int MaxSteps = 1 << 16;

    private static Vector4 Run(ReadOnlySpan<byte> code, Func<int, int> feature, Func<uint, Vector4> param)
    {
        var stack = new Stack<Vector4>();
        var locals = new Vector4[256];
        int pc = 0;
        for (var step = 0; ; step++)
        {
            if (step == MaxSteps)
                throw new InvalidDataException($"A shader expression ran {MaxSteps} steps without returning.");
            if (pc >= code.Length)
                throw new InvalidDataException("A shader expression ran past its end.");
            byte op = code[pc++];
            switch (op)
            {
                case 0x00:
                    return stack.Pop();
                case 0x01:
                    break;
                case 0x02:
                    pc = BinaryPrimitives.ReadUInt16LittleEndian(code[pc..]);
                    break;
                case 0x04:
                {
                    int whenTrue = BinaryPrimitives.ReadUInt16LittleEndian(code[pc..]);
                    int whenFalse = BinaryPrimitives.ReadUInt16LittleEndian(code[(pc + 2)..]);
                    pc = stack.Pop().X != 0 ? whenTrue : whenFalse;
                    break;
                }
                case 0x07:
                    stack.Push(new Vector4(BinaryPrimitives.ReadSingleLittleEndian(code[pc..])));
                    pc += 4;
                    break;
                case 0x08:
                    locals[code[pc++]] = stack.Pop();
                    break;
                case 0x09:
                    stack.Push(locals[code[pc++]]);
                    break;
                case 0x0C:
                    stack.Push(new Vector4(stack.Pop().X == 0 ? 1 : 0));
                    break;
                case 0x18:
                    stack.Push(-stack.Pop());
                    break;
                case >= 0x0A and <= 0x17:
                {
                    var b = stack.Pop();
                    var a = stack.Pop();
                    stack.Push(op switch
                    {
                        0x0A => Bool(a.X != 0 || b.X != 0),
                        0x0B => Bool(a.X != 0 && b.X != 0),
                        0x0D => Bool(a.X == b.X),
                        0x0E => Bool(a.X != b.X),
                        0x0F => Bool(a.X > b.X),
                        0x10 => Bool(a.X >= b.X),
                        0x11 => Bool(a.X < b.X),
                        0x12 => Bool(a.X <= b.X),
                        0x13 => a + b,
                        0x14 => a - b,
                        0x15 => a * b,
                        0x16 => a / b,
                        _ => throw new InvalidDataException($"Shader expression opcode {op:X2} is not measured."),
                    });
                    break;
                }
                case 0x1A:
                    stack.Push(new Vector4(feature(code[pc++])));
                    break;
                case 0x1D:
                    stack.Push(param(BinaryPrimitives.ReadUInt32LittleEndian(code[pc..])));
                    pc += 4;
                    break;
                case 0x1E:
                {
                    var v = stack.Pop();
                    byte s = code[pc++];
                    stack.Push(new Vector4(v[s & 3], v[(s >> 2) & 3], v[(s >> 4) & 3], v[(s >> 6) & 3]));
                    break;
                }
                default:
                    throw new InvalidDataException($"Shader expression opcode {op:X2} is not measured.");
            }
        }
    }

    private static Vector4 Bool(bool b) => new(b ? 1 : 0);
}
