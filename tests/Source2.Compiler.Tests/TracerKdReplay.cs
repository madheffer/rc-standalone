using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our rebuild of the tracer's kd tree against the one the compile holds in
/// memory, dumped from a live compile into <c>%TEMP%/vis_capture/&lt;map&gt;.tracer.jsonl</c>.
/// Compared from the root down, because node numbering in the compile depends
/// on its thread pool and nothing else about the tree does. Behind <c>KD=&lt;map&gt;</c>.
/// </summary>
public class TracerKdReplay(ITestOutputHelper output)
{
    [Fact]
    public void TheTreeIsTheCompilesTree()
    {
        if (Environment.GetEnvironmentVariable("KD") is not { Length: > 0 } map)
            return;
        var addon = map == "ze_hold_em_p" ? "s2c_lighting" : "s2c_rc_probe";
        var rte = RayTraceEnvironment.ReadFile(
            Path.Combine(Path.GetTempPath(), "csgo_addons", addon, "maps", map + ".rte"));
        byte[] nodes = [], index = [];
        foreach (var line in File.ReadLines(Path.Combine(Path.GetTempPath(), "vis_capture", map + ".tracer.jsonl")))
        {
            var e = JsonDocument.Parse(line).RootElement;
            var ev = e.GetProperty("ev").GetString();
            if (ev == "kdnodes")
                nodes = Convert.FromHexString(e.GetProperty("hex").GetString()!);
            else if (ev == "kdindex")
                index = Convert.FromHexString(e.GetProperty("hex").GetString()!);
        }

        var kd = new TracerKd(rte);
        output.WriteLine($"valve {nodes.Length / 8} nodes, {index.Length / 4} indices; ours {kd.Nodes.Count} nodes,"
                       + $" {kd.Nodes.Where(n => n.Axis == 3).Sum(n => n.Slots.Length)} indices;"
                       + $" tracer slots {rte.TracerOrder.Length}");

        int same = 0, differ = 0, shown = 0;
        void Compare(int valve, int ours, string path)
        {
            var word = BitConverter.ToUInt32(nodes, valve * 8);
            var mine = kd.Nodes[ours];
            var axis = (int)(word & 3);
            if (axis != mine.Axis)
            {
                differ++;
                if (shown++ < 10)
                    output.WriteLine($"  {path}: valve axis {axis} split {BitConverter.ToSingle(nodes, valve * 8 + 4):R},"
                                   + $" ours axis {mine.Axis} split {mine.Split:R}");
                return;
            }
            if (axis == 3)
            {
                var at = (int)(word >> 2);
                var count = BitConverter.ToInt32(nodes, valve * 8 + 4);
                var theirs = Enumerable.Range(0, count).Select(k => BitConverter.ToInt32(index, (at + k) * 4)).ToArray();
                if (theirs.SequenceEqual(mine.Slots))
                    same++;
                else
                {
                    differ++;
                    if (shown++ < 10)
                        output.WriteLine($"  {path}: leaf valve [{string.Join(",", theirs)}] ours [{string.Join(",", mine.Slots)}]");
                }
                return;
            }
            var split = BitConverter.ToSingle(nodes, valve * 8 + 4);
            if (BitConverter.SingleToInt32Bits(split) != BitConverter.SingleToInt32Bits(mine.Split))
            {
                differ++;
                if (shown++ < 10)
                    output.WriteLine($"  {path}: axis {axis} valve split {split:R} ours {mine.Split:R}");
                return;
            }
            same++;
            var child = (int)(word >> 2);
            Compare(child, mine.Lower, path + "L");
            Compare(child + 1, mine.Lower + 1, path + "U");
        }
        Compare(0, 0, "root");
        output.WriteLine($"nodes identical {same}, differing {differ}");
        Assert.Equal(0, differ);
    }
}
