#pragma warning disable IDE1006, CS8981 // GLSL's lower-case names
namespace Source2.Compiler.Lighting.Bake.Glsl;

/// <summary>mat3: three vec3 columns (GLSL column-major).</summary>
public struct mat3
{
    public vec3 c0, c1, c2;
    public mat3(vec3 a, vec3 b, vec3 c) { c0 = a; c1 = b; c2 = c; }
    public vec3 this[int i]
    {
        readonly get => i switch { 0 => c0, 1 => c1, 2 => c2, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: c0 = value; break; case 1: c1 = value; break; case 2: c2 = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static vec3 operator *(mat3 m, vec3 v) => m.c0 * v.x + m.c1 * v.y + m.c2 * v.z;
    public static vec3 operator *(vec3 v, mat3 m) => new(GL.dot(v, m.c0), GL.dot(v, m.c1), GL.dot(v, m.c2));
    public static mat3 operator *(mat3 a, mat3 b) => new(a * b.c0, a * b.c1, a * b.c2);
    public static mat3 operator *(mat3 a, float s) => new(a.c0 * s, a.c1 * s, a.c2 * s);
}

/// <summary>mat4: four vec4 columns.</summary>
public struct mat4
{
    public vec4 c0, c1, c2, c3;
    public mat4(vec4 a, vec4 b, vec4 c, vec4 d) { c0 = a; c1 = b; c2 = c; c3 = d; }
    public vec4 this[int i]
    {
        readonly get => i switch { 0 => c0, 1 => c1, 2 => c2, 3 => c3, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: c0 = value; break; case 1: c1 = value; break; case 2: c2 = value; break; case 3: c3 = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static vec4 operator *(mat4 m, vec4 v) => m.c0 * v.x + m.c1 * v.y + m.c2 * v.z + m.c3 * v.w;
    public static vec4 operator *(vec4 v, mat4 m) => new(GL.dot(v, m.c0), GL.dot(v, m.c1), GL.dot(v, m.c2), GL.dot(v, m.c3));
    public static mat4 operator *(mat4 a, mat4 b) => new(a * b.c0, a * b.c1, a * b.c2, a * b.c3);
}

/// <summary>mat3x4: three columns of four rows.</summary>
public struct mat3x4
{
    public vec4 c0, c1, c2;
    public mat3x4(vec4 a, vec4 b, vec4 c) { c0 = a; c1 = b; c2 = c; }
    public vec4 this[int i]
    {
        readonly get => i switch { 0 => c0, 1 => c1, 2 => c2, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: c0 = value; break; case 1: c1 = value; break; case 2: c2 = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static vec4 operator *(mat3x4 m, vec3 v) => m.c0 * v.x + m.c1 * v.y + m.c2 * v.z;
    public static vec3 operator *(vec4 v, mat3x4 m) => new(GL.dot(v, m.c0), GL.dot(v, m.c1), GL.dot(v, m.c2));
}

/// <summary>mat4x3: four columns of three rows (gl_ObjectToWorldEXT).</summary>
public struct mat4x3
{
    public vec3 c0, c1, c2, c3;
    public mat4x3(vec3 a, vec3 b, vec3 c, vec3 d) { c0 = a; c1 = b; c2 = c; c3 = d; }
    public vec3 this[int i]
    {
        readonly get => i switch { 0 => c0, 1 => c1, 2 => c2, 3 => c3, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: c0 = value; break; case 1: c1 = value; break; case 2: c2 = value; break; case 3: c3 = value; break; default: throw new IndexOutOfRangeException(); } }
    }
    public static vec3 operator *(mat4x3 m, vec4 v) => m.c0 * v.x + m.c1 * v.y + m.c2 * v.z + m.c3 * v.w;
    public static vec4 operator *(vec3 v, mat4x3 m) => new(GL.dot(v, m.c0), GL.dot(v, m.c1), GL.dot(v, m.c2), GL.dot(v, m.c3));
}

/// <summary>
/// The GLSL built-in functions the transpiled shaders call, in single precision.
/// min, max and clamp prefer the operand that is not NaN (IEEE minNum and
/// maxNum, as GPUs implement them), so clamp(0 / 0, a, b) is a bound.
/// The GPU's transcendental and reciprocal-square-root approximations are not
/// modelled (MathF throughout).
/// </summary>
public static class GL
{
    // Conversions (GLSL's uint(x), int(x), float(x), bool(x)).
    public static uint uint_(float v) => v <= 0f ? 0u : v >= 4294967296f ? uint.MaxValue : (uint)v;
    public static uint uint_(int v) => unchecked((uint)v);
    public static uint uint_(uint v) => v;
    public static uint uint_(bool v) => v ? 1u : 0u;
    public static int int_(float v) => float.IsNaN(v) ? 0 : v >= 2147483648f ? int.MaxValue : v <= -2147483648f ? int.MinValue : (int)v;
    public static int int_(int v) => v;
    public static int int_(uint v) => unchecked((int)v);
    public static int int_(bool v) => v ? 1 : 0;
    public static float float_(float v) => v;
    public static float float_(int v) => v;
    public static float float_(uint v) => v;
    public static float float_(bool v) => v ? 1f : 0f;
    public static bool bool_(int v) => v != 0;
    public static bool bool_(uint v) => v != 0;
    public static bool bool_(float v) => v != 0f;
    public static bool bool_(bool v) => v;

    // Half precision: values stored in the payload's float16 fields round through Half.
    public static float f16(float v) => (float)(Half)v;
    public static float f16(int v) => (float)(Half)v;
    public static vec2 f16vec2(vec2 v) => new(f16(v.x), f16(v.y));
    public static vec3 f16vec3(vec3 v) => new(f16(v.x), f16(v.y), f16(v.z));
    public static vec3 f16vec3(float x, float y, float z) => new(f16(x), f16(y), f16(z));
    public static vec3 f16vec3(float s) => new(f16(s));
    public static vec3 f16vec3(ivec3 v) => new(f16(v.x), f16(v.y), f16(v.z));
    public static vec4 f16vec4(vec4 v) => new(f16(v.x), f16(v.y), f16(v.z), f16(v.w));
    public static vec4 f16vec4(ivec4 v) => new(f16(v.x), f16(v.y), f16(v.z), f16(v.w));
    public static vec4 f16vec4(float x, float y, float z, float w) => new(f16(x), f16(y), f16(z), f16(w));
    public static uvec2 u16vec2(uvec2 v) => new(v.x & 0xffff, v.y & 0xffff);
    public static uvec2 u16vec2(uint x, uint y) => new(x & 0xffff, y & 0xffff);
    public static vec2 uint16BitsToFloat16(uvec2 v) => new((float)BitConverter.UInt16BitsToHalf((ushort)v.x), (float)BitConverter.UInt16BitsToHalf((ushort)v.y));

    public static float exp(float v) => MathF.Exp(v);
    public static vec3 exp(vec3 v) => new(MathF.Exp(v.x), MathF.Exp(v.y), MathF.Exp(v.z));
    public static vec3 exp2(vec3 v) => new(MathF.Pow(2f, v.x), MathF.Pow(2f, v.y), MathF.Pow(2f, v.z));
    public static vec3 reflect(vec3 i, vec3 n) => i - n * (2f * dot(n, i));
    public static uvec3 min(uvec3 a, uvec3 b) => new(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
    public static uvec3 max(uvec3 a, uvec3 b) => new(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
    public static ivec4 mix(ivec4 a, ivec4 b, bvec4 t) => new(t.x ? b.x : a.x, t.y ? b.y : a.y, t.z ? b.z : a.z, t.w ? b.w : a.w);
    public static bvec4 notEqual(ivec4 a, ivec4 b) => new(a.x != b.x, a.y != b.y, a.z != b.z, a.w != b.w);
    public static bvec4 equal(ivec4 a, ivec4 b) => new(a.x == b.x, a.y == b.y, a.z == b.z, a.w == b.w);
    public static float atan(float y, float x) => MathF.Atan2(y, x);
    public static float atan(float v) => MathF.Atan(v);
    public static float acos(float v) => MathF.Acos(v);
    public static float asin(float v) => MathF.Asin(v);
    public static float fma(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);
    public static vec2 fma(vec2 a, vec2 b, vec2 c) => new(fma(a.x, b.x, c.x), fma(a.y, b.y, c.y));
    public static vec3 fma(vec3 a, vec3 b, vec3 c) => new(fma(a.x, b.x, c.x), fma(a.y, b.y, c.y), fma(a.z, b.z, c.z));
    public static int findLSB(uint v) => v == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(v);
    public static int findLSB(int v) => findLSB(unchecked((uint)v));
    public static bool isinf(float v) => float.IsInfinity(v);
    public static bool isnan(float v) => float.IsNaN(v);
    public static float uintBitsToFloat(uint v) => BitConverter.UInt32BitsToSingle(v);
    public static uint floatBitsToUint(float v) => BitConverter.SingleToUInt32Bits(v);
    public static float intBitsToFloat(int v) => BitConverter.Int32BitsToSingle(v);
    public static int floatBitsToInt(float v) => BitConverter.SingleToInt32Bits(v);

    public static float abs(float v) => MathF.Abs(v);
    public static vec2 abs(vec2 v) => new(MathF.Abs(v.x), MathF.Abs(v.y));
    public static vec3 abs(vec3 v) => new(MathF.Abs(v.x), MathF.Abs(v.y), MathF.Abs(v.z));
    public static vec4 abs(vec4 v) => new(MathF.Abs(v.x), MathF.Abs(v.y), MathF.Abs(v.z), MathF.Abs(v.w));
    public static int abs(int v) => Math.Abs(v);

    public static float sign(float v) => v > 0f ? 1f : v < 0f ? -1f : 0f;
    public static vec3 sign(vec3 v) => new(sign(v.x), sign(v.y), sign(v.z));
    public static float floor(float v) => MathF.Floor(v);
    public static vec2 floor(vec2 v) => new(MathF.Floor(v.x), MathF.Floor(v.y));
    public static vec3 floor(vec3 v) => new(MathF.Floor(v.x), MathF.Floor(v.y), MathF.Floor(v.z));
    public static float fract(float v) => v - MathF.Floor(v);
    public static vec2 fract(vec2 v) => v - floor(v);
    public static vec3 fract(vec3 v) => v - floor(v);
    public static float sqrt(float v) => MathF.Sqrt(v);
    public static float inversesqrt(float v) => 1f / MathF.Sqrt(v);
    public static float sin(float v) => MathF.Sin(v);
    public static float cos(float v) => MathF.Cos(v);
    public static float exp2(float v) => MathF.Pow(2f, v);
    public static float log2(float v) => MathF.Log2(v);
    public static float pow(float a, float b) => MathF.Pow(a, b);
    public static vec2 pow(vec2 a, vec2 b) => new(MathF.Pow(a.x, b.x), MathF.Pow(a.y, b.y));
    public static vec3 pow(vec3 a, vec3 b) => new(MathF.Pow(a.x, b.x), MathF.Pow(a.y, b.y), MathF.Pow(a.z, b.z));
    public static vec4 pow(vec4 a, vec4 b) => new(MathF.Pow(a.x, b.x), MathF.Pow(a.y, b.y), MathF.Pow(a.z, b.z), MathF.Pow(a.w, b.w));

    public static float min(float a, float b) => float.MinNumber(a, b);
    public static float max(float a, float b) => float.MaxNumber(a, b);
    public static int min(int a, int b) => Math.Min(a, b);
    public static int max(int a, int b) => Math.Max(a, b);
    public static uint min(uint a, uint b) => Math.Min(a, b);
    public static uint max(uint a, uint b) => Math.Max(a, b);
    public static vec2 min(vec2 a, vec2 b) => new(float.MinNumber(a.x, b.x), float.MinNumber(a.y, b.y));
    public static vec3 min(vec3 a, vec3 b) => new(float.MinNumber(a.x, b.x), float.MinNumber(a.y, b.y), float.MinNumber(a.z, b.z));
    public static vec4 min(vec4 a, vec4 b) => new(float.MinNumber(a.x, b.x), float.MinNumber(a.y, b.y), float.MinNumber(a.z, b.z), float.MinNumber(a.w, b.w));
    public static vec2 max(vec2 a, vec2 b) => new(float.MaxNumber(a.x, b.x), float.MaxNumber(a.y, b.y));
    public static vec3 max(vec3 a, vec3 b) => new(float.MaxNumber(a.x, b.x), float.MaxNumber(a.y, b.y), float.MaxNumber(a.z, b.z));
    public static vec4 max(vec4 a, vec4 b) => new(float.MaxNumber(a.x, b.x), float.MaxNumber(a.y, b.y), float.MaxNumber(a.z, b.z), float.MaxNumber(a.w, b.w));
    public static vec2 min(vec2 a, float b) => min(a, new vec2(b));
    public static vec3 min(vec3 a, float b) => min(a, new vec3(b));
    public static vec4 min(vec4 a, float b) => min(a, new vec4(b));
    public static vec2 max(vec2 a, float b) => max(a, new vec2(b));
    public static vec3 max(vec3 a, float b) => max(a, new vec3(b));
    public static vec4 max(vec4 a, float b) => max(a, new vec4(b));
    public static float clamp(float v, float lo, float hi) => float.MinNumber(float.MaxNumber(v, lo), hi);
    public static vec2 clamp(vec2 v, vec2 lo, vec2 hi) => min(max(v, lo), hi);
    public static vec3 clamp(vec3 v, vec3 lo, vec3 hi) => min(max(v, lo), hi);
    public static vec4 clamp(vec4 v, vec4 lo, vec4 hi) => min(max(v, lo), hi);
    public static vec3 clamp(vec3 v, float lo, float hi) => min(max(v, lo), hi);
    public static float saturate(float v) => clamp(v, 0f, 1f);
    public static vec2 saturate(vec2 v) => new(saturate(v.x), saturate(v.y));
    public static vec3 saturate(vec3 v) => new(saturate(v.x), saturate(v.y), saturate(v.z));
    public static vec4 saturate(vec4 v) => new(saturate(v.x), saturate(v.y), saturate(v.z), saturate(v.w));
    public static float step(float e, float v) => v < e ? 0f : 1f;
    public static float smoothstep(float e0, float e1, float v)
    {
        var t = saturate((v - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    public static vec2 smoothstep(vec2 e0, vec2 e1, vec2 v) => new(smoothstep(e0.x, e1.x, v.x), smoothstep(e0.y, e1.y, v.y));
    public static vec3 smoothstep(vec3 e0, vec3 e1, vec3 v) => new(smoothstep(e0.x, e1.x, v.x), smoothstep(e0.y, e1.y, v.y), smoothstep(e0.z, e1.z, v.z));

    public static float mix(float a, float b, float t) => a + (b - a) * t;
    public static vec2 mix(vec2 a, vec2 b, vec2 t) => a + (b - a) * t;
    public static vec3 mix(vec3 a, vec3 b, vec3 t) => a + (b - a) * t;
    public static vec4 mix(vec4 a, vec4 b, vec4 t) => a + (b - a) * t;
    public static vec2 mix(vec2 a, vec2 b, float t) => a + (b - a) * t;
    public static vec3 mix(vec3 a, vec3 b, float t) => a + (b - a) * t;
    public static vec4 mix(vec4 a, vec4 b, float t) => a + (b - a) * t;
    public static float mix(float a, float b, bool t) => t ? b : a;
    public static vec2 mix(vec2 a, vec2 b, bvec2 t) => new(t.x ? b.x : a.x, t.y ? b.y : a.y);
    public static vec3 mix(vec3 a, vec3 b, bvec3 t) => new(t.x ? b.x : a.x, t.y ? b.y : a.y, t.z ? b.z : a.z);
    public static vec4 mix(vec4 a, vec4 b, bvec4 t) => new(t.x ? b.x : a.x, t.y ? b.y : a.y, t.z ? b.z : a.z, t.w ? b.w : a.w);

    public static float dot(vec2 a, vec2 b) => a.x * b.x + a.y * b.y;
    public static float dot(vec3 a, vec3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    public static float dot(vec4 a, vec4 b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
    public static vec3 cross(vec3 a, vec3 b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    public static float length(vec2 v) => MathF.Sqrt(dot(v, v));
    public static float length(vec3 v) => MathF.Sqrt(dot(v, v));
    public static float length(vec4 v) => MathF.Sqrt(dot(v, v));
    public static float length(float v) => MathF.Abs(v);
    public static vec2 normalize(vec2 v) => v * (1f / length(v));
    public static vec3 normalize(vec3 v) => v * (1f / length(v));
    public static vec4 normalize(vec4 v) => v * (1f / length(v));

    public static mat3 transpose(mat3 m) => new(new(m.c0.x, m.c1.x, m.c2.x), new(m.c0.y, m.c1.y, m.c2.y), new(m.c0.z, m.c1.z, m.c2.z));
    public static mat4 transpose(mat4 m) => new(new(m.c0.x, m.c1.x, m.c2.x, m.c3.x), new(m.c0.y, m.c1.y, m.c2.y, m.c3.y), new(m.c0.z, m.c1.z, m.c2.z, m.c3.z), new(m.c0.w, m.c1.w, m.c2.w, m.c3.w));
    public static mat3x4 transpose(mat4x3 m) => new(new(m.c0.x, m.c1.x, m.c2.x, m.c3.x), new(m.c0.y, m.c1.y, m.c2.y, m.c3.y), new(m.c0.z, m.c1.z, m.c2.z, m.c3.z));
    public static mat4x3 transpose(mat3x4 m) => new(new(m.c0.x, m.c1.x, m.c2.x), new(m.c0.y, m.c1.y, m.c2.y), new(m.c0.z, m.c1.z, m.c2.z), new(m.c0.w, m.c1.w, m.c2.w));

    public static bool any(bvec2 v) => v.x || v.y;
    public static bool any(bvec3 v) => v.x || v.y || v.z;
    public static bool any(bvec4 v) => v.x || v.y || v.z || v.w;
    public static bool all(bvec2 v) => v.x && v.y;
    public static bool all(bvec3 v) => v.x && v.y && v.z;
    public static bool all(bvec4 v) => v.x && v.y && v.z && v.w;
    public static bvec2 greaterThanEqual(vec2 a, vec2 b) => new(a.x >= b.x, a.y >= b.y);
    public static bvec3 greaterThanEqual(vec3 a, vec3 b) => new(a.x >= b.x, a.y >= b.y, a.z >= b.z);
    public static bvec4 greaterThanEqual(vec4 a, vec4 b) => new(a.x >= b.x, a.y >= b.y, a.z >= b.z, a.w >= b.w);
    public static bvec2 greaterThan(vec2 a, vec2 b) => new(a.x > b.x, a.y > b.y);
    public static bvec3 greaterThan(vec3 a, vec3 b) => new(a.x > b.x, a.y > b.y, a.z > b.z);
    public static bvec4 greaterThan(vec4 a, vec4 b) => new(a.x > b.x, a.y > b.y, a.z > b.z, a.w > b.w);
    public static bvec2 lessThan(vec2 a, vec2 b) => new(a.x < b.x, a.y < b.y);
    public static bvec3 lessThan(vec3 a, vec3 b) => new(a.x < b.x, a.y < b.y, a.z < b.z);
    public static bvec2 lessThanEqual(vec2 a, vec2 b) => new(a.x <= b.x, a.y <= b.y);
    public static bvec3 lessThanEqual(vec3 a, vec3 b) => new(a.x <= b.x, a.y <= b.y, a.z <= b.z);
    public static bvec2 equal(vec2 a, vec2 b) => new(a.x == b.x, a.y == b.y);
    public static bvec3 equal(vec3 a, vec3 b) => new(a.x == b.x, a.y == b.y, a.z == b.z);
    public static bvec4 equal(vec4 a, vec4 b) => new(a.x == b.x, a.y == b.y, a.z == b.z, a.w == b.w);
    public static bvec2 equal(uvec2 a, uvec2 b) => new(a.x == b.x, a.y == b.y);
    public static bvec3 equal(uvec3 a, uvec3 b) => new(a.x == b.x, a.y == b.y, a.z == b.z);
    public static bvec2 notEqual(vec2 a, vec2 b) => new(a.x != b.x, a.y != b.y);
    public static bvec3 notEqual(vec3 a, vec3 b) => new(a.x != b.x, a.y != b.y, a.z != b.z);
    public static bvec4 notEqual(vec4 a, vec4 b) => new(a.x != b.x, a.y != b.y, a.z != b.z, a.w != b.w);
    public static bvec2 notEqual(uvec2 a, uvec2 b) => new(a.x != b.x, a.y != b.y);
    public static bvec3 notEqual(uvec3 a, uvec3 b) => new(a.x != b.x, a.y != b.y, a.z != b.z);
}
