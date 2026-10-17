using Source2.Compiler.Lighting.Bake;
using Xunit;

namespace Source2.Compiler.Tests;

public class Sfmt19937Tests
{
    /// <summary>SFMT-19937's reference output (SFMT.19937.out.txt, init_gen_rand(1234), 32-bit).</summary>
    [Fact]
    public void MatchesTheReferenceSequence()
    {
        var rng = new Sfmt19937(1234);
        uint[] expected = [3440181298, 1564997079, 1510669302, 2930277156, 1452439940, 3796268453, 423124208, 2143818589, 3827219408, 2987036003];
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(_ => rng.Next()));
    }
}
