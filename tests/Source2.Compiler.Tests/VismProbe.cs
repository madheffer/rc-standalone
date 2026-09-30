using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VISMPROBE=1</c>): the atixref meshes whose material the
/// <c>vism*_mt_*</c> node models carry, with where they hang and the
/// <c>CMapMesh</c> settings that are not defaults.
/// </summary>
public class VismProbe(ITestOutputHelper output)
{
    [Fact]
    public void Meshes()
    {
        if (Environment.GetEnvironmentVariable("VISMPROBE") != "1")
            return;
        var vmap = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\atixref.vmap";
        var doc = DmxBinary.Read(File.ReadAllBytes(vmap));
        foreach (var e in doc.Elements)
        {
            foreach (var (k, v) in e.Attributes)
            {
                var hit = v is string s1 && s1.Contains("muesli", StringComparison.Ordinal)
                          || v is object?[] arr && arr.OfType<string>().Any(x => x.Contains("muesli", StringComparison.Ordinal));
                if (hit)
                {
                    var owners = doc.Elements.Where(o => o.Attributes.Values.Any(x => ReferenceEquals(x, e) || x is object?[] a2 && a2.Contains(e))).Select(o => $"{o.Type}:{o.Name}");
                    output.WriteLine($"{e.Type}:{e.Name}.{k} owned by {string.Join(",", owners)}; attrs {string.Join(" ", e.Attributes.Keys)}");
                }
            }
        }
        var meshes = MapMeshes.Read(doc);
        string[] wanted = ["muesli_logo", "sg_logo", "helipad", "it_poster007", "train_bombsite_arrow"];
        foreach (var m in meshes)
        {
            var mats = m.Faces.Select(f => f.Material).Distinct().ToList();
            if (!mats.Any(x => wanted.Any(w => x.Contains(w, StringComparison.Ordinal))))
                continue;
            var attrs = m.Element?.Attributes.Where(a => a.Value is bool or int or float or string && a.Key != "name")
                .Select(a => $"{a.Key}={a.Value}") ?? [];
            output.WriteLine($"node {m.NodeId} under {m.ParentType}/{m.ParentClass} hidden {m.Hidden}: {m.Faces.Length} faces, {string.Join(",", mats)}");
            output.WriteLine("    " + string.Join(" ", attrs));
        }
    }
}
