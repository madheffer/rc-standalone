using System.Numerics;
using System.Text;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a compiled world node model's buffers decoded
/// (<c>WNMODEL=&lt;vmdl_c path&gt;[|&lt;out file&gt;]</c>): each vertex buffer's
/// layout and every vertex (position, the rest as raw hex), each index buffer,
/// and each draw call with its range, material, bounds and UV density.
/// </summary>
public class WorldNodeProbe(ITestOutputHelper output)
{
    /// <summary>
    /// <c>WNSPEC=&lt;map vpk&gt;|&lt;out dir&gt;</c>: every worldnodes model extracted to the
    /// directory, and spec.json listing each one's vertex and index buffers'
    /// element counts and sizes (for tools that re-encode the MVTX/MIDX blocks).
    /// </summary>
    [Fact]
    public void Spec()
    {
        if (Environment.GetEnvironmentVariable("WNSPEC") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        Directory.CreateDirectory(p[1]);
        using var package = new ValvePak.Package();
        package.Read(p[0]);
        var items = new List<object>();
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains("/worldnodes/", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var bytes);
            var file = Path.Combine(p[1], Path.GetFileName(path));
            File.WriteAllBytes(file, bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var model = (Model)resource.DataBlock!;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
            {
                items.Add(new
                {
                    path = file,
                    vb = mesh.VBIB.VertexBuffers.Select(b => new[] { (long)b.ElementCount, (long)b.ElementSizeInBytes }).ToArray(),
                    ib = mesh.VBIB.IndexBuffers.Select(b => new[] { (long)b.ElementCount, (long)b.ElementSizeInBytes }).ToArray(),
                });
                break;
            }
        }
        File.WriteAllText(Path.Combine(p[1], "spec.json"), System.Text.Json.JsonSerializer.Serialize(items));
        output.WriteLine($"{items.Count} models");
    }

    /// <summary><c>WNTYPED=&lt;resource path&gt;|&lt;out file&gt;</c>: every block's tree with its value types.</summary>
    [Fact]
    public void Typed()
    {
        if (Environment.GetEnvironmentVariable("WNTYPED") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var trees = WorldPhysicsAuthorTests.Trees(File.ReadAllBytes(p[0]));
        var text = new StringBuilder();
        foreach (var (name, tree) in trees)
        {
            text.AppendLine($"== {name}");
            foreach (var line in KvTreeDiff.Typed(tree, "", 0, 3))
                text.AppendLine(line);
        }
        File.WriteAllText(p[1], text.ToString());
    }

    /// <summary>
    /// <c>WNFIELDS=&lt;vpk&gt;[;&lt;vpk&gt;...]|&lt;out file&gt;</c>: over every worldnodes model, each
    /// tree field path (array indices dropped) with its distinct types and up to
    /// eight distinct values, to tell constant fields from computed ones.
    /// </summary>
    [Fact]
    public void Fields()
    {
        if (Environment.GetEnvironmentVariable("WNFIELDS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var seen = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>();
        void Walk(ValveKeyValue.KVObject v, string path)
        {
            switch (v.ValueType)
            {
                case ValveKeyValue.KVValueType.Collection:
                    foreach (var c in v.Children)
                        Walk(c.Value, path + "." + c.Key);
                    return;
                case ValveKeyValue.KVValueType.Array:
                    foreach (var item in v.Values)
                        Walk(item, path + "[]");
                    if (!v.Values.Any())
                        Add(path + "[]", "(empty)");
                    return;
                default:
                    Add(path, $"{v.ValueType}{(v.Flag != 0 ? "/" + v.Flag : "")} {v}");
                    return;
            }
        }
        void Add(string path, string value)
        {
            if (!seen.TryGetValue(path, out var set))
                seen[path] = set = new SortedSet<string>(StringComparer.Ordinal);
            if (set.Count < 9)
                set.Add(value.Length > 80 ? value[..80] : value);
            counts[path] = counts.GetValueOrDefault(path) + 1;
        }
        var models = 0;
        foreach (var vpk in p[0].Split(';'))
        {
            using var package = new ValvePak.Package();
            package.Read(vpk);
            foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
            {
                if (!entry.GetFullPath().Contains("/worldnodes/", StringComparison.Ordinal))
                    continue;
                package.ReadEntry(entry, out var bytes);
                models++;
                foreach (var (name, tree) in WorldPhysicsAuthorTests.Trees(bytes))
                    Walk(tree, name);
            }
        }
        var text = new StringBuilder($"{models} models{Environment.NewLine}");
        foreach (var (path, values) in seen)
            text.AppendLine($"{path} ({counts[path]}): {string.Join(" | ", values)}");
        File.WriteAllText(p[1], text.ToString());
    }

    [Fact]
    public void DumpModel()
    {
        if (Environment.GetEnvironmentVariable("WNMODEL") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        using var resource = new Resource();
        resource.Read(p[0]);
        var model = (Model)resource.DataBlock!;
        var text = new StringBuilder();
        foreach (var (mesh, index, name, lod) in model.GetEmbeddedMeshesAndLoD())
        {
            text.AppendLine($"mesh {index} {name} lod {lod}");
            var vbib = mesh.VBIB;
            for (var b = 0; b < vbib.VertexBuffers.Count; b++)
            {
                var vb = vbib.VertexBuffers[b];
                text.AppendLine($" vb {b}: {vb.ElementCount} x {vb.ElementSizeInBytes}: "
                                + string.Join(", ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}@{f.Offset}:{f.Format}")));
                for (var v = 0; v < vb.ElementCount; v++)
                {
                    var at = (int)(v * vb.ElementSizeInBytes);
                    var pos = new Vector3(BitConverter.ToSingle(vb.Data, at), BitConverter.ToSingle(vb.Data, at + 4), BitConverter.ToSingle(vb.Data, at + 8));
                    var rest = Convert.ToHexString(vb.Data, at + 12, (int)vb.ElementSizeInBytes - 12);
                    text.AppendLine($"  v{v}: {pos.X:R} {pos.Y:R} {pos.Z:R} | {rest}");
                }
            }
            for (var b = 0; b < vbib.IndexBuffers.Count; b++)
            {
                var ib = vbib.IndexBuffers[b];
                var list = new List<int>();
                for (var i = 0; i < ib.ElementCount; i++)
                    list.Add(ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4));
                text.AppendLine($" ib {b}: {ib.ElementCount} x {ib.ElementSizeInBytes}");
                for (var i = 0; i < list.Count; i += 3)
                    text.AppendLine($"  t{i / 3}: {string.Join(' ', list.Skip(i).Take(3))}");
            }
            foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
            {
                text.AppendLine($" sceneobject {string.Join(' ', so.GetArray<double>("m_vMinBounds"))} .. {string.Join(' ', so.GetArray<double>("m_vMaxBounds"))}");
                foreach (var dc in so.GetArray("m_drawCalls"))
                    text.AppendLine($"  draw {dc.GetStringProperty("m_material")} base {dc.GetInt32Property("m_nBaseVertex")} verts {dc.GetInt32Property("m_nVertexCount")}"
                                    + $" start {dc.GetInt32Property("m_nStartIndex")} count {dc.GetInt32Property("m_nIndexCount")} uvdensity {dc.GetDoubleProperty("m_flUvDensity"):R}");
            }
        }
        if (p.Length > 1)
            File.WriteAllText(p[1], text.ToString());
        else
            output.WriteLine(text.ToString());
    }
}
