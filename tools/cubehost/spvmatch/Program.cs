// Finds the .vcs program, static combo and dynamic combos each SPIR-V module came from (modules written by
// rdc_spirv.py from a capture), by hashing every static combo's shader files in the given shader vpks.
//   dotnet run -c Release -- --cbuffers <vpk> <vcs path>...
//   dotnet run -c Release -- <dir of .spv> <max compressed vcs bytes> <shaders_vulkan_dir.vpk>...
using System.Security.Cryptography;
using ValvePak;
using ValveResourceFormat.CompiledShader;

// --res <file>...: a compiled resource's blocks as text (RED2 edit info, DATA), for diffing ours against Valve's.
if (args[0] == "--res")
{
    foreach (var path in args.Skip(1))
    {
        using var res = new ValveResourceFormat.Resource();
        res.Read(path);
        Console.WriteLine($"== {path}");
        foreach (var block in res.Blocks)
        {
            Console.WriteLine($"-- {block.Type}");
            using var iw = new ValveResourceFormat.Utils.IndentedTextWriter();
            block.WriteText(iw);
            Console.WriteLine(iw.ToString());
        }
    }
    return;
}

// --entities <vmap> <class substring>...: matching CMapEntity elements with their origin, angles and keys, for
// finding a probe's or light's numbers in a capture's buffers.
if (args[0] == "--entities")
{
    var doc = Source2.Compiler.DmxBinary.ReadFile(args[1]);
    foreach (var e in doc.OfType("CMapEntity"))
    {
        var props = e.Get<Source2.Compiler.DmxBinary.Element>("entity_properties");
        var cls = props?.Attributes.GetValueOrDefault("classname") as string ?? "";
        if (!args.Skip(2).Any(s => cls.Contains(s, StringComparison.OrdinalIgnoreCase)))
            continue;
        Console.WriteLine($"{cls} origin {e.Attributes.GetValueOrDefault("origin")} angles {e.Attributes.GetValueOrDefault("angles")}");
        foreach (var kv in props!.Attributes)
            Console.WriteLine($"    {kv.Key} = {kv.Value}");
    }
    return;
}

// --vcsinfo <vpk> <vcs path>: every collection property of the program file with its count, to find where a
// VCS version keeps a table.
if (args[0] == "--vcsinfo")
{
    using var pak = new Package();
    pak.Read(args[1]);
    pak.ReadEntry(pak.FindEntry(args[2])!, out var bytes);
    using var p = new VfxProgramData();
    p.Read(Path.GetFileName(args[2]), new MemoryStream(bytes));
    foreach (var prop in p.GetType().GetProperties())
    {
        var v = prop.GetValue(p);
        Console.WriteLine($"{prop.Name}: {(v is System.Collections.ICollection c ? $"{c.Count} items" : v)}");
    }
    return;
}

// --cbuffers <vpk> <vcs path>...: each program's constant buffer layouts (name, size, members with byte
// offsets), the names Valve's SPIR-V leaves out.
if (args[0] == "--cbuffers")
{
    using var pak = new Package();
    pak.Read(args[1]);
    foreach (var path in args.Skip(2))
    {
        pak.ReadEntry(pak.FindEntry(path)!, out var bytes);
        using var p = new VfxProgramData();
        p.Read(Path.GetFileName(path), new MemoryStream(bytes));
        foreach (var b in p.ExtConstantBufferDescriptions)
        {
            Console.WriteLine($"{path}: {b.Name} size {b.BufferSize} push {b.IsPushConstantBuffer} type {b.Type} crc {b.BlockCrc:x8}");
            foreach (var v in b.Variables)
                Console.WriteLine($"    +{v.Offset * 4,-5} {v.Name} vec{v.VectorSize} rows {v.RowCount} x{v.ElementCount}");
        }
    }
    return;
}

var files = Directory.GetFiles(args[0], "*.spv");
var want = files.ToDictionary(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), Path.GetFileName);
var max = long.Parse(args[1]);
var found = new HashSet<string>();
foreach (var vpk in args.Skip(2))
{
    using var pak = new Package();
    pak.Read(vpk);
    foreach (var e in pak.Entries!["vcs"])
    {
        if (e.TotalLength > max || e.FileName.EndsWith("_features"))
            continue;
        var name = e.GetFullPath();
        try
        {
            pak.ReadEntry(e, out var bytes);
            using var p = new VfxProgramData();
            p.Read(Path.GetFileName(name), new MemoryStream(bytes));
            foreach (var sid in p.StaticComboEntries.Keys)
            {
                var combo = p.GetStaticCombo(sid);
                for (var i = 0; i < combo.ShaderFiles.Length; i++)
                {
                    var code = combo.ShaderFiles[i].Bytecode;
                    if (code == null || code.Length < 20 || !want.TryGetValue(Convert.ToHexString(SHA256.HashData(code)), out var m))
                        continue;
                    Console.WriteLine($"{m} = {name} static {sid} ({Decode(p.StaticComboArray, sid)}) file {i}");
                    foreach (var r in combo.DynamicComboRenderStates.Where(r => r.ShaderFileId == i).Take(6))
                        Console.WriteLine($"    dynamic {r.DynamicComboId}: {Decode(p.DynamicComboArray, r.DynamicComboId)}");
                    found.Add(m);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{name}: {ex.Message}");
        }
    }
}
foreach (var f in files.Select(Path.GetFileName).Where(f => !found.Contains(f!)))
    Console.WriteLine($"{f} not found");

// A combo id's value per combo: (id / step) mod range, plus the range's minimum.
static string Decode(VfxCombo[] combos, long id)
    => string.Join(" ", combos.Select(c => $"{c.Name}={id / c.ComboIndexValue % (c.RangeMax - c.RangeMin + 1) + c.RangeMin}"));
