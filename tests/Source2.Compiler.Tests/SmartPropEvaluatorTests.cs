using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

public class SmartPropEvaluatorTests
{
    // atixref's radiator (CMapSmartProp 6783): its FitOnLine is seeded
    // 829579488 and picks seven fillers from tiles a, b, c; the configuration
    // the compile stored names them b b a c c a c, one RandomInt(0, 2) each.
    [Fact]
    public void RandomIntFollowsTheRadiatorsStoredPicks()
    {
        var stream = new SmartPropEvaluator.UniformRandomStream();
        stream.SetSeed(829579488);
        var picks = Enumerable.Range(0, 7).Select(_ => stream.RandomInt(0, 2)).ToArray();
        Assert.Equal([1, 1, 0, 2, 2, 0, 2], picks);
    }

    [Fact]
    public void RandomIntOverAnEmptyRangeDrawsNothing()
    {
        var a = new SmartPropEvaluator.UniformRandomStream();
        var b = new SmartPropEvaluator.UniformRandomStream();
        a.SetSeed(5);
        b.SetSeed(5);
        Assert.Equal(3, a.RandomInt(3, 3));
        Assert.Equal(b.RandomInt(0, 9), a.RandomInt(0, 9));
    }
}
