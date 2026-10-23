using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays resourcecompiler's own simplifications captured by
/// <c>tools/physics/capture_mapsimplify.py</c> (<c>MAPSIMPLIFY=&lt;capture&gt;</c>):
/// each call's input through <see cref="MeshSimplifier"/>; a map builder call
/// (a brush entity piece) must give the next MapBuilder_TriangleMesh input
/// float for float, a CMesh_Simplify call (pre-settle world, cables) the
/// output its extraction hands back.
/// </summary>
public class MapSimplifyReplay(ITestOutputHelper output)
{
    /// <summary>The map builder's call site's return address in the installed build (rc 0923: 181308a99).</summary>
    private const int MapBuilderCaller = 19963406;

    [Fact]
    public void SimplificationsMatchValve()
    {
        if (Environment.GetEnvironmentVariable("MAPSIMPLIFY") is not { } path)
            return;
        var records = SimplifyReplay.Records(path).ToList();
        int builder = 0, builderExact = 0, cmesh = 0, cmeshExact = 0, shown = 0;
        for (var k = 0; k < records.Count; k++)
        {
            if (records[k].Head.GetProperty("ev").GetString() != "run")
                continue;
            // tris, points, run[, tris (the extraction's output)]
            var tris = MemoryMarshal.Cast<byte, int>(records[k - 2].Blob).ToArray();
            var points = MemoryMarshal.Cast<byte, float>(records[k - 1].Blob).ToArray();
            var error = records[k].Head.GetProperty("error").GetDouble();
            var caller = records[k].Head.GetProperty("caller").GetInt32();
            var (pos, idx) = MeshSimplifier.Run(points, tris, error, -1);
            var mine = pos.Select(x => (float)x).ToArray();
            bool same;
            if (caller == MapBuilderCaller)
            {
                builder++;
                var next = records.Skip(k + 1).First(r => r.Head.GetProperty("ev").GetString() == "trimesh");
                var n = next.Head.GetProperty("n").GetInt32();
                var want = MemoryMarshal.Cast<byte, float>(next.Blob.AsSpan(0, n * 12)).ToArray();
                var wantIdx = MemoryMarshal.Cast<byte, int>(next.Blob.AsSpan(n * 12)).ToArray();
                same = idx.AsSpan().SequenceEqual(wantIdx) && mine.Select(BitConverter.SingleToInt32Bits).SequenceEqual(want.Select(BitConverter.SingleToInt32Bits));
                if (same)
                    builderExact++;
            }
            else
            {
                cmesh++;
                var outTris = MemoryMarshal.Cast<byte, int>(records[k + 1].Blob).ToArray();
                same = idx.AsSpan().SequenceEqual(outTris);
                if (same)
                    cmeshExact++;
            }
            if (!same && shown++ < 10)
                output.WriteLine($"run {records[k].Head.GetProperty("id")} caller {caller} error {error}: {points.Length / 3} vertices {tris.Length / 3} triangles in, ours {idx.Length / 3} triangles");
        }
        output.WriteLine($"map builder: {builderExact} of {builder} exact; CMesh_Simplify: {cmeshExact} of {cmesh} triangle lists exact");
        Assert.Equal(builder, builderExact);
        Assert.Equal(cmesh, cmeshExact);
    }
}
