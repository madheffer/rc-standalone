using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (MODELLAYOUT=a.vpk[,b.vpk]|substring): every model in the
/// packages whose path contains the substring: its vertex buffers' element
/// count and input layout (semantic, index, format), and its draw calls.
/// </summary>
public class ModelLayoutProbe(ITestOutputHelper output)
{
    /// <summary>
    /// MODELCMP=a.vpk|b.vpk|path.vmdl_c: both models' vertex buffers decoded,
    /// per buffer and field the vertices that differ (first few shown), and
    /// whether the index buffers are equal.
    /// </summary>
    /// <summary>
    /// UVDVB=vpk|model|capture.bin|call[|draw]: the captured UV density call (capture_uvdensity.py)
    /// against the model's vertex buffer at the same draw (the n-th call is draw n): per
    /// index, the captured position and texcoord beside the buffer's, and how many differ.
    /// </summary>
    [Fact]
    public void UvDensityInputsAgainstBuffer()
    {
        if (Environment.GetEnvironmentVariable("UVDVB") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var package = new ValvePak.Package();
        package.Read(p[0]);
        package.ReadEntry(package.FindEntry(p[1]) ?? throw new FileNotFoundException(p[1]), out var bytes);
        var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
        var vb = mesh.VBIB.VertexBuffers[0];
        var ib = mesh.VBIB.IndexBuffers[0];
        var draws = mesh.Data.GetArray("m_sceneObjects")[0].GetArray("m_drawCalls");
        var data = File.ReadAllBytes(p[2]);
        var call = int.Parse(p[3]);
        var at = 0;
        for (var c = 0; c < call; c++)
            at += 20 + BitConverter.ToInt32(data, at) * 20;
        var count = BitConverter.ToInt32(data, at);
        var draw = draws[p.Length > 4 ? int.Parse(p[4]) : call];
        var start = draw.GetInt32Property("m_nStartIndex");
        var baseVertex = draw.GetInt32Property("m_nBaseVertex");
        var pos = vb.InputLayoutFields.First(f => f.SemanticName == "POSITION");
        var tex = vb.InputLayoutFields.First(f => f.SemanticName == "TEXCOORD" && f.SemanticIndex == 0);
        // The draw's own vertices, as sets: the buffer's order is the cache optimiser's.
        var bufferPositions = new HashSet<(int, int, int)>();
        var bufferUvs = new HashSet<(short, short)>();
        for (var k = 0; k < draw.GetInt32Property("m_nIndexCount"); k++)
        {
            var index = (int)(ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, (start + k) * 2) : BitConverter.ToUInt32(ib.Data, (start + k) * 4)) + baseVertex;
            var o = (int)(index * vb.ElementSizeInBytes);
            bufferPositions.Add((BitConverter.ToInt32(vb.Data, o + (int)pos.Offset), BitConverter.ToInt32(vb.Data, o + (int)pos.Offset + 4), BitConverter.ToInt32(vb.Data, o + (int)pos.Offset + 8)));
            bufferUvs.Add((BitConverter.ToInt16(vb.Data, o + (int)tex.Offset), BitConverter.ToInt16(vb.Data, o + (int)tex.Offset + 2)));
        }
        int found = 0, foundUv = 0;
        for (var k = 0; k < count; k++)
        {
            var q = at + 20 + k * 20;
            if (bufferPositions.Contains((BitConverter.ToInt32(data, q), BitConverter.ToInt32(data, q + 4), BitConverter.ToInt32(data, q + 8))))
                found++;
            static short Pack(float v) => (short)(Math.Clamp(v, -1f, 1f) * 32767f);
            if (bufferUvs.Contains((Pack(BitConverter.ToSingle(data, q + 12)), Pack(BitConverter.ToSingle(data, q + 16)))))
                foundUv++;
        }
        output.WriteLine($"call {call}: captured positions found bit for bit in the draw's buffer vertices: {found}/{count}; texcoords (packed): {foundUv}/{count}");
        int posDiff = 0, texDiff = 0, shown = 0;
        for (var k = 0; k < count; k++)
        {
            var index = (int)(ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, (start + k) * 2) : BitConverter.ToUInt32(ib.Data, (start + k) * 4)) + baseVertex;
            var o = (int)(index * vb.ElementSizeInBytes);
            var bp = new System.Numerics.Vector3(BitConverter.ToSingle(vb.Data, o + (int)pos.Offset), BitConverter.ToSingle(vb.Data, o + (int)pos.Offset + 4), BitConverter.ToSingle(vb.Data, o + (int)pos.Offset + 8));
            float Snorm(int off) => Math.Max(BitConverter.ToInt16(vb.Data, o + (int)tex.Offset + off) / 32767f, -1f);
            var bt = new System.Numerics.Vector2(Snorm(0), Snorm(2));
            var q = at + 20 + k * 20;
            var cp = new System.Numerics.Vector3(BitConverter.ToSingle(data, q), BitConverter.ToSingle(data, q + 4), BitConverter.ToSingle(data, q + 8));
            var ct = new System.Numerics.Vector2(BitConverter.ToSingle(data, q + 12), BitConverter.ToSingle(data, q + 16));
            var pd = cp != bp;
            var td = MathF.Abs(ct.X - bt.X) > 1.6e-5f || MathF.Abs(ct.Y - bt.Y) > 1.6e-5f;
            posDiff += pd ? 1 : 0;
            texDiff += td ? 1 : 0;
            if ((pd || td) && shown++ < 6)
                output.WriteLine($"  #{k} index {index}: captured ({cp.X:R} {cp.Y:R} {cp.Z:R}) uv ({ct.X:R} {ct.Y:R}); buffer ({bp.X:R} {bp.Y:R} {bp.Z:R}) uv ({bt.X:R} {bt.Y:R})");
        }
        output.WriteLine($"call {call}: {count} indices (draw {draw.GetInt32Property("m_nIndexCount")} from {start}, base {baseVertex}); positions differ at {posDiff}, texcoords beyond snorm rounding at {texDiff}");
    }

    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("MODELCMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        Mesh Load(string vpk)
        {
            var package = new ValvePak.Package();
            package.Read(vpk);
            package.ReadEntry(package.FindEntry(p[2]) ?? throw new FileNotFoundException(p[2]), out var bytes);
            var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            return ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
        }
        var (x, y) = (Load(p[0]), Load(p[1]));
        for (var b = 0; b < Math.Min(x.VBIB.VertexBuffers.Count, y.VBIB.VertexBuffers.Count); b++)
        {
            var (u, v) = (x.VBIB.VertexBuffers[b], y.VBIB.VertexBuffers[b]);
            output.WriteLine($"vb {b}: {u.ElementCount} x {u.ElementSizeInBytes} against {v.ElementCount} x {v.ElementSizeInBytes}");
            if (u.ElementCount != v.ElementCount || u.ElementSizeInBytes != v.ElementSizeInBytes)
                continue;
            foreach (var f in u.InputLayoutFields)
            {
                var next = u.InputLayoutFields.Select(g => g.Offset).Where(o => o > f.Offset).DefaultIfEmpty(u.ElementSizeInBytes).Min();
                var size = (int)(next - f.Offset);
                var diffs = new List<string>();
                var count = 0;
                for (var i = 0; i < u.ElementCount; i++)
                {
                    var at = (int)(i * u.ElementSizeInBytes + f.Offset);
                    if (!u.Data.AsSpan(at, size).SequenceEqual(v.Data.AsSpan(at, size)))
                    {
                        count++;
                        if (diffs.Count < 4)
                            diffs.Add($"#{i} {Convert.ToHexString(u.Data, at, size)} / {Convert.ToHexString(v.Data, at, size)}");
                    }
                }
                output.WriteLine($"  {f.SemanticName}{f.SemanticIndex}:{f.Format} differs at {count} vertices {string.Join("; ", diffs)}");
            }
        }
        for (var b = 0; b < Math.Min(x.VBIB.IndexBuffers.Count, y.VBIB.IndexBuffers.Count); b++)
            output.WriteLine($"ib {b}: equal {x.VBIB.IndexBuffers[b].Data.AsSpan().SequenceEqual(y.VBIB.IndexBuffers[b].Data)}");
    }

    [Fact]
    public void Layouts()
    {
        if (Environment.GetEnvironmentVariable("MODELLAYOUT") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        foreach (var vpk in p[0].Split(','))
        {
            using var package = new ValvePak.Package();
            package.Read(vpk);
            foreach (var entry in (package.Entries?.GetValueOrDefault("vmdl_c") ?? []).Where(e => e.GetFullPath().Contains(p[1], StringComparison.Ordinal)).OrderBy(e => e.GetFullPath()))
            {
                package.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var model = (Model)resource.DataBlock!;
                output.WriteLine($"{Path.GetFileName(vpk)} {entry.GetFullPath()}");
                foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                {
                    foreach (var vb in mesh.VBIB.VertexBuffers)
                    {
                        var bounds = "";
                        if (vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "POSITION") is { } pos && pos.Format.ToString() == "R32G32B32_FLOAT")
                        {
                            var lo = new System.Numerics.Vector3(float.MaxValue);
                            var hi = new System.Numerics.Vector3(float.MinValue);
                            for (var v = 0; v < vb.ElementCount; v++)
                            {
                                var at = (int)(v * vb.ElementSizeInBytes + pos.Offset);
                                var q = new System.Numerics.Vector3(BitConverter.ToSingle(vb.Data, at), BitConverter.ToSingle(vb.Data, at + 4), BitConverter.ToSingle(vb.Data, at + 8));
                                lo = System.Numerics.Vector3.Min(lo, q);
                                hi = System.Numerics.Vector3.Max(hi, q);
                            }
                            bounds = $" bounds {lo:F0} {hi:F0}";
                        }
                        output.WriteLine($"  vb {vb.ElementCount} x {vb.ElementSizeInBytes}: {string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}"))}{bounds}");
                    }
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                        foreach (var dc in so.GetArray("m_drawCalls"))
                            output.WriteLine($"  draw {dc.GetStringProperty("m_material")} indices {dc.GetInt32Property("m_nIndexCount")} vertices {dc.GetInt32Property("m_nVertexCount")} tint {string.Join(",", (dc.GetArray<double>("m_vTintColor") ?? []).Select(t => BitConverter.SingleToUInt32Bits((float)t).ToString("x8")))} depth {(dc.ContainsKey("m_nDepthVertexBufferIndex") ? dc.GetInt32Property("m_nDepthVertexBufferIndex") : -9)} vbs {dc.GetArray("m_vertexBuffers").Count}");
                }
            }
        }
    }
}
