using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every element with a node id, in a map and the maps its prefabs
/// load, with its type, class, model and enclosing prefab.
/// <c>NODEFIND=&lt;addon&gt;|&lt;map&gt;|&lt;node id&gt;</c>.
/// </summary>
public class NodeFindProbe(ITestOutputHelper output)
{
    [Fact]
    public void FindNode()
    {
        if (Environment.GetEnvironmentVariable("NODEFIND") is not { Length: > 0 } spec || spec.Split('|') is not [var addon, var map, var id])
            return;
        var source = MapFixtures.VmapSource(addon, map)!;
        var doc = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        void Search(DmxBinary.Document d, string where)
        {
            foreach (var e in d.Elements)
            {
                if (e.GetValue<int>("nodeID")?.ToString() == id)
                {
                    var props = e.Get<DmxBinary.Element>("entity_properties");
                    output.WriteLine($"{where}: {e.Type} class {props?.Get<string>("classname")} model {props?.Get<string>("model")} origin {e.GetValue<System.Numerics.Vector3>("origin")}");
                }
                if (e.Attributes.GetValueOrDefault(Maps.MapPrefabs.DocumentKey) is DmxBinary.Document inner)
                    Search(inner, where + $" > prefab {e.GetValue<int>("nodeID")} ({e.Get<string>("targetMapPath")})");
            }
        }
        Search(doc, map);
    }
}
