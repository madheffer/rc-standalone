using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The signature manifest, held to the job it exists for: finding visbuilder's
/// addresses again in whatever build is installed.
///
/// <para>This is the thing that would have saved a day. Every address in the vis
/// notes was a literal offset into the build of 2026-07-09; CS2 rebuilt on
/// 2026-09-23 and all of them moved at once. Signatures find them by shape, and
/// what has to be true for that to keep working is that most patterns still
/// match, each exactly once.</para>
/// </summary>
public class BinarySignatureTests(ITestOutputHelper output)
{
    /// <summary>
    /// How much of the manifest must still resolve. It is a floor, not a
    /// target: a rebuild is expected to cost a few symbols, and the ones it
    /// costs are named so they can be re-signed. Falling under this means the
    /// manifest has aged out rather than drifted.
    /// </summary>
    public const double Floor = 0.8;

    [Fact]
    public void MostOfTheManifestStillFindsItsSymbol()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var dll = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..")),
                               "bin", "win64", "visbuilder.dll");
        if (!File.Exists(dll))
            return;

        Assert.True(BinarySignatures.ManifestPath() is not null,
            "docs/visbuilder.signatures.json is missing; regenerate it with MakeSignatures.java");
        var found = BinarySignatures.Resolve(dll)!;
        var total = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(BinarySignatures.ManifestPath()!))
            .RootElement.GetProperty("symbols").EnumerateArray().Count();

        var lost = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(BinarySignatures.ManifestPath()!))
            .RootElement.GetProperty("symbols").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()!)
            .Where(name => !found.ContainsKey(name))
            .ToList();

        output.WriteLine($"{found.Count} of {total} symbols resolve in {Path.GetFileName(dll)}"
                       + $" ({(double)found.Count / total:P1})");
        if (lost.Count > 0)
            output.WriteLine($"  not found: {string.Join(", ", lost)}");

        Assert.True(found.Count >= total * Floor,
            $"only {found.Count} of {total} signatures still match ({string.Join(", ", lost)}"
          + "); run tools/sigscan.py against the installed DLL and re-sign what it names.");
    }

    /// <summary>
    /// A resolved address has to be worth something, so check the ones whose
    /// value we know independently. These are the numbers the merge is built
    /// from, read back out of whichever build is on the machine.
    /// </summary>
    [Fact]
    public void TheConstantsItFindsHoldWhatTheyShould()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var dll = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, "..")),
                               "bin", "win64", "visbuilder.dll");
        if (!File.Exists(dll) || BinarySignatures.Resolve(dll) is not { } found)
            return;

        (string Name, double Value, bool Double)[] expected =
        [
            ("Half", 0.5, false),
            ("One", 1.0, false),
            ("ZLimit", VisMergeCost.ZLimit, false),
            ("PassCell512", VisClusterSet.Passes[0].Cell, false),
            ("PassCell2048", VisClusterSet.Passes[2].Cell, false),
            ("AreaLimitAndPassCell4096", VisClusterSet.Passes[4].Cell, false),
            ("FineBoxSizeAndPass2Margin", VisClusterSet.Passes[1].Margin, false),
            ("PreMergeMaxDimensionAndPass4Margin", VisClusterSet.Passes[3].Margin, false),
            ("BudgetMult1", VisClusterSet.Passes[0].Budget, false),
            ("BudgetMult2", VisClusterSet.Passes[1].Budget, false),
            ("BudgetMult3", VisClusterSet.Passes[2].Budget, false),
            ("BudgetMult4", VisClusterSet.Passes[3].Budget, false),
            ("BudgetMult5", VisClusterSet.Passes[4].Budget, false),
            ("SpreadPenaltyAndMarchBackOff", VisOutside.MarchBackOff, false),
            ("Better", 1e-4f, false),
            ("Tie", 1e-3f, false),
            ("LoopSeedCostAndSlack", -1.0, false),
            ("GainFloor", -1e-4f, false),
        ];

        var image = File.ReadAllBytes(dll);
        var imageBase = BinarySignatures.BaseOf(image);
        var read = 0;
        foreach (var (name, ours, isDouble) in expected)
        {
            if (!found.TryGetValue(name, out var address))
                continue;
            var at = BinarySignatures.Offset(image, imageBase, address);
            Assert.True(at.HasValue, $"{name} resolved to {address:x}, which is not mapped.");
            var theirs = isDouble
                ? System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(image.AsSpan(at!.Value))
                : System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(image.AsSpan(at!.Value));
            Assert.True(theirs == ours,
                $"{name} at {address:x}: visbuilder.dll has {theirs}, we use {ours}.");
            read++;
        }
        output.WriteLine($"{read} of {expected.Length} known constants read back and matched");
        Assert.True(read >= expected.Length * BinarySignatureTests.Floor,
            $"only {read} of {expected.Length} known constants could be found by signature.");
    }
}
