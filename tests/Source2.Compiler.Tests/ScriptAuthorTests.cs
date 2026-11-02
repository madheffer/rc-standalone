using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="ScriptAuthor"/> against resourcecompiler's own compile of cs_script_demo's scripts on this install
/// (game/csgo_addons/cs_script_demo/maps/scripts, recompiled 2026-11-02 with the 1.41.9.4 tools). A file still as the
/// update shipped it (Valve's build machine, preprocessor present) is skipped: it is not what a user's RC writes.
/// </summary>
public sealed class ScriptAuthorTests(ITestOutputHelper output)
{
    [Fact]
    public void CsScriptDemoByteForByte()
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetDirectoryName(Path.GetDirectoryName(pak))!;
        var content = Path.Combine(Path.GetDirectoryName(game)!, "content", "csgo_addons", "cs_script_demo", "maps", "scripts");
        var compiled = Path.Combine(game, "csgo_addons", "cs_script_demo", "maps", "scripts");
        if (!Directory.Exists(content) || !Directory.Exists(compiled))
            return;
        var checkedCount = 0;
        foreach (var js in Directory.GetFiles(content, "*.js").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(js);
            var vjs = Path.Combine(compiled, name + ".vjs_c");
            if (!File.Exists(vjs))
                continue;
            var valve = File.ReadAllBytes(vjs);
            if (System.Text.Encoding.ASCII.GetString(valve).Contains("preprocessor_config.txt", StringComparison.Ordinal))
            {
                output.WriteLine($"{name}: as shipped (preprocessor present), skipped");
                continue;
            }
            var ours = ScriptAuthor.Compile($"maps/scripts/{name}.js", "csgo_addons/cs_script_demo", File.ReadAllBytes(js));
            var first = Enumerable.Range(0, Math.Min(ours.Length, valve.Length)).FirstOrDefault(i => ours[i] != valve[i], -1);
            output.WriteLine($"{name}: ours {ours.Length}, Valve's {valve.Length}, {(first < 0 && ours.Length == valve.Length ? "identical" : $"first difference at {first}")}");
            Assert.True(ours.AsSpan().SequenceEqual(valve), name);
            checkedCount++;
        }
        output.WriteLine($"{checkedCount} scripts checked");
    }
}
