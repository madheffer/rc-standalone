using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: a compiled resource's data block as KV3 text. <c>RESDUMP=&lt;path in pak01&gt;[|&lt;out file&gt;]</c>.</summary>
public class ResourceDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("RESDUMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        using var package = new Package();
        package.Read(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk");
        package.ReadEntry(package.FindEntry(p[0])!, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var text = resource.DataBlock!.ToString() ?? "";
        if (p.Length > 1)
            File.WriteAllText(p[1], text);
        else
            output.WriteLine(text);
    }
}
