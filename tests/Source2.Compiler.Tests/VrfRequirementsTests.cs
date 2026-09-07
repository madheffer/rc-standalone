using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Keeps <see cref="VrfRequirements"/> honest.
///
/// <para>It exists so a host that supplies its own ValveResourceFormat can verify
/// compatibility mechanically. That is only worth anything if the declaration
/// describes this library as it actually is, so both halves are derived from the
/// repository rather than trusted: the patch ids against
/// <c>third_party/patches/index.json</c>, and the reachable namespaces against
/// the <c>using</c> directives in the source.</para>
/// </summary>
public class VrfRequirementsTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "third_party", "VENDORED.json")))
                return dir;
        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void RequiredPatchIds_AreExactlyThePatchesThisRepoCarries()
    {
        var index = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "third_party", "patches", "index.json")));
        var actual = index.RootElement.GetProperty("patches").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var declared = VrfRequirements.RequiredPatchIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // Not a subset check: a patch carried but not declared would be one a host
        // is never told to apply, which is the same failure as declaring one that
        // does not exist.
        Assert.Equal(actual, declared);
    }

    [Fact]
    public void UpstreamSha_MatchesThePinThisRepoVendors()
    {
        var pin = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "third_party", "VENDORED.json")));
        Assert.Equal(pin.RootElement.GetProperty("vrf").GetProperty("sha").GetString(),
                     VrfRequirements.UpstreamSha);
    }

    [Fact]
    public void ReachableNamespaces_CoverEveryVrfNamespaceTheLibraryImports()
    {
        var src = Path.Combine(RepoRoot(), "src", "Source2.Compiler");
        var imported = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(file),
                         @"^\s*using\s+(?:static\s+)?(ValveResourceFormat(?:\.[\w.]+)?)\s*;", RegexOptions.Multiline))
                imported.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(imported);

        // Every namespace the code actually imports must be declared reachable.
        // The reverse is allowed: declaring one the code has stopped using is
        // conservative, and only ever makes a host's compatibility check stricter.
        var undeclared = imported.Except(VrfRequirements.ReachableNamespaces, StringComparer.Ordinal).ToArray();
        Assert.True(undeclared.Length == 0,
            "VrfRequirements.ReachableNamespaces does not mention " + string.Join(", ", undeclared) +
            ". A host checks its own VRF patches against that list, so a namespace missing from it " +
            "is a patch nobody will think to check.");
    }
}
