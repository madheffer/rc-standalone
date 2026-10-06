using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>LAYOUTSIG=&lt;Valve's vpk&gt;|&lt;path prefix&gt;</c>): each model's vertex
/// buffer layouts beside its materials' input and depth signatures.
/// </summary>
public class LayoutSignatureProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("LAYOUTSIG") is not { Length: > 0 } spec || spec.Split('|') is not [var vpk, var prefix]
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", "s2c_rc_probe"));
        using var package = new Package();
        package.Read(vpk);
        foreach (var e in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.TypeName == "vmdl_c")
                     .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            package.ReadEntry(e, out var bytes);
            using var r = new Resource();
            r.Read(new MemoryStream(bytes));
            if (r.DataBlock is Model model)
                foreach (var mesh in model.GetEmbeddedMeshesAndLoD().Select(x => x.Mesh))
                    output.WriteLine($"{e.GetFileName()}: " + string.Join(" / ", mesh.VBIB.VertexBuffers.Select(vbuf =>
                        string.Join(",", vbuf.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.ShaderSemantic}")))));
            if (r.EditInfo is ResourceEditInfo2 { Data: { } red2 } && red2.Root["m_ArgumentDependencies"] is { IsArray: true } args)
                output.WriteLine("  args " + string.Join(" ", args.Values.Select(a => $"{a["m_ParameterName"]}={a["m_nFingerprint"]}")));
            foreach (var m in r.ExternalReferences!.ResourceRefInfoList.Select(x => x.Name).Where(n => n.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)))
            {
                var raw = content.Read(m + "_c");
                output.WriteLine(raw == null ? $"  {m}: absent"
                    : $"  {m}: elems {string.Join(",", MaterialAuthor.ExtractInputSignature(raw).Select(x => x.Semantic))}; depth {string.Join(",", MaterialAuthor.ExtractDepthSignature(raw).Select(x => x.Semantic))}");
            }
        }
    }
}
