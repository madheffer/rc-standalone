using System.Numerics;
using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The small pieces of tier0 and the map compiler the entity lump leans on, each
/// pinned to what the decompiled function does. None of these need a game
/// install.
/// </summary>
public class EntityValueParsingTests
{
    [Theory]
    // V_atoi reads the longest prefix: a decimal truncates, trailing text is
    // ignored, and it knows hex and a quoted character.
    [InlineData("0.600000", 0)]
    [InlineData("  -12abc", -12)]
    [InlineData("+7", 7)]
    [InlineData("0x1F", 31)]
    [InlineData("'A", 65)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void Atoi_ReadsAPrefix(string text, long expected)
        => Assert.Equal(expected, CNumbers.Atoi(text));

    [Theory]
    // The V_StringTo* family is strict: anything that does not parse to the end
    // is the default of 0 resourcecompiler passes.
    [InlineData("7", 7)]
    [InlineData(" 7", 7)]
    [InlineData("-3", -3)]
    [InlineData("12abc", 0)]
    [InlineData("1.5", 0)]
    [InlineData("", 0)]
    [InlineData("99999999999", 0)]
    public void ToInt32_IsStrict(string text, int expected)
        => Assert.Equal(expected, CNumbers.ToInt32(text));

    [Theory]
    [InlineData("6144", 6144u)]
    [InlineData("-1", 0u)]
    [InlineData("4294967295", uint.MaxValue)]
    [InlineData("4294967296", 0u)]
    [InlineData("2 ", 0u)]
    public void ToUInt32_IsStrict(string text, uint expected)
        => Assert.Equal(expected, CNumbers.ToUInt32(text));

    [Theory]
    [InlineData("0.1", 0.1f)]
    [InlineData("  +2", 2f)]
    [InlineData("-1e3", -1000f)]
    [InlineData("1.5 ", 0f)]
    [InlineData("1.5x", 0f)]
    [InlineData("", 0f)]
    public void ToFloat32_NeedsTheWholeString(string text, float expected)
        => Assert.Equal(expected, CNumbers.ToFloat32(text));

    [Fact]
    public void FloatArray_StopsAtTheFirstBadValueAndZeroFills()
    {
        Assert.Equal([1f, 2f, 0f], CNumbers.FloatArray("1 2", 3));
        Assert.Equal([1f, 0f, 0f], CNumbers.FloatArray("1 x 3", 3));
        Assert.Equal([-1.5f, 2f, 3f], CNumbers.FloatArray(" -1.5\t+2 3 4", 3));
        Assert.Equal([0f, 0f, 0f, 0f], CNumbers.FloatArray("", 4));
    }

    [Fact]
    public void Scan_TakesWhatMatches()
    {
        Assert.Equal([1.5f, 0f], CNumbers.Scan("1.5", 2));
        Assert.Equal([-6.268013f, 0f], CNumbers.Scan("-6.268013 0 0", 2));
    }

    [Theory]
    [InlineData("255 0 10", 255, 0, 10, 255)]
    [InlineData("1 2 3 4", 1, 2, 3, 4)]
    [InlineData("FF8000", 255, 128, 0, 255)]
    [InlineData("11223344", 0x11, 0x22, 0x33, 0x44)]
    // Trailing blanks, a missing channel, one out of range or text after the
    // values all make it black with a full alpha.
    [InlineData("255 255 255 ", 0, 0, 0, 255)]
    [InlineData("1 2", 0, 0, 0, 255)]
    [InlineData("256 0 0", 0, 0, 0, 255)]
    [InlineData("-1 0 0", 0, 0, 0, 255)]
    [InlineData("", 0, 0, 0, 255)]
    public void ToColor_FollowsVStringToColor(string text, int r, int g, int b, int a)
        => Assert.Equal(((byte)r, (byte)g, (byte)b, (byte)a), CNumbers.ToColor(text));

    [Theory]
    [InlineData("  3.5abc", 3.5f)]
    [InlineData("2", 2f)]
    [InlineData("x", 0f)]
    public void Atof_ReadsAPrefix(string text, float expected)
        => Assert.Equal(expected, CNumbers.Atof(text));

    [Theory]
    // The wildcards are in the entity's own name, the first argument.
    [InlineData("door*", "door_left", true)]
    [InlineData("Door", "DOOR", true)]
    [InlineData("door", "door2", false)]
    [InlineData("d?or", "door", true)]
    [InlineData("*", "anything", true)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*c", "abcd", false)]
    [InlineData("door**", "door", true)]
    [InlineData("", "x", false)]
    [InlineData("x", "", false)]
    public void Wildcard_MatchesAsTier0Does(string name, string literal, bool expected)
        => Assert.Equal(expected, Wildcard.Matches(name, literal));

    [Theory]
    [InlineData("flag_banner_01", "vpcf", "flag_banner_01.vpcf")]
    [InlineData("Materials\\Sprites\\Glow.vmt", "vmat", "materials/sprites/glow.vmat")]
    [InlineData("models/Props/Crate.vmdl", "vmdl", "models/props/crate.vmdl")]
    [InlineData("a/./b/../c.vmdl", "vmdl", "a/c.vmdl")]
    [InlineData("a//b.vmdl", "vmdl", "a/b.vmdl")]
    [InlineData("/rooted.vmdl", "vmdl", "")]
    [InlineData("c:/absolute.vmdl", "vmdl", "")]
    [InlineData("", "vmdl", "")]
    [InlineData("Some\\Path.XML", "", "some/path.xml")]
    public void ResourcePath_FixesUpAsTheLumpWriterDoes(string text, string extension, string expected)
        => Assert.Equal(expected, ResourcePath.Fixup(text, extension));

    [Fact]
    public void TemplateTransform_ReadsAnglesBackThroughAMatrix()
    {
        // Valve's atixref template members at yaw 270 ship as [-0, -90, 0]: the
        // pitch is atan2 of a negative zero.
        var (origin, angles) = TemplateTransform.Relative(
            new Vector3(1, 2, 3), Vector3.Zero, new Vector3(10, 20, 30), new Vector3(0, 270, 0));
        Assert.Equal(new Vector3(9, 18, 27), origin);
        Assert.True(float.IsNegative(angles.X) && angles.X == 0f, $"pitch {angles.X} is not -0");
        Assert.Equal(-90f, angles.Y, 3);
        Assert.Equal(0f, angles.Z);
    }

    [Fact]
    public void TemplateTransform_RotatesIntoTheTemplatesFrame()
    {
        // A template turned 90 degrees: a member 10 units along world +Y sits 10
        // units along the template's own +X.
        var (origin, angles) = TemplateTransform.Relative(
            Vector3.Zero, new Vector3(0, 90, 0), new Vector3(0, 10, 0), new Vector3(0, 90, 0));
        Assert.Equal(10f, origin.X, 4);
        Assert.Equal(0f, origin.Y, 4);
        Assert.Equal(0f, angles.Y, 4);
    }

    [Fact]
    public void MatrixAngles_TakesTheGimbalBranchStraightUp()
    {
        var m = TemplateTransform.AngleMatrix(Vector3.Zero, new Vector3(-90, 0, 0));
        var angles = TemplateTransform.MatrixAngles(m);
        Assert.Equal(-90f, angles.X, 3);
        Assert.Equal(0f, angles.Z);
    }
}
