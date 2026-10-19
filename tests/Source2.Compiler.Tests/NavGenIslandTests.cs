using Source2.Compiler.Maps.NavGen;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>IslandRemoval (stage 029) on hand-made polygons.</summary>
public sealed class NavGenIslandTests
{
    private static NavGenPolys Squares(params (double X, double Y, double Z)[] corners)
    {
        var g = new NavGenPolys();
        foreach (var (x, y, z) in corners)
        {
            var at = g.Pool.Count;
            g.Pool.AddRange([(x, y, z), (x + 32, y, z), (x + 32, y + 32, z), (x, y + 32, z)]);
            g.Polys.Add(new NavPoly { Indices = [at, at + 1, at + 2, at + 3], Nz = 1, Links = new NavLinks() });
        }
        return g;
    }

    private static void Join(NavGenPolys g, int a, int ea, int b, int eb)
    {
        g.Polys[a].Links!.Add(new NavConnection { PolyA = a, EdgeA = ea, PolyB = b, EdgeB = eb });
        g.Polys[b].Links!.Add(new NavConnection { PolyA = b, EdgeA = eb, PolyB = a, EdgeB = ea });
    }

    [Fact]
    public void KeepsWhatTheSeedsReach()
    {
        // 0-1 joined on the ground, 2 three units over 1 only, 3 far away, 4 three units over 0 only;
        // the seed's box reaches 0 alone.
        var g = Squares((0, 0, 0), (0, 32, 0), (8, 40, 3), (1000, 1000, 0), (16, -20, 3));
        Join(g, 0, 2, 1, 0);
        var p2 = g.Polys[2];
        g.IslandRemoval(NavIslandInput.FromSeeds([(-35f, -10f, 0f)]), allHulls: true, skip: false);
        // The seed's box starts the flood at 0 (not listed), which reaches 1 (listed); only listed
        // polygons pull in what lies within 4 units, so 2 stays and 4 (over the start) goes.
        Assert.Equal(3, g.Polys.Count);
        Assert.Same(p2, g.Polys[2]);
        Assert.Equal(1, g.Polys[0].Links!.Count);
        Assert.Equal(1, g.Polys[0].Links!.Connections[0].PolyB);
        Assert.Equal(0, g.Polys[1].Links!.Connections[0].PolyB);
    }

    [Fact]
    public void WithoutSeedsEverythingGoes()
    {
        var g = Squares((0, 0, 0), (32, 0, 0));
        Join(g, 0, 1, 1, 3);
        g.IslandRemoval(NavIslandInput.FromSeeds([]), allHulls: true, skip: false);
        Assert.Empty(g.Polys);
    }

    [Fact]
    public void SkippedKeepsAll()
    {
        var g = Squares((0, 0, 0), (1000, 0, 0));
        g.IslandRemoval(NavIslandInput.FromSeeds([(16f, 16f, 0f)]), allHulls: true, skip: true);
        Assert.Equal(2, g.Polys.Count);
    }
}
