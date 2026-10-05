using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: a .viscfg as VisConfig reads it. <c>VISCFG=&lt;path&gt;</c>.</summary>
public class VisConfigProbe(ITestOutputHelper output)
{
    [Fact]
    public void PrintConfig()
    {
        if (Environment.GetEnvironmentVariable("VISCFG") is not { Length: > 0 } path)
            return;
        var config = VisConfig.Read(path);
        output.WriteLine($"pvstype {config.PvsType} sun {config.DirToSun}");
        foreach (var h in config.Hints)
            output.WriteLine($"hint type {h.Type} {h.Mins} - {h.Maxs}");
        var splits = VisClusters.SplitHints.From(config.Hints);
        output.WriteLine($"split hints x {splits.X.Count} y {splits.Y.Count} z {splits.Z.Count}");
    }
}
