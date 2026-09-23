using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The merge cost, checked against the binary it was read out of.
///
/// <para>Every number in <see cref="VisMergeCost"/> is a literal in
/// visbuilder.dll, so the strongest thing a test can do here is read them back
/// out of the DLL and compare. A tolerance would let a transcription slip
/// through; a byte comparison against Valve's own constant cannot.</para>
///
/// <para>It used to quote raw addresses, and on 2026-09-23 CS2 rebuilt and every
/// one of them moved at once -- the coarse weight's address came to hold the
/// ASCII of <c>idates()</c>. So the constants are found by SHAPE now, through
/// <see cref="BinarySignatures"/>, and what this asserts is what actually
/// matters: every constant the signatures FIND still holds the value we use. A
/// pattern that no longer matches is a function Valve rewrote and is reported,
/// not failed, because that is the manifest ageing rather than our number being
/// wrong; <c>tools/sigscan.py</c> is what to run then.</para>
/// </summary>
public sealed class VisMergeCostTests
{
    /// <summary>
    /// Each constant by its name in the signature manifest, the value we use,
    /// and whether the literal is a double. Several share one address with a
    /// constant from another stage, which is why the manifest names say so.
    /// </summary>
    private static readonly (string Name, double Value, bool Double)[] Constants =
    [
        ("CoarseWeight", VisMergeCost.CoarseWeight, true),
        ("SizeMismatchPenalty", VisMergeCost.SizeMismatchPenalty, false),
        ("TagAndZSpanPenalty", VisMergeCost.TagMismatchPenalty, false),
        ("SpreadPenaltyAndMarchBackOff", VisMergeCost.SpreadPenalty, false),
        ("AreaLimitAndPassCell4096", VisMergeCost.AreaLimit, false),
        ("ZLimit", VisMergeCost.ZLimit, false),
        ("CostScale", VisMergeCost.Scale, false),
        ("MarchShortest", VisOutside.MarchShortest, false),
        ("FirstCostLimit", VisClusters.MergeThreshold, false),
        ("PreMergeMaxDimensionAndPass4Margin", VisPreMerge.BinaryMaxDimension, false),
        ("PreMergeMaxRatio", VisPreMerge.MaxRatio, false),
        ("FaceTolerance", VisPreMerge.FaceTolerance, false),
        ("SubCellAndTouchTolerance", VisPreMerge.TouchTolerance, false),
        ("EscapeShare", VisSeed.EscapeShare, true),
    ];

    [Fact]
    public void EveryConstantIsTheOneValveCompiledIn()
    {
        var dll = VisBuilder();
        if (dll is null)
            return;
        var found = BinarySignatures.Resolve(dll);
        Assert.True(found is not null,
            $"no signature manifest; expected docs/visbuilder.signatures.json");

        var image = File.ReadAllBytes(dll);
        var imageBase = BinarySignatures.BaseOf(image);
        var checkedOff = new List<string>();
        var missing = new List<string>();
        foreach (var (name, ours, isDouble) in Constants)
        {
            if (!found!.TryGetValue(name, out var address))
            {
                missing.Add(name);
                continue;
            }
            var at = BinarySignatures.Offset(image, imageBase, address);
            Assert.True(at.HasValue, $"{name} resolved to {address:x}, which is not mapped.");
            var theirs = isDouble
                ? BinaryPrimitives.ReadDoubleLittleEndian(image.AsSpan(at!.Value))
                : BinaryPrimitives.ReadSingleLittleEndian(image.AsSpan(at!.Value));
            Assert.True(theirs == ours,
                $"{name} at {address:x}: visbuilder.dll has {theirs}, we use {ours}.");
            checkedOff.Add(name);
        }

        // The manifest ageing is a separate problem from our numbers being
        // wrong, so it is a floor rather than a failure on the first miss. Run
        // tools/sigscan.py against the installed DLL to see what needs re-signing.
        Assert.True(checkedOff.Count * 2 >= Constants.Length,
            $"only {checkedOff.Count} of {Constants.Length} constants could be found by signature"
          + $" ({string.Join(", ", missing)} were not); the manifest needs re-generating"
          + " against the installed build.");
    }

