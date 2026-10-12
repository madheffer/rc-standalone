using Source2.Compiler;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the map resources this compiler can author against Valve's own compiles
/// of them, taken from subscribed workshop maps.
///
/// <para>The contract is the same one <see cref="Source2ContainerAuthorTests"/>
/// holds for the standalone types: container identity, the RED2 the engine is
/// judged by, the RERL ids it resolves through, and the decoded data tree. A map
/// resource adds one wrinkle, which is the point of these tests - it is compiled
/// as a CHILD of a .vmap rather than from a source file of its own, so it records
/// no input dependency, omits the special input dependency list, and declares
/// IsChildResource. Those three were measured across 116 maps before being
/// written down here; see docs/CONTAINERS.md.</para>
/// </summary>
public class MapResourceAuthoringTests
{
    [Theory]
    [InlineData("/world.vwrld_c", ".vwrld", "World Compiler Version", "CompileWorld", 1)]
    [InlineData("/entities/default_ents.vents_c", ".vents", "Entity Lump Compiler Version", "CompileEntityLump", 3)]
    [InlineData(".vwnod_c", ".vwnod", "World Node Compiler Version", "CompileWorldNode", 1)]
    public void MapKv3Resource_RoundTripsThroughOurAuthor(
        string suffix, string sourceExtension, string special, string identifier, int fingerprint)
    {
        var bytes = MapFixtures.Resource(suffix);
        if (bytes is null) { MapFixtures.Skip(suffix); return; }

        using var valve = ResourceTrees.Read(bytes, Path.GetFileName(suffix));

        // Re-author Valve's own data tree and compare what the engine consumes.
        // Anything the author gets wrong about the type - the resource version, the
        // identity, the reference ids, an integer width - lands here.
        //
        // The tree is taken from the parsed resource rather than from decompiled
        // TEXT on purpose. KV3 text carries neither a field's integer width nor a
        // float's full precision, so a text round trip fails on Valve's own output
        // for reasons that have nothing to do with the author: a world's
        // m_nCompileTimestamp is UInt32 in the struct RC serialized, and an entity
        // lump's 0.1f renders as "0.1" and returns as a double.
        var data = Assert.IsAssignableFrom<KeyValuesOrNTRO>(valve.DataBlock);
        var ours = Source2ContainerAuthor.AuthorKv3Tree(data.Data, data.Format!.Value, sourceExtension);
        using var mine = ResourceTrees.Read(ours, Path.GetFileName(suffix));

        Assert.Equal(valve.Version, mine.Version);
        Assert.Equal(ResourceTrees.Render(valve), ResourceTrees.Render(mine));

        AssertIdentity(valve, mine, special, identifier, fingerprint);
        AssertReferences(valve, mine);
    }

    [Fact]
    public void ResourceManifest_ReproducesValvesDataBytesExactly()
    {
        var bytes = MapFixtures.Resource("/world.vrman_c");
        if (bytes is null) { MapFixtures.Skip("/world.vrman_c"); return; }

        using var valve = ResourceTrees.Read(bytes, "world.vrman_c");
        var theirs = BlockBytes(bytes, valve, BlockType.DATA);

        var groups = ResourceManifestAuthor.ReadData(theirs);
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.NotEmpty(g));

        // The manifest payload is pure layout with no encoder in it, so unlike
        // every KV3 type this one is byte-reproducible, and asserting the weaker
        // "same paths" would not notice a wrong offset base.
        Assert.Equal(theirs, ResourceManifestAuthor.BuildData(groups));

        var ours = ResourceManifestAuthor.Author(groups);
        using var mine = ResourceTrees.Read(ours, "world.vrman_c");

