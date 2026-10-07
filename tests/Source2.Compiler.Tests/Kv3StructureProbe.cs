using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>KV3STRUCT=&lt;vpk&gt;[|&lt;path substring&gt;]</c>): how Valve encodes
/// each node of every KV3 v5 block, as tallies: node types, which arrays are
/// typed (10, 24 byte length, 25 auxiliary buffer) and with what element type
/// and length, and plain arrays whose elements all share one scalar type (the
/// cases a "homogeneous scalars are typed" rule would get wrong).
/// </summary>
public class Kv3StructureProbe(ITestOutputHelper output)
{
    private sealed class Cursor(byte[] data, int start, int end)
    {
        public int At = start;
        public readonly int End = end;
        public byte Byte() => data[At++];
        public short I16() { var v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(At)); At += 2; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(At)); At += 4; return v; }
        public long I64() { var v = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(At)); At += 8; return v; }
    }

    private sealed class Buffers
    {
        public Cursor B1 = null!, B2 = null!, B4 = null!, B8 = null!;
    }

    private sealed class Ctx
    {
        public Cursor Types = null!, Objects = null!;
        public Buffers Main = null!, Aux = null!;
        public readonly Dictionary<string, int> Tally = [];
        public readonly List<string> Examples = [];
        public void Count(string k) => Tally[k] = Tally.GetValueOrDefault(k) + 1;
    }

    private static Buffers Split(byte[] data, int b1, int b2, int b4, int b8, ref int at)
    {
        var r = new Buffers();
        r.B1 = new Cursor(data, at, at + b1);
        at += b1;
        if (b2 > 0) at = (at + 1) & ~1;
        r.B2 = new Cursor(data, at, at + (b2 * 2));
        at += b2 * 2;
        if (b4 > 0) at = (at + 3) & ~3;
        r.B4 = new Cursor(data, at, at + (b4 * 4));
        at += b4 * 4;
        if (b8 > 0) at = (at + 7) & ~7;
        r.B8 = new Cursor(data, at, at + (b8 * 8));
        at += b8 * 8;
        return r;
    }

    private static (int Type, int Flag) Type(Ctx c)
    {
        int t = c.Types.Byte(), f = 0;
        if ((t & 0x80) != 0)
        {
            t &= 0x3f;
            f = c.Types.Byte();
        }
        return (t, f);
    }

    private static readonly HashSet<int> Scalars = [2, 3, 4, 5, 6, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23];

    // Reads one value of encoded type t; returns that type for array homogeneity checks.
    private static void Value(Ctx c, int t, string path)
    {
        var b = c.Main;
        switch (t)
        {
            case 1 or 13 or 14 or 15 or 16 or 17 or 18: return;
            case 2 or 23: b.B1.Byte(); return;
            case 20 or 21: b.B2.I16(); return;
            case 11 or 12 or 19 or 6: b.B4.I32(); return;
            case 3 or 4 or 5: b.B8.I64(); return;
            case 7: return; // blob length lives in the blob table
            case 8:
            {
                var n = b.B4.I32();
                var types = new List<int>();
                for (var i = 0; i < n; i++)
                {
                    var (et, ef) = Type(c);
                    types.Add(et * 256 + ef);
                    Value(c, et, path + "[]");
                }
                if (n > 0 && types.Distinct().Count() == 1 && Scalars.Contains(types[0] / 256))
                {
                    c.Count($"plain array, uniform scalar {types[0] / 256} flag {types[0] % 256}, n {(n < 256 ? "<256" : ">=256")}");
                    if (c.Examples.Count < 30)
                        c.Examples.Add($"{path}: plain array of {n} type {types[0] / 256}");
                }
                else
                    c.Count(n == 0 ? "plain array, empty" : $"plain array, mixed or non-scalar ({string.Join(",", types.Select(x => x / 256).Distinct().Take(4))})");
                return;
            }
            case 10 or 24 or 25:
            {
                var n = t == 10 ? b.B4.I32() : b.B1.Byte();
                var (et, ef) = Type(c);
                c.Count($"typed {t}, element {et} flag {ef}, n {(n < 32 ? "<32" : n < 256 ? "<256" : ">=256")}");
                if (t == 25)
                    (c.Main, c.Aux) = (c.Aux, c.Main);
                for (var i = 0; i < n; i++)
                    Value(c, et, path + "[]");
                if (t == 25)
                    (c.Main, c.Aux) = (c.Aux, c.Main);
                return;
            }
            case 9:
            {
                var n = c.Objects.I32();
                for (var i = 0; i < n; i++)
                {
                    var (ct, cf) = Type(c);
                    c.Main.B4.I32(); // member name id
                    c.Count($"scalar {ct}{(cf != 0 ? " flag " + cf : "")}" + (Scalars.Contains(ct) ? "" : ""));
                    Value(c, ct, path + "." + i);
                }
                return;
            }
            default: throw new InvalidDataException($"type {t} at {path}");
        }
    }

    [Fact]
    public void Tally()
    {
        if (Environment.GetEnvironmentVariable("KV3STRUCT") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        using var pkg = new Package();
        pkg.Read(parts[0]);
        var all = new Ctx();
        var blocks = 0;
        foreach (var e in pkg.Entries!.SelectMany(kv => kv.Value).Where(e => parts.Length < 2 || e.GetFullPath().Contains(parts[1], StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = Io.VpkEntries.Read(pkg, e);
            if (bytes.Length < 16)
                continue;
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            for (var i = 0; i < count && 16 + (i * 12) + 12 <= bytes.Length; i++)
            {
                var at = 16 + (i * 12);
                var start = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                if (start + 120 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start)) != 0x4B563305)
                    continue;
                try
                {
                    Block(bytes.AsSpan(start).ToArray(), all, $"{e.GetFileName()}:{Encoding.ASCII.GetString(bytes, at, 4)}");
                    blocks++;
                }
                catch (Exception ex)
                {
                    output.WriteLine($"{e.GetFullPath()} {Encoding.ASCII.GetString(bytes, at, 4)}: {ex.Message}");
                }
            }
        }
        output.WriteLine($"{blocks} v5 blocks");
        foreach (var (k, v) in all.Tally.OrderBy(x => x.Key, StringComparer.Ordinal))
            output.WriteLine($"  {v,8} {k}");
        foreach (var x in all.Examples)
            output.WriteLine("  e.g. " + x);
    }

    private static void Block(byte[] b, Ctx c, string name)
    {
        var method = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(20));
        int R(int off) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(off));
        int bytes1 = R(28), bytes4 = R(32), bytes8 = R(36), countTypes = R(40);
        int blobCount = R(56), bytes2 = R(64);
        int u1 = R(72), c1 = R(76), u2 = R(80), c2 = R(84);
        int b1b2 = R(88), b2b2 = R(92), b4b2 = R(96), b8b2 = R(100), objects2 = R(108);
        byte[] Unpack(int offset, int compressed, int raw)
        {
            if (method == 0)
                return b.AsSpan(offset, raw).ToArray();
            var o = new byte[raw];
            if (method == 1)
                LZ4Codec.Decode(b.AsSpan(offset, compressed), o);
            else
            {
                using var z = new ZstdSharp.Decompressor();
                z.Unwrap(b.AsSpan(offset, compressed), o);
            }
            return o;
        }
        var p1 = Unpack(120, method == 0 ? u1 : c1, u1);
        var p2 = Unpack(120 + (method == 0 ? u1 : c1), method == 0 ? u2 : c2, u2);
        var at1 = 0;
        var aux = Split(p1, bytes1, bytes2, bytes4, bytes8, ref at1);
        var strings = aux.B4.I32();
        for (var s = 0; s < strings; s++)
            while (aux.B1.Byte() != 0) { }
        var at2 = objects2 * 4;
        var main = Split(p2, b1b2, b2b2, b4b2, b8b2, ref at2);
        c.Main = main;
        c.Aux = aux;
        c.Objects = new Cursor(p2, 0, objects2 * 4);
        c.Types = new Cursor(p2, at2, at2 + countTypes);
        var (t, f) = Type(c);
        c.Count($"root {t}");
        Value(c, t, name);
        _ = blobCount;
    }
}
