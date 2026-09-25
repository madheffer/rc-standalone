using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The light record's builders (<see cref="LightMath"/>, <see cref="LightBuild"/>)
/// against resourcecompiler.dll's, called in this process on random inputs.
/// </summary>
public sealed unsafe class LightBuildOracleTests(ITestOutputHelper output)
{
    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static string Hex(ReadOnlySpan<float> v) => string.Join(' ', v.ToArray().Select(f => Bits(f).ToString("x8")));

    private static void Same(ReadOnlySpan<float> valve, ReadOnlySpan<float> ours, string what)
    {
        if (!valve.SequenceEqual(ours) && Hex(valve) != Hex(ours))
            Assert.Fail($"{what}: valve {Hex(valve)} ours {Hex(ours)}");
    }

    private static float[] RandomMatrix(Random r, int n, double k) => Enumerable.Range(0, n).Select(_ => F(r, -k, k)).ToArray();

    private static Quaternion RandomQuat(Random r) =>
        Quaternion.Normalize(new Quaternion(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)));

    /// <summary>A rigid 3x4 as a map entity's transform might be.</summary>
    private static float[] RandomRigid(Random r)
    {
        var m = LightMath.QuatMatrix34(RandomQuat(r), new Vector3(F(r, -8000, 8000), F(r, -8000, 8000), F(r, -3000, 15000)));
        return m;
    }

    [Fact]
    public void TheMatrixRoutinesMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var mul = (delegate* unmanaged<float*, float*, float*, void>)ResourceCompilerOracle.At(module, 0x181263eb0);
        var inv = (delegate* unmanaged<float*, float*, byte>)ResourceCompilerOracle.At(module, 0x1812636e0);
        var concat = (delegate* unmanaged<float*, float*, float*, void>)ResourceCompilerOracle.At(module, 0x181258890);
        var invRigid = (delegate* unmanaged<float*, float*, void>)ResourceCompilerOracle.At(module, 0x18125b130);
        var quatMatrix = (delegate* unmanaged<float*, float*, float*, void>)ResourceCompilerOracle.At(module, 0x18125f4e0);
        var normalize = (delegate* unmanaged<float*, float>)ResourceCompilerOracle.At(module, 0x18013e340);
        var forward = (delegate* unmanaged<float*, float*, float*>)ResourceCompilerOracle.At(module, 0x18125dcc0);
        var left = (delegate* unmanaged<float*, float*, float*>)ResourceCompilerOracle.At(module, 0x18125dd50);
        var up = (delegate* unmanaged<float*, float*, float*>)ResourceCompilerOracle.At(module, 0x18125dde0);
        var random = new Random(101);
        var a = (float*)NativeMemory.AlignedAlloc(256, 16);
        var b = (float*)NativeMemory.AlignedAlloc(256, 16);
        var o = (float*)NativeMemory.AlignedAlloc(256, 16);
        for (var i = 0; i < 20000; i++)
        {
            var ma = RandomMatrix(random, 16, random.Next(2) == 0 ? 2 : 500);
            var mb = RandomMatrix(random, 16, random.Next(2) == 0 ? 2 : 500);
            ma.CopyTo(new Span<float>(a, 16));
            mb.CopyTo(new Span<float>(b, 16));
            mul(a, b, o);
            Same(new ReadOnlySpan<float>(o, 16), LightMath.Mul4(ma, mb), $"Mul4 {i}");
            NativeMemory.Clear(o, 64);
            var ok = inv(a, o) != 0;
            var ours = new float[16];
            var ourOk = LightMath.Inverse4(ma, ours);
            Assert.Equal(ok, ourOk);
            if (ok)
                Same(new ReadOnlySpan<float>(o, 16), ours, $"Inverse4 {i}");
            concat(a, b, o);
            Same(new ReadOnlySpan<float>(o, 12), LightMath.Concat34(ma.AsSpan(0, 12), mb.AsSpan(0, 12)), $"Concat34 {i}");
            var rigid = RandomRigid(random);
            rigid.CopyTo(new Span<float>(a, 12));
            invRigid(a, o);
            Same(new ReadOnlySpan<float>(o, 12), LightMath.InvertRigid34(rigid), $"InvertRigid34 {i}");
            var q = RandomQuat(random);
            var t = new Vector3(F(random, -100, 100), F(random, -100, 100), F(random, -100, 100));
            a[0] = q.X; a[1] = q.Y; a[2] = q.Z; a[3] = q.W;
            b[0] = t.X; b[1] = t.Y; b[2] = t.Z;
            quatMatrix(a, b, o);
            Same(new ReadOnlySpan<float>(o, 12), LightMath.QuatMatrix34(q, t), $"QuatMatrix34 {i}");
            forward(a, o);
            var f = LightMath.Forward(q);
            Same(new ReadOnlySpan<float>(o, 3), [f.X, f.Y, f.Z], $"Forward {i}");
            left(a, o);
            f = LightMath.Left(q);
            Same(new ReadOnlySpan<float>(o, 3), [f.X, f.Y, f.Z], $"Left {i}");
            up(a, o);
            f = LightMath.Up(q);
            Same(new ReadOnlySpan<float>(o, 3), [f.X, f.Y, f.Z], $"Up {i}");
            var v = new Vector3(F(random, -1000, 1000), F(random, -1000, 1000), F(random, -1000, 1000));
            a[0] = v.X; a[1] = v.Y; a[2] = v.Z;
            var length = normalize(a);
            var ourLength = LightMath.Normalize(ref v);
            Same([a[0], a[1], a[2], length], [v.X, v.Y, v.Z, ourLength], $"Normalize {i}");
        }
        output.WriteLine("20000 trials of each");
    }

    [Fact]
    public void TheBarnPiecesMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var barnMatrix = (delegate* unmanaged<float*, float*, float, float*, float*>)ResourceCompilerOracle.At(module, 0x18129a760);
        var barnPosition = (delegate* unmanaged<float*, float*, float, float*, float*>)ResourceCompilerOracle.At(module, 0x181297ed0);
        var plane = (delegate* unmanaged<float*, float*, float*>)ResourceCompilerOracle.At(module, 0x181292ce0);
        var shape = (delegate* unmanaged<float, float>)ResourceCompilerOracle.At(module, 0x18129a5b0);
        var skirt = (delegate* unmanaged<float*, float, float, float*, float, float*>)ResourceCompilerOracle.At(module, 0x18129a5f0);
        var luminaire = (delegate* unmanaged<float*, float, float, float, float*>)ResourceCompilerOracle.At(module, 0x181298050);
        var tan = (delegate* unmanaged<float, float>)ResourceCompilerOracle.Tier0("V_tanf");
        var random = new Random(102);
        var o = (float*)NativeMemory.AlignedAlloc(256, 16);
        var size = (float*)NativeMemory.AlignedAlloc(64, 16);
        var shear = (float*)NativeMemory.AlignedAlloc(64, 16);
        for (var i = 0; i < 20000; i++)
        {
            var s = new Vector3(F(random, 1, 300), F(random, 1, 300), random.Next(3) == 0 ? 0f : F(random, 0, 50));
            var sh = random.Next(3) == 0 ? Vector2.Zero : new Vector2(F(random, -50, 50), F(random, -50, 50));
            var range = F(random, 1, 3000);
            size[0] = s.X; size[1] = s.Y; size[2] = s.Z;
            shear[0] = sh.X; shear[1] = sh.Y;
            barnMatrix(o, size, range, shear);
            var m = LightBuild.BarnMatrix(s, range, sh);
            Same(new ReadOnlySpan<float>(o, 16), m, $"BarnMatrix {i}");
            barnPosition(o, size, range, shear);
            var p = LightBuild.BarnPosition(s, range, sh);
            Same(new ReadOnlySpan<float>(o, 4), [p.X, p.Y, p.Z, p.W], $"BarnPosition {i}");
            var inverse = new float[16];
            if (LightMath.Inverse4(m, inverse))
            {
                fixed (float* pi = inverse)
                    plane(o, pi);
                var pl = LightBuild.Plane(inverse);
                Same(new ReadOnlySpan<float>(o, 4), [pl.X, pl.Y, pl.Z, pl.W], $"Plane {i}");
            }
            var k = F(random, -0.5, 1.5);
            Assert.Equal(Bits(shape(k)), Bits(LightBuild.ShapeCurve(k)));
            float near = F(random, -0.5, 1.5), far = F(random, -0.5, 1.5);
            skirt(o, near, far, size, range);
            var sk = LightBuild.Skirt(near, far, s.Z, range);
            Same(new ReadOnlySpan<float>(o, 2), [sk.X, sk.Y], $"Skirt {i}");
            float lsize = F(random, -10, 120), aniso = F(random, -2, 2), w = random.Next(3) == 0 ? 0f : F(random, 0, 1e6);
            luminaire(o, lsize, aniso, w);
            var ls = LightBuild.LuminaireSize(lsize, aniso, w);
            Same(new ReadOnlySpan<float>(o, 2), [ls.X, ls.Y], $"LuminaireSize {i}");
        }
        for (var i = 0; i < 1000000; i++)
        {
            var x = F(random, -1.6, 1.6);
            Assert.True(Bits(tan(x)) == Bits(LightBuild.Tan(x)), $"tan({x:R}): valve {tan(x):R} ours {LightBuild.Tan(x):R}");
        }
        output.WriteLine("20000 trials of each, 1000000 tangents");
    }

    /// <summary>A random light record, filled the way the builders leave one.</summary>
    private static LightShape RandomLight(Random r, byte type)
    {
        var l = new LightShape();
        for (var k = 0; k < 74; k++)
            l.F[k] = F(r, -200, 200);
        l.Orientation = RandomQuat(r);
        l[0x9c] = r.Next(3) == 0 ? 0f : F(r, 0.01, 10);
        l.Type = type;
        l.Flags = (byte)r.Next(4);
        return l;
    }

    [Fact]
    public void TheLuminairesAndTheTransformMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var rect = (delegate* unmanaged<byte*, float, float, void>)ResourceCompilerOracle.At(module, 0x181296340);
        var disc = (delegate* unmanaged<byte*, float, float, void>)ResourceCompilerOracle.At(module, 0x181296170);
        var transform = (delegate* unmanaged<byte*, byte*, float*, byte*>)ResourceCompilerOracle.At(module, 0x18129a8d0);
        var copy = (delegate* unmanaged<byte*, byte*, byte*>)ResourceCompilerOracle.At(module, 0x180ee9e30);
        var random = new Random(103);
        var native = (byte*)NativeMemory.AlignedAlloc(512, 16);
        var scratch = (byte*)NativeMemory.AlignedAlloc(512, 16);
        var matrix = (float*)NativeMemory.AlignedAlloc(64, 16);
        for (var i = 0; i < 20000; i++)
        {
            var l = RandomLight(random, (byte)random.Next(6));
            float a = F(random, 0.1, 100), b = F(random, 0.1, 100);
            l.Raw.CopyTo(new Span<byte>(native, LightShape.Size));
            var ours = l.Clone();
            if (i % 2 == 0)
            {
                rect(native, a, b);
                LightBuild.RectLuminaire(ours, a, b);
            }
            else
            {
                disc(native, a, b);
                LightBuild.DiscLuminaire(ours, a, b);
            }
            Compare(native, ours, $"luminaire {i}");
            var m = RandomRigid(random);
            m.CopyTo(new Span<float>(matrix, 12));
            l = RandomLight(random, (byte)random.Next(6));
            l.Raw.CopyTo(new Span<byte>(native, LightShape.Size));
            ours = l.Clone();
            NativeMemory.Clear(scratch, 512);
            transform(native, scratch, matrix);
            copy(native, scratch);
            LightBuild.Transform(ours, m);
            Compare(native, ours, $"transform {i} type {l.Type}");
        }
        output.WriteLine("20000 trials of each");
    }

    private static void Compare(byte* native, LightShape ours, string what)
    {
        var valve = new ReadOnlySpan<byte>(native, LightShape.Size);
        for (var k = 0; k < LightShape.Size; k += 4)
            if (!valve.Slice(k, 4).SequenceEqual(ours.Raw.AsSpan(k, 4)))
                Assert.Fail($"{what}: +0x{k:x} valve {BitConverter.ToSingle(valve.Slice(k, 4)):R} ours {BitConverter.ToSingle(ours.Raw, k):R}\nvalve {Convert.ToHexString(valve)}\nours  {Convert.ToHexString(ours.Raw)}");
    }

    /// <summary>A barn record with random keys, sometimes with a luminaire, moved by a random rigid transform.</summary>
    internal static LightShape RandomBarn(Random r)
    {
        var l = LightBuild.Barn(
            new Vector3(F(r, 0, 200), F(r, 0, 200), r.Next(3) == 0 ? 0f : F(r, 0, 20)),
            F(r, 10, 2000), r.Next(2) == 0 ? Vector2.Zero : new Vector2(F(r, -20, 20), F(r, -20, 20)),
            r.Next(2) == 0 ? 0f : F(r, 0, 1), r.Next(2) == 0 ? 0f : F(r, 0, 1), F(r, 0, 1), F(r, 0, 1), F(r, -0.2, 1.2),
            r.Next(3), F(r, 1, 80), F(r, -1, 1));
        LightBuild.Transform(l, RandomRigid(r));
        return l;
    }

    [Fact]
    public void TheSamplerMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var sample = (delegate* unmanaged<byte*, float*, float*, float*, float>)ResourceCompilerOracle.At(module, 0x181294fc0);
        var random = new Random(104);
        var native = (byte*)NativeMemory.AlignedAlloc(512, 16);
        var start = (float*)NativeMemory.AlignedAlloc(64, 16);
        var dir = (float*)NativeMemory.AlignedAlloc(64, 16);
        var h = (float*)NativeMemory.AlignedAlloc(64, 16);
        var counts = new int[8];
        for (var i = 0; i < 100000; i++)
        {
            var l = RandomBarn(random);
            var kind = random.Next(4);
            if (kind == 1)
                LightBuild.SphereLuminaire(l, random.Next(2) == 0 ? 1e-6f : F(random, 0.1, 30));
            if (kind >= 2)
                l.Flags |= 1;
            if (kind == 3)
            {
                l.Type = 0;
                l[0xe0] = F(random, -1, 3);
                l[0xe4] = random.Next(3) == 0 ? 0f : F(random, -3, 3);
                l[0xc8] = F(random, 0, 2);
                l[0xcc] = random.Next(3) == 0 ? 0f : F(random, -0.01, 0.01);
            }
            l.Raw.CopyTo(new Span<byte>(native, LightShape.Size));
            for (var k = 0; k < 4; k++)
                h[k] = (float)random.NextDouble();
            NativeMemory.Clear(start, 16);
            NativeMemory.Clear(dir, 16);
            var t = sample(native, start, dir, h);
            var ours = LightSampler.Sample(l, new ReadOnlySpan<float>(h, 4), out var s, out var d);
            counts[(l.Type == 0 ? 0 : 4) + ((l.Flags & 1) != 0 ? 2 : 0) + (t > 0f ? 1 : 0)]++;
            Same([t, start[0], start[1], start[2], dir[0], dir[1], dir[2]], [ours, s.X, s.Y, s.Z, d.X, d.Y, d.Z],
                 $"sample {i} type {l.Type} flags {l.Flags}");
        }
        output.WriteLine($"cases (luminaire, directional, reached): {string.Join(' ', counts)}");
    }

    /// <summary>tier0's V_atofloat32, which reads a light's float keys, against float.Parse.</summary>
    [Fact]
    public void TheFloatKeysParseAlike()
    {
        if (ResourceCompilerOracle.Load() is null)
            return;
        var atof = (delegate* unmanaged<byte*, float>)ResourceCompilerOracle.Tier0("V_atofloat32");
        var random = new Random(9);
        var buffer = stackalloc byte[64];
        for (var i = 0; i < 300000; i++)
        {
            var d = (random.NextDouble() - 0.3) * Math.Pow(10, random.Next(-3, 6));
            var text = (i % 3) switch
            {
                0 => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                1 => d.ToString("F" + random.Next(0, 9), System.Globalization.CultureInfo.InvariantCulture),
                _ => ((float)d).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            };
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            bytes.CopyTo(new Span<byte>(buffer, 64));
            buffer[bytes.Length] = 0;
            Assert.True(Bits(atof(buffer)) == Bits(float.Parse(text, System.Globalization.CultureInfo.InvariantCulture)), text);
        }
    }

    /// <summary>FUN_181260200 (quaternion to angles) and the "%f %f %f" the key writers print, on random and axis-aligned boxes.</summary>
    [Fact]
    public void TheBoxAnglesMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var angles = (delegate* unmanaged<float*, float*, float*>)ResourceCompilerOracle.At(module, 0x181260200);
        var q = stackalloc float[4];
        var a = stackalloc float[4];
        var random = new Random(5);
        for (var i = 0; i < 200000; i++)
        {
            var r = i % 2 == 0 ? RandomQuat(random)
                : CTransform.AngleQuaternion(new Vector3(random.Next(-9, 10) * 5f, random.Next(-9, 10) * 5f, random.Next(-9, 10) * 5f));
            q[0] = r.X; q[1] = r.Y; q[2] = r.Z; q[3] = r.W;
            angles(q, a);
            var valve = string.Join(' ', Enumerable.Range(0, 3).Select(k => ((double)a[k]).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal(valve, LightPrecompute.Angles(r));
        }
    }

    /// <summary>FUN_181298170, whole and split, on random omni settings.</summary>
    [Fact]
    public void TheOmniFrustaMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var frusta = (delegate* unmanaged<byte*, float*, byte, float, float, float, float, float, float, uint>)
            ResourceCompilerOracle.At(module, 0x181298170);
        var native = (byte*)NativeMemory.AlignedAlloc(6 * 300, 16);
        var colour = stackalloc float[3];
        var random = new Random(11);
        var counts = new int[7];
        for (var i = 0; i < 40000; i++)
        {
            var directional = i % 2 == 1;
            var range = i % 7 == 0 ? F(random, 1, 60000) : F(random, 1, 2000);
            var outer = (i / 2) % 5 switch { 0 => 180f, 1 => F(random, 0, 45), 2 => F(random, 45, 135), 3 => F(random, 135, 181), _ => F(random, -5, 200) };
            var inner = (i / 10) % 3 switch { 0 => 180f, 1 => F(random, 0, outer), _ => F(random, -5, 200) };
            var length = i % 3 == 0 ? F(random, 0, 64) : 0f;
            var skirt = F(random, -0.2, 1.2);
            var p9 = i % 11 == 0 ? F(random, 0.5, 4) : 1f;
            colour[0] = F(random, 0, 10); colour[1] = F(random, 0, 10); colour[2] = F(random, 0, 10);
            NativeMemory.Clear(native, 6 * 300);
            var n = (int)frusta(native, colour, (byte)(directional ? 1 : 0), range, outer, inner, length, skirt, p9);
            var ours = LightOmni.Frusta(directional, range, outer, inner, length, skirt, p9, new Vector3(colour[0], colour[1], colour[2]));
            Assert.Equal(n, ours.Length);
            counts[n]++;
            for (var k = 0; k < n; k++)
            {
                var valve = new ReadOnlySpan<byte>(native + k * 300, 300);
                if (!valve.SequenceEqual(ours[k].Raw))
                {
                    var at = 0;
                    while (valve[at] == ours[k].Raw[at])
                        at++;
                    at &= ~3;
                    Assert.Fail($"case {i} record {k}/{n}: first difference at 0x{at:x}: valve {BitConverter.ToUInt32(valve[at..]):x8} ours {BitConverter.ToUInt32(ours[k].Raw, at):x8}"
                                + $" (range {range}, outer {outer}, inner {inner}, length {length}, skirt {skirt}, p9 {p9})");
                }
            }
        }
        output.WriteLine($"records per call (1..6): {string.Join(' ', counts[1..])}");
    }

    /// <summary>tier0's cosf, sincosf and atan2f against LightCrt's.</summary>
    [Fact]
    public void TheTrigMatches()
    {
        if (ResourceCompilerOracle.Load() is null)
            return;
        var cos = (delegate* unmanaged<float, float>)ResourceCompilerOracle.Tier0("V_cosf");
        var sincos = (delegate* unmanaged<float, float*, float*, void>)ResourceCompilerOracle.Tier0("V_sincosf");
        var atan2 = (delegate* unmanaged<float, float, float>)ResourceCompilerOracle.Tier0("V_atan2f");
        var random = new Random(3);
        float s, c;
        var bad = new int[4];
        for (var i = 0; i < 2000000; i++)
        {
            var x = i % 2 == 0 ? F(random, -7, 7) : F(random, 0, 180) * 0.017453292f;
            if (Bits(cos(x)) != Bits(LightCrt.Cos(x)))
                bad[0]++;
            sincos(x, &s, &c);
            LightCrt.SinCos(x, out var s2, out var c2);
            if (Bits(s) != Bits(s2))
                bad[1]++;
            if (Bits(c) != Bits(c2))
                bad[2]++;
            var y = F(random, -2, 2);
            if (Bits(atan2(y, x)) != Bits(LightCrt.Atan2(y, x)))
                bad[3]++;
        }
        output.WriteLine($"cos, sin, cos (sincos), atan2 mismatches: {string.Join(' ', bad)}");
        Assert.Equal([0, 0, 0, 0], bad);
    }

    /// <summary>
    /// The keep test's pieces: tier0's powf, the Halton numbers, the clip
    /// projection, the falloff and the barn shape, at the middles of rays the
    /// sampler makes on random barn lights.
    /// </summary>
    [Fact]
    public void TheKeepTestPiecesMatch()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var pow = (delegate* unmanaged<float, float, float>)ResourceCompilerOracle.Tier0("V_powf");
        var halton = (delegate* unmanaged<int*, int, float>)ResourceCompilerOracle.At(module, 0x18125d8e0);
        var project = (delegate* unmanaged<byte*, float*, float*, float*>)ResourceCompilerOracle.At(module, 0x180251040);
        var falloff = (delegate* unmanaged<byte*, float*, float*, float*, byte, float>)ResourceCompilerOracle.At(module, 0x1812933f0);
        var shape = (delegate* unmanaged<byte*, float*, float>)ResourceCompilerOracle.At(module, 0x181293b70);
        var random = new Random(77);
        for (var i = 0; i < 200000; i++)
        {
            var x = i % 2 == 0 ? F(random, 0, 4) : F(random, 0, 1e-3);
            var y = F(random, -40, 40);
            Same([pow(x, y)], [LightPow.Pow(x, y)], $"powf({Bits(x):x8}, {Bits(y):x8})");
        }
        var hs = stackalloc int[3];
        foreach (var radix in new[] { 2, 3, 5, 7 })
        {
            hs[1] = radix;
            ((float*)hs)[2] = radix;
            for (var n = 0; n < 100000; n++)
                Same([halton(hs, n)], [LightTrace.Halton(radix, n)], $"halton {radix} {n}");
        }
        var native = (byte*)NativeMemory.AlignedAlloc(512, 16);
        var nativeFalloff = (byte*)NativeMemory.AlignedAlloc(512, 16);
        var a = stackalloc float[16];
        var b = stackalloc float[16];
        var o = stackalloc float[16];
        Span<float> h = stackalloc float[4];
        var cases = new int[4];
        for (var i = 0; i < 20000; i++)
        {
            var l = RandomBarn(random);
            if (i % 4 == 1)
                LightBuild.SphereLuminaire(l, 1e-6f);
            var f = l.Clone();
            f[0xac] = 0f;
            f[0xb0] = 0f;
            l.Raw.CopyTo(new Span<byte>(native, LightShape.Size));
            f.Raw.CopyTo(new Span<byte>(nativeFalloff, LightShape.Size));
            for (var k = 0; k < 8; k++)
            {
                for (var j = 0; j < 4; j++)
                    h[j] = (float)random.NextDouble();
                var t = LightSampler.Sample(l, h, out var s, out var d);
                if (!(t > 0f))
                    continue;
                var e = new Vector3(t * d.X + s.X, t * d.Y + s.Y, t * d.Z + s.Z);
                var mid = new Vector3((e.X + s.X) * 0.5f, (s.Y + e.Y) * 0.5f, (s.Z + e.Z) * 0.5f);
                a[0] = mid.X; a[1] = mid.Y; a[2] = mid.Z;
                project(native, b, a);
                var clip = LightTrace.Project(l, mid);
                Same(new ReadOnlySpan<float>(b, 3), [clip.X, clip.Y, clip.Z], $"project {i}");
                var fv = falloff(nativeFalloff, o, b, a, 0);
                var fo = LightTrace.Falloff(f, clip, mid);
                Same([fv], [fo], $"falloff {i} type {l.Type}");
                var sv = shape(native, b);
                Same([sv], [LightTrace.Shape(l, clip)], $"shape {i} (shape {l[0xa8]})");
                cases[(fv > 0f ? 2 : 0) + (sv > 0f ? 1 : 0)]++;
            }
        }
        output.WriteLine($"falloff/shape zero/positive cases: {string.Join(' ', cases)}");
    }

    /// <summary>
    /// LightPrecompute_TraceSamples on a fake world whose ray trace scene
    /// (world +0x2bd0) is freshly constructed and empty: sampling, falloff,
    /// shape, bounds and the oriented box, against the port with an empty scene.
    /// </summary>
    [Fact]
    public void TheEmptySceneTraceMatches()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var sceneCtor = (delegate* unmanaged<byte*, byte*>)ResourceCompilerOracle.At(module, 0x1801f6f40);
        var traceSamples = (delegate* unmanaged<uint, byte*, byte*, byte*, float*, float*, float*, byte*, void>)
            ResourceCompilerOracle.At(module, 0x180f19840);
        var world = (byte*)NativeMemory.AlignedAlloc(0x4000, 16);
        NativeMemory.Clear(world, 0x4000);
        sceneCtor(world + 0x2bd0);
        var entity = (byte*)NativeMemory.AlignedAlloc(0x1000, 16);
        NativeMemory.Clear(entity, 0x1000);
        var functor = (byte*)NativeMemory.AlignedAlloc(0x100, 16);
        NativeMemory.Clear(functor, 0x100);
        var native = (byte*)NativeMemory.AlignedAlloc(512, 16);
        var bounds = (float*)NativeMemory.AlignedAlloc(64, 16);
        var obb = (float*)NativeMemory.AlignedAlloc(64, 16);
        var range = (float*)NativeMemory.AlignedAlloc(64, 16);
        var random = new Random(105);
        const int trials = 16;
        for (var i = 0; i < trials; i++)
        {
            var l = RandomBarn(random);
            if (i % 4 == 1)
                LightBuild.SphereLuminaire(l, 1e-6f);
            l.Raw.CopyTo(new Span<byte>(native, LightShape.Size));
            for (var k = 0; k < 3; k++)
            {
                bounds[k] = float.MaxValue;
                bounds[4 + k] = -float.MaxValue;
            }
            bounds[3] = bounds[7] = 0f;
            NativeMemory.Clear(obb, 64);
            NativeMemory.Clear(range, 64);
            var count = i % 3 == 0 ? 0x6000 : 1 + random.Next(3000);
            traceSamples((uint)count, world, entity, native, bounds, obb, range, functor);
            var ours = LightTrace.Run(l, count, EmptyLightScene.Instance);
            var b = ours.Box;
            Same([bounds[0], bounds[1], bounds[2], bounds[4], bounds[5], bounds[6]],
                 [ours.Mins.X, ours.Mins.Y, ours.Mins.Z, ours.Maxs.X, ours.Maxs.Y, ours.Maxs.Z], $"bounds {i} ({count} rays, type {l.Type})");
            Same(new ReadOnlySpan<float>(obb, 10),
                 [b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W, b.Origin.X, b.Origin.Y, b.Origin.Z, b.Extent.X, b.Extent.Y, b.Extent.Z],
                 $"box {i}");
            Same(new ReadOnlySpan<float>(range, 2), [0f, 0f], $"range {i}");
        }
        output.WriteLine($"{trials} lights traced exactly");
    }
}
