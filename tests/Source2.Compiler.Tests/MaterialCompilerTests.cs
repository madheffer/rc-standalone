using Source2.Compiler.Compiling;
using Source2.Compiler.Materials;
using ValveKeyValue;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <c>.vmat</c> sources through <see cref="MaterialCompiler"/>, against resourcecompiler's
/// <c>.vmat_c</c> for the same source (RcReference/materials): each KV3 block as a tree first,
/// so a failure names the field, then the whole file. Needs CS2's shaders and content.
/// </summary>
public class MaterialCompilerTests
{
    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", "materials", name);
    }

    private static CompileResult? Compile(string name, bool children = false)
    {
        if (CS2Fixtures.StockPak() is not { } pak)
            return null;
        var csgo = Path.GetDirectoryName(pak)!;
        var game = Path.GetDirectoryName(Path.GetDirectoryName(csgo)!)!;
        var addon = new MemoryContentSource();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Fixture("color.png"))!))
            addon.Add("materials/s2cmat/" + Path.GetFileName(file), File.ReadAllBytes(file));
        return ResourceCompiler.CreateDefault().Compile(new CompileRequest($"materials/s2cmat/{name}.vmat", File.ReadAllBytes(Fixture(name + ".vmat")))
        {
            SearchPath = "csgo_addons/vpkstuff_rc_probe",
            Content = addon,
            Settings = new MaterialCompileSettings
            {
                ShaderPackage = Path.Combine(csgo, "shaders_pc_dir.vpk"),
                Mounts = [new("csgo", new DirectoryContentSource(Path.Combine(game, "content", "csgo"))),
                          new("core", new DirectoryContentSource(Path.Combine(game, "content", "core")))],
                CompileChildren = children,
            },
        });
    }

    [Theory]
    [InlineData("cx_color")]
    [InlineData("cx_full")]
    [InlineData("cx_translucent")]
    [InlineData("cx_params")]
    [InlineData("cx_vtex")]
    [InlineData("char")]
    [InlineData("unlit")]
    [InlineData("cx_ao256")]
    [InlineData("cx_tiny")]
    [InlineData("cx_odd")]
    [InlineData("cx_metal_noise")]
    [InlineData("cx_order")]
    public void Vmat_CompilesToResourceCompilersBytes(string name)
    {
        if (Compile(name) is not { } result) { CS2Fixtures.Skip("shaders_pc_dir.vpk"); return; }
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var rcBytes = File.ReadAllBytes(Fixture($"compiled/{name}.vmat_c"));
        var rc = CompiledResource.Read(rcBytes);
        var ours = CompiledResource.Read(result.Output!);
        Assert.Equal(rc.File.Blocks.Select(b => b.Type), ours.File.Blocks.Select(b => b.Type));
        Assert.Equal(rc.References, ours.References);
        foreach (var block in new[] { "RED2", "DATA", "INSG" })
            Assert.Equal("", Diff(rc.Kv3(block)!, ours.Kv3(block)!, block));
        Assert.True(rcBytes.AsSpan().SequenceEqual(result.Output), $"{name}.vmat_c: trees equal, bytes differ");
    }

    /// <summary>The whole compile, children included: the material and every child texture equal RC's files.</summary>
    [Theory]
    [InlineData("cx_color")]
    [InlineData("cx_full")]
    [InlineData("cx_translucent")]
    [InlineData("cx_params")]
    [InlineData("cx_vtex")]
    [InlineData("char")]
    [InlineData("unlit")]
    [InlineData("cx_ao256")]
    [InlineData("cx_tiny")]
    [InlineData("cx_odd")]
    [InlineData("cx_metal_noise")]
    [InlineData("cx_order")]
    public void Vmat_WithChildren_CompilesToResourceCompilersFiles(string name)
    {
        if (Compile(name, children: true) is not { } result) { CS2Fixtures.Skip("shaders_pc_dir.vpk"); return; }
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.True(File.ReadAllBytes(Fixture($"compiled/{name}.vmat_c")).AsSpan().SequenceEqual(result.Output), $"{name}.vmat_c");
        var rcChildren = CompiledResource.Read(result.Output!).Kv3("RED2")!["m_ChildResourceList"]!.Values.Select(v => (string)v + "_c").ToList();
        Assert.Equal(rcChildren, result.Children.Select(c => c.Path).Order(StringComparer.Ordinal).ToList());
        foreach (var child in result.Children)
            Assert.True(File.ReadAllBytes(Fixture("compiled/" + Path.GetFileName(child.Path))).AsSpan().SequenceEqual(child.Bytes), child.Path);
    }

    /// <summary>Every child whose algorithms are ported, against RC's file; the rest are listed as pending.</summary>
    [Fact]
    public void ChildTextures_CompileToResourceCompilersBytes()
    {
        if (CS2Fixtures.StockPak() is not { } pak) { CS2Fixtures.Skip("shaders_pc_dir.vpk"); return; }
        var csgo = Path.GetDirectoryName(pak)!;
        var game = Path.GetDirectoryName(Path.GetDirectoryName(csgo)!)!;
        var addon = new MemoryContentSource();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Fixture("color.png"))!))
            addon.Add("materials/s2cmat/" + Path.GetFileName(file), File.ReadAllBytes(file));
        var mounts = new List<ContentMount> { new("csgo_addons/vpkstuff_rc_probe", addon),
            new("csgo", new DirectoryContentSource(Path.Combine(game, "content", "csgo"))),
            new("core", new DirectoryContentSource(Path.Combine(game, "content", "core"))) };
        (string, byte[])? Find(string path)
        {
            foreach (var m in mounts)
                if (m.Content.Read(path) is { } b) return (m.SearchPath, b);
            return null;
        }

        using var shaders = new ValvePak.Package();
        shaders.Read(Path.Combine(csgo, "shaders_pc_dir.vpk"));
        Shaders.ShaderProgram Program(string shader, string kind)
        {
            shaders.ReadEntry(shaders.FindEntry($"shaders/vfx/{Path.GetFileNameWithoutExtension(shader)}_pc_50_{kind}.vcs")!, out var bytes);
            return Shaders.ShaderProgram.Read(bytes);
        }

        var compared = new List<string>();
        var pending = new List<string>();
        // Known: odd_png (24x40) differs from RC only in the reflectivity's green, by one ulp:
        // our float 3x5 -> 1x2 step rounds differently (every 8-bit level matches). Settled by a
        // Frida capture of QuarterSize on that chain (docs/MATERIALS.md, open).
        var known = new HashSet<string> { };
        foreach (var vmatFile in Directory.GetFiles(Path.GetDirectoryName(Fixture("color.png"))!, "*.vmat"))
        {
            var vmat = VmatSource.Parse(File.ReadAllBytes(vmatFile), vmatFile);
            var textures = MaterialTextures.Resolve(Program(vmat.Shader, "features"), Program(vmat.Shader, "ps"), vmat.ByName,
                path => Find(path) is not null);
            foreach (var g in textures.Where(t => t.Generated is not null).Select(t => t.Generated!))
            {
                var name = Path.GetFileName(g.ChildPath);
                if (compared.Contains(name) || pending.Contains(name))
                    continue;
                byte[] ours;
                try { ours = ChildTextureCompiler.Compile(g, Find, "csgo_addons/vpkstuff_rc_probe", RcTexture.Bc7Vendor.Amd); }
                catch (InvalidOperationException e) when (e.Message.Contains("not compiled here yet") || e.Message.Contains("not measured"))
                {
                    pending.Add(name);
                    continue;
                }
                if (known.Contains(name))
                {
                    pending.Add(name + " (known)");
                    continue;
                }
                var rcBytes = File.ReadAllBytes(Fixture($"compiled/{name}_c"));
                var rc = CompiledResource.Read(rcBytes);
                var mine = CompiledResource.Read(ours);
                Assert.Equal("", Diff(rc.Kv3("RED2")!, mine.Kv3("RED2")!, name + " RED2"));
                if (!rcBytes.AsSpan().SequenceEqual(ours))
                    File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"s2c_child_{name}_c"), ours);
                Assert.True(rcBytes.AsSpan().SequenceEqual(ours), $"{name}_c differs from RC's (ours in %TEMP%/s2c_child_{name}_c)");
                compared.Add(name);
            }
        }
        Console.WriteLine($"children identical: {string.Join(", ", compared)}; pending: {string.Join(", ", pending)}");
        Assert.NotEmpty(compared);
    }

    // The first difference between two KV3 trees, as a path and both values; empty when equal.
    private static string Diff(KVObject a, KVObject b, string path)
    {
        if (a.ValueType != b.ValueType && !(IsInt(a) && IsInt(b)))
            return $"{path}: {a.ValueType} {Show(a)} vs {b.ValueType} {Show(b)}";
        if (a.IsArray || a.ValueType == KVValueType.Collection)
        {
            var ac = a.IsArray ? a.Values.Select((v, i) => ($"[{i}]", v)).ToList() : a.Children.Select(c => ("." + c.Key, c.Value)).ToList();
            var bc = b.IsArray ? b.Values.Select((v, i) => ($"[{i}]", v)).ToList() : b.Children.Select(c => ("." + c.Key, c.Value)).ToList();
            for (int i = 0; i < Math.Min(ac.Count, bc.Count); i++)
            {
                if (ac[i].Item1 != bc[i].Item1)
                    return $"{path}: key {ac[i].Item1} vs {bc[i].Item1}";
                var d = Diff(ac[i].Item2, bc[i].Item2, path + ac[i].Item1);
                if (d.Length > 0)
                    return d;
            }
            return ac.Count == bc.Count ? "" : $"{path}: {ac.Count} entries vs {bc.Count} ({string.Join(",", ac.Skip(bc.Count).Concat(bc.Skip(ac.Count)).Select(x => x.Item1))})";
        }
        if (a.Flag != b.Flag)
            return $"{path}: flag {a.Flag} vs {b.Flag}";
        return Show(a) == Show(b) ? "" : $"{path}: {Show(a)} vs {Show(b)}";
    }

    private static bool IsInt(KVObject o) => o.ValueType is KVValueType.Int32 or KVValueType.Int64 or KVValueType.UInt32 or KVValueType.UInt64 or KVValueType.Int16 or KVValueType.UInt16;

    private static string Show(KVObject o) => o.ValueType switch
    {
        KVValueType.BinaryBlob => Convert.ToHexString(o.AsBlob()),
        KVValueType.Null => "null",
        _ => o.ToString() ?? "",
    };
}
