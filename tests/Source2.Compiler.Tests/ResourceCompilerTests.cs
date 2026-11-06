using Source2.Compiler.Compiling;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>The single compile entry point: routing by extension, RC's bytes through it,
/// dependencies read back from RED2, and failures reported rather than thrown.</summary>
public class ResourceCompilerTests
{
    private const string ProbeSearchPath = "csgo_addons/vpkstuff_rc_probe";

    private static string RcReference(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", name);
    }

    [Theory]
    [InlineData("rc_probe.vsndevts", "soundevents/rc_probe.vsndevts")]
    [InlineData("rc_probe.vdata", "scripts/rc_probe.vdata")]
    [InlineData("rc_probe_ints.vdata", "scripts/rc_probe_ints.vdata")]
    public void Kv3Source_CompilesToResourceCompilersBytes(string file, string contentPath)
    {
        var source = File.ReadAllBytes(RcReference(file));
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest(contentPath, source) { SearchPath = ProbeSearchPath });

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal(contentPath + "_c", result.OutputPath);
        Assert.Equal(File.ReadAllBytes(RcReference(file + "_c")), result.Output);
        var self = result.InputDependencies[0];
        Assert.Equal(contentPath, self.Path);
        Assert.Equal(ProbeSearchPath, self.SearchPath);
        Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(source), self.Crc);
        Assert.True(self.Exists);
    }

    // RcReference/kv3types: compiled by RC 20261103 on 11-06 in vpkstuff_rc_probe (tools/re/texture/rc_probe.py).
    [Theory]
    // Stock compiles decompiled to text (surfaceproperties.vsurf_c, engrave_trophy.vcompmat_c): the hashes recomputed
    // in place, the base members' order, two resource_name weak references sorted.
    [InlineData("kv3types/s2c_kv3_surf1.vsurf", "surfaceproperties/s2ckv3/s2c_kv3_surf1.vsurf")]
    [InlineData("kv3types/s2c_kv3_cm1.vcompmat", "compmatdata/s2ckv3/s2c_kv3_cm1.vcompmat")]
    // Hand-written: base chains, a base later in the list, hidden at both depths, keys in other case,
    // inherited audiosounds, a uint above int32, extra root keys.
    [InlineData("kv3types/s2c_kv3_surf2.vsurf", "surfaceproperties/s2ckv3/s2c_kv3_surf2.vsurf")]
    // Hand-written: thumbnail and point preview members removed, disabled containers (one with a texture input)
    // and mutators removed, a backslashed mixed-case resource_name fixed up.
    [InlineData("kv3types/s2c_kv3_cm3.vcompmat", "compmatdata/s2ckv3/s2c_kv3_cm3.vcompmat")]
    public void Kv3TypeSource_CompilesToResourceCompilersBytes(string file, string contentPath)
    {
        var source = File.ReadAllBytes(RcReference(file));
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest(contentPath, source) { SearchPath = ProbeSearchPath });

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal(contentPath + "_c", result.OutputPath);
        Assert.Equal(File.ReadAllBytes(RcReference(file + "_c")), result.Output);
    }

    // A nameless entry takes its base's name, so RC refuses it as a duplicate (kv3types/s2c_kv3_surf3.vsurf.rc.log).
    [Fact]
    public void SurfaceProperties_NamelessEntryInheritsItsBasesName()
    {
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("surfaceproperties/s2ckv3/s2c_kv3_surf3.vsurf",
            File.ReadAllBytes(RcReference("kv3types/s2c_kv3_surf3.vsurf"))) { SearchPath = ProbeSearchPath });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Surface Property \"default\" is a duplicate of \"default\""));
    }

    // An enabled container's texture input needs the child texture compile (18015a270), which is not ported.
    [Fact]
    public void CompositeMaterial_TextureInputIsRefusedByName()
    {
        var kit = """
            <!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
            { m_Points = [ { m_vecCompositeMaterialAssemblyProcedures = [ { m_vecCompositeInputContainers = [
                { m_vecLooseVariables = [ { m_strName = "t" m_strTextureContentAssetPath = "materials/x.tga" } ] } ] } ] } ] }
            """u8.ToArray();
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("compmatdata/x.vcompmat", kit));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("materials/x.tga") && d.Message.Contains("not ported"));
    }

    [Fact]
    public void UnknownExtension_IsAnErrorNotAnException()
    {
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("models/x.fbx", [1, 2, 3]));
        Assert.False(result.Succeeded);
        Assert.Null(result.Output);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains(".fbx"));
    }

    [Fact]
    public void BrokenSource_IsAnErrorWithItsPath()
    {
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("scripts/bad.vdata", "not kv3 at all"u8.ToArray()));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Path == "scripts/bad.vdata");
    }

    [Fact]
    public void Svg_RoutesToTheVectorGraphicCompiler()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\"><rect width=\"8\" height=\"8\" fill=\"red\"/></svg>"u8.ToArray();
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("panorama/images/x.svg", svg)
        {
            Settings = new VectorGraphicSettings(Sanitize: true),
        });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal("panorama/images/x.vsvg_c", result.OutputPath);
        Assert.Equal(ResourceBuilder.BuildPanoramaSvg(null, svg, "panorama/images/x.svg"), result.Output);
        Assert.Equal("panorama/images/x.svg", result.InputDependencies[0].Path);
    }

    // RcReference/sounds/t.wav compiled by RC 1.41.9.5 on 11-03 in vpkstuff_rc_probe: the v5
    // layout with the 20261023 schema's CTRL and the .vmix sibling probe.
    [Fact]
    public void Wav_CompilesToResourceCompilersBytes()
    {
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("sounds/s2cprobe/t.wav",
            File.ReadAllBytes(RcReference("sounds/t.wav"))) { SearchPath = "csgo_addons/vpkstuff_rc_probe" });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal("sounds/s2cprobe/t.vsnd_c", result.OutputPath);
        Assert.Equal(File.ReadAllBytes(RcReference("sounds/t.vsnd_c")), result.Output);
    }

    // RcReference/panorama/t.svg compiled by RC on 11-03 in vpkstuff_rc_probe: the file's bytes
    // stored as they are (rc 18015e2d0), RED2 with its CRC.
    [Fact]
    public void Svg_CompilesToResourceCompilersBytes()
    {
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("panorama/images/s2cprobe/t.svg",
            File.ReadAllBytes(RcReference("panorama/t.svg"))) { SearchPath = "csgo_addons/vpkstuff_rc_probe" });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal(File.ReadAllBytes(RcReference("panorama/t.vsvg_c")), result.Output);
    }

    [Fact]
    public void Registry_LetsACompilerBeAddedOrReplaced()
    {
        var compiler = ResourceCompiler.CreateDefault().Register(new EchoCompiler());
        Assert.True(compiler.CanCompile("x/y.echo"));
        var result = compiler.Compile(new CompileRequest("x/y.echo", [7]) { OutputPath = "x/z.echo_c" });
        Assert.Equal("x/z.echo_c", result.OutputPath);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void DirectoryContent_RefusesToLeaveItsRoots()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), "x");
            var content = new DirectoryContentSource(dir.FullName);
            Assert.NotNull(content.Read("a.txt"));
            Assert.Null(content.Read("../a.txt"));
            Assert.Null(content.Read("missing.txt"));
        }
        finally { dir.Delete(true); }
    }

    private sealed class EchoCompiler : IResourceCompiler
    {
        public IReadOnlyCollection<string> SourceExtensions { get; } = [".echo"];
        public string OutputPathFor(string sourcePath) => sourcePath + "_c";
        public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        {
            diagnostics.Add(new(DiagnosticSeverity.Info, "echoed"));
            return new(Kv3SourceCompiler.Compile(File.ReadAllBytes(RcReference("rc_probe.vdata")), ".vdata", request.SourcePath));
        }
    }
}
