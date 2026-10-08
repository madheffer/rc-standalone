using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The vvis's ___OverrideInputData___ fingerprint: the CRC32 of RC's vis input
/// blob, captured on this machine's paths (capture_overrides.py, probe01
/// -world -vis, 2026-10-09; cardtest's value from its reference package).
/// </summary>
public class VisInputFingerprintTests
{
    private const string Temp = @"C:\Users\User\AppData\Local\Temp\";
    private const string Game = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game";
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content";

    [Theory]
    [InlineData("probe01", 1380197390u)]
    [InlineData("cardtest", 3853475347u)]
    public void MatchesValve(string map, uint expected)
        => Assert.Equal(expected, VisibilityAuthor.InputFingerprint(Temp, Game, Content, "s2c_rc_probe", map));

    [Fact]
    public void BlobIsBinaryKeyValues()
    {
        var blob = VisibilityAuthor.InputBlob(Temp, Game, Content, "s2c_rc_probe", "probe01");
        Assert.Equal(412, blob.Length);
        Assert.Equal("VBKV"u8.ToArray(), blob[..4]);
        Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(blob.AsSpan(8)), BitConverter.ToUInt32(blob, 4));
    }
}
