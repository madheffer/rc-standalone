using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Diagnosis (CUBEKEYS=&lt;package&gt;): the keys of the entities a cubemap face reads.</summary>
public sealed class CubemapSceneKeysProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListKeys()
    {
        if (Environment.GetEnvironmentVariable("CUBEKEYS") is not { Length: > 0 } vpk)
            return;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        string[] classes = ["env_combined_light_probe_volume", "func_combined_light_probe_volume", "env_cubemap", "env_cubemap_box", "env_cubemap_fog", "light_environment", "env_sky", "env_light_probe_volume", "worldspawn"];
        foreach (var entry in package.Entries!.GetValueOrDefault("vents_c") ?? [])
        {
            package.ReadEntry(entry, out var bytes);
            var data = (ValveResourceFormat.ResourceTypes.EntityLump)ResourceTrees.Read(bytes, entry.GetFullPath()).DataBlock!;
            foreach (var entity in data.Data.GetArray("m_entityKeyValues"))
            {
                var values = entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values")?.Children.ToList() ?? [];
                var cls = values.FirstOrDefault(v => v.Key == "classname").Value?.ToString() ?? "";
                if (!classes.Contains(cls))
                    continue;
                output.WriteLine($"== {entry.GetFullPath()} {cls}");
                foreach (var kv in values)
                    output.WriteLine($"   {kv.Key} = {(kv.Value.IsArray ? string.Join(" ", kv.Value.Values.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) : kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            }
        }
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.TypeName is "vwrld_c"))
        {
            package.ReadEntry(entry, out var bytes);
            var w = (ValveResourceFormat.ResourceTypes.World)ResourceTrees.Read(bytes, entry.GetFullPath()).DataBlock!;
            output.WriteLine($"== {entry.GetFullPath()} m_worldLightingInfo: {w.Data.GetSubCollection("m_worldLightingInfo")}");
        }
    }
}
