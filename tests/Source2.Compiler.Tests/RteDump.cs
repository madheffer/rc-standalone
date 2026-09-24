using System.Buffers.Binary;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Every triangle of a <c>.rte</c> as JSON lines, for matching against the
/// geometry it was built from: its rebuilt corners, plane, raw and converted
/// flags, and the 8 byte per-triangle field. <c>RTEDUMP=in.rte;out.jsonl</c>.
/// </summary>
public class RteDump
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("RTEDUMP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var bytes = File.ReadAllBytes(parts[0]);
        var rte = RayTraceEnvironment.Read(bytes);
        int nodes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)),
            triangles = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)),
            indices = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        var extra = RayTraceEnvironment.HeaderSize + (nodes * 8) + (triangles * 48) + (indices * 4);
        using var output = new StreamWriter(parts[1]);
        for (var i = 0; i < triangles; i++)
        {
            var v = rte.Vertices(i);
            var (n, d) = rte.Plane(i);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                i,
                raw = rte.RawFlags(i),
                flags = rte.Flags(i),
                id = Convert.ToHexString(bytes, extra + (i * 8), 8),
                n = new[] { n.X, n.Y, n.Z },
                d,
                v = v?.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            }));
        }
    }
}
