using System.Text.Json;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays the map builder's 1/32 welds captured by
/// <c>tools/hulls/capture_weld.py</c> (<c>WELD=&lt;capture&gt;</c>): each
/// mesh Valve welded goes through <see cref="MeshWeld"/>, and the result must
/// match what Valve's weld returned, float for float.
/// </summary>
public class WeldReplay(ITestOutputHelper output)
{
    [Fact]
    public void WeldsMatchValve()
    {
        if (Environment.GetEnvironmentVariable("WELD") is not { } path)
            return;
        var data = File.ReadAllBytes(path);
        var pending = new Dictionary<int, (JsonElement Head, byte[] Blob)>();
        int total = 0, exact = 0, shown = 0;
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var ev = head.GetProperty("ev").GetString();
            if (ev == "in")
            {
                pending[head.GetProperty("id").GetInt32()] = (head, blob);
                continue;
            }
            if (ev != "out")
                continue;
            var (inHead, inBlob) = pending[head.GetProperty("id").GetInt32()];
            pending.Remove(head.GetProperty("id").GetInt32());
            // Only the map builder's call (FUN_18020b230) passes 1/32.
            if (!inHead.TryGetProperty("caller", out var caller) || caller.GetInt32() != MapBuilderCall)
                continue;
            var stride = inHead.GetProperty("stride").GetInt32();
            var streams = inHead.GetProperty("streams").EnumerateArray().Select(s => new MeshWeld.Stream(
                s.GetProperty("name").GetString() ?? "", s.GetProperty("first").GetInt32(), s.GetProperty("count").GetInt32(),
                s.GetProperty("flag").GetInt32() != 0, s.GetProperty("type").GetInt32())).ToList();
            var (vin, iin) = Mesh(inHead, inBlob);
            var (vout, iout) = Mesh(head, blob);
            var (vours, iours) = MeshWeld.Weld(vin, stride, iin, streams, 1f / 32f);
            total++;
            var same = vours.Length == vout.Length && iours.SequenceEqual(iout)
                && vours.Select(BitConverter.SingleToInt32Bits).SequenceEqual(vout.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 10)
                output.WriteLine($"weld {head.GetProperty("id").GetInt32()}: valve {vout.Length / stride} vertices {iout.Length} indices, ours {vours.Length / stride} vertices {iours.Length} indices");
        }
        output.WriteLine($"{exact} of {total} welds exact");
        Assert.Equal(total, exact);
    }

    // The return address of the weld call in FUN_18020b230, as an RVA.
    private const int MapBuilderCall = 0x20b56a;

    private static (float[] Vertices, int[] Indices) Mesh(JsonElement head, byte[] blob)
    {
        var nv = head.GetProperty("nv").GetInt32();
        var stride = head.GetProperty("stride").GetInt32();
        var ni = head.GetProperty("ni").GetInt32();
        var floats = new float[nv * stride];
        Buffer.BlockCopy(blob, 0, floats, 0, floats.Length * 4);
        var idx = new int[ni];
        Buffer.BlockCopy(blob, floats.Length * 4, idx, 0, ni * 4);
        return (floats, idx);
    }
}
