using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Source2.Compiler.Lighting.Bake;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Gpu;

/// <summary>
/// vrad3's light probe volume compute (compute_lpv) on the GPU with Valve's rtx_vrad3_lpv: D_LPV_PASS 0
/// voxelizes the bordered grid into the solid mask, the flood fill runs on the CPU as vrad3 does
/// (LightProbeVolumeBaker.NearestEmpty / FloodFill), and D_LPV_PASS 1 accumulates the luxels over the
/// script's compute blocks. Set 6 is the pass's: g_Luxels 158, g_SolidMask 159, g_LightBins 160,
/// g_RandomSamples 161 (the Halton volume) and g_LpvBlockConverged 162; the push constants are
/// LpvPushConstants_t (128 bytes; the row-major mat4x3 is three 16-byte rows).
/// </summary>
public sealed unsafe partial class RtxBakeHost
{
    private byte[][] _hitModules = [];
    private RtxShaderLibrary? _rtxShaders;
    private int _executionReordering;
    private PipelineLayout _lpvLayout;
    private (Pipeline Pipeline, GpuDevice.GpuBuffer Sbt, StridedDeviceAddressRegionKHR[] Regions) _lpvVoxelize, _lpvCompute;

    private const int LpvLuxelSize = 192;

    private void CreateLpv()
    {
        if (_lpvLayout.Handle != 0)
            return;
        var push = new PushConstantRange(ShaderStageFlags.RaygenBitKhr, 0, 128);
        fixed (DescriptorSetLayout* pl = _setLayouts)
        {
            var info = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 7, PSetLayouts = pl, PushConstantRangeCount = 1, PPushConstantRanges = &push };
            GpuDevice.Check(_vk.CreatePipelineLayout(_gpu.Device, &info, null, out _lpvLayout), "vkCreatePipelineLayout");
        }
        var program = _rtxShaders!.Get("rtx_vrad3_lpv") ?? throw new FileNotFoundException("rtx_vrad3_lpv");
        var reorder = program.Rtx.StaticComboArray.Single(c => c.Name == "S_EXECUTION_REORDERING").ComboIndexValue * _executionReordering;
        var pass = program.Rtx.DynamicComboArray.Single(c => c.Name == "D_LPV_PASS").ComboIndexValue;
        _lpvVoxelize = Pipeline(Spirv(program.Rtx, reorder, 0), _hitModules, _lpvLayout);
        _lpvCompute = Pipeline(Spirv(program.Rtx, reorder, pass), _hitModules, _lpvLayout);
    }

    private void DestroyLpv()
    {
        if (_lpvLayout.Handle == 0)
            return;
        _vk.DestroyPipeline(_gpu.Device, _lpvVoxelize.Pipeline, null);
        _vk.DestroyPipeline(_gpu.Device, _lpvCompute.Pipeline, null);
        _vk.DestroyPipelineLayout(_gpu.Device, _lpvLayout, null);
        _lpvLayout = default;
    }

    /// <summary>
    /// One compute_lpv routine: lpv_init and lpv_image_gpu (the grid), lpv_voxelize_gpu, lpv_flood_fill and
    /// each lpv_compute_block_gpu of the script (blocks of at most 96 cells per axis, each reseeding its
    /// random draws), then the readback as LightProbeVolumeBaker gives it.
    /// </summary>
    public LightProbeVolumeBaker.Result BakeLpv(LightProbeVolumeBaker.Volume v, int minSpp, int maxSpp, float error, Action<string>? log = null)
    {
        CreateLpv();
        var n = new[] { v.CellsX, v.CellsY, v.CellsZ };
        var min = new[] { v.BoxMin.X, v.BoxMin.Y, v.BoxMin.Z };
        var max = new[] { v.BoxMax.X, v.BoxMax.Y, v.BoxMax.Z };
        var bn = new int[3];
        var bmin = new float[3];
        var bmax = new float[3];
        for (var a = 0; a < 3; a++)
        {
            bn[a] = n[a] + 2;
            bmin[a] = min[a] - (max[a] - min[a]) / n[a];
            bmax[a] = (max[a] - min[a]) / n[a] + max[a];
        }
        var m = v.LocalToWorld;
        var localToWorld = new mat4x3(new vec3(m[0], m[4], m[8]), new vec3(m[1], m[5], m[9]), new vec3(m[2], m[6], m[10]), new vec3(m[3], m[7], m[11]));

        // lpv_voxelize_gpu: 32 passes of file 0 over the bordered grid.
        var bordered = bn[0] * bn[1] * bn[2];
        var mask = Keep(_gpu.CreateBuffer((ulong)(bordered * 4), BufferUsageFlags.StorageBufferBit));
        new Span<byte>((void*)mask.Mapped, bordered * 4).Clear();
        var luxels = Keep(_gpu.CreateBuffer((ulong)(n[0] * n[1] * n[2] * LpvLuxelSize), BufferUsageFlags.StorageBufferBit));
        var set = Allocate(6);
        WriteBuffers(set, 158, DescriptorType.StorageBuffer, luxels);
        WriteBuffers(set, 159, DescriptorType.StorageBuffer, mask);
        WriteBuffers(set, 160, DescriptorType.StorageBuffer, _lightBins);
        WriteImages(set, 161, DescriptorType.StorageImage, [_random.View], ImageLayout.General);
        try
        {
            var rng = new Sfmt19937(LightProbeVolumeBaker.Seed);
            for (var pass = 0; pass < 32; pass++)
            {
                var (d1, d2, d3) = (rng.Next(), rng.Next(), rng.Next());
                var push = Push(pass, default, LightProbeVolumeBaker.Offset(d1, d2, d3), new vec4(bmin[0], bmin[1], bmin[2], 0f), new vec4(bmax[0], bmax[1], bmax[2], 0f), localToWorld);
                Trace(_lpvVoxelize, set, push, (uint)bn[0], (uint)bn[1], (uint)bn[2], _lpvLayout);
            }
            var words = new Span<uint>((void*)mask.Mapped, bordered);
            var occupied = new bool[bordered];
            for (var i = 0; i < bordered; i++)
                occupied[i] = (words[i] & 1) != 0;
            var solidAfterVoxelize = occupied.Count(o => o);

            // lpv_flood_fill (1800323f0), on the CPU as vrad3.
            var seedCell = new int[3];
            for (var a = 0; a < 3; a++)
            {
                var q = MathF.Floor((0f - bmin[a]) / ((bmax[a] - bmin[a]) / bn[a]));
                var i = float.IsNaN(q) || q >= 2147483648f || q < -2147483648f ? int.MinValue : (int)q;
                seedCell[a] = i < 0 ? 0 : Math.Min(i, bn[a] - 1);
            }
            var start = LightProbeVolumeBaker.NearestEmpty(occupied, bn, (seedCell[2] * bn[1] + seedCell[1]) * bn[0] + seedCell[0], 8);
            if (start < 0)
                log?.Invoke("Cannot flood fill light probe volume - no empty space near center");
            else
            {
                var filled = LightProbeVolumeBaker.FloodFill(occupied, bn, start);
                if (filled.All(o => o))
                    filled[start] = false;
                occupied = filled;
            }
            var solid = new uint[bordered];
            for (var i = 0; i < bordered; i++)
                words[i] = solid[i] = occupied[i] ? 1u : 0u;

            // lpv_compute_block_gpu over the script's blocks (at most 96 cells per axis, z outermost). Each
            // block is its own launch: g_Luxels and g_LpvBlockConverged are indexed by the launch, the cells
            // placed in the block's own box over the launch size, and only g_SolidMask reads the whole
            // bordered grid, at the packed start (low 16 bits) of the packed counts (high 16).
            var cells = n[0] * n[1] * n[2];
            var result = new LpvLuxel_t_natural[cells];
            var cell = new float[3];
            for (var a = 0; a < 3; a++)
                cell[a] = (max[a] - min[a]) / n[a];
            for (var z0 = 0; z0 < n[2]; z0 += 96)
                for (var y0 = 0; y0 < n[1]; y0 += 96)
                    for (var x0 = 0; x0 < n[0]; x0 += 96)
                    {
                        int x1 = Math.Min(x0 + 96, n[0]), y1 = Math.Min(y0 + 96, n[1]), z1 = Math.Min(z0 + 96, n[2]);
                        int[] rn = [x1 - x0, y1 - y0, z1 - z0];
                        var regionCells = rn[0] * rn[1] * rn[2];
                        var lux = new Span<byte>((void*)luxels.Mapped, regionCells * LpvLuxelSize);
                        lux.Clear();
                        for (var c = 0; c < regionCells; c++)
                            MemoryMarshal.Cast<byte, int>(lux.Slice(c * LpvLuxelSize + 176, 16)).Fill(255);
                        int cx = (rn[0] + 2) / 3, cy = (rn[1] + 2) / 3, cz = (rn[2] + 2) / 3;
                        var blockConverged = Image3D((uint)cx, (uint)cy, (uint)cz, Format.R32Uint, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit);
                        UploadImage(blockConverged, new byte[cx * cy * cz * 4], (uint)cx, (uint)cy, (uint)cz, ImageLayout.General);
                        WriteImages(set, 162, DescriptorType.StorageImage, [blockConverged.View], ImageLayout.General);
                        var regionMin = new vec4(x0 * cell[0] + min[0], y0 * cell[1] + min[1], z0 * cell[2] + min[2], 0f);
                        var regionMax = new vec4(x1 * cell[0] + min[0], y1 * cell[1] + min[1], z1 * cell[2] + min[2], 0f);
                        var packed = new uvec4((uint)n[0] << 16 | (uint)x0, (uint)n[1] << 16 | (uint)y0, (uint)n[2] << 16 | (uint)z0, 0u);
                        rng = new Sfmt19937(LightProbeVolumeBaker.Seed);
                        for (var pass = 0; pass < maxSpp; pass++)
                        {
                            if (pass % 16 == 0 && (pass == 0 || minSpp <= pass))
                            {
                                var flags = new uint[cx * cy * cz];
                                LightProbeVolumeBaker.TestConverged(Decode(lux), flags, rn, pass == 0 ? 0f : error);
                                UploadImage(blockConverged, MemoryMarshal.AsBytes(flags.AsSpan()).ToArray(), (uint)cx, (uint)cy, (uint)cz, ImageLayout.General);
                            }
                            GenerateHalton(pass);
                            var (d1, d2, d3) = (rng.Next(), rng.Next(), rng.Next());
                            Trace(_lpvCompute, set, Push(pass, packed, LightProbeVolumeBaker.Offset(d1, d2, d3), regionMin, regionMax, localToWorld),
                                  (uint)rn[0], (uint)rn[1], (uint)rn[2], _lpvLayout);
                        }
                        var block = Decode(lux);
                        for (var z = 0; z < rn[2]; z++)
                            for (var y = 0; y < rn[1]; y++)
                                for (var x = 0; x < rn[0]; x++)
                                    result[((z0 + z) * n[1] + y0 + y) * n[0] + x0 + x] = block[(z * rn[1] + y) * rn[0] + x];
                        DestroyImage(blockConverged);
                    }

            return LightProbeVolumeBaker.ReadBack(result, n, solid, solidAfterVoxelize);
        }
        finally
        {
            var s = set;
            _vk.FreeDescriptorSets(_gpu.Device, _pool, 1, &s);
        }
    }

    // LpvLuxel_t (std430, 192 bytes): six ambient cube faces, three SH vectors, the baked shadow sums,
    // their unoccluded weights and the four light ids.
    private static LpvLuxel_t_natural[] Decode(ReadOnlySpan<byte> bytes)
    {
        var f = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        var i = MemoryMarshal.Cast<byte, int>(bytes).ToArray();
        var result = new LpvLuxel_t_natural[bytes.Length / LpvLuxelSize];
        vec4 V(int at) => new(f[at], f[at + 1], f[at + 2], f[at + 3]);
        for (var c = 0; c < result.Length; c++)
        {
            var o = c * LpvLuxelSize / 4;
            var cube = new vec4[6];
            for (var k = 0; k < 6; k++)
                cube[k] = V(o + k * 4);
            var sh = new vec4[3];
            for (var k = 0; k < 3; k++)
                sh[k] = V(o + 24 + k * 4);
            result[c] = new LpvLuxel_t_natural(new _Array_natural_vector_float_4_6 { data = cube }, new _Array_natural_vector_float_4_3 { data = sh },
                V(o + 36), V(o + 40), new ivec4(i[o + 44], i[o + 45], i[o + 46], i[o + 47]));
        }
        return result;
    }

    // LpvPushConstants_t: m_nEstimate, m_nDimensionCount 4, m_nFilterRadius 1, m_nFog 0, the packed start
    // and count, the offset, the bounds and the row-major local-to-world (three rows of four, 128 bytes).
    private static byte[] Push(int pass, uvec4 packed, vec4 offset, vec4 boundsMin, vec4 boundsMax, mat4x3 l2w)
    {
        var w = new Writer(128);
        w.I(pass);
        w.I(4);
        w.I(1);
        w.I(0);
        w.U(packed.x); w.U(packed.y); w.U(packed.z); w.U(packed.w);
        w.V(offset);
        w.V(boundsMin);
        w.V(boundsMax);
        w.RowMajor(l2w);
        if (w.At != 128)
            throw new InvalidOperationException($"LpvPushConstants_t is {w.At} bytes");
        return w.Bytes;
    }
}
