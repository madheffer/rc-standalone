using System.Text.RegularExpressions;
using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The same comparison as <see cref="EntityLumpAuthorTests"/>, on maps that still
/// have gaps, asserting that every difference left is one of the gaps we have
/// WRITTEN DOWN.
///
/// <para>A test that simply skips an unfinished area stops reporting the day it
/// starts mattering. This one keeps running the real comparison and fails on any
/// difference outside the known list, so finishing a gap is visible (the list
/// shrinks), and a regression anywhere else is visible too (a new category
/// appears) - which is the whole point of comparing against the real compiler
/// rather than against ourselves.</para>
/// </summary>
public partial class EntityLumpKnownGapsTests
{
    [Theory]
    [InlineData("ze_doom_p2", "cardtest")]
    [InlineData("s2probe", "probe01")]
    public void RemainingDifferences_AreOnlyTheDocumentedGaps(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null) { MapFixtures.Skip($"the {addon}/{map} source"); return; }

        var valve = MapFixtures.RcCompiledLump(source);
        if (valve is null) { MapFixtures.Skip("resourcecompiler.exe"); return; }

        var document = DmxBinary.ReadFile(source);
        var ours = EntityLumpAuthor.Author(MapEntities.From(document), MapFixtures.GameSchema(), worldName: map,
                                           fixupEntityNames: MapEntities.FixupEntityNames(document));

        var report = EntityLumpComparison.Diff(
            EntityLumpComparison.Read(valve, $"{map}.valve.vents_c"),
            EntityLumpComparison.Read(ours, $"{map}.ours.vents_c"));

        var unexplained = report.Where(line => !IsKnownGap(line)).ToList();
        Assert.True(unexplained.Count == 0,
            $"{addon}/{map}: differences outside the documented gaps. "
          + EntityLumpComparison.Summarize(unexplained));
    }

    /// <summary>
    /// The gaps, each with the reason it is still open. Keep this list in step with
    /// the "known and not yet done" paragraph in docs/MAP_RESOURCES.md.
    /// </summary>
    private static bool IsKnownGap(string line)
        // The lighting and cubemap bakes write their results back INTO the entity
        // lump: a light's baked ids, a light probe volume's atlas textures, its
        // handshake and its probe dimensions. Those are vrad3's output, so they
        // arrive with the lighting tier and not before. Since the compiler keeps
        // empty values, a texture key the bake fills shows as a value difference
        // against our "" rather than as a missing key.
        => BakedLightingKeys().IsMatch(line)
        // A key a mod REMOVED from the FGD that the engine nonetheless types.
        // light_environment's nearclipplane is declared remove_key in csgo.fgd and
        // still ships as a float; ambient_occlusion, range and occlusion_exponent
        // sit beside it removed AND untyped. The FGD cannot tell those apart, so
        // this waits for the engine's own schema rather than a guess.
        || line.Contains("nearclipplane: type valve FloatingPoint64", StringComparison.Ordinal);

    [GeneratedRegex(@"(bakedshadowindex|light_map_uniqueid|light_path_uniqueid|brightness_legacy"
                  + @"|brightness_lumens|lightprobetexture|cubemaptexture|handshake|light_probe_size"
                  + @"|light_probe_atlas|array_index)\w*: (missing|value valve maps/)")]
    private static partial Regex BakedLightingKeys();
}