        Assert.Equal(valve.Version, mine.Version);
        Assert.Equal(theirs, BlockBytes(ours, mine, BlockType.DATA));
        AssertIdentity(valve, mine, "Manifest Compiler Version", "CompileResourceManifest", 2);
        AssertReferences(valve, mine);
    }

    [Fact]
    public void MapRoot_MatchesValvesManifestForTheSameChildren()
    {
        var bytes = MapFixtures.Resource(".vmap_c");
        if (bytes is null) { MapFixtures.Skip(".vmap_c"); return; }

        using var valve = ResourceTrees.Read(bytes, "map.vmap_c");
        var red2 = Assert.IsType<ResourceEditInfo2>(valve.EditInfo).Data!.Root;
        var children = red2.GetArray<string>("m_ChildResourceList")!;
        var source = valve.EditInfo!.InputDependencies.Single(d => d.ContentRelativeFilename.EndsWith(".vmap"));

        // Feed our author exactly what RC compiled and compare the manifest. The
        // external assets are the references that are NOT children: a map root
        // points at the game content it needs as well as at what it built.
        var external = valve.ExternalReferences!.ResourceRefInfoList
            .Select(r => r.Name)
            .Except(children, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ours = Source2ContainerAuthor.AuthorMapRoot(
            source.ContentRelativeFilename, source.FileCRC, children, external);
        using var mine = ResourceTrees.Read(ours, "map.vmap_c");

        Assert.Equal(valve.Version, mine.Version);

        // Same blocks in the same order, and the map's data really is nothing:
        // an empty DATA block, which is what all 116 shipped maps carry.
        var theirTable = ResourceTrees.BlockTable(bytes);
        var myTable = ResourceTrees.BlockTable(ours);
        Assert.Equal(theirTable.Select(b => b.FourCC), myTable.Select(b => b.FourCC));
        Assert.Equal(0u, Assert.Single(myTable, b => b.FourCC == "DATA").Size);
        Assert.Equal(0u, Assert.Single(theirTable, b => b.FourCC == "DATA").Size);

        // The identity union: an identity is present for a kind of child if and
        // only if the map has one, which holds without exception across 116 maps
        // and 5 kinds of child.
        //
        // Compared DISTINCT, because "Texture Encode Quality" is a compile SETTING
        // rather than a version: its user data is 3 in 49 maps and 4 in 51, and a
        // map whose textures were compiled at both qualities carries the row twice.
        // Authoring no textures of our own, there is nothing to repeat it for.
        Assert.Equal(Identities(valve).Distinct(), Identities(mine).Distinct());

        var myRed2 = Assert.IsType<ResourceEditInfo2>(mine.EditInfo).Data!.Root;
        Assert.Equal(children, myRed2.GetArray<string>("m_ChildResourceList")!);
        Assert.Equal(1, (int)myRed2["m_SearchableUserData"]!["WorldModelDocAll"]!);
        Assert.Equal(0, (int)myRed2["m_SearchableUserData"]!["IsChildResource"]!);

        var mySource = Assert.Single(mine.EditInfo!.InputDependencies);
        Assert.Equal(source.ContentRelativeFilename, mySource.ContentRelativeFilename);
        Assert.Equal(source.FileCRC, mySource.FileCRC);

        // Every child, under the id the engine resolves it by.
        AssertReferences(valve, mine);
        Assert.Equal(
            valve.ExternalReferences!.ResourceRefInfoList.Select(r => r.Name),
            mine.ExternalReferences!.ResourceRefInfoList.Select(r => r.Name));
    }

    /// <summary>
    /// The map child identity, which is what separates these types from every
    /// other one the compiler writes.
    /// </summary>
    private static void AssertIdentity(Resource valve, Resource mine, string special, string identifier, int fingerprint)
    {
        var expected = new[] { (special, identifier, (uint)fingerprint) };
        Assert.Equal(expected, Identities(valve));
        Assert.Equal(expected, Identities(mine));

        Assert.Empty(valve.EditInfo!.InputDependencies);
        Assert.Empty(mine.EditInfo!.InputDependencies);

        // Workshop fixtures come from older compilers, which omit
        // m_SpecialInputDependencies; the 0923 compiler writes it empty, as we do.
        foreach (var res in new[] { valve, mine })
        {
            var red2 = Assert.IsType<ResourceEditInfo2>(res.EditInfo).Data!.Root;
            if (res == mine || red2.ContainsKey("m_SpecialInputDependencies"))
                Assert.Empty(red2["m_SpecialInputDependencies"]!.Values);
            Assert.Equal(1, (int)red2["m_SearchableUserData"]!["IsChildResource"]!);
        }
    }

    private static (string, string, uint)[] Identities(Resource res)
        => [.. res.EditInfo!.SpecialDependencies.Select(d => (d.String, d.CompilerIdentifier, d.Fingerprint))];

    /// <summary>Every reference Valve recorded, under the same engine id.</summary>
    private static void AssertReferences(Resource valve, Resource mine)
    {
        var ours = (mine.ExternalReferences?.ResourceRefInfoList ?? []).ToDictionary(r => r.Id, r => r.Name);
        foreach (var entry in valve.ExternalReferences?.ResourceRefInfoList ?? [])
            Assert.Equal(entry.Name, Assert.Contains(entry.Id, (IDictionary<ulong, string>)ours));
    }

    private static byte[] BlockBytes(byte[] file, Resource res, BlockType type)
    {
        var block = res.GetBlockByType(type)
                    ?? throw new InvalidOperationException($"No {type} block.");
        return file.AsSpan((int)block.Offset, (int)block.Size).ToArray();
    }
}
