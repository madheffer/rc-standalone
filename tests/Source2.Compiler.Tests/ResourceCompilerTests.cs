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
        var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest("panorama/images/x.svg", svg));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal("panorama/images/x.vsvg_c", result.OutputPath);
        Assert.Equal(ResourceBuilder.BuildPanoramaSvg(null, svg, "panorama/images/x.svg"), result.Output);
        Assert.Equal("panorama/images/x.svg", result.InputDependencies[0].Path);
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
