using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>The ported collision group table against the one a bundle capture dumped with its world (SETTLE).</summary>
public sealed class CollisionGroupTableTests
{
    [Fact]
    public void TableMatchesTheCapture()
    {
        if (Environment.GetEnvironmentVariable("SETTLE") is not { Length: > 0 } path || !File.Exists(path))
            return;
        var capture = SettleCapture.Read(path);
        if (capture.Groups is not { } captured)
            return;
        Assert.Equal(captured, CollisionGroupTable.Build());
        Assert.Equal(0, capture.GroupDefault);
    }
}
