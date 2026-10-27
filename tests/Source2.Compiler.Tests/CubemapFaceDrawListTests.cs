using Source2.Compiler.Cubemaps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary><see cref="CubemapFaceDrawList.MaterialOrder"/> on the measured engine materials (GROUND_TRUTH 156).</summary>
public sealed class CubemapFaceDrawListTests
{
    [Fact]
    public void EngineMaterialsLoad()
    {
        Assert.Equal(957, CubemapFaceDrawList.EngineMaterials.Count);
        Assert.Contains("materials/dev/reflectivity_30.vmat", CubemapFaceDrawList.EngineMaterials);
        Assert.DoesNotContain("materials/dev/dev_measuregeneric01.vmat", CubemapFaceDrawList.EngineMaterials);
    }

    /// <summary>probe01's two aggregates: reflectivity_30 is an engine material, so it is drawn first (as in every captured run).</summary>
    [Fact]
    public void EngineMaterialsComeFirst()
    {
        Assert.Equal([1, 0], CubemapFaceDrawList.MaterialOrder(["materials/dev/dev_measuregeneric01.vmat", "materials/dev/reflectivity_30.vmat"]));
        // Engine materials in creation order, then the map's by path, then a missing material.
        Assert.Equal([2, 1, 3, 0], CubemapFaceDrawList.MaterialOrder([null, "materials/dev/reflectivity_30.vmat", "materials/dev/roomwalls_white.vmat", "materials/a.vmat"]));
    }
}
