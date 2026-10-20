using System.Runtime.InteropServices;
using Source2.Compiler.RcTexture;
using TinyBCSharp;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary><see cref="Bc6hDecoder"/> against TinyBCSharp's BC6H_UF16 decoder (MIT) on random blocks of every mode.</summary>
public sealed class Bc6hDecoderTests(ITestOutputHelper output)
{
    [Fact]
    public void RandomBlocksDecodeAsTinyBcSharp()
    {
        var tiny = BlockDecoder.Create(BlockFormat.BC6HUf16);
        var rng = new Random(6);
        var block = new byte[16];
        var theirs = new byte[16 * 4 * 2];
        var ours = new ushort[48];
        int total = 0, differ = 0;
        var modes = new HashSet<int>();
        for (var i = 0; i < 200000; i++)
        {
            rng.NextBytes(block);
            // Every mode code evenly: the low two bits, then for the 5-bit modes the next three.
            var mode = i % 32;
            block[0] = (byte)((block[0] & 0xe0) | mode);
            modes.Add((mode & 2) == 0 ? mode & 3 : mode);
            tiny.Decode(block, 4, 4, theirs, 4, 4);
            Bc6hDecoder.DecodeBlock(block, ours);
            var halves = MemoryMarshal.Cast<byte, ushort>(theirs);
            total++;
            var same = true;
            for (var p = 0; p < 16 && same; p++)
                for (var c = 0; c < 3; c++)
                    same &= halves[p * 4 + c] == ours[p * 3 + c];
            if (!same && differ++ < 8)
                output.WriteLine($"mode {mode:x2} block {Convert.ToHexString(block)}: ours {ours[0]:x4} {ours[1]:x4} {ours[2]:x4}, tiny {halves[0]:x4} {halves[1]:x4} {halves[2]:x4}");
        }
        output.WriteLine($"{total - differ} of {total} blocks equal ({modes.Count} mode codes)");
        Assert.Equal(0, differ);
    }
}