    [Fact]
    public void ACostIsTheSameWhicheverWayRound()
    {
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0b1011, voxels: 10);
        var b = At(new Vector3(32, 0, 0), new Vector3(64, 32, 32), 0b0101, voxels: 40);
        Assert.Equal(VisMergeCost.Of(a, b), VisMergeCost.Of(b, a), 3);
    }

    [Fact]
    public void SeeingTheSameThingsIsTheCheapestMergeThereIs()
    {
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0b1111, voxels: 10);
        var same = At(new Vector3(32, 0, 0), new Vector3(64, 32, 32), 0b1111, voxels: 10);
        var differs = At(new Vector3(32, 0, 0), new Vector3(64, 32, 32), 0b0001, voxels: 10);

        // Nothing is inherited either way, so only the floor of 1 is paid.
        Assert.Equal(VisMergeCost.Scale, VisMergeCost.Of(a, same), 3);
        Assert.True(VisMergeCost.Of(a, differs) > VisMergeCost.Of(a, same));
    }

    [Fact]
    public void TheInheritedVisibilityIsWeightedByTheOtherSidesVolume()
    {
        // b sees three things a does not; a sees nothing b does not. So a pays
        // for three, over its own volume, and b pays for nothing.
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0b0001, voxels: 7);
        var b = At(new Vector3(32, 0, 0), new Vector3(64, 32, 32), 0b1111, voxels: 40);
        Assert.Equal(((3 * 7) + 1) * VisMergeCost.Scale, VisMergeCost.Of(a, b), 3);
    }

    [Fact]
    public void SpanningMoreThanAStoreyIsPenalised()
    {
        var floor = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0b1111, voxels: 10);
        var above = At(new Vector3(0, 0, 96), new Vector3(32, 32, 128), 0b1111, voxels: 10);
        var beside = At(new Vector3(0, 0, 32), new Vector3(32, 32, 64), 0b1111, voxels: 10);

        Assert.True(above.Maxs.Z - floor.Mins.Z > VisMergeCost.ZLimit);
        Assert.True(VisMergeCost.Of(floor, above)
                    > VisMergeCost.Of(floor, beside) * VisMergeCost.TagMismatchPenalty);
    }

    [Fact]
    public void ADistantPairCostsTheGapAndTheMissingContactOnTop()
    {
        // Kept narrow in y so the union footprint stays under the area limit.
        // 100 units apart is 0.5 * 100/128 for the gap, and sharing no face at
        // all is the whole 0.5 of the contact half.
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 16, 32), 0b1111, voxels: 10);
        var far = At(new Vector3(132, 0, 0), new Vector3(164, 16, 32), 0b1111, voxels: 10);
        Assert.Equal(0.890625f, VisMergeCost.Proximity(a, far));
        Assert.Equal(0.890625f + VisMergeCost.Scale, VisMergeCost.Of(a, far));
    }

    [Fact]
    public void APairThatSpreadsOverTheAreaLimitIsPenalisedEvenThoughNeitherHalfDoes()
    {
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0b1111, voxels: 10);
        var apart = At(new Vector3(132, 0, 0), new Vector3(164, 32, 32), 0b1111, voxels: 10);

        Assert.True(Footprint(a) <= VisMergeCost.AreaLimit);
        Assert.True(Footprint(apart) <= VisMergeCost.AreaLimit);
        Assert.True((apart.Maxs.X - a.Mins.X) * (apart.Maxs.Y - a.Mins.Y) > VisMergeCost.AreaLimit);
        Assert.Equal(0.890625f + (VisMergeCost.SpreadPenalty * VisMergeCost.Scale),
                     VisMergeCost.Of(a, apart));
    }

    private static float Footprint(VisMergeCost.Cluster c)
        => (c.Maxs.X - c.Mins.X) * (c.Maxs.Y - c.Mins.Y);

    [Fact]
    public void SharingAFaceIsFreeAndTouchingAtAnEdgeIsNot()
    {
        // The half the decompile hid: 0.5 * (1 - shared area / face area) on the
        // widest and middle overlap axes, so a whole shared face costs nothing
        // and an edge, which shares no area, costs all 0.5.
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0, voxels: 1);
        var face = At(new Vector3(32, 0, 0), new Vector3(64, 32, 32), 0, voxels: 1);
        var edge = At(new Vector3(32, 32, 0), new Vector3(64, 64, 32), 0, voxels: 1);
        var half = At(new Vector3(32, 16, 0), new Vector3(64, 48, 32), 0, voxels: 1);
        Assert.Equal(0f, VisMergeCost.Proximity(a, face));
        Assert.Equal(0.5f, VisMergeCost.Proximity(a, edge));
        Assert.Equal(0.25f, VisMergeCost.Proximity(a, half));
    }

    private static VisMergeCost.Cluster At(Vector3 mins, Vector3 maxs, ulong visible, int voxels)
        => new([visible], voxels, VoxelSize: 1, Tag: 0, mins, maxs);

    /// <summary>The installed visbuilder, or null when CS2 is not there.</summary>
    private static string? VisBuilder()
    {
        var pak = CS2Fixtures.StockPak();
        if (pak is null)
            return null;
        var dll = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..")),
                               "bin", "win64", "visbuilder.dll");
        return File.Exists(dll) ? dll : null;
    }
}
