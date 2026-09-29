using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary><see cref="TangentFrame"/> against probe01's cluster model (vertices 0-2, packed from face 6 of mesh 101).</summary>
public class TangentFrameTests
{
    [Theory]
    [InlineData(-6.143906E-08f, 5.5096223E-08f, 1f, -1.6505884E-07f, -1f, 5.5096216E-08f, -1f, 0x801FFBFEu)]
    [InlineData(0.12309153f, 0.123091444f, 0.98473203f, 0.022668863f, -0.99236774f, 0.1212123f, -1f, 0x8CE33C08u)]
    [InlineData(0.12309144f, -0.12309157f, 0.9847319f, -0.022669569f, -0.99236774f, -0.12121236f, -1f, 0x73233BF4u)]
    public void PacksAsValveDoes(float nx, float ny, float nz, float tx, float ty, float tz, float tw, uint packed)
        => Assert.Equal(packed, TangentFrame.Compress(new Vector3(nx, ny, nz), new Vector4(tx, ty, tz, tw)));

    // The asin polynomial's constants, as the binary stores them (1823f78d8 and on).
    [Theory]
    [InlineData(0.430477f, 0x3edc677b)]
    [InlineData(0.0594935f, 0x3d73af75)]
    [InlineData(0.779384f, 0x3f4785b6)]
    [InlineData(1.24068f, 0x3f9ece9a)]
    [InlineData(0.216183f, 0x3e5d5f14)]
    [InlineData(1.00008f, 0x3f80029f)]
    [InlineData(57.295776f, 0x42652ee0)]
    [InlineData(0.0027777778f, 0x3b360b61)]
    public void ConstantsMatchTheBinary(float value, int bits) => Assert.Equal(bits, BitConverter.SingleToInt32Bits(value));
}
