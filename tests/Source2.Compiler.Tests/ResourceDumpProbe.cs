using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a compiled resource's blocks as text. <c>RESDUMP=&lt;path&gt;[|&lt;out file&gt;]</c>,
/// the path a file on disk or else a path in pak01. Every block is printed
/// under its type, the data block first.
/// </summary>
public class ResourceDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("RESDUMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        byte[] bytes;
        if (File.Exists(p[0]))
            bytes = File.ReadAllBytes(p[0]);
        else
        {
            using var package = new Package();
            package.Read(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk");
            package.ReadEntry(package.FindEntry(p[0])!, out bytes);
        }
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var text = new System.Text.StringBuilder();
        text.AppendLine($"== {resource.ResourceType} resver {resource.Version} blocks {string.Join(' ', resource.Blocks.Select(b => $"{b.Type}@{b.Offset}+{b.Size}"))}");
        foreach (var block in resource.Blocks)
        {
            if (block.Type is BlockType.RERL or BlockType.REDI)
                continue;
            text.AppendLine($"== {block.Type}");
            try
            {
                text.AppendLine(block.ToString());
            }
            catch (Exception e)
            {
                text.AppendLine($"(no text: {e.GetType().Name})");
            }
        }
        if (resource.ExternalReferences is { } rerl)
        {
            text.AppendLine("== RERL");
            foreach (var r in rerl.ResourceRefInfoList)
                text.AppendLine($"  {r.Id:x16} {r.Name}");
        }
        if (p.Length > 1)
            File.WriteAllText(p[1], text.ToString());
        else
            output.WriteLine(text.ToString());
    }
}
