using System.Buffers.Binary;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="Kv3V5Writer"/> against Valve's own blocks (<c>KV3ROUNDTRIP=&lt;vpk&gt;[|&lt;path
/// substring&gt;]</c>): each KV3 v5 block decoded by VRF and written again must
/// give Valve's bytes. Reports exact blocks and, per kind of miss, the first
/// differing header field or buffer. <c>KV3ROUNDTRIP_AUTO=1</c> lets the writer
/// choose the compression as binary_auto does instead of taking Valve's.
/// </summary>
public class Kv3V5WriterTests(ITestOutputHelper output)
{
    private static readonly string[] Fields =
    [
        "magic", "fmt0", "fmt1", "fmt2", "fmt3", "method", "dict+frame", "bytes1", "bytes4", "bytes8", "types", "objects+arrays",
        "sizeU", "sizeC", "blobs", "blobBytes", "bytes2", "blockSizes", "u1", "c1", "u2", "c2", "m1", "m2", "m4", "m8",
        "nodes", "objects2", "arraysMain", "elementsMain",
    ];

    [Fact]
    public void RoundTrip()
    {
        if (Environment.GetEnvironmentVariable("KV3ROUNDTRIP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        using var pkg = new Package();
        pkg.Read(parts[0]);
        int exact = 0, total = 0, skipped = 0;
        var misses = new Dictionary<string, (int Count, string Example)>();
        foreach (var e in pkg.Entries!.SelectMany(kv => kv.Value).Where(e => parts.Length < 2 || e.GetFullPath().Contains(parts[1], StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = Io.VpkEntries.Read(pkg, e);
            if (bytes.Length < 16)
                continue;
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            {
                for (var i = 0; i < count; i++)
                {
                    var at = 16 + (i * 12);
                    var start = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
                    if (start + size > bytes.Length || size < 120 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start)) != 0x4B563305)
                        continue;
                    using var holder = new Resource();
                    var kv3 = new BinaryKV3 { Resource = holder, Offset = (uint)start, Size = (uint)size };
                    try
                    {
                        using var reader = new BinaryReader(new MemoryStream(bytes));
                        reader.BaseStream.Position = start;
                        kv3.Read(reader);
                    }
                    catch
                    {
                        continue;
                    }
                    var valve = bytes.AsSpan(start, size);
                    var method = (KV3BinaryCompressionMethod)BinaryPrimitives.ReadInt32LittleEndian(valve[20..]);
                    byte[] ours;
                    try
                    {
                        ours = Kv3V5Writer.Write(kv3.Data.Root, kv3.Data.Header!.Format.Id, Environment.GetEnvironmentVariable("KV3ROUNDTRIP_AUTO") == "1" ? null : method);
                    }
                    catch (NotSupportedException ex)
                    {
                        skipped++;
                        misses[$"skipped: {ex.Message}"] = (misses.GetValueOrDefault($"skipped: {ex.Message}").Count + 1, e.GetFullPath());
                        continue;
                    }
                    total++;
                    if (valve.SequenceEqual(ours))
                    {
                        exact++;
                        continue;
                    }
                    var why = "trailing bytes";
                    for (var f = 0; f < Fields.Length && (f * 4) + 4 <= Math.Min(valve.Length, ours.Length); f++)
                        if (!valve.Slice(f * 4, 4).SequenceEqual(ours.AsSpan(f * 4, 4)))
                        {
                            why = $"{Fields[f]}: {BinaryPrimitives.ReadInt32LittleEndian(valve[(f * 4)..])} vs {BinaryPrimitives.ReadInt32LittleEndian(ours.AsSpan(f * 4))}";
                            break;
                        }
                    if (why == "trailing bytes" && valve.Length != ours.Length)
                        why = $"length {valve.Length} vs {ours.Length}";
                    else if (why == "trailing bytes")
                    {
                        var first = 0;
                        while (first < valve.Length && valve[first] == ours[first])
                            first++;
                        why = $"payload bytes from {first} of {valve.Length}";
                    }
                    var key = $"{System.Text.Encoding.ASCII.GetString(bytes, at, 4)} {why.Split(':')[0]}";
                    if (!misses.ContainsKey(key) && Environment.GetEnvironmentVariable("KV3ROUNDTRIP_HEADERS") == "1")
                    {
                        output.WriteLine($"{e.GetFullPath()} {key}");
                        var vh = valve[..120].ToArray();
                        output.WriteLine($"  valve {string.Join(",", Enumerable.Range(0, 30).Select(f => BinaryPrimitives.ReadInt32LittleEndian(vh.AsSpan(f * 4))))}");
                        output.WriteLine($"  ours  {string.Join(",", Enumerable.Range(0, 30).Select(f => BinaryPrimitives.ReadInt32LittleEndian(ours.AsSpan(f * 4))))}");
                    }
                    misses[key] = (misses.GetValueOrDefault(key).Count + 1, misses.TryGetValue(key, out var m) ? m.Example : $"{e.GetFullPath()} ({why})");
                }
            }
        }
        output.WriteLine($"{exact} of {total} KV3 v5 blocks byte-exact, {skipped} skipped");
        foreach (var (k, (n, ex)) in misses.OrderByDescending(x => x.Value.Count))
            output.WriteLine($"  {n,6} {k}  e.g. {ex}");
    }
}
