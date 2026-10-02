using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration (<c>VSMART=&lt;addon&gt;|&lt;file&gt;</c>): a smart prop definition's elements and modifiers with their keys.</summary>
public class VsmartDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("VSMART") is not { } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var parts = spec.Split('|');
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", parts[0]));
        var root = content.SmartProp(parts[1]) ?? throw new InvalidOperationException("no definition");
        void Walk(KVObject e, string indent)
        {
            output.WriteLine($"{indent}{(e.ContainsKey("_class") ? e["_class"] : "root")}: {string.Join(", ", e.Keys.Where(k => k is not ("m_Children" or "m_Modifiers" or "_class")).Select(k => $"{k}={Short(e[k])}"))}");
            foreach (var m in (IEnumerable<KVObject>?)e.GetArray("m_Modifiers") ?? [])
                output.WriteLine($"{indent}  modifier {m["_class"]}: {string.Join(", ", m.Keys.Where(k => k != "_class").Select(k => $"{k}={Short(m[k])}"))}");
            foreach (var c in (IEnumerable<KVObject>?)e.GetArray("m_Children") ?? [])
                Walk(c, indent + "    ");
        }
        static string Short(KVObject? v) => v == null ? "null" : v.ValueType is KVValueType.Collection or KVValueType.Array ? "{" + string.Join(" ", v.Keys.Take(6).Select(k => $"{k}:{Short(v[k])}")) + "}" : v.ToString() ?? "";
        foreach (var v in (IEnumerable<KVObject>?)root.GetArray("m_Variables") ?? [])
            output.WriteLine($"variable {Short(v)}");
        Walk(root, "");
    }
}
