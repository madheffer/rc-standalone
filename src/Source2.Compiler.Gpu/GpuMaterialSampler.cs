using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;
using Value = Source2.Compiler.Gpu.VfxExpression.Value;

namespace Source2.Compiler.Gpu;

/// <summary>
/// physicsbuilder's material sampler render (CMaterialSampler) on Vulkan: a
/// material's ToolsVis programs (vs with D_COMPRESSED_NORMALS_AND_TANGENTS,
/// ps at S_SHADER_QUALITY 1) in mode 80, its constants and textures bound
/// bindless, each point drawn as a small triangle on its own pixel of an
/// RGBA8 target and read back. Matched against resourcecompiler's own
/// render on ze_hold_em_nb: bit for bit on 106 of 106 points and within one
/// step on the rest of 3,710, every point's layer the same (that compile ran
/// the DX11 programs; see docs/PHYSICS.md).
/// </summary>
public sealed unsafe class GpuMaterialSampler : IDisposable
{
    private readonly GpuDevice _gpu = new();
    private readonly Package _shaders = new();
    private readonly Func<string, byte[]?> _files;
    private readonly Dictionary<string, MaterialSampler.Renderer?> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, bool Srgb), GpuDevice.GpuImage> _textures = [];
    private readonly List<ProgramRunner> _runners = [];
    private readonly List<Sampler> _samplers = [];
    private readonly GpuDevice.GpuImage _grey;

    /// <summary>The device's name, for logs.</summary>
    public string DeviceName => _gpu.Name;

    /// <param name="shaderVpk">CS2's shaders_vulkan_dir.vpk.</param>
    /// <param name="files">A compiled file's bytes by its path (materials/..._c), or null.</param>
    public GpuMaterialSampler(string shaderVpk, Func<string, byte[]?> files)
    {
        _shaders.Read(shaderVpk);
        _files = files;
        _grey = Upload1x1([128, 128, 128, 255]);
    }

    /// <summary>
    /// The renderer for a material (its .vmat path), or null when it cannot be
    /// drawn here: not found, or no ToolsVis programs for its shader.
    /// </summary>
    public MaterialSampler.Renderer? For(string material)
    {
        var path = material.Replace('\\', '/');
        if (!path.EndsWith("_c", StringComparison.Ordinal))
            path += "_c";
        if (_renderers.TryGetValue(path, out var cached))
            return cached;
        MaterialSampler.Renderer? renderer = null;
        if (_files(path) is { } bytes)
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is Material mat)
                renderer = Build(mat);
        }
        _renderers[path] = renderer;
        return renderer;
    }

    private MaterialSampler.Renderer? Build(Material mat)
    {
        var features = mat.IntParams.Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => (int)p.Value);
        var shader = Path.GetFileNameWithoutExtension(mat.ShaderName);
        ValveProgram vs, ps;
        try
        {
            vs = ValveProgram.Load(_shaders, shader, "vulkan_50", "vs", new Dictionary<string, int> { ["S_MODE_TOOLS_VIS"] = 1 }, new Dictionary<string, int> { ["D_COMPRESSED_NORMALS_AND_TANGENTS"] = 1 }, features);
            ps = ValveProgram.Load(_shaders, shader, "vulkan_50", "ps", new Dictionary<string, int> { ["S_MODE_TOOLS_VIS"] = 1, ["S_SHADER_QUALITY"] = 1 }, new Dictionary<string, int>(), features);
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException)
        {
            return null;
        }
        // Binding 0: the pixel triangles; 1: the 40-byte records (uv0, uv1,
        // packed frame at 28, paint RGBA8 at 32, tint at 36); 2: zeros for the
        // inputs the MaterialSampler layout leaves out.
        ProgramRunner.Attribute[] attributes =
        [
            new(0, 0, Format.R32G32B32Sfloat, 0),
            new(1, 1, Format.R32G32Sfloat, 0),
            new(2, 1, Format.R32G32Sfloat, 8),
            new(3, 1, Format.R32Uint, 28),
            new(4, 2, Format.R32G32B32A32Uint, 0),
            new(5, 2, Format.R32Uint, 16),
            new(6, 1, Format.R8G8B8A8Unorm, 36),
            new(7, 1, Format.R8G8B8A8Unorm, 32),
        ];
        var runner = new ProgramRunner(_gpu, vs, ps, [12, 40, 32], attributes, Format.R8G8B8A8Unorm);
        _runners.Add(runner);

        var samplerSlots = BindSamplers(runner, ps, mat);
        runner.SetImage(4, 46, 0, _grey.View);
        var textureSlots = new Dictionary<(string, bool), uint>();
        int TextureSlot(string name, VfxVariableDescription v)
        {
            if (!mat.TextureParams.TryGetValue(name, out var path))
                return 0;
            var key = (path, v.SrgbRead);
            if (!textureSlots.TryGetValue(key, out var slot))
            {
                if (!_textures.TryGetValue(key, out var image))
                {
                    var bytes = _files(path.EndsWith("_c", StringComparison.Ordinal) ? path : path + "_c");
                    using var resource = new Resource();
                    if (bytes == null)
                        return 0;
                    resource.Read(new MemoryStream(bytes));
                    if (resource.DataBlock is not Texture tex)
                        return 0;
                    _textures[key] = image = GpuTextures.Upload(_gpu, tex, v.SrgbRead);
                }
                slot = (uint)textureSlots.Count + 1;
                textureSlots[key] = slot;
                runner.SetImage(4, 46, slot, image.View);
            }
            return (int)slot;
        }

        // The render attributes the sampler sets: ToolsVis mode 80, MaterialSamplerMode 0.
        var attributesByName = new Dictionary<string, Value> { ["g_nToolsVisMode"] = Value.Of(80), ["MaterialSamplerMode"] = Value.Of(0) };
        foreach (var (program, stageSet) in new[] { (vs, 0), (ps, 1) })
        {
            var scope = new ProgramGlobals.MaterialScope(mat, program, attributesByName);
            foreach (var r in program.Reflection.Resources.Where(r => r.Kind is SpirvReflection.Kind.UniformBuffer or SpirvReflection.Kind.StorageBuffer))
            {
                // The SPIR-V is stripped of names: the stage's set holds _Globals_
                // at binding 0, PerViewConstantBuffer_t at 1 (the camera at the
                // origin looking along +X, orthographic: clip = (-y, z, 0.5, 1),
                // RowMajor); set 4 the transforms (30, one identity) and the
                // instances (32, one: white tint, transform 0).
                var data = new byte[Math.Max(16, r.BlockSize)];
                if (r.Set == stageSet && r.Binding == 0)
                    ProgramGlobals.Fill(program, data, scope, TextureSlot, name => samplerSlots.GetValueOrDefault(name));
                else if (r.Set == 0 && r.Binding == 1)
                    MemoryMarshal.AsBytes(new float[] { 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0.5f, 1 }.AsSpan()).CopyTo(data);
                else if (r.Set == 4 && r.Binding == 30)
                    data = MemoryMarshal.AsBytes(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }.AsSpan()).ToArray();
                else if (r.Set == 4 && r.Binding == 32)
                    data = MemoryMarshal.AsBytes(new uint[] { 0xffffffff, 0, 0, 0, 0, 0, 0, 0 }.AsSpan()).ToArray();
                runner.SetBuffer(r.Set, r.Binding, data);
            }
        }
        return (records, count, side) => Draw(runner, records, count, side);
    }

    /// <summary>
    /// FUN_18064dfb0's pixel triangles: point i on pixel i of a side x side
    /// target, row by row from the top, a small triangle around the pixel's
    /// centre in the view's (y, z) plane.
    /// </summary>
    public static float[] PixelTriangles(int count, int side)
    {
        var positions = new float[count * 9];
        float w = side, h = side, top = 1f, left = 1f;
        var dx = ((1f - (w + w) / w) - left) / w;
        var dy = (top - (1f - (h + h) / h)) / h;
        for (var i = 0; i < count; i++)
        {
            var row = i / side;
            var cy = top - (row + 0.5f) * dy;
            var cx = ((i - row * side) + 0.5f) * dx + left;
            float[] tri = [0, cx, (dy * 0.75f) + cy, 0, cx - (dx * 0.75f), cy - (dy * 0.375f), 0, (dx * 0.75f) + cx, cy - (dy * 0.375f)];
            tri.CopyTo(positions, i * 9);
        }
        return positions;
    }

    private static byte[] Draw(ProgramRunner runner, byte[] records, int count, int side)
    {
        var positions = PixelTriangles(count, side);
        var pixels = runner.Draw([MemoryMarshal.AsBytes(positions.AsSpan()).ToArray(), records, new byte[count * 3 * 32]], (uint)(count * 3), (uint)side, (uint)side, [0, 0, 0, 0]);
        var rgb = new byte[count * 3];
        for (var i = 0; i < count; i++)
            (rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]) = (pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2]);
        return rgb;
    }

    // The engine's samplers by name (the same states VRF names them from), and
    // g_sUserConfig, whose address modes the ps evaluates from the material.
    private Dictionary<string, int> BindSamplers(ProgramRunner runner, ValveProgram ps, Material mat)
    {
        Sampler Make(Filter filter, SamplerMipmapMode mip, SamplerAddressMode u, SamplerAddressMode v)
        {
            var info = new SamplerCreateInfo { SType = StructureType.SamplerCreateInfo, MagFilter = filter, MinFilter = filter, MipmapMode = mip, AddressModeU = u, AddressModeV = v, AddressModeW = v, MaxLod = 1000 };
            GpuDevice.Check(_gpu.Vk.CreateSampler(_gpu.Device, &info, null, out var s), "vkCreateSampler");
            _samplers.Add(s);
            return s;
        }
        static SamplerAddressMode Address(int mode) => mode switch
        {
            1 => SamplerAddressMode.MirroredRepeat,
            2 => SamplerAddressMode.ClampToEdge,
            3 => SamplerAddressMode.ClampToBorder,
            4 => SamplerAddressMode.MirrorClampToEdge,
            _ => SamplerAddressMode.Repeat,
        };
        // g_sUserConfig is the render state whose Filter is 255; its AddressU/V are literals or expressions.
        var scope = new ProgramGlobals.MaterialScope(mat, ps, new Dictionary<string, Value>());
        var states = ps.Sequence.RenderState.Select(f => (f.LayoutSet, f.BindingSlot, Variable: ps.Program.VariableDescriptions[f.VariableIndex]))
            .Where(f => f.Variable.RegisterType == VfxRegisterType.SamplerState).ToList();
        int State(int slot, string name)
        {
            var v = states.FirstOrDefault(s => s.BindingSlot == slot && s.Variable.Name == name).Variable;
            return v == null ? 0 : v.CompiledExpression.Length > 0 ? (int)VfxExpression.Evaluate(v.CompiledExpression, scope)[0] : v.IntDefs[0];
        }
        var user = states.FirstOrDefault(s => s.Variable.Name == "Filter" && s.Variable.IntDefs[0] == 255);
        var userU = user.Variable == null ? 0 : State(user.BindingSlot, "AddressU");
        var userV = user.Variable == null ? 0 : State(user.BindingSlot, "AddressV");
        var slots = new Dictionary<string, (int Slot, Sampler Sampler)>
        {
            ["g_sBilinearClamp"] = (1, Make(Filter.Linear, SamplerMipmapMode.Nearest, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge)),
            ["g_sTrilinearWrap"] = (2, Make(Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.Repeat, SamplerAddressMode.Repeat)),
            ["g_sTrilinearClamp"] = (3, Make(Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge)),
            ["g_sPointClamp"] = (4, Make(Filter.Nearest, SamplerMipmapMode.Nearest, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge)),
            ["g_sUserConfig"] = (5, Make(Filter.Linear, SamplerMipmapMode.Linear, Address(userU), Address(userV))),
            ["g_sCookieSampler"] = (6, Make(Filter.Linear, SamplerMipmapMode.Nearest, SamplerAddressMode.ClampToBorder, SamplerAddressMode.ClampToBorder)),
        };
        runner.SetSampler(4, 29, 0, slots["g_sBilinearClamp"].Sampler);
        foreach (var (_, (slot, s)) in slots)
            runner.SetSampler(4, 29, (uint)slot, s);
        return slots.ToDictionary(kv => kv.Key, kv => kv.Value.Slot);
    }

    private GpuDevice.GpuImage Upload1x1(byte[] rgba)
    {
        var image = _gpu.CreateImage(1, 1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        var staging = _gpu.Upload(rgba, BufferUsageFlags.TransferSrcBit);
        _gpu.Submit(cmd =>
        {
            _gpu.Transition(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), new Offset3D(0, 0, 0), new Extent3D(1, 1, 1));
            _gpu.Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image.Image, ImageLayout.TransferDstOptimal, 1, &region);
            _gpu.Transition(cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        });
        _gpu.Destroy(staging);
        return image;
    }

    private void Destroy(GpuDevice.GpuImage image)
    {
        _gpu.Vk.DestroyImageView(_gpu.Device, image.View, null);
        _gpu.Vk.DestroyImage(_gpu.Device, image.Image, null);
        _gpu.Vk.FreeMemory(_gpu.Device, image.Memory, null);
    }

    public void Dispose()
    {
        foreach (var r in _runners)
            r.Dispose();
        foreach (var s in _samplers)
            _gpu.Vk.DestroySampler(_gpu.Device, s, null);
        foreach (var image in _textures.Values)
            Destroy(image);
        Destroy(_grey);
        _shaders.Dispose();
        _gpu.Dispose();
    }
}
