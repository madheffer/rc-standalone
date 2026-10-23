using System.Text.Json;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays physicsbuilder's mesh simplification captured by
/// <c>tools/physics/capture_simplify.py</c> (<c>SIMPLIFY_CAPTURE=&lt;capture&gt;</c>):
/// each piece PhysicsBuilder_MeshPieces welded goes through <see cref="MeshWeld"/>
/// at 1/32 and must give the mesh CMesh_Simplify read, float for float; that
/// mesh goes through <see cref="MeshSimplifier"/> and must give what it returned.
/// </summary>
public class SimplifyReplay(ITestOutputHelper output)
{
    [Fact]
    public void SimplificationsMatchValve()
    {
        if (Environment.GetEnvironmentVariable("SIMPLIFY_CAPTURE") is not { } path)
            return;
        var records = Records(path).ToList();
        int total = 0, welds = 0, simplified = 0;
        for (var i = 0; i + 2 < records.Count; i++)
        {
            if (records[i].Head.GetProperty("ev").GetString() != "weld")
                continue;
            var (weldHead, weldBlob) = records[i];
            var (inHead, inBlob) = records[i + 1];
            var (outHead, outBlob) = records[i + 2];
            total++;
            var stride = weldHead.GetProperty("stride").GetInt32();
            var streams = weldHead.GetProperty("streams").EnumerateArray().Select(s => new MeshWeld.Stream(
                s.GetProperty("name").GetString() ?? "", s.GetProperty("first").GetInt32(), s.GetProperty("count").GetInt32(),
                s.GetProperty("flag").GetInt32() != 0, s.GetProperty("type").GetInt32())).ToList();
            var (vw, iw) = Mesh(weldHead, weldBlob);
            var (vin, iin) = Mesh(inHead, inBlob);
            var (vout, iout) = Mesh(outHead, outBlob);
            var id = weldHead.GetProperty("id").GetInt32();
            var (vours, iours) = stride == 0 ? ([], []) : MeshWeld.Weld(vw, stride, iw, streams, 1f / 32f);
            if (Same(vours, iours, vin, iin))
                welds++;
            else
                output.WriteLine($"piece {id}: weld gives {vours.Length / Math.Max(stride, 1)} vertices {iours.Length} indices, Valve {vin.Length / Math.Max(stride, 1)} and {iin.Length}");
            var error = outHead.GetProperty("error").ValueKind == JsonValueKind.Number ? outHead.GetProperty("error").GetDouble() : 0;
            var positions = new float[vin.Length / Math.Max(stride, 1) * 3];
            for (var v = 0; v < positions.Length / 3; v++)
                Array.Copy(vin, v * stride, positions, v * 3, 3);
            var ours = MeshSimplifier.Simplify(positions, iin, (float)error);
            var (vs, isimp) = ours ?? (vin, iin);
            if (Same(vs, isimp, vout, iout))
                simplified++;
            else
                output.WriteLine($"piece {id} (error {error}): simplify gives {vs.Length / 3} vertices {isimp.Length} indices, Valve {vout.Length / 3} and {iout.Length}");
        }
        output.WriteLine($"{total} pieces: {welds} welds exact, {simplified} simplifications exact");
        Assert.Equal(total, welds);
        Assert.Equal(total, simplified);
    }

    private static bool Same(float[] v, int[] i, float[] wv, int[] wi)
        => i.AsSpan().SequenceEqual(wi) && v.Length == wv.Length
           && v.Select(BitConverter.SingleToInt32Bits).SequenceEqual(wv.Select(BitConverter.SingleToInt32Bits));

    internal static IEnumerable<(JsonElement Head, byte[] Blob)> Records(string path)
    {
        var data = File.ReadAllBytes(path);
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            yield return (head, blob);
        }
    }

    internal static (float[] Vertices, int[] Indices) Mesh(JsonElement head, byte[] blob)
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
