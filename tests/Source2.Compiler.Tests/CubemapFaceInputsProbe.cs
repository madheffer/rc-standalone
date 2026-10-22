using System.Text.Json.Nodes;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Where the face render's captured inputs come from (CUBEREPLAY as for <see cref="CubemapFaceReplayTests"/>,
/// <c>CUBEREPLAY_VPK=&lt;the captured map's package&gt;</c>): for each world draw, whether its vertex stream
/// and its indices are the package's world node model buffers, decoded, at which offsets.
/// </summary>
public sealed class CubemapFaceInputsProbe(ITestOutputHelper output)
{
    /// <summary>
    /// generic's <c>_Globals_</c> (set 1 binding 0) for each colour draw: the captured block labelled with the
    /// combo's write sequence, against <see cref="ProgramGlobals.Fill"/> for the draw's material.
    /// <c>CUBEREPLAY_MATERIAL</c> names the material (the capture does not), <c>CUBEREPLAY_ATTRS</c> render
    /// attributes as name=value pairs.
    /// </summary>
    [Fact]
    public void MaterialGlobals()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_SPIRV") is not { Length: > 0 } spirvDir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_MATERIAL") is not { Length: > 0 } materialPath)
            return;
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(game, Environment.GetEnvironmentVariable("CUBEREPLAY_SHADERVPK") ?? "csgo", "shaders_vulkan_dir.vpk"));
        byte[]? material = null;
        foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
        {
            using var p = new ValvePak.Package();
            p.Read(pak);
            if (material == null && p.FindEntry(materialPath + "_c") is { } e)
                p.ReadEntry(e, out material);
        }
        using var resource = new Resource();
        resource.Read(new MemoryStream(material ?? throw new FileNotFoundException(materialPath)));
        var mat = (Material)resource.DataBlock!;
        var features = mat.IntParams.ToDictionary(kv => kv.Key, kv => (int)kv.Value);
        // CUBEREPLAY_PROGRAM=<shader>|<module id>|<statics>|<dynamics> (pairs name=value, comma separated);
        // generic's colour combo by default.
        var spec = (Environment.GetEnvironmentVariable("CUBEREPLAY_PROGRAM") ?? "generic|1267|S_MODE_TOOLS_VIS=1|D_BAKED_LIGHTING_FROM_LIGHTMAP=1").Split('|');
        static Dictionary<string, int> Pairs(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        var module = int.Parse(spec[1]);
        var program = Gpu.ValveProgram.Load(shaders, spec[0], "vulkan_50", "ps", Pairs(spec[2]), Pairs(spec.Length > 3 ? spec[3] : ""), features);
        var captured = File.ReadAllBytes(Path.Combine(spirvDir, $"{module:x}.spv"));
        output.WriteLine($"{program.Name}: SPIR-V {(program.Spirv.AsSpan().SequenceEqual(captured) ? "equals" : "DIFFERS from")} the capture's module");
        var attributes = (Environment.GetEnvironmentVariable("CUBEREPLAY_ATTRS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Split('=')).ToDictionary(a => a[0], a => Gpu.VfxExpression.Value.Of(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture)));
        var ours = new byte[128];
        var idColors = Gpu.ProgramGlobals.ShaderIdColors(File.ReadAllText(Path.Combine(game, "csgo", "gameinfo.gi")));
        var (r, g, b) = idColors.GetValueOrDefault(mat.ShaderName, (0, 0, 0));
        var scope = new Gpu.ProgramGlobals.MaterialScope(mat, program, attributes, Gpu.ProgramGlobals.ShaderIdColor(r, g, b));
        Gpu.ProgramGlobals.Fill(program, ours, scope, (_, _) => -1, _ => -1);

        // The material's textures, every mip, by content: which captured subresources they are.
        var mips = new Dictionary<string, string>();
        foreach (var (param, path) in mat.TextureParams)
        {
            byte[]? texBytes = null;
            foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
            {
                using var p = new ValvePak.Package();
                p.Read(pak);
                if (texBytes == null && p.FindEntry(path + "_c") is { } e)
                    p.ReadEntry(e, out texBytes);
            }
            if (texBytes == null)
                continue;
            using var texResource = new Resource();
            texResource.Read(new MemoryStream(texBytes));
            var tex = (Texture)texResource.DataBlock!;
            output.WriteLine($"{param} = {path}: {tex.Width} x {tex.Height} {tex.Format}, {tex.NumMipLevels} mips");
            foreach (var (level, _, _, _, size) in tex.GetEveryMipLevelMetrics())
            {
                var data = new byte[size];
                tex.ReadTextureMipLevel(data, level);
                mips[Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data))] = $"{param} mip {level}";
            }
        }

        var seen = new HashSet<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["stages"]?["ps"] is not { } ps || !((string)ps["shader"]!).EndsWith($" {module}", StringComparison.Ordinal))
                continue;
            foreach (var ro in ps["readOnly"]!.AsArray())
            {
                var name = (string)ro!["descriptor"]!["resource"]!;
                if (rec["contents"]?["before"]?[name] is not JsonArray subs)
                    continue;
                var hits = subs.Select(s => mips.GetValueOrDefault((string)s!["sha1"]!)).Where(h => h != null).ToList();
                if (hits.Count > 0)
                    output.WriteLine($"{rec["eid"]} b{ro["bind"]!["binding"]} {name}: {subs.Count} subresources, {hits.Count} are the material's: {string.Join(", ", hits)}");
            }
            var cb = ps["constantBlocks"]!.AsArray().First(c => (int)c!["block"]!["fixedBindSetOrSpace"]! == 1 && (int)c["block"]!["fixedBindNumber"]! == 0)!;
            var sha = (string)cb["bytes"]!["sha1"]!;
            if (!seen.Add(sha))
                continue;
            var theirs = File.ReadAllBytes(Path.Combine(dir, "blobs", sha + ".bin"));
            output.WriteLine($"-- {rec["eid"]}: block {sha[..8]}");
            foreach (var (name, offset, v) in program.Constants.OrderBy(c => c.Offset))
            {
                var n = Math.Min(Gpu.ProgramGlobals.Width(v.VfxType), 4) * 4;
                var t = theirs.AsSpan(offset, n).ToArray();
                var o = ours.AsSpan(offset, n).ToArray();
                string Show(byte[] b) => string.Join(" ", Enumerable.Range(0, b.Length / 4).Select(i => BitConverter.ToUInt32(b, i * 4) is var u && u is > 0 and < 0x10000 ? u.ToString() : BitConverter.ToSingle(b, i * 4).ToString("G9")));
                output.WriteLine($"  +{offset,-4} {name} ({v.VfxType}, {v.VariableSource} '{v.SourceString}'): {Show(t)}{(t.AsSpan().SequenceEqual(o) ? "" : $"   ours {Show(o)}")}");
            }
            var covered = new bool[theirs.Length];
            foreach (var (_, offset, v) in program.Constants)
                for (var i = 0; i < Math.Min(Gpu.ProgramGlobals.Width(v.VfxType), 4) * 4 && offset + i < covered.Length; i++)
                    covered[offset + i] = true;
            for (var i = 0; i < theirs.Length; i += 4)
                if (!covered[i] && BitConverter.ToUInt32(theirs, i) != 0)
                    output.WriteLine($"  +{i,-4} (not in the write sequence): {BitConverter.ToUInt32(theirs, i):x8}");
        }
    }

    /// <summary>Every captured texture subresource that is a mip (or a layer of a mip) of one of the package's .vtex_c.</summary>
    [Fact]
    public void MapTextures()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_VPK") is not { Length: > 0 } vpk)
            return;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var known = new Dictionary<string, string>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vtex_c") ?? [])
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var tex = (Texture)resource.DataBlock!;
            output.WriteLine($"{entry.GetFullPath()}: {tex.Width} x {tex.Height} x {tex.Depth} {tex.Format}, {tex.NumMipLevels} mips, flags {tex.Flags}");
            foreach (var (level, _, _, _, size) in tex.GetEveryMipLevelMetrics())
            {
                var data = new byte[size];
                tex.ReadTextureMipLevel(data, level);
                known[Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data))] = $"{entry.FileName} mip {level}";
                // A cube or array level holds its layers one after another; each one alone too.
                var layers = tex.Depth > 1 ? tex.Depth : (tex.Flags & VTexFlags.CUBE_TEXTURE) != 0 ? 6 : 1;
                for (var l = 0; layers > 1 && l < layers; l++)
                    known[Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data.AsSpan(l * (size / layers), size / layers)))] = $"{entry.FileName} mip {level} layer {l}";
            }
        }
        var seen = new HashSet<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["contents"]?["before"] is not JsonObject before)
                continue;
            foreach (var (name, value) in before)
            {
                if (value is not JsonArray subs || !seen.Add(name + string.Join(",", subs.Select(s => (string)s!["sha1"]!))))
                    continue;
                var hits = subs.Select(s => known.GetValueOrDefault((string)s!["sha1"]!)).Where(h => h != null).ToList();
                output.WriteLine($"{rec["eid"]} {name}: {subs.Count} subresources, {(hits.Count == 0 ? "none known" : $"{hits.Count} known: {string.Join(", ", hits.Take(8))}")}");
            }
        }
    }

    [Fact]
    public void WorldStreams()
    {
        if (Environment.GetEnvironmentVariable("CUBEREPLAY") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREPLAY_VPK") is not { Length: > 0 } vpk)
            return;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var vbs = new List<(string Name, byte[] Data, uint Stride)>();
        var ibs = new List<(string Name, byte[] Data, uint Size)>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
            {
                for (var i = 0; i < mesh.VBIB.VertexBuffers.Count; i++)
                    vbs.Add(($"{entry.FileName} vb{i}", mesh.VBIB.VertexBuffers[i].Data, mesh.VBIB.VertexBuffers[i].ElementSizeInBytes));
                for (var i = 0; i < mesh.VBIB.IndexBuffers.Count; i++)
                    ibs.Add(($"{entry.FileName} ib{i}", mesh.VBIB.IndexBuffers[i].Data, mesh.VBIB.IndexBuffers[i].ElementSizeInBytes));
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                        output.WriteLine($"{entry.FileName}: draw {dc.GetStringProperty("m_material")} start {dc.GetInt32Property("m_nStartIndex")} count {dc.GetInt32Property("m_nIndexCount")} base {dc.GetInt32Property("m_nBaseVertex")} vertices {dc.GetInt32Property("m_nVertexCount")}");
            }
        }
        foreach (var (n, d, s) in vbs)
            output.WriteLine($"{n}: {d.Length} bytes, stride {s}");
        foreach (var (n, d, s) in ibs)
            output.WriteLine($"{n}: {d.Length} bytes, {s}-byte indices");

        static int Find(byte[] hay, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= hay.Length; i++)
                if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                    return i;
            return -1;
        }
        var seen = new HashSet<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "actions.jsonl")))
        {
            var rec = JsonNode.Parse(line)!;
            if (rec["contents"] is null || rec["vertexInput"] is null || !((string)rec["flags"]!).Contains("Indexed"))
                continue;
            var before = rec["contents"]!["before"]!;
            var vb0 = rec["vertexInput"]!["vertexBuffers"]![0]!;
            var vbName = (string)vb0["resourceId"]!;
            var vbData = File.ReadAllBytes(Path.Combine(dir, "blobs", (string)before[vbName]!["sha1"]! + ".bin"));
            if (seen.Add(vbName))
                foreach (var (n, d, _) in vbs)
                {
                    var at = Find(vbData, d);
                    output.WriteLine($"{vbName} ({vbData.Length} bytes): {n} {(at < 0 ? "not found" : $"at {at}")}");
                }
            var ib = rec["ibuffer"]!;
            var ibData = File.ReadAllBytes(Path.Combine(dir, "blobs", (string)before[(string)ib["resourceId"]!]!["sha1"]! + ".bin"));
            var stride = (int)ib["byteStride"]!;
            var start = (int)(long)ib["byteOffset"]! + ((int)rec["indexOffset"]! * stride);
            var count = (int)rec["numIndices"]!;
            var slice = ibData.AsSpan(start, count * stride).ToArray();
            var where = ibs.Select(b => (b.Name, At: Find(b.Data, slice))).Where(x => x.At >= 0).Select(x => $"{x.Name} at {x.At}").ToList();
            output.WriteLine($"{rec["eid"]} {rec["name"]}: vb {vbName} +{vb0["byteOffset"]}, base vertex {rec["baseVertex"]}, indices {count} from byte {start}: {(where.Count == 0 ? "not found" : string.Join(", ", where))}");
        }
    }
}
