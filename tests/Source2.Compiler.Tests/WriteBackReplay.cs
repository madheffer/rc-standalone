using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays the settle's write-backs captured by tools/settle/probes/settle_bundle.js:
/// each resting body through <see cref="SettleWriteBack"/>, and the origin and
/// angles must come out as the floats Valve put in the node. Set WRITEBACK to
/// the capture's events.jsonl; without it the test does nothing.
/// </summary>
public class WriteBackReplay(ITestOutputHelper output)
{
    [Fact]
    public void CapturedWriteBacksMatch()
    {
        if (Environment.GetEnvironmentVariable("WRITEBACK") is not { Length: > 0 } path || !File.Exists(path))
            return;
        int total = 0, exact = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("\"writeback\""))
                continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.GetProperty("bindCount").GetInt32() != 0)
                continue;
            total++;
            var body = MemoryMarshal.Read<RnBodyState>(Convert.FromHexString(e.GetProperty("rn").GetString()!));
            var pose = SettleWriteBack.EntityPose(body);
            var ours = new[] { pose.Origin.X, pose.Origin.Y, pose.Origin.Z, pose.Angles.X, pose.Angles.Y, pose.Angles.Z };
            var theirs = MemoryMarshal.Cast<byte, float>(Convert.FromHexString(e.GetProperty("after").GetString()!)).ToArray();
            if (ours.Select(BitConverter.SingleToUInt32Bits).SequenceEqual(theirs.Select(BitConverter.SingleToUInt32Bits)))
                exact++;
            else if (total - exact <= 8)
                output.WriteLine($"node {e.GetProperty("node").GetUInt32()}: ours {string.Join(" ", ours.Select(f => f.ToString("R")))}"
                                 + $" valve {string.Join(" ", theirs.Select(f => f.ToString("R")))}");
        }
        output.WriteLine($"{exact} of {total} write-backs exact");
        Assert.True(total > 0);
        Assert.Equal(total, exact);
    }
}
