using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// CompressTangentFrame's inputs and words as Valve's compile made them
/// (tools/vis/capture_tangentframe.py on atixref, 2026-10-09: the agg_prop
/// vertices whose angle comes out at 180 degrees), through TangentFrame.
/// </summary>
public class TangentFrameCaptureTests
{
    private static float F(string hex) => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(hex, 16)));

    [Theory]
    [InlineData("3ab559dc,bf3229d3,3f37d4a2", "3f7fffef,3a12ebb4,bab559dd,bf800000", 0x41200800u)]
    [InlineData("3ab559dc,3f3229d3,3f37d4a2", "3f7fffef,ba12e347,bab55df2,bf800000", 0xbee00800u)]
    [InlineData("3ab559dc,bf3229d3,3f37d4a2", "bf7fffe9,bad9eaa8,39a56469,bf800000", 0x41200800u)]
    [InlineData("3ab39966,bf19fe11,3f4c8138", "bf7fffe8,badcc299,39ea5c34,bf800000", 0x49200800u)]
    [InlineData("3ab38037,3f4d180d,3f1934f7", "3f7fffee,ba33e75b,bab384eb,bf800000", 0xc9200800u)]
    [InlineData("3ab38038,bf4d180d,3f1934fa", "3f7fffee,3a33ee60,bab38038,bf800000", 0x36e00800u)]
    [InlineData("3ab39966,bf19fe11,3f4c8138", "3f7ffff0,39f03b40,bab39968,bf800000", 0x49200800u)]
    [InlineData("3a9e88bd,3e8c934a,3f762961", "3f7ffff5,b931581c,ba9e8a5a,bf800000", 0x9c600800u)]
    [InlineData("3ab38038,bf4d180d,3f1934fa", "bf7fffea,bad25b7e,3912af38,bf800000", 0x36e00800u)]
    public void MatchesValve(string normal, string tangent, uint word)
    {
        var n = normal.Split(',').Select(F).ToArray();
        var t = tangent.Split(',').Select(F).ToArray();
        Assert.Equal(word.ToString("x8"), TangentFrame.Compress(new Vector3(n[0], n[1], n[2]), new Vector4(t[0], t[1], t[2], t[3])).ToString("x8"));
    }
}
