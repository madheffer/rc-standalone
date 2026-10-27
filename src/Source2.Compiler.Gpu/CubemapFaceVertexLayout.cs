using Silk.NET.Vulkan;
using ValveResourceFormat.CompiledShader;

namespace Source2.Compiler.Gpu;

/// <summary>
/// A cubemap face draw's vertex layout (ledger 154): each input of the vertex program, by the D3D semantic its
/// shader file's attribute map gives the location, fed from the mesh's field of that semantic (binding 0); the
/// instance id (TEXCOORD13) from the per-instance id stream (binding 1, 4 bytes); an input the mesh lacks from the
/// 16-byte zero stream (binding 2, stride 0, per instance). Checked against every world, sky and downsample draw.
/// </summary>
public static class CubemapFaceVertexLayout
{
    /// <summary>A mesh's vertex field: semantic name and index, format, byte offset.</summary>
    public readonly record struct Field(string Semantic, int Index, Format Format, uint Offset);

    /// <summary>The vertex bindings: the mesh's stream, the instance ids, the zero stream.</summary>
    public static (uint Stride, bool PerInstance)[] Bindings(uint meshStride, bool instanced)
        => Bindings([meshStride], instanced);

    /// <summary>
    /// The vertex bindings of a mesh drawn from several streams (props carry a position stream and the rest): the
    /// streams in order, then the instance ids and the zero stream (atixref's captures bind them so).
    /// </summary>
    public static (uint Stride, bool PerInstance)[] Bindings(IReadOnlyList<uint> streamStrides, bool instanced)
        => [.. streamStrides.Select(s => (s, false)), .. instanced ? new[] { (4u, true), (0u, true) } : [(0u, true)]];

    /// <summary>The attributes of <paramref name="vertex"/>'s inputs over a mesh's fields.</summary>
    public static ProgramRunner.Attribute[] Attributes(ValveProgram vertex, IReadOnlyList<Field> mesh, bool instanced)
        => Attributes(vertex, [mesh], instanced);

    /// <summary>
    /// The attributes over a mesh's streams: an input's semantic from the first stream that has it (binding = the
    /// stream's index), the instance id and the inputs no stream has after the streams' bindings.
    /// </summary>
    public static ProgramRunner.Attribute[] Attributes(ValveProgram vertex, IReadOnlyList<IReadOnlyList<Field>> streams, bool instanced)
    {
        var ids = (uint)streams.Count;
        var zero = ids + (instanced ? 1u : 0u);
        var file = vertex.ShaderFile as VfxShaderFileVulkan ?? throw new InvalidDataException($"{vertex.Name} has no Vulkan attribute map");
        var attributes = new List<ProgramRunner.Attribute>();
        foreach (var input in vertex.Reflection.Inputs.OrderBy(i => i.Location))
        {
            if (!file.TryGetInputSemantic((uint)input.Location, out var semantic, out var index))
                throw new InvalidDataException($"{vertex.Name}: no semantic for location {input.Location}");
            var location = (uint)input.Location;
            var stream = Enumerable.Range(0, streams.Count).FirstOrDefault(s => streams[s].Any(f => f.Semantic == semantic && f.Index == index), -1);
            if (instanced && semantic == "TEXCOORD" && index == 13)
                attributes.Add(new ProgramRunner.Attribute(location, ids, Format.R32Uint, 0));
            else if (stream >= 0 && streams[stream].First(f => f.Semantic == semantic && f.Index == index) is var field)
                attributes.Add(new ProgramRunner.Attribute(location, (uint)stream, field.Format, field.Offset));
            else if (instanced)
                attributes.Add(new ProgramRunner.Attribute(location, zero, ZeroFormat(input.Type, input.Components), 0));
            else
                throw new InvalidDataException($"{vertex.Name}: the mesh has no {semantic}{index}");
        }
        return [.. attributes];
    }

    /// <summary>An input the mesh lacks reads the zero stream in its own type and width.</summary>
    private static Format ZeroFormat(string type, int components) => (type, components) switch
    {
        ("uint", 4) => Format.R32G32B32A32Uint,
        ("uint", 1) => Format.R32Uint,
        ("float", 4) => Format.R32G32B32A32Sfloat,
        ("float", 3) => Format.R32G32B32Sfloat,
        ("float", 2) => Format.R32G32Sfloat,
        ("float", 1) => Format.R32Sfloat,
        _ => throw new NotSupportedException($"zero stream for {type} x{components}"),
    };

    /// <summary>A model vertex buffer field's DXGI format as Vulkan's.</summary>
    public static Format FromDxgi(string dxgi) => dxgi switch
    {
        "R32G32B32_FLOAT" => Format.R32G32B32Sfloat,
        "R32G32_FLOAT" => Format.R32G32Sfloat,
        "R32G32B32A32_FLOAT" => Format.R32G32B32A32Sfloat,
        "R32_UINT" => Format.R32Uint,
        "R32_FLOAT" => Format.R32Sfloat,
        "R16G16_UNORM" => Format.R16G16Unorm,
        "R16G16_FLOAT" => Format.R16G16Sfloat,
        "R16G16_SNORM" => Format.R16G16SNorm,
        "R8G8B8A8_UNORM" => Format.R8G8B8A8Unorm,
        "R8G8B8A8_UINT" => Format.R8G8B8A8Uint,
        "R16G16B16A16_FLOAT" => Format.R16G16B16A16Sfloat,
        _ => throw new NotSupportedException($"vertex format {dxgi}"),
    };

    /// <summary>The sky cube's field (12-byte vertices) and the depth downsample quad's (32-byte vertices, the position first).</summary>
    public static readonly Field[] SkyCube = [new("POSITION", 0, Format.R32G32B32Sfloat, 0)];

    public static readonly Field[] DownsampleQuad = [new("POSITION", 0, Format.R32G32Sfloat, 0)];
}
