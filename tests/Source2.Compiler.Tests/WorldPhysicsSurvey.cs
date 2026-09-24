using ValveKeyValue;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: what a map's <c>world_physics.vmdl_c</c> holds. Parts,
/// shapes per part with their collision attributes and surface properties,
/// and each mesh shape's field list and sizes.
/// <c>WPHYS=&lt;map vpk&gt;</c>, <c>WPHYS_KV3=1</c> also prints the first mesh
/// shape's KV3 (blobs abbreviated).
/// </summary>
public class WorldPhysicsSurvey(ITestOutputHelper output)
{
    [Fact]
    public void WhatWorldPhysicsHolds()
    {
        if (Environment.GetEnvironmentVariable("WPHYS") is not { Length: > 0 } vpk)
            return;
        using var package = new Package();
        package.Read(vpk);
        var entry = package.Entries.SelectMany(kv => kv.Value).FirstOrDefault(e => e.GetFullPath().EndsWith("world_physics.vmdl_c", StringComparison.Ordinal));
        if (entry == null)
        {
            output.WriteLine("no world_physics.vmdl_c");
            return;
        }
        package.ReadEntry(entry, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var model = (Model)resource.DataBlock!;
        var phys = model.GetEmbeddedPhys() ?? throw new InvalidDataException("no embedded physics");
        var data = phys.Data;
        output.WriteLine($"PHYS keys: {string.Join(", ", data.Children.Select(c => c.Key))}");
        foreach (var key in new[] { "m_collisionAttributes", "m_surfacePropertyHashes", "m_boneNames" })
            if (data.ContainsKey(key))
                output.WriteLine($"{key}: {Describe(data[key])}");
        var parts = data.GetArray("m_parts");
        output.WriteLine($"parts: {parts.Count}");
        var shown = false;
        foreach (var part in parts)
        {
            var shape = part.GetSubCollection("m_rnShape");
            foreach (var kind in new[] { "m_spheres", "m_capsules", "m_hulls", "m_meshes" })
            {
                var list = shape.GetArray(kind);
                output.WriteLine($"  {kind}: {list.Count}");
                foreach (var s in list.Take(40))
                {
                    var name = s.GetStringProperty("m_UserFriendlyName", "");
                    var attr = s.ContainsKey("m_nCollisionAttributeIndex") ? s.GetIntegerProperty("m_nCollisionAttributeIndex") : -1;
                    var surf = s.ContainsKey("m_nSurfacePropertyIndex") ? s.GetIntegerProperty("m_nSurfacePropertyIndex") : -1;
                    var inner = kind == "m_meshes" ? s.GetSubCollection("m_Mesh") : null;
                    var sizes = inner == null ? "" : string.Join(" ", inner.Children.Select(p => $"{p.Key}={Size(p.Value)}"));
                    output.WriteLine($"    '{name}' attr {attr} surf {surf} {sizes}");
                    if (inner != null && !shown && Environment.GetEnvironmentVariable("WPHYS_KV3") == "1")
                    {
                        shown = true;
                        foreach (var line in s.ToKV3String().Split((char)10).Take(120))
                            output.WriteLine("KV3 " + (line.Length > 200 ? line[..200] + "..." : line));
                    }
                }
            }
        }
    }

    private static string Size(KVObject v) => v.ValueType switch
    {
        KVValueType.BinaryBlob => $"{v.AsBlob().Length}B",
        KVValueType.Array => $"[{v.Count}]",
        KVValueType.Collection => "{" + string.Join(",", v.Children.Select(c => c.Key)) + "}",
        _ => v.ToString() ?? "",
    };

    private static string Describe(KVObject v) => v.ValueType == KVValueType.Array
        ? $"[{v.Count}] " + string.Join(" | ", Enumerable.Range(0, Math.Min(12, v.Count)).Select(i => v[i].ValueType == KVValueType.Collection ? v[i].ToKV3String().Replace((char)10, ' ').Replace("	", "") : v[i].ToString()))
        : v.ToString() ?? "null";
}
