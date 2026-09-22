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
/// through; a byte comparison against Valve's own constant cannot, and it also
/// fails the day a CS2 update changes one, which is exactly when we want to
/// know.</para>
/// </summary>
public sealed class VisMergeCostTests
{
    /// <summary>Where each constant lives, as a virtual address in the DLL.</summary>
    private static readonly (string Name, ulong Address, double Value, bool Double)[] Constants =
    [
        ("CoarseWeight", 0x18017f128, VisMergeCost.CoarseWeight, true),
        ("SizeMismatchPenalty", 0x18017f1a4, VisMergeCost.SizeMismatchPenalty, false),
        ("TagMismatchPenalty", 0x18017f190, VisMergeCost.TagMismatchPenalty, false),
        ("SpreadPenalty", 0x18017f170, VisMergeCost.SpreadPenalty, false),
        ("AreaLimit", 0x18017f1cc, VisMergeCost.AreaLimit, false),
        ("ZLimit", 0x18017f19c, VisMergeCost.ZLimit, false),
        ("Scale", 0x18017f174, VisMergeCost.Scale, false),
    ];

    [Fact]
    public void EveryConstantIsTheOneValveCompiledIn()
    {
        var dll = VisBuilder();
        if (dll is null)
            return;

        var image = File.ReadAllBytes(dll);
        foreach (var (name, address, ours, isDouble) in Constants)
        {
            var at = FileOffsetOf(image, address);
            Assert.True(at.HasValue, $"{name} at {address:x} is outside the image.");
            var theirs = isDouble
                ? BinaryPrimitives.ReadDoubleLittleEndian(image.AsSpan(at!.Value))
                : BinaryPrimitives.ReadSingleLittleEndian(image.AsSpan(at!.Value));
            Assert.True(theirs == ours, $"{name}: visbuilder.dll has {theirs}, we use {ours}.");
        }
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
    public void ADistantPairCostsTheGapOnTop()
    {
        // Kept narrow in y so the union footprint stays under the area limit and
        // the gap is the only thing separating this from a touching pair.
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 16, 32), 0b1111, voxels: 10);
        var far = At(new Vector3(132, 0, 0), new Vector3(164, 16, 32), 0b1111, voxels: 10);
        Assert.Equal(100f, VisMergeCost.Distance(a, far), 3);
        Assert.Equal(100f + VisMergeCost.Scale, VisMergeCost.Of(a, far), 3);
    }

    [Fact]
    public void APairThatSpreadsOverTheAreaLimitIsPenalisedEvenThoughNeitherHalfDoes()
    {
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0b1111, voxels: 10);
        var apart = At(new Vector3(132, 0, 0), new Vector3(164, 32, 32), 0b1111, voxels: 10);

        Assert.True(Footprint(a) <= VisMergeCost.AreaLimit);
        Assert.True(Footprint(apart) <= VisMergeCost.AreaLimit);
        Assert.True((apart.Maxs.X - a.Mins.X) * (apart.Maxs.Y - a.Mins.Y) > VisMergeCost.AreaLimit);
        Assert.Equal(100f + (VisMergeCost.SpreadPenalty * VisMergeCost.Scale),
                     VisMergeCost.Of(a, apart), 3);
    }

    private static float Footprint(VisMergeCost.Cluster c)
        => (c.Maxs.X - c.Mins.X) * (c.Maxs.Y - c.Mins.Y);

    [Fact]
    public void TouchingBoxesHaveNoGap()
    {
        var a = At(new Vector3(0, 0, 0), new Vector3(32, 32, 32), 0, voxels: 1);
        var b = At(new Vector3(32, 0, 0), new Vector3(64, 32, 32), 0, voxels: 1);
        Assert.Equal(0f, VisMergeCost.Distance(a, b), 5);
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

    /// <summary>Map a virtual address onto an offset in the file on disk.</summary>
    private static int? FileOffsetOf(byte[] image, ulong address)
    {
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        var optional = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(pe + 48));

        var rva = (long)(address - imageBase);
        for (var i = 0; i < sections; i++)
        {
            var header = pe + 24 + optional + (i * 40);
            var virtualSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 8));
            var virtualAddress = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 12));
            var rawSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 16));
            var rawOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 20));
            if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
                return (int)(rawOffset + (rva - virtualAddress));
        }
        return null;
    }
}
