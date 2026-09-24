using System.Buffers.Binary;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our map mesh triangles against the <c>.rte</c> the compile wrote from the
/// same <c>.vmap</c>. Each triangle is turned into the tracer's record from its
/// corners and looked up bit for bit among the file's records, so a match means
/// the same corners in the same order. <c>RTEGEO=&lt;vmap&gt;;&lt;rte&gt;</c>.
/// </summary>
public class MapTrianglesReplay(ITestOutputHelper output)
{
    [Fact]
    public void OurTrianglesAgainstTheRte()
    {
        if (Environment.GetEnvironmentVariable("RTEGEO") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var meshes = MapMeshes.Read(DmxBinary.ReadFile(parts[0]));
        var rte = RayTraceEnvironment.ReadFile(parts[1]);

        var file = new Dictionary<string, List<int>>();
        for (var i = 0; i < rte.TriangleCount; i++)
            file.GetOrAdd(Key(rte.FileRecord(i))).Add(i);
        var claimed = new HashSet<int>();

        var byOrigin = new Dictionary<string, (int Ours, int Found)>();
        Span<float> corners = stackalloc float[9];
        Span<float> record = stackalloc float[13];
        foreach (var mesh in meshes)
        {
            foreach (var face in mesh.Faces)
            {
                var n = face.Corners.Length;
                int[] triangles = n == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate(face.Corners);
                var origin = $"{mesh.ParentType}{(mesh.ParentClass is { } c ? "/" + c : "")} {Path.GetFileNameWithoutExtension(face.Material)}";
                var tally = byOrigin.GetValueOrDefault(origin);
                for (var t = 0; t < triangles.Length; t += 3)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var p = face.Corners[triangles[t + k]];
                        corners[k * 3] = p.X;
                        corners[(k * 3) + 1] = p.Y;
                        corners[(k * 3) + 2] = p.Z;
                    }
                    tally.Ours++;
                    if (!RayTraceEnvironment.RecordFromCorners(corners, record))
                        continue;
                    if (file.TryGetValue(Key(record), out var hits) && hits.FirstOrDefault(h => !claimed.Contains(h), -1) is var h && h >= 0)
                    {
                        claimed.Add(h);
                        tally.Found++;
                    }
                }
                byOrigin[origin] = tally;
            }
        }
        foreach (var (origin, (ours, found)) in byOrigin.OrderBy(kv => kv.Key))
            output.WriteLine($"  {origin}: ours {ours}, in the rte {found}");
        output.WriteLine($"rte triangles {rte.TriangleCount}, matched {claimed.Count}, unmatched {rte.TriangleCount - claimed.Count}");
    }

    // The fields both sides define: normal, plane, the edge equations and the axes.
    private static string Key(ReadOnlySpan<byte> r)
    {
        Span<float> f = stackalloc float[13];
        for (var k = 0; k < 11; k++)
            f[k] = BinaryPrimitives.ReadSingleLittleEndian(r[(k * 4)..]);
        f[11] = r[0x2c];
        f[12] = r[0x2d];
        return Key(f);
    }

    private static string Key(ReadOnlySpan<float> f)
    {
        var parts = new string[12];
        var at = 0;
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 })
            parts[at++] = BitConverter.SingleToInt32Bits(f[k]).ToString("x8");
        return string.Join(",", parts);
    }
}

internal static class DictionaryExtensions
{
    public static List<int> GetOrAdd(this Dictionary<string, List<int>> d, string key)
    {
        if (!d.TryGetValue(key, out var list))
            d[key] = list = [];
        return list;
    }
}
