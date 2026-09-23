using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The dynamic AABB tree, held to the one property the merge depends on: a
/// query returns EXACTLY the leaves whose boxes meet it, whatever shape the
/// insertions happened to build.
/// </summary>
public class VisBoxTreeTests(ITestOutputHelper output)
{
    private static bool Meets(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && bMin.X <= aMax.X
        && aMin.Y <= bMax.Y && bMin.Y <= aMax.Y
        && aMin.Z <= bMax.Z && bMin.Z <= aMax.Z;

    /// <summary>
    /// Cluster boxes are not random: they sit on a coarse voxel grid, so many
    /// coincide exactly or nest, and a set can be tiny. That is what the merge
    /// feeds the tree.
    /// </summary>
    [Fact]
    public void AQueryMatchesAScanOnBoxesShapedLikeClusters()
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var random = new Random(seed);
            var tree = new VisBoxTree();
            var boxes = new Dictionary<int, (Vector3 Mins, Vector3 Maxs)>();
            var count = 2 + random.Next(120);

            for (var id = 0; id < count; id++)
            {
                var at = new Vector3(random.Next(0, 4) * 8, random.Next(0, 4) * 8, random.Next(0, 3) * 8);
                var box = (at, at + new Vector3(8f * random.Next(1, 3)));
                boxes[id] = box;
                tree.Create(box.Item1, box.Item2, id);
            }

            var found = new List<int>();
            for (var id = 0; id < count; id++)
            {
                var grow = new Vector3(1f);
                found.Clear();
                tree.Query(boxes[id].Mins - grow, boxes[id].Maxs + grow, found);
                var expected = boxes.Where(b => Meets(boxes[id].Mins - grow, boxes[id].Maxs + grow,
                                                      b.Value.Mins, b.Value.Maxs))
                                    .Select(b => b.Key).Order().ToList();
                Assert.True(expected.SequenceEqual(found.Order()),
                    $"seed {seed}, {count} boxes, querying {id}: tree gave {found.Count}"
                  + $" and a scan gives {expected.Count}"
                  + $" (missing {string.Join(",", expected.Except(found))})");
            }
        }
    }

    /// <summary>
    /// The merge's exact pattern: absorb a pair, so the survivor's box GROWS to
    /// the union and the other is destroyed, and query straight afterwards. A
    /// tree that is only checked at the end can hide a broken intermediate.
    /// </summary>
    [Fact]
    public void AQueryIsRightAfterEveryAbsorb()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            var random = new Random(seed);
            var tree = new VisBoxTree();
            var boxes = new Dictionary<int, (Vector3 Mins, Vector3 Maxs)>();
            var proxies = new Dictionary<int, int>();
            var count = 4 + random.Next(120);

            for (var id = 0; id < count; id++)
            {
                var at = new Vector3(random.Next(0, 4) * 8, random.Next(0, 4) * 8, random.Next(0, 3) * 8);
                boxes[id] = (at, at + new Vector3(8f * random.Next(1, 3)));
                proxies[id] = tree.Create(boxes[id].Mins, boxes[id].Maxs, id);
            }

            var found = new List<int>();
            while (boxes.Count > 1)
            {
                var live = boxes.Keys.ToList();
                var owner = live[random.Next(live.Count)];
                var other = live[random.Next(live.Count)];
                if (owner == other)
                    continue;

                boxes[owner] = (Vector3.Min(boxes[owner].Mins, boxes[other].Mins),
                                Vector3.Max(boxes[owner].Maxs, boxes[other].Maxs));
                tree.Move(proxies[owner], boxes[owner].Mins, boxes[owner].Maxs);
                tree.Destroy(proxies[other]);
                boxes.Remove(other);
                proxies.Remove(other);

                var grow = new Vector3(1f);
                found.Clear();
                tree.Query(boxes[owner].Mins - grow, boxes[owner].Maxs + grow, found);
                var expected = boxes.Where(b => Meets(boxes[owner].Mins - grow, boxes[owner].Maxs + grow,
                                                      b.Value.Mins, b.Value.Maxs))
                                    .Select(b => b.Key).Order().ToList();
                Assert.True(expected.SequenceEqual(found.Order()),
                    $"seed {seed}: after absorbing {other} into {owner} with {boxes.Count} left,"
                  + $" tree gave {found.Count} and a scan gives {expected.Count}"
                  + $" (missing {string.Join(",", expected.Except(found))})");
            }
        }
    }

    [Fact]
    public void AQueryMatchesABruteForceScanOverEverySurvivor()
    {
        var random = new Random(20260923);
        var tree = new VisBoxTree();
        var boxes = new Dictionary<int, (Vector3 Mins, Vector3 Maxs)>();
        var proxies = new Dictionary<int, int>();

        Vector3 Spot() => new(random.Next(-600, 600), random.Next(-600, 600), random.Next(-600, 600));
        (Vector3, Vector3) Box()
        {
            var at = Spot();
            return (at, at + new Vector3(random.Next(1, 90), random.Next(1, 90), random.Next(1, 90)));
        }

        for (var id = 0; id < 400; id++)
        {
            var (mins, maxs) = Box();
            boxes[id] = (mins, maxs);
            proxies[id] = tree.Create(mins, maxs, id);
        }

        // Churn it the way a merge does: move survivors, destroy the absorbed.
        for (var round = 0; round < 300; round++)
        {
            var live = boxes.Keys.ToList();
            var id = live[random.Next(live.Count)];
            if (round % 3 == 0 && live.Count > 50)
            {
                tree.Destroy(proxies[id]);
                boxes.Remove(id);
                proxies.Remove(id);
                continue;
            }
            var (mins, maxs) = Box();
            boxes[id] = (mins, maxs);
            tree.Move(proxies[id], mins, maxs);
        }

        Assert.Equal(boxes.Count, tree.Count);

        var found = new List<int>();
        var checks = 0;
        for (var probe = 0; probe < 200; probe++)
        {
            var (mins, maxs) = Box();
            found.Clear();
            tree.Query(mins, maxs, found);

            var expected = boxes.Where(b => Meets(mins, maxs, b.Value.Mins, b.Value.Maxs))
                                .Select(b => b.Key).Order().ToList();
            Assert.Equal(expected, found.Order());
            checks += expected.Count;
        }

        Assert.True(checks > 0, "every probe missed, so the comparison proved nothing");
        output.WriteLine($"{boxes.Count} live boxes, 200 queries, {checks} hits, all matching a scan");
    }

    /// <summary>
    /// The balance <c>18010a6e0</c> does, pinned by the one thing it is
    /// observable through. A query tests boxes, so it answers correctly whatever
    /// shape the tree is in, and every other test here would pass with the
    /// rotation removed. What it changes is the ORDER candidates come back in,
    /// and the proxy for that is the height: boxes inserted in a line are the
    /// degenerate case, and without a balance the tree becomes a list.
    /// </summary>
    [Fact]
    public void BoxesInsertedInALineStayShallow()
    {
        var tree = new VisBoxTree();
        const int Count = 4096;
        for (var i = 0; i < Count; i++)
            tree.Create(new Vector3(i * 8f, 0f, 0f), new Vector3((i * 8f) + 8f, 8f, 8f), i);

        Assert.Equal(Count, tree.Count);
        Assert.Null(tree.Validate(0));

        // A balanced tree of 4,096 leaves is about 12 deep and tolerates some
        // slack; an unbalanced one built from a sorted line is thousands.
        output.WriteLine($"{Count:n0} boxes in a line: height {tree.Height}");
        Assert.InRange(tree.Height, 1, 40);

        // And it still answers exactly, which is what the height must not cost.
        var found = new List<int>();
        tree.Query(new Vector3(1000f, 0f, 0f), new Vector3(1100f, 8f, 8f), found);
        var expected = Enumerable.Range(0, Count)
            .Where(i => i * 8f <= 1100f && (i * 8f) + 8f >= 1000f).ToList();
        Assert.Equal(expected, found.Order());
    }

    /// <summary>An empty tree answers nothing, and the pool grows past its first 32.</summary>
    [Fact]
    public void ItStartsEmptyAndGrowsPastTheFirstBlock()
    {
        var tree = new VisBoxTree();
        var found = new List<int>();
        tree.Query(Vector3.Zero, new Vector3(10f), found);
        Assert.Empty(found);
        Assert.Equal(0, tree.Count);

        for (var i = 0; i < VisBoxTree.FirstNodes * 4; i++)
            tree.Create(new Vector3(i), new Vector3(i + 1), i);

        Assert.Equal(VisBoxTree.FirstNodes * 4, tree.Count);
        tree.Query(new Vector3(-1f), new Vector3(1000f), found);
        Assert.Equal(VisBoxTree.FirstNodes * 4, found.Count);
    }

    /// <summary>A box that touches only at a face still meets, because the test is inclusive.</summary>
    [Fact]
    public void TouchingAtAFaceCounts()
    {
        var tree = new VisBoxTree();
        tree.Create(Vector3.Zero, new Vector3(10f), 7);

        var found = new List<int>();
        tree.Query(new Vector3(10f, 0f, 0f), new Vector3(20f, 10f, 10f), found);
        Assert.Equal([7], found);

        found.Clear();
        tree.Query(new Vector3(10.5f, 0f, 0f), new Vector3(20f, 10f, 10f), found);
        Assert.Empty(found);
    }
}
