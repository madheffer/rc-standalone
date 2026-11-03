using System.Security.Cryptography;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>BAKEHASH=&lt;vmap&gt;[|&lt;vmap&gt;...]</c>, <c>BAKEHASH_OUT=&lt;file&gt;</c>): a hash of
/// <see cref="SubdivisionBake.Bake"/>'s export (positions, indices, faces, paint) for every subdivided
/// mesh, one line each, so two builds of the bake can be diffed mesh by mesh.
/// </summary>
public class SubdivisionBakeHashProbe(ITestOutputHelper output)
{
    [Fact]
    public void Hashes()
    {
        if (Environment.GetEnvironmentVariable("BAKEHASH") is not { Length: > 0 } spec)
            return;
        var lines = new List<string>();
        foreach (var path in spec.Split('|'))
            foreach (var mesh in MapMeshes.Read(DmxBinary.ReadFile(path)).DistinctBy(m => m.NodeId))
            {
                var data = mesh.Element?.Get<DmxBinary.Element>("meshData");
                var levels = data?.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels");
                if (data == null || levels == null || !levels.Any(x => x is int i && i > 0))
                    continue;
                string hash;
                try
                {
                    var r = SubdivisionBake.Bake(data);
                    var ms = new MemoryStream();
                    var w = new BinaryWriter(ms);
                    foreach (var p in r.Positions)
                    {
                        w.Write(p.X);
                        w.Write(p.Y);
                        w.Write(p.Z);
                    }
                    foreach (var i in r.Indices)
                        w.Write(i);
                    foreach (var f in r.Faces)
                        w.Write(f);
                    foreach (var p in r.Paint ?? [])
                    {
                        w.Write(p.X);
                        w.Write(p.W);
                    }
                    hash = Convert.ToHexString(SHA1.HashData(ms.ToArray()))[..16] + $" {r.Positions.Count}v {r.Indices.Count / 3}t";
                }
                catch (Exception e)
                {
                    hash = "error " + e.Message;
                }
                lines.Add($"{Path.GetFileName(path)} {mesh.NodeId} {hash}");
            }
        File.WriteAllLines(Environment.GetEnvironmentVariable("BAKEHASH_OUT") ?? "bakehash.txt", lines);
        output.WriteLine($"{lines.Count} meshes");
    }
}
