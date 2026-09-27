using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Exploration: a SPIR-V program's resources and inputs (<see cref="SpirvReflection"/>). <c>SPIRVREFLECT=&lt;file&gt;[;&lt;file&gt;...]</c>.</summary>
public class SpirvReflectProbe(ITestOutputHelper output)
{
    [Fact]
    public void Reflect()
    {
        if (Environment.GetEnvironmentVariable("SPIRVREFLECT") is not { Length: > 0 } spec)
            return;
        foreach (var path in spec.Split(';'))
        {
            var r = SpirvReflection.Parse(File.ReadAllBytes(path));
            output.WriteLine($"== {Path.GetFileName(path)} entry {r.EntryPoint} caps {string.Join(",", r.Capabilities)}");
            foreach (var i in r.Inputs)
                output.WriteLine($"  input {i.Location} {i.Name} {i.Type}x{i.Components}");
            foreach (var res in r.Resources)
            {
                output.WriteLine($"  set {res.Set} binding {res.Binding} {res.Kind} {res.Name}:{res.TypeName} array {res.ArrayLength} {res.Dim} size {res.BlockSize}");
                foreach (var m in res.Members)
                    output.WriteLine($"      +{m.Offset} {m.Name} {m.Type} ({m.Size})");
            }
        }
    }
}
