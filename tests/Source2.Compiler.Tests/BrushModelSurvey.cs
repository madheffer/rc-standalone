using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: what the compile builds for each brush entity, read out of a
/// compiled map package. Every entity model under
/// <c>maps/&lt;map&gt;/entities/</c> with its render meshes, draw calls,
/// triangles, materials and physics shapes. <c>BRUSHMODEL=&lt;map .vpk&gt;</c>.
/// </summary>
public class BrushModelSurvey(ITestOutputHelper output)
{
    [Fact]
    public void ListEntityModels()
    {
        if (Environment.GetEnvironmentVariable("BRUSHMODEL") is not { Length: > 0 } path)
            return;
        using var package = new Package();
        package.Read(path);
        var entries = package.Entries.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().Contains("/entities/", StringComparison.Ordinal)).ToList();
        if (Environment.GetEnvironmentVariable("BRUSHMODEL_FIND") is { } find)
            foreach (var e in package.Entries.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().Contains(find, StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"found {e.GetFullPath()} {e.TotalLength}");
        if (Environment.GetEnvironmentVariable("BRUSHMODEL_EXTRACT") is { } extract)
        {
            var parts = extract.Split(';');
            var hit = package.Entries.SelectMany(kv => kv.Value).First(e => e.GetFullPath().EndsWith(parts[0], StringComparison.OrdinalIgnoreCase));
            package.ReadEntry(hit, out var raw);
            File.WriteAllBytes(parts[1], raw);
            using var res = new Resource();
            res.Read(new MemoryStream(raw));
            using var text = new StreamWriter(parts[1] + ".blocks.txt");
            foreach (var block in res.Blocks)
            {
                text.WriteLine($"==== {block.Type} offset {block.Offset} size {block.Size}");
                try { text.WriteLine(block.ToString()); } catch (Exception ex) { text.WriteLine($"({ex.GetType().Name})"); }
            }
        }
        if (Environment.GetEnvironmentVariable("BRUSHMODEL_ALL") is not null)
            foreach (var g in package.Entries.SelectMany(kv => kv.Value).GroupBy(e => $"{e.DirectoryName} *.{e.TypeName}").OrderBy(g => g.Key))
                output.WriteLine($"all {g.Key}: {g.Count()}");
        foreach (var group in entries.GroupBy(e => e.TypeName).OrderBy(g => g.Key))
            output.WriteLine($"{group.Key}: {group.Count()}");
        var limit = int.TryParse(Environment.GetEnvironmentVariable("BRUSHMODEL_LIMIT"), out var l) ? l : 12;
        foreach (var entry in entries.Where(e => e.TypeName == "vmdl_c").Take(limit))
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var model = (Model)resource.DataBlock!;
            var line = $"{entry.GetFullPath()}: ";
            foreach (var (mesh, index, name, lod) in model.GetEmbeddedMeshesAndLoD())
            {
                var draws = mesh.Data.GetArray("m_sceneObjects").SelectMany(o => o.GetArray("m_drawCalls")).ToList();
                var tris = draws.Sum(d => d.GetInt32Property("m_nIndexCount")) / 3;
                var mats = string.Join(",", draws.Select(d => Path.GetFileNameWithoutExtension(d.GetStringProperty("m_material"))).Distinct());
                line += $"mesh {name} lod {lod} draws {draws.Count} tris {tris} [{mats}]; ";
            }
            foreach (var reference in model.GetReferenceMeshNamesAndLoD())
                line += $"ref mesh {reference.MeshName}; ";
            if (model.GetEmbeddedPhys() is { } phys)
            {
                foreach (var part in phys.Parts)
                    line += $"phys hulls {part.Shape.Hulls.Length} meshes {part.Shape.Meshes.Length} spheres {part.Shape.Spheres.Length} capsules {part.Shape.Capsules.Length}; ";
                line += $"collision attrs {phys.CollisionAttributes.Count}";
            }
            else
                line += "no phys";
            output.WriteLine(line);
        }
    }
}
