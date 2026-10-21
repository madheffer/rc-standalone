// Finds the .vcs program, static combo and dynamic combos each SPIR-V module came from (modules written by
// rdc_spirv.py from a capture), by hashing every static combo's shader files in the given shader vpks.
//   dotnet run -c Release -- <dir of .spv> <max compressed vcs bytes> <shaders_vulkan_dir.vpk>...
using System.Security.Cryptography;
using ValvePak;
using ValveResourceFormat.CompiledShader;

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
