using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using Vortice.SpirvCross;

// usage:
//  combos <vcs>                                  list static combo ids with decoded state
//  dump <vcs> <S_X=v,...|id=N> <outdir> [D_X=v,...|all]   dump one static combo: summary + glsl per file
var mode = args[0];
var path = args[1];
if (mode == "vfx")
{
    // args: vfx <dir> <shadername> <platform> <out.vfx>
    var progs = new List<VfxProgramData>();
    foreach (var f in Directory.GetFiles(path, $"{args[2]}_{args[3]}_*.vcs"))
    {
        var pd = new VfxProgramData(); pd.Read(f); progs.Add(pd);
    }
    using var coll = ShaderCollection.FromEnumerable(progs);
    var ex = new ValveResourceFormat.IO.ShaderExtract(coll);
    var opts = ValveResourceFormat.IO.ShaderExtract.ShaderExtractParams.Inspect;
    var r = ex.ToVFX(ref opts);
    File.WriteAllText(args[4], r.VfxContent);
    foreach (var kv in r.Includes) File.WriteAllText(args[4] + "." + Path.GetFileName(kv.Key), kv.Value);
    return;
}
using var program = new VfxProgramData();
program.Read(path);
var name = Path.GetFileNameWithoutExtension(path);
Dictionary<string, int> Decode(long cid, IEnumerable<VfxCombo> arr)
    => arr.ToDictionary(c => c.Name, c => (int)(cid / Math.Max(1, c.CalculatedComboId) % (c.RangeMax - c.RangeMin + 1)) + c.RangeMin);
string Fmt(Dictionary<string, int> s) => string.Join(" ", s.Where(kv => kv.Value != 0).Select(kv => $"{kv.Key}={kv.Value}"));
if (mode == "combos")
{
    foreach (var cid in program.StaticComboEntries.Keys.Order())
        Console.WriteLine($"{cid}: {Fmt(Decode(cid, program.StaticComboArray))}");
    return;
}
var spec = args[2];
var outDir = args[3];
Directory.CreateDirectory(outDir);
long id = 0;
if (spec.StartsWith("id="))
    id = long.Parse(spec[3..]);
else
{
    var settings = Parse(spec);
    foreach (var c in program.StaticComboArray)
        id += (settings.GetValueOrDefault(c.Name, 0) - c.RangeMin) * c.CalculatedComboId;
}
Console.WriteLine($"{name}: static id {id} exists {program.StaticComboEntries.ContainsKey(id)} : {Fmt(Decode(id, program.StaticComboArray))}");
if (!program.StaticComboEntries.ContainsKey(id)) return;
var combo = program.GetStaticCombo(id);
using (var sw = new StringWriter())
{
    using var w = new IndentedTextWriter(sw);
    new PrintZFrameSummary(combo, w);
    File.WriteAllText(Path.Combine(outDir, $"{name}_s{id}_summary.txt"), sw.ToString());
}
// dynamic combo -> file index map
var map = new List<string>();
foreach (var dc in combo.DynamicCombos)
{
    map.Add($"dyn {dc.DynamicComboId} -> file {dc.ShaderFileId}: {Fmt(Decode(dc.DynamicComboId, program.DynamicComboArray))}");
}
File.WriteAllLines(Path.Combine(outDir, $"{name}_s{id}_dynmap.txt"), map);
HashSet<int> wantFiles = null;
if (args.Length > 4 && args[4] != "all")
{
    var dyn = Parse(args[4]);
    long did = 0;
    foreach (var c in program.DynamicComboArray)
        did += (dyn.GetValueOrDefault(c.Name, 0) - c.RangeMin) * c.CalculatedComboId;
    wantFiles = [combo.GetDynamicComboIndex(did)];
    Console.WriteLine($"  dynamic {did} -> file {wantFiles.First()}");
}
for (var i = 0; i < combo.ShaderFiles.Length; i++)
{
    if (wantFiles != null && !wantFiles.Contains(i)) continue;
    var file = combo.ShaderFiles[i];
    if (file.IsEmpty()) continue;
    var stem = Path.Combine(outDir, $"{name}_s{id}_f{i}");
    if (file is VfxShaderFileVulkan vk && ShaderSpirvReflection.ReflectSpirv(vk, Backend.GLSL, out var glsl))
        File.WriteAllText(stem + ".glsl", glsl);
    if (file is VfxShaderFileVulkan vk2 && ShaderSpirvReflection.ReflectSpirv(vk2, Backend.HLSL, out var hlsl))
        File.WriteAllText(stem + ".hlsl", hlsl);
    else
        File.WriteAllBytes(stem + ".bin", file.Bytecode);
}
Console.WriteLine($"  {combo.ShaderFiles.Length} files, {combo.DynamicCombos.Length} dyn combos");

static Dictionary<string, int> Parse(string s)
    => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));
