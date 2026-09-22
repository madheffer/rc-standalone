using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The PVS rule, pinned to what <c>18001b690</c>, <c>18002d9e0</c> and
/// <c>18002c2a0</c> do rather than to any map.
/// </summary>
public class VisVisibilityTests
{
    /// <summary>
    /// Every cluster a line crosses sees every other, itself included, and the
    /// matrix is symmetric because each row is ORed with the same set.
    /// </summary>
    [Fact]
    public void ALineMakesEveryClusterItCrossedMutuallyVisible()
    {
        var matrix = new VisVisibility.Matrix(100);
        var added = matrix.Mark([3, 40, 41, 99]);

        Assert.True(added > 0);
        foreach (var a in new[] { 3, 40, 41, 99 })
        {
            foreach (var b in new[] { 3, 40, 41, 99 })
                Assert.True(matrix.Sees(a, b), $"{a} should see {b}");
            Assert.False(matrix.Sees(a, 7));
            Assert.False(matrix.Sees(7, a));
        }
    }

    /// <summary>
    /// The return is how many WORDS changed, so repeating a line adds nothing.
    /// That is exactly the test that makes a ray "useful", and it is why the
    /// counter is not simply the ray count.
    /// </summary>
    [Fact]
    public void RemarkingTheSameSetAddsNothing()
    {
        var matrix = new VisVisibility.Matrix(64);
        Assert.True(matrix.Mark([1, 2, 3]) > 0);
        Assert.Equal(0, matrix.Mark([1, 2, 3]));
        Assert.Equal(0, matrix.Mark([1, 2]));
        Assert.True(matrix.Mark([1, 2, 33]) > 0);
    }

    /// <summary>
    /// Ids inside one 32 bit word collapse to a single run, which is the whole
    /// reason the caller sorts and dedupes before marking. Two ids a word apart
    /// cannot collapse.
    /// </summary>
    [Fact]
    public void IdsSharingAWordCollapseIntoOneRun()
    {
        var near = new VisVisibility.Matrix(96);
        var far = new VisVisibility.Matrix(96);

        // 0..3 are one word, so each of the 4 rows takes 1 word: 4 changes.
        Assert.Equal(4, near.Mark([0, 1, 2, 3]));

        // 0, 32, 64 are three words, so each of the 3 rows takes 3: 9 changes.
        Assert.Equal(9, far.Mark([0, 32, 64]));
    }

    /// <summary>A segment down one axis crosses exactly the cells on that axis.</summary>
    [Fact]
    public void AnAxisAlignedSegmentCrossesOnlyItsOwnColumn()
    {
        var mins = Vector3.Zero;
        const float Size = 16f;

        // Straight up through the low x, low y column of a 4x4x4 split.
        var origin = new Vector3(2f, 2f, -8f);
        var delta = new Vector3(0f, 0f, 32f);
        var cells = VisVisibility.Crossed(origin, One(delta), mins, Size, 4);

        Assert.Equal(4, System.Numerics.BitOperations.PopCount(cells));
        for (var z = 0; z < 4; z++)
            Assert.True((cells & (1UL << (z * 16))) != 0, $"cell at z={z} should be crossed");
    }

    /// <summary>
    /// A diagonal lights up the cells it actually crosses and NOT the box around
    /// them. <c>18010c6e0</c> gives each of the 64 cells its own slab test, so the
    /// answer is a staircase; the range product it is easy to mistake it for would
    /// return the whole bounding sub box, including the two far corners asserted
    /// clear here.
    /// </summary>
    [Fact]
    public void ADiagonalCrossesItsOwnCellsAndNotTheBoxAroundThem()
    {
        // Across a 16 unit box on a 3:1 slope, flat in z, so the segment runs
        // through the x row 0..3 while y steps from row 0 to row 1 halfway.
        var delta = new Vector3(12f, 4f, 0f);
        var cells = VisVisibility.Crossed(new Vector3(2f, 2f, 2f), One(delta), Vector3.Zero, 16f, 4);

        foreach (var (x, y) in new[] { (0, 0), (1, 0), (2, 0), (1, 1), (2, 1), (3, 1) })
            Assert.True((cells & (1UL << (x + (y * 4)))) != 0, $"cell {x},{y} is on the line");

        // The bounding box of the passage is the whole 4x2 block. These two are
        // its corners, and the line comes nowhere near either.
        Assert.True((cells & (1UL << 3)) == 0, "cell 3,0 is only in the bounding box");
        Assert.True((cells & (1UL << 4)) == 0, "cell 0,1 is only in the bounding box");
        Assert.Equal(6, System.Numerics.BitOperations.PopCount(cells));
    }

