using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="MeshEntryFlags"/> against WRB_MeshEntryFlags itself: 400
/// random cases (Data/entry_flags_cases.json) were run through the 0923
/// binary under unicorn (tools/re/emu_entry_flags.py, which answers
/// GetGameInfoBool with its default, so the static env map switch is off),
/// giving Data/entry_flags_valve.json.
/// </summary>
public class MeshEntryFlagsEmulated(ITestOutputHelper output)
{
    private sealed class Attributes(JsonElement c) : MeshEntryFlags.IAttributes
    {
        public bool? Bool(uint key) => c.GetProperty("bools").TryGetProperty($"0x{key:x8}", out var v) ? v.GetInt32() != 0 : null;
        public int? Int(uint key) => c.GetProperty("ints").TryGetProperty($"0x{key:x8}", out var v) ? v.GetInt32() : null;
        public string? String(uint key) => c.GetProperty("skybox").ValueKind == JsonValueKind.String ? c.GetProperty("skybox").GetString() : null;
        public (int Width, int Height) TextureSize => (c.GetProperty("size")[0].GetInt32(), c.GetProperty("size")[1].GetInt32());
    }

    [Fact]
    public void MatchesTheBinary()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "entry_flags_cases.json"))).RootElement;
        var valve = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "entry_flags_valve.json"))).RootElement;
        var settings = new MeshEntryFlags.Settings(UseStaticEnvMapForObjectsWithLightingOrigin: false);
        var bad = 0;
        for (var i = 0; i < cases.GetArrayLength(); i++)
        {
            var c = cases[i];
            var r = c.GetProperty("rec");
            var record = new MeshEntryFlags.Record((byte)r.GetProperty("0x36").GetInt32(), r.GetProperty("0x37").GetUInt64(),
                r.GetProperty("0xb4").GetSingle(), r.GetProperty("0x13").GetInt32() != 0,
                r.GetProperty("name12").ValueKind == JsonValueKind.String ? r.GetProperty("name12").GetString() : null, false);
            var ours = MeshEntryFlags.Compute(new Attributes(c), record, settings);
            var want = valve[i].GetProperty("flags").GetUInt64();
            var size = valve[i].GetProperty("size").GetUInt64();
            if (ours.Flags != want || (ulong)(uint)ours.Width != (size & 0xffffffff) || (ulong)(uint)ours.Height != size >> 32)
            {
                if (bad++ < 10)
                    output.WriteLine($"case {i}: ours {ours.Flags:x} valve {want:x} (xor {ours.Flags ^ want:x}); size {ours.Width}x{ours.Height} vs {size:x}");
            }
        }
        output.WriteLine($"{cases.GetArrayLength()} cases, {bad} differ");
        Assert.Equal(0, bad);
    }
}
