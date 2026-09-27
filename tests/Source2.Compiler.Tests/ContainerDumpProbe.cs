using System.Buffers.Binary;
using ValvePak;
using ValveResourceFormat;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every block of a compiled resource inside a package, with the
/// container header, the block table and each KV3 block's own header, written
/// as text files beside each other.
/// <c>CONTAINERDUMP=&lt;.vpk&gt;|&lt;path in it&gt;|&lt;out dir&gt;</c>.
/// </summary>
public class ContainerDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("CONTAINERDUMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        using var package = new Package();
        package.Read(p[0]);
        package.ReadEntry(package.FindEntry(p[1])!, out var bytes);
        Directory.CreateDirectory(p[2]);
        File.WriteAllBytes(Path.Combine(p[2], Path.GetFileName(p[1])), bytes);
        var lines = new List<string>
        {
            $"file {bytes.Length} bytes, header fileSize {BinaryPrimitives.ReadUInt32LittleEndian(bytes)}, headerVersion {BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4))}, " +
            $"resourceVersion {BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6))}, blockOffset {BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8))}, blocks {BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12))}",
        };
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (var b = 0; b < count; b++)
        {
            var at = 16 + (b * 12);
            var fourCc = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var offset = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
            var head = bytes.AsSpan(offset, Math.Min(size, 64)).ToArray();
            lines.Add($"block {b} {fourCc} at {offset} size {size}: {Convert.ToHexString(head)}");
        }
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        foreach (var block in resource.Blocks)
        {
            var text = block.ToString() ?? "";
            File.WriteAllText(Path.Combine(p[2], $"{block.Type}.txt"), text);
            ValveKeyValue.KVObject? tree = block switch
            {
                ValveResourceFormat.ResourceTypes.KeyValuesOrNTRO k => k.Data,
                ValveResourceFormat.ResourceTypes.BinaryKV3 k => k.Data.Root,
                ValveResourceFormat.Blocks.ResourceEditInfo2 r => r.Data?.Root,
                _ => null,
            };
            if (tree != null)
            {
                File.WriteAllLines(Path.Combine(p[2], $"{block.Type}.typed.txt"), KvTreeDiff.Typed(tree, block.Type.ToString(), 0, 1));
                // The tables in full.
                foreach (var key in new[] { "m_collisionAttributes", "m_surfacePropertyHashes" })
                    if (tree.ContainsKey(key))
                        File.WriteAllLines(Path.Combine(p[2], $"{block.Type}.{key}.txt"), KvTreeDiff.Typed(tree[key]!, key, 0, int.MaxValue));
            }
            lines.Add($"{block.Type}: {block.GetType().Name}, {text.Length} chars of text");
        }
        File.WriteAllLines(Path.Combine(p[2], "container.txt"), lines);
        foreach (var l in lines)
            output.WriteLine(l);
    }
}
