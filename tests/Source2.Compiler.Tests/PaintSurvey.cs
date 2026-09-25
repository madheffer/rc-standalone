using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every .vmap under a folder, its meshes whose vertex paint
/// (faceVertexData VertexPaintBlendParams) is not the same everywhere: how
/// many, how many are subdivided, how many .vmap vertices carry different
/// paint at different corners, and the materials. For finding maps that
/// exercise the blend split.
/// <c>PAINTSURVEY=&lt;folder&gt;</c>.
/// </summary>
public class PaintSurvey(ITestOutputHelper output)
{
    [Fact]
    public void VaryingPaint()
    {
        if (Environment.GetEnvironmentVariable("PAINTSURVEY") is not { Length: > 0 } dir)
            return;
        foreach (var vmap in Directory.EnumerateFiles(dir, "*.vmap", SearchOption.AllDirectories))
        {
            DmxBinary.Document doc;
            try
            {
                doc = DmxBinary.ReadFile(vmap);
            }
            catch (Exception e)
            {
                output.WriteLine($"{vmap}: {e.GetType().Name}");
                continue;
            }
            int varying = 0, subdivided = 0, split = 0;
            var materials = new SortedSet<string>();
            var world = Source2.Compiler.Maps.MapMeshes.Read(doc).Where(m => m.ParentType is "CMapWorld" or "CMapGroup").Select(m => m.Element).ToHashSet();
            var inWorld = 0;
            foreach (var mesh in doc.OfType("CMapMesh"))
            {
                var data = mesh.Get<DmxBinary.Element>("meshData");
                var paint = data?.Get<DmxBinary.Element>("faceVertexData")?.GetElements("streams")
                    .FirstOrDefault(s => s.Name.Split(':')[0] == "VertexPaintBlendParams")?.Get<object?[]>("data");
                if (paint == null || paint.Length == 0 || paint.All(p => Equals(p, paint[0])))
                    continue;
                varying++;
                if (world.Contains(mesh))
                    inWorld++;
                if (data!.Get<DmxBinary.Element>("subdivisionData")?.Get<object?[]>("subdivisionLevels") is { } levels && levels.Any(x => x is int i && i > 0))
                    subdivided++;
                var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
                var corner = (data.Get<object?[]>("edgeVertexDataIndices") ?? []).Select(x => (int)x!).ToArray();
                var seen = new Dictionary<int, Vector4>();
                var differ = new HashSet<int>();
                for (var h = 0; h < to.Length && h < corner.Length; h++)
                {
                    if (corner[h] < 0 || corner[h] >= paint.Length || paint[corner[h]] is not Vector4 p)
                        continue;
                    if (!seen.TryAdd(to[h], p) && seen[to[h]] != p)
                        differ.Add(to[h]);
                }
                if (differ.Count > 0)
                    split++;
                foreach (var m in data.Get<object?[]>("materials") ?? [])
                    materials.Add(Path.GetFileNameWithoutExtension(m as string ?? ""));
            }
            if (varying > 0)
                output.WriteLine($"{Path.GetRelativePath(dir, vmap)}: {varying} painted meshes ({inWorld} world), {subdivided} subdivided, {split} with corners painted differently; {string.Join(" ", materials.Take(12))}");
        }
    }
}
