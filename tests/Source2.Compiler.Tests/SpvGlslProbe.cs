using Vortice.SpirvCross;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a raw SPIR-V module (rdc_spirv.py writes them from a capture) decompiled to GLSL through SPIRV-Cross,
/// without Valve's names. <c>SPV2GLSL=&lt;module.spv&gt;[;&lt;module.spv&gt;...]</c>: each written beside it as .glsl (.hlsl with SPV2GLSL_HLSL=1).
/// </summary>
public class SpvGlslProbe
{
    [Fact]
    public void Decompile()
    {
        if (Environment.GetEnvironmentVariable("SPV2GLSL") is not { Length: > 0 } spec)
            return;
        foreach (var path in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = File.ReadAllBytes(path);
            var words = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
            Assert.Equal(Result.Success, SpirvCrossApi.spvc_context_create(out var context));
            try
            {
                Assert.Equal(Result.Success, SpirvCrossApi.spvc_context_parse_spirv(context, words, out var ir));
                // HLSL when GLSL cannot express a buffer block's layout (SPV2GLSL_HLSL=1).
                var hlsl = Environment.GetEnvironmentVariable("SPV2GLSL_HLSL") == "1";
                Assert.Equal(Result.Success, SpirvCrossApi.spvc_context_create_compiler(context, hlsl ? Backend.HLSL : Backend.GLSL, ir, CaptureMode.TakeOwnership, out var compiler));
                Assert.Equal(Result.Success, SpirvCrossApi.spvc_compiler_create_compiler_options(compiler, out var options));
                if (hlsl)
                    SpirvCrossApi.spvc_compiler_options_set_uint(options, CompilerOption.HLSLShaderModel, 61);
                else
                {
                    SpirvCrossApi.spvc_compiler_options_set_uint(options, CompilerOption.GLSLVersion, 460);
                    SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.GLSLES, SpirvCrossApi.SPVC_FALSE);
                    SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.GLSLVulkanSemantics, SpirvCrossApi.SPVC_TRUE);
                    // As VRF decompiles Valve's shaders: uniform buffers as plain uniforms (their layouts need not fit std140).
                    SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.GLSLEmitUniformBufferAsPlainUniforms, SpirvCrossApi.SPVC_TRUE);
                }
                SpirvCrossApi.spvc_compiler_install_compiler_options(compiler, options);
                var compiled = SpirvCrossApi.spvc_compiler_compile(compiler, out var code);
                Assert.True(compiled == Result.Success, $"{path}: {compiled} {SpirvCrossApi.spvc_context_get_last_error_string(context)}");
                File.WriteAllText(Path.ChangeExtension(path, hlsl ? ".hlsl" : ".glsl"), code);
            }
            finally
            {
                SpirvCrossApi.spvc_context_destroy(context);
            }
        }
    }
}