    /// <summary>A segment that never reaches the box crosses nothing.</summary>
    [Fact]
    public void ASegmentThatMissesTheBoxCrossesNothing()
    {
        var delta = new Vector3(0f, 0f, 1f);
        Assert.Equal(0UL, VisVisibility.Crossed(
            new Vector3(-100f, -100f, -100f), One(delta), Vector3.Zero, 16f, 4));

        // It points at the box but stops short, because t is clamped to 1.
        var shy = new Vector3(0f, 0f, 4f);
        Assert.Equal(0UL, VisVisibility.Crossed(
            new Vector3(2f, 2f, -100f), One(shy), Vector3.Zero, 16f, 4));
    }

    /// <summary>
    /// A blocking entry ends the walk when the batch had no sight ray, and is
    /// walked straight through when it did.
    /// </summary>
    [Fact]
    public void ABlockerStopsTheWalkOnlyWhenTheBatchHadNoSightRay()
    {
        // One leaf covering the whole root, holding a cluster then a blocker.
        VisVisibility.Node[] nodes = [new(1, 2)];
        VisVisibility.Entry[] entries =
        [
            new(5, 0, ulong.MaxValue),
            new(6, VisVisibility.Blocking, ulong.MaxValue),
        ];

        var origin = new Vector3(2f, 2f, -8f);
        var delta = new Vector3(0f, 0f, 32f);

        Assert.Null(VisVisibility.Walk(
            nodes, entries, Vector3.Zero, 16f, origin, delta, throughBlockers: false));

        var through = VisVisibility.Walk(
            nodes, entries, Vector3.Zero, 16f, origin, delta, throughBlockers: true);
        Assert.Equal([5], through);
    }

    /// <summary>A skipped entry is never collected, whatever it covers.</summary>
    [Fact]
    public void ASkippedEntryIsNeverCollected()
    {
        VisVisibility.Node[] nodes = [new(1, 2)];
        VisVisibility.Entry[] entries =
        [
            new(5, VisVisibility.Skipped, ulong.MaxValue),
            new(9, 0, ulong.MaxValue),
        ];

        var crossed = VisVisibility.Walk(
            nodes, entries, Vector3.Zero, 16f, new Vector3(2f, 2f, -8f),
            new Vector3(0f, 0f, 32f), throughBlockers: true);
        Assert.Equal([9], crossed);
    }

    /// <summary>
    /// An entry whose sub cells the line never enters is not collected even
    /// though its leaf is, which is what the 64 bit mask is for.
    /// </summary>
    [Fact]
    public void AnEntryOffTheLinesSubCellsIsNotCollected()
    {
        VisVisibility.Node[] nodes = [new(1, 2)];
        VisVisibility.Entry[] entries =
        [
            new(5, 0, 1UL << 63),
            new(9, 0, 1UL),
        ];

        // Up the low x, low y column, which is cells 0, 16, 32 and 48.
        var crossed = VisVisibility.Walk(
            nodes, entries, Vector3.Zero, 16f, new Vector3(2f, 2f, -8f),
            new Vector3(0f, 0f, 32f), throughBlockers: true);
        Assert.Equal([9], crossed);
    }

    /// <summary>
    /// The driver counts a segment as useful only when it changed the matrix, so
    /// the same line twice counts once.
    /// </summary>
    [Fact]
    public void ASegmentIsUsefulOnlyTheFirstTimeItChangesAnything()
    {
        var matrix = new VisVisibility.Matrix(32);
        var line = new VisSampler.Segment(Vector3.Zero, new Vector3(0f, 0f, 32f));
        List<VisSampler.Segment> twice = [line, line];

        var useful = VisVisibility.Accumulate(
            matrix, twice, (_, _, _) => [4, 4, 11], anySight: true);

        Assert.Equal(1, useful);
        Assert.True(matrix.Sees(4, 11));
        Assert.True(matrix.Sees(11, 4));
    }

    /// <summary>
    /// Without a sight ray the origin steps one unit along the line first,
    /// because those segments start on the surface that produced them.
    /// </summary>
    [Fact]
    public void ABatchWithoutASightRayNudgesTheOriginOffTheSurface()
    {
        var matrix = new VisVisibility.Matrix(8);
        var starts = new List<Vector3>();
        List<VisSampler.Segment> one = [new(Vector3.Zero, new Vector3(0f, 0f, 10f))];

        VisVisibility.Accumulate(matrix, one, (origin, _, _) =>
        {
            starts.Add(origin);
            return [1];
        }, anySight: false);

        VisVisibility.Accumulate(matrix, one, (origin, _, _) =>
        {
            starts.Add(origin);
            return [1];
        }, anySight: true);

        Assert.Equal(new Vector3(0f, 0f, 1f), starts[0]);
        Assert.Equal(Vector3.Zero, starts[1]);
    }

    private static Vector3 One(Vector3 delta) =>
        new(1f / delta.X, 1f / delta.Y, 1f / delta.Z);
}
