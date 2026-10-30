using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="BarnLightDescriptions"/> against the light entries captured in cubehost
/// (<c>CUBEBARN=&lt;frida.jsonl of tools/cubehost/barn_record_io.js&gt;</c>, <c>CUBEBARN_VPK</c> the map's package):
/// each entry's transform (+0x144) must be its light_barn's AngleMatrix, which also finds the entity, and every word
/// of the description (+0x18) must be ours, bit for bit, except the flags byte +0xed.
/// </summary>
public sealed class BarnLightDescriptionsTests(ITestOutputHelper output)
{
    [Fact]
    public void DescriptionsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEBARN") is not { Length: > 0 } path
            || Environment.GetEnvironmentVariable("CUBEBARN_VPK") is not { Length: > 0 } vpk)
            return;
        var lights = new List<(LightPrecompute.KeyReader Key, float[] Transform)>();
        using (var package = new ValvePak.Package())
        {
            package.Read(vpk);
            foreach (var entry in (package.Entries!.GetValueOrDefault("vents_c") ?? []).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
            {
                package.ReadEntry(entry, out var bytes);
                foreach (var (cls, key) in Lighting.Vrad3Job.LumpEntities(bytes, entry.GetFullPath()).Where(e => e.Class == "light_barn"))
                    lights.Add((key, BarnLightDescriptions.Transform(key)));
            }
        }
        var pairs = File.ReadLines(path).Select(l => JsonNode.Parse(l)!).Where(n => (string?)n["kind"] == "pair").ToList();
        var checkedLights = new HashSet<int>();
        var wrong = new Dictionary<int, int>();
        var shown = 0;
        foreach (var pair in pairs)
        {
            var entry = Convert.FromHexString((string)pair["entry"]!);
            var xf = Enumerable.Range(0, 12).Select(i => BinaryPrimitives.ReadSingleLittleEndian(entry.AsSpan(0x144 + 4 * i))).ToArray();
            var light = lights.FindIndex(l => l.Transform.SequenceEqual(xf));
            if (light < 0)
            {
                Assert.True(entry[0x18 + 0xec] == 0, "a barn-shaped entry with no light_barn whose AngleMatrix is its transform");
                continue;
            }
            if (!checkedLights.Add(light))
                continue;
            var ours = BarnLightDescriptions.Description(lights[light].Key);
            for (var o = 0; o < BarnLightDescriptions.Size; o += 4)
            {
                var a = BinaryPrimitives.ReadUInt32LittleEndian(ours.AsSpan(o));
                var x = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0x18 + o));
                if (o == 0xec)
                {
                    a &= 0xffff00ff;
                    x &= 0xffff00ff;
                }
                if (a == x)
                    continue;
                wrong[o] = wrong.GetValueOrDefault(o) + 1;
                if (shown++ < 40)
                    output.WriteLine($"light {light} +0x{o:x}: ours {BitConverter.UInt32BitsToSingle(a):R} ({a:x8}), game {BitConverter.UInt32BitsToSingle(x):R} ({x:x8})");
            }
        }
        output.WriteLine($"{checkedLights.Count} light_barn descriptions checked; words differing: {string.Join(", ", wrong.OrderBy(w => w.Key).Select(w => $"+0x{w.Key:x}: {w.Value}"))}");
        Assert.NotEmpty(checkedLights);
        Assert.Empty(wrong);
    }
}
