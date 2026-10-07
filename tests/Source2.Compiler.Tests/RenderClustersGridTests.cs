using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The node's box grid (rc 180283520), which clusters a node built without
/// vis: probe01 and cardtest's -world packages match Valve's with it.
/// </summary>
public class RenderClustersGridTests
{
    [Fact]
    public void WholeCellsPaddedByAHundredth()
    {
        var boxes = RenderClusters.Grid(Vector3.Zero, new Vector3(1500, 100, 10), 1024f);
        Assert.Equal(2, boxes.Count);
        // The far corner moves to whole cells (2048, 1024, 1024), then everything grows by 0.01.
        Assert.Equal(new Vector3(-0.01f), boxes[0].Min);
        Assert.Equal(boxes[0].Max.X, boxes[1].Min.X);
        Assert.Equal(boxes[0].Min.Y, boxes[1].Min.Y);
        Assert.Equal((2048f + 0.01f - -0.01f) / 2 + -0.01f, boxes[0].Max.X);
        Assert.Equal(1024f + 0.01f - -0.01f + -0.01f, boxes[1].Max.Y);
    }

    [Fact]
    public void XRunsFastestThenY()
    {
        var boxes = RenderClusters.Grid(Vector3.Zero, new Vector3(2000, 2000, 2000), 1024f);
        Assert.Equal(8, boxes.Count);
        Assert.True(boxes[1].Min.X > boxes[0].Min.X && boxes[1].Min.Y == boxes[0].Min.Y);
        Assert.True(boxes[2].Min.Y > boxes[0].Min.Y && boxes[2].Min.X == boxes[0].Min.X);
        Assert.True(boxes[4].Min.Z > boxes[0].Min.Z);
    }

    [Fact]
    public void PastFourThousandCellsTheCellGrows()
    {
        // 64 x 64 x 2 cells is over 0x1000: cbrt(volume / 4096) = 1290.16, so 50 x 50 x 1.
        var boxes = RenderClusters.Grid(Vector3.Zero, new Vector3(65536, 65536, 2048), 1024f);
        Assert.Equal(2500, boxes.Count);
    }
}
