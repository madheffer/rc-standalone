using System.Numerics;
using Source2.Compiler.Physics;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The region SVM a box hull gets: walking its tree from a point must land on
/// the feature nearest that point. Bit-exactness against Valve's is checked
/// on whole maps by <see cref="HullFromVmap"/> (<c>svm exact</c>).
/// </summary>
public class RegionSvmTests
{
    private static uint Walk(RegionSvm svm, Vector3 p)
    {
        var i = 0;
        while ((svm.Nodes[i] & 0x80000000) != 0)
        {
            var (n, d) = svm.Planes[(int)((svm.Nodes[i] >> 16) & 0x7fff)];
            var front = i + 1 + (int)(svm.Nodes[i] & 0xffff);
            i = Vector3.Dot(n, p) - d > 0 ? front : front + 1;
        }
        return svm.Nodes[i];
    }

    [Fact]
    public void BoxPointsLandOnTheirNearestFeature()
    {
        var hull = RnHullBuilder.CreateBox(new Vector3(10, 20, 30), new Vector3(1, 2, 3));
        var svm = RegionSvmBuilder.Build(hull);
        Assert.Equal(hull.Faces.Length + (2 * hull.Edges.Length), svm.Planes.Length);

        Assert.Equal(0u, Walk(svm, new Vector3(1, 2, 3)));

        var face = Walk(svm, new Vector3(1, 2, 100));
        Assert.Equal(0x60000000u, face & 0xe0000000);
        Assert.True(hull.Planes[(int)(face & 0xff)].Normal.Z > 0.99f);

        var corner = new Vector3(11, 22, 33);
        var vertex = Walk(svm, corner + new Vector3(5, 5, 5));
        Assert.Equal(0x20000000u, vertex & 0xe0000000);
        Assert.Equal(corner, hull.VertexPositions[(int)(vertex & 0xff)]);

        var edge = Walk(svm, new Vector3(1, 30, 40));
        Assert.Equal(0x40000000u, edge & 0xe0000000);
        var e = (int)(edge & 0xff);
        var ends = new[] { hull.VertexPositions[hull.Edges[e].Origin], hull.VertexPositions[hull.Edges[e ^ 1].Origin] };
        Assert.All(ends, v => Assert.Equal((22f, 33f), (v.Y, v.Z)));
    }
}
