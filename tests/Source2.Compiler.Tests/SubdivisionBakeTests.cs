using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="SubdivisionBake"/> (the half-edge emulation of the builder's
/// bake) against <see cref="MeshTessellation.TriangulateBuilder"/> (the
/// measured model, exact where it says it covers a mesh): every subdivided
/// mesh of <c>BAKECMP=&lt;vmap&gt;[|&lt;vmap&gt;...]</c> the model covers must
/// come out the same, triangle for triangle.
/// </summary>
public class SubdivisionBakeTests(ITestOutputHelper output)
{
    [Fact]
    public void MatchesTheModelWhereItIsExact()
    {
        if (Environment.GetEnvironmentVariable("BAKECMP") is not { Length: > 0 } spec)
            return;
        int same = 0, differ = 0, stitched = 0;
        foreach (var path in spec.Split('|'))
        {
            var doc = DmxBinary.ReadFile(path);
            foreach (var mesh in MapMeshes.Read(doc).DistinctBy(m => m.NodeId))
            {
                var data = mesh.Element?.Get<DmxBinary.Element>("meshData");
                var levels = data?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels");
                if (data == null || levels == null || !levels.Any(x => x is int i && i > 0))
                    continue;
                var (model, covered) = MeshTessellation.TriangulateBuilder(data);
                MeshTessellation.Result bake;
                try
                {
                    bake = SubdivisionBake.Bake(data);
                }
                catch (Exception ex)
                {
                    differ++;
                    output.WriteLine($"{Path.GetFileName(path)} node {mesh.NodeId}: bake threw {ex.GetType().Name}: {ex.Message} {(differ == 1 ? ex.StackTrace : "")}");
                    continue;
                }
                if (!covered)
                {
                    stitched++;
                    output.WriteLine($"{Path.GetFileName(path)} node {mesh.NodeId}: stitched; model {model.Faces.Count} triangles, bake {bake.Faces.Count}");
                    continue;
                }
                var first = FirstDifference(model, bake);
                if (first == null)
                    same++;
                else
                {
                    differ++;
                    output.WriteLine($"{Path.GetFileName(path)} node {mesh.NodeId}: {first}");
                }
            }
        }
        output.WriteLine($"covered meshes: {same} same, {differ} differ; {stitched} stitched");
        Assert.Equal(0, differ);
    }

    private static string? FirstDifference(MeshTessellation.Result a, MeshTessellation.Result b)
    {
        if (a.Faces.Count != b.Faces.Count)
            return $"{a.Faces.Count} triangles against {b.Faces.Count}";
        for (var t = 0; t < a.Faces.Count; t++)
        {
            for (var k = 0; k < 3; k++)
            {
                var pa = a.Positions[a.Indices[(t * 3) + k]];
                var pb = b.Positions[b.Indices[(t * 3) + k]];
                if (!Bits(pa, pb))
                    return $"triangle {t} corner {k}: {pa} against {pb} (faces {a.Faces[t]} / {b.Faces[t]})";
            }
            if (a.Faces[t] != b.Faces[t])
                return $"triangle {t}: face {a.Faces[t]} against {b.Faces[t]}";
        }
        return null;
    }

    private static bool Bits(Vector3 a, Vector3 b)
        => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
           && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
           && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);
}
