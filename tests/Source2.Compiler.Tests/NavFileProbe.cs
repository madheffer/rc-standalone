using System.Buffers.Binary;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <c>NAVDUMP=&lt;file.nav&gt;[;...]</c>: a .nav's layout, the raw words between its embedded binary KV3 v5
/// blobs and each blob as text, with whether Kv3V5Writer re-encodes it byte for byte.
/// </summary>
public class NavFileProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("NAVDUMP") is not { Length: > 0 } spec)
            return;
        foreach (var path in spec.Split(';'))
        {
            var bytes = File.ReadAllBytes(path);
            output.WriteLine($"== {path}: {bytes.Length} bytes");
            var at = 0;
            var pos = 0;
            while (pos < bytes.Length)
            {
                var next = -1;
                for (var i = pos; i + 4 <= bytes.Length; i++)
                    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x4B563305)
                    {
                        next = i;
                        break;
                    }
                var gapEnd = next < 0 ? bytes.Length : next;
                if (gapEnd > pos)
                    output.WriteLine($"  raw {pos}..{gapEnd}: {Convert.ToHexString(bytes.AsSpan(pos, gapEnd - pos))}");
                if (next < 0)
                    break;
                using var holder = new Resource();
                var kv3 = new BinaryKV3 { Resource = holder, Offset = (uint)next, Size = (uint)(bytes.Length - next) };
                using var reader = new BinaryReader(new MemoryStream(bytes));
                reader.BaseStream.Position = next;
                kv3.Read(reader);
                var end = (int)reader.BaseStream.Position;
                var method = (KV3BinaryCompressionMethod)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(next + 20));
                var ours = Kv3V5Writer.Write(kv3.Data.Root, kv3.Data.Header!.Format.Id, method);
                var exact = ours.AsSpan().SequenceEqual(bytes.AsSpan(next, Math.Min(ours.Length, bytes.Length - next))) ? "exact" : "differs";
                output.WriteLine($"  kv3 #{at++} {next}..{end} ({end - next} bytes, method {method}, ours {ours.Length} bytes {exact}):");
                Print("root", kv3.Data.Root, "    ");
                pos = Math.Max(end, next + 4);
            }
        }
    }

    private void Print(string name, ValveKeyValue.KVObject o, string indent)
    {
        if (o.ValueType is ValveKeyValue.KVValueType.Collection)
        {
            output.WriteLine($"{indent}{name} (collection):");
            foreach (var (k, c) in o)
                Print(k, c, indent + "  ");
        }
        else if (o.ValueType is ValveKeyValue.KVValueType.Array)
        {
            output.WriteLine($"{indent}{name} (array):");
            var i = 0;
            foreach (var c in o.Values)
                Print($"[{i++}]", c, indent + "  ");
        }
        else
            output.WriteLine($"{indent}{name} = {o} ({o.ValueType})");
    }
}

/// <summary><c>CLASSORDER=&lt;vmap&gt;</c>: the map's entity classes in document order (MapEntities.From), with counts.</summary>
public class ClassOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("CLASSORDER") is not { Length: > 0 } path)
            return;
        var document = DmxBinary.ReadFile(path);
        var all = MapEntities.From(document).Select(e => e.ClassName).ToList();
        output.WriteLine("order: " + string.Join(" ", all));
        output.WriteLine("counts: " + string.Join(" ", all.GroupBy(c => c).Select(g => $"{g.Key}={g.Count()}")));
        // CLASSORDER_FIND=<text>: each element attribute whose string value holds the text.
        if (Environment.GetEnvironmentVariable("CLASSORDER_FIND") is { Length: > 0 } find)
            foreach (var e in document.Elements)
                foreach (var (k, v) in e.Attributes)
                    if (v is string str && str.Contains(find, StringComparison.OrdinalIgnoreCase))
                        output.WriteLine($"found: element {e.Type} '{e.Name}' attribute {k} = {str}");
        // Every element with a classname, by element type, against MapEntities' count of that class.
        var counts = all.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var elements = document.Elements.Select(e => (e.Type, Class: e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname"))).Where(x => x.Class != null).ToList();
        output.WriteLine("element types: " + string.Join(" ", document.Elements.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).Take(30).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var g in elements.GroupBy(x => x.Class!).Where(g => g.Count() != counts.GetValueOrDefault(g.Key)))
            output.WriteLine($"  {g.Key}: elements {g.Count()} ({string.Join(",", g.GroupBy(x => x.Type).Select(t => $"{t.Key}={t.Count()}"))}), MapEntities {counts.GetValueOrDefault(g.Key)}");
    }
}
