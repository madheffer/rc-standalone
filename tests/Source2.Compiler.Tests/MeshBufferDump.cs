using Source2.Compiler;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the mesh DMX the map builder reads each node from
/// (<c>tools/hulls/dump_meshbuf.py</c> output). <c>MESHBUF=&lt;dir&gt;</c>
/// lists the element types and, for <c>MESHBUF_SHOW=&lt;n&gt;</c>, every
/// attribute of dump n.
/// </summary>
public class MeshBufferDump(ITestOutputHelper output)
{
    [Fact]
    public void ReadDumpedMeshBuffers()
    {
        if (Environment.GetEnvironmentVariable("MESHBUF") is not { Length: > 0 } dir)
            return;
        var types = new Dictionary<string, int>();
        foreach (var file in Directory.GetFiles(dir, "*.dmx"))
        {
            var doc = DmxBinary.Read(File.ReadAllBytes(file));
            foreach (var e in doc.Elements)
                types[e.Type] = types.GetValueOrDefault(e.Type) + 1;
            var format = doc.Elements.FirstOrDefault(e => e.Type == "DmeVertexData")?.Get<object?[]>("vertexFormat");
            var key = "FORMAT " + string.Join(",", (format ?? []).Select(x => x as string));
            types[key] = types.GetValueOrDefault(key) + 1;
        }
        foreach (var (k, v) in types.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"TYPE {k}: {v}");
        if (Environment.GetEnvironmentVariable("MESHBUF_SHOW") is { Length: > 0 } show)
        {
            var doc = DmxBinary.Read(File.ReadAllBytes(Path.Combine(dir, show + ".dmx")));
            foreach (var e in doc.Elements)
            {
                output.WriteLine($"ELEMENT {e.Type} '{e.Name}'");
                foreach (var (k, v) in e.Attributes)
                    output.WriteLine($"  {k} : {Describe(v)}");
            }
        }
    }

    private static string Describe(object? v) => v switch
    {
        null => "null",
        DmxBinary.Element el => $"-> {el.Type} '{el.Name}'",
        object?[] arr => $"{arr.GetType().Name}[{arr.Length}] {string.Join(", ", arr.Take(6).Select(x => x is DmxBinary.Element el ? $"-> {el.Type} '{el.Name}'" : x?.ToString()))}",
        byte[] b => $"byte[{b.Length}]",
        _ => $"{v.GetType().Name} {v}",
    };
}
