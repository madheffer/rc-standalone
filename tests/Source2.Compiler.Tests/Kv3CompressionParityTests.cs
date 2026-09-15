using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the per-block KV3 compression choice to resourcecompiler's, against the real
/// RC outputs in <c>RcReference/</c>.
///
/// <para>RC does not compress uniformly and does not decide per resource type: of the two
/// <c>.vdata_c</c> references, <c>rc_probe</c> carries DATA uncompressed and
/// <c>rc_probe_ints</c> carries it LZ4. Those two files are what make this test
/// discriminating - a uniform rule in either direction fails one of them.</para>
/// </summary>
public class Kv3CompressionParityTests
{
    private static string Reference(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "RcReference", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"RcReference/{name} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>compressionMethod per KV3 block type, read straight out of the container.</summary>
    private static Dictionary<string, uint> CompressionByBlock(byte[] bytes)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        var blockOffset = BitConverter.ToUInt32(bytes, 8);
        var blockCount = BitConverter.ToUInt32(bytes, 12);
        for (var i = 0; i < blockCount; i++)
        {
            var entry = (int)(8 + blockOffset + (i * 12));
            var type = System.Text.Encoding.ASCII.GetString(bytes, entry, 4);
            var payload = entry + 4 + (int)BitConverter.ToUInt32(bytes, entry + 4);
            if (payload + 24 > bytes.Length)
            {
                continue;
            }

            // KV3 magic (v4 0x4B563304 / v5 0x4B563305), then a 16-byte format guid,
            // then the compression method.
            var magic = BitConverter.ToUInt32(bytes, payload);
            if (magic is not (0x4B563304 or 0x4B563305))
            {
                continue;
            }

            result[type] = BitConverter.ToUInt32(bytes, payload + 20);
        }

        return result;
    }

    [Theory]
    [InlineData("rc_probe.vdata", "rc_probe.vdata_c")]
    [InlineData("rc_probe_ints.vdata", "rc_probe_ints.vdata_c")]
    [InlineData("rc_probe.vsndevts", "rc_probe.vsndevts_c")]
    [InlineData("rc_probe_refs.vpcf", "rc_probe_refs.vpcf_c")]
    public void EveryBlockWeAuthorUsesTheCompressionRcChose(string source, string rcOutput)
    {
        var ours = CompressionByBlock(
            Kv3SourceCompiler.Compile(File.ReadAllBytes(Reference(source)), Path.GetExtension(source)));
        var valve = CompressionByBlock(File.ReadAllBytes(Reference(rcOutput)));

        Assert.NotEmpty(ours);
        foreach (var (block, valveMethod) in valve)
        {
            // FLCI is editor-only line-map metadata this compiler deliberately does not
            // author, so there is nothing of ours to compare.
            if (block == "FLCI" || !ours.TryGetValue(block, out var ourMethod))
            {
                continue;
            }

            Assert.True(valveMethod == ourMethod,
                $"{rcOutput} {block}: RC used compressionMethod={valveMethod}, we used {ourMethod}");
        }
    }

    [Fact]
    public void TheThresholdSitsInTheWindowTheSamplesBracket()
    {
        // Largest block RC left uncompressed was 252 B; smallest it compressed was 279 B,
        // measured over ~3,600 Valve-compiled v5 blocks. A threshold outside that window
        // contradicts the corpus, whichever round number is eventually confirmed.
        Assert.InRange(AuthoredKv3.CompressionThreshold, 253, 279);
    }
}
