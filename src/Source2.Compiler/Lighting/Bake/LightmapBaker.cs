using System.Buffers.Binary;
using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// vrad3's lightmap stage on the CPU (lightmap_image_gpu,
/// lightmap_load_block_gpu, lightmap_compute_block_gpu, then the post-process
/// script), from the same inputs:
/// <list type="bullet">
/// <item>per block, the chart ids from lightmap_info.dat; per pass p of
/// <c>passes</c>: on p mod 4 = 0 new samples (<see cref="Sample"/>) and their
/// validation (<see cref="Validate"/>), then one path-traced estimate per
/// luxel with that pass's Halton volume (frame p);</item>
/// <item>the readback (18003010 tail): luxels with samples get the
/// irradiance, SH and quadratic channels, the sample count, the valid bit,
/// AO, the normal and tangent with their spreads, and the baked shadow
/// slots (sum over the unoccluded sum, the light id);</item>
/// <item>the post-process as resourcecompiler's script runs it.</item>
/// </list>
/// Small charts take an area-weighted point on the chart's triangles inside
/// the luxel, large charts the jittered pixel centre's triangle (the nearest
/// TEA depth), as lightmap_generate_small_chart_samples and
/// lightmap_rasterize do (hand ports over BlockGeometry's 8 x 8 tile clip).
/// With the Valve ray generation (the default) the samples then go through
/// vrad3's own validation shader (<see cref="SampleValidationRaygen"/>); the
/// hand-ported Validate is kept for the hand-port path. On ze_hold_em_p's
/// block 5 the luxel normals then equal Valve's raw image bit for bit.
/// </summary>
public sealed class LightmapBaker(BakeScene scene, PackingGeometry packing, ushort[] haltonPermutations)
{
    private readonly BakeHits _hits = new(scene);

    public int Width { get; init; } = 4096;
    public int Height { get; init; } = 4096;
    public int BlockSize { get; init; } = 1024;
    public int Passes { get; init; } = 16;
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// The path tracer: vrad3's own ray generation shader transpiled (Vrad3Raygen, the default), or the
    /// hand port (PathTracer) when false.
    /// </summary>
    public bool ValveRaygen { get; init; } = Environment.GetEnvironmentVariable("S2C_HANDPORT") != "1";

    /// <summary>Runs Valve's ray generation shaders on a device (the GPU) in place of their transpiled C#; implies <see cref="ValveRaygen"/>.</summary>
    public ILightmapDevice? Device { get; init; }

    /// <summary>The material hit groups (RtxScene); without them every material uses the default hit group.</summary>
    public RtxScene? Rtx { init => _hits.Rtx = value; }

    /// <summary>The hits' emission scale (<see cref="BakeHits.Emission"/>).</summary>
    public float Emission { init => _hits.Emission = value; }

    /// <summary>A check run: only this block, into an image of the block's size (before the post-process).</summary>
    public int? CropBlock { get; init; }

    /// <summary>A check run: trace only luxels whose x and y are multiples of this (others are left invalid).</summary>
    public int Stride { get; init; } = 1;

    /// <summary>A check: one block's first sampling pass (validated), with its chart ids.</summary>
    public (LightmapSample[] Samples, uint[] Ids) DebugSamples(byte[] lightmapInfo, int block)
    {
        var bx = (Width + BlockSize - 1) / BlockSize;
        var ox = block % bx * BlockSize;
        var oy = block / bx * BlockSize;
        var w = Math.Min(BlockSize, Width - ox);
        var h = Math.Min(BlockSize, Height - oy);
        var ids = new uint[w * h];
        var at = ((long)(block % bx) * BlockSize + (long)(block / bx) * Width) * BlockSize * 4;
        for (var i = 0; i < ids.Length; i++)
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(lightmapInfo.AsSpan((int)(at + (long)i * 4)));
        var samples = Sample(ox, oy, w, h, ids, BlockGeometry.Build(packing, Width, Height, ox, oy, w, h), new Sfmt19937(0xdee533d5));
        Validate(samples);
        return (samples, ids);
    }

    /// <summary>The image after the block computes (before the post-process).</summary>
    public LightmapImage Compute(byte[] lightmapInfo, Action<string>? log = null)
    {
        var image = CropBlock is null ? new LightmapImage(Width, Height) : new LightmapImage(BlockSize, BlockSize);
        uint[] masks = [7, 15, 15, 15, 15, 15, 15, 7, 1, 7, 1, 15, 1];
        for (var c = 0; c < masks.Length; c++)
            image.AddChannel(c, masks[c]);
        // Ambient occlusion starts at 1 (vrad3's raw image holds 1 on every luxel no chart covers).
        var ao = image.Channels[LightmapImage.Ch.AmbientOcclusion]!.Data;
        for (var p = 0; p < image.Width * image.Height; p++)
            ao[p * 4] = 1f;
        // The records start as vrad3's do (measured on its raw image): chart id 0xffffffff, the
        // sample count word over 0.5f and the flags word over 1.0f (the count and flags only take
        // their low bits), the shadow slots' light ids 0xff and their four values 1.0f.
        for (var p = 0; p < image.Width * image.Height; p++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12), 0xffffffff);
            BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12 + 4), 0x3f000000);
            BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12 + 8), 0x3f800000);
            image.ShadowRecords.AsSpan(p * 20, 4).Fill(0xff);
            for (var k = 0; k < 4; k++)
                BinaryPrimitives.WriteSingleLittleEndian(image.ShadowRecords.AsSpan(p * 20 + 4 + k * 4), 1f);
        }
        var bx = (Width + BlockSize - 1) / BlockSize;
        var by = (Height + BlockSize - 1) / BlockSize;
        var tracer = new PathTracer(scene, _hits);
        var prototype = ValveRaygen ? new Vrad3Raygen(scene, _hits) : null;
        for (var block = 0; block < bx * by; block++)
        {
            if (CropBlock is { } only && block != only)
                continue;
            var ox = block % bx * BlockSize;
            var oy = block / bx * BlockSize;
            var w = Math.Min(BlockSize, Width - ox);
            var h = Math.Min(BlockSize, Height - oy);
            var ids = new uint[w * h];
            var any = false;
            var at = ((long)(block % bx) * BlockSize + (long)(block / bx) * Width) * BlockSize * 4;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(lightmapInfo.AsSpan((int)(at + ((long)y * w + x) * 4)));
                    ids[y * w + x] = id;
                    var luxel = CropBlock is null ? (oy + y) * Width + ox + x : y * image.Width + x;
                    BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(luxel * 12), id);
                    if (id != 0xffffffff)
                    {
                        any = true;
                        var f = packing.Charts[id].Flags;
                        image.LuxelRecords[luxel * 12 + 8] = (byte)(1 | (f >> 28 & 1) << 2 | (f >> 29 & 1) << 1);
                    }
                }
            if (!any)
                continue;
            log?.Invoke($"block {block}: {ids.Count(i => i != 0xffffffff)} luxels");
            var luxels = new Luxel[w * h];
            var samples = new LightmapSample[w * h];
            var geometry = BlockGeometry.Build(packing, Width, Height, ox, oy, w, h);
            log?.Invoke($"block {block}: {geometry.SmallStart} large-chart and {geometry.TriangleCount - geometry.SmallStart} clipped small-chart triangles");
            var sfmt = new Sfmt19937(0xdee533d5);
            var gpuLuxels = new Glsl.Raygen.Luxel_t_natural[w * h];
            var gpuSamples = Array.Empty<Glsl.Raygen.LightmapSample_t_natural>();
            Device?.BeginBlock(w, h, ids);
            Glsl.Raygen.LightmapSample_t_natural[]? debugSamples = null;
            Vrad3Raygen? debugRaygen = null;
            for (var pass = 0; pass < Passes; pass++)
            {
                var (jx, jy) = (0f, 0f);
                var (rngX, rngY) = (0u, 0u);
                if (pass % 4 == 0)
                {
                    (samples, rngX, rngY, jx, jy) = SampleWithJitter(ox, oy, w, h, ids, geometry, sfmt);
                    if (!ValveRaygen && Device == null)
                        Validate(samples);
                }
                var random = HaltonSamples.Generate(haltonPermutations, (uint)pass);
                var p = pass;
                if (Device != null)
                {
                    if (pass % 4 == 0)
                    {
                        Glsl.Raygen.LightmapSample_t_natural[] cpu = [.. samples.Select(s => Vrad3Raygen.Sample(s))];
                        // The small charts' luxels from the device's compute pass (or the CPU's), the large charts' from its raster.
                        if (!Device.SmallChartSamples(geometry, packing, ox, oy, Width, Height, rngX, rngY))
                            Device.WriteSamples(cpu, i => ids[i] != 0xffffffff && (packing.Charts[(int)ids[i]].Flags & 0x30000000) != 0);
                        if (!Device.RasterSamples(geometry, packing, ox, oy, Width, Height, jx, jy))
                            Device.WriteSamples(cpu, _ => true);
                        Device.ValidateSamples();
                        if (Vrad3Raygen.Debug != null)
                            debugSamples = Device.ReadSamples();
                    }
                    if (Vrad3Raygen.Debug != null && debugSamples != null)
                    {
                        // The debug luxel on the CPU too, from the device's own samples: its rays, logged.
                        debugRaygen ??= new Vrad3Raygen(prototype!);
                        debugRaygen.Block(w, h, ids, gpuLuxels);
                        debugRaygen.Samples(debugSamples);
                        debugRaygen.Pass(p, 4, random, new Vector2(ox, oy), new Vector2(Width, Height));
                        debugRaygen.Run(Vrad3Raygen.DebugAt.X, Vrad3Raygen.DebugAt.Y);
                    }
                    Device.Pass(p, 4, random, new Vector2(ox, oy), new Vector2(Width, Height));
                    log?.Invoke($"block {block}: pass {pass} on the device");
                    continue;
                }
                if (ValveRaygen)
                {
                    if (pass % 4 == 0)
                    {
                        // Valve's own sample validation, in place on the GPU samples.
                        gpuSamples = [.. samples.Select(s => Vrad3Raygen.Sample(s))];
                        SampleValidationRaygen.Run(scene, _hits, gpuSamples, w, h, Threads);
                    }
                    using var raygens = new ThreadLocal<Vrad3Raygen>(() =>
                    {
                        var r = new Vrad3Raygen(prototype!);
                        r.Block(w, h, ids, gpuLuxels);
                        r.Samples(gpuSamples);
                        r.Pass(p, 4, random, new Vector2(ox, oy), new Vector2(Width, Height));
                        return r;
                    });
                    Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Threads }, y =>
                    {
                        if (y % Stride != 0)
                            return;
                        var r = raygens.Value!;
                        for (var x = 0; x < w; x += Stride)
                            if (ids[y * w + x] != 0xffffffff)
                                r.Run(x, y);
                    });
                    continue;
                }
                Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Threads }, y =>
                {
                    if (y % Stride != 0)
                        return;
                    for (var x = 0; x < w; x += Stride)
                        if (ids[y * w + x] != 0xffffffff)
                            tracer.Luxel(x, y, w, h, ids, samples, random, p, new Vector2(ox, oy), new Vector2(Width, Height), ref luxels[y * w + x]);
                });
            }
            if (Device != null)
                gpuLuxels = Device.EndBlock();
            if (ValveRaygen || Device != null)
                for (var i = 0; i < luxels.Length; i++)
                    luxels[i] = ids[i] == 0xffffffff ? Bake.Luxel.Empty() : Vrad3Raygen.ToLuxel(gpuLuxels[i]);
            if (CropBlock is null)
                Readback(image, luxels, ox, oy, w, h);
            else
                Readback(image, luxels, 0, 0, w, h);
        }
        return image;
    }

    // The samples of one block for one sampling pass: lightmap_generate_small_chart_samples, then lightmap_rasterize over it.
    private LightmapSample[] Sample(int ox, int oy, int w, int h, uint[] ids, BlockGeometry g, Sfmt19937 sfmt) => SampleWithJitter(ox, oy, w, h, ids, g, sfmt).Samples;

    // The same, with the small-chart pass's two SFMT words and the raster pass's jitter (in atlas uv).
    private (LightmapSample[] Samples, uint RngX, uint RngY, float Jx, float Jy) SampleWithJitter(int ox, int oy, int w, int h, uint[] ids, BlockGeometry g, Sfmt19937 sfmt)
    {
        var samples = new LightmapSample[w * h];
        var rngX = RenderInt(sfmt.Next());
        var rngY = RenderInt(sfmt.Next());
        SmallCharts(ox, oy, w, h, ids, g, rngX, rngY, samples);
        var jy = (sfmt.Next() * 2.328306e-10f - 0.5f) / Height;
        var jx = (sfmt.Next() * 2.328306e-10f - 0.5f) / Width;
        Rasterize(ox, oy, w, h, ids, g, jx, jy, samples);
        return (samples, rngX, rngY, jx, jy);
    }

    /// <summary>
    /// An int render attribute as rendersystem writes it into a constant buffer: clamped to the variable
    /// description's default range [-999999999, 999999999] (rendersystemvulkan 180254611; the VCS files
    /// declare +-1e9). g_nRNGOffsetX / Y are raw SFMT words, so about half of them clamp
    /// (capture_smallcharts.py: cs_script_demo's second generation got 999999999, -999999999).
    /// </summary>
    public static uint RenderInt(uint word) => (uint)Math.Clamp((int)word, -999999999, 999999999);

    private uint ChartInfo(uint chart) => packing.Charts[(int)chart].Flags & 0x0fffffff;

    /// <summary>
    /// lightmap_generate_small_chart_samples, one 8 x 8 thread group per tile:
    /// 16 rounds in which each thread picks one of the tile's triangles by area
    /// (a reservoir on its second TEA number), a uniform point on it (the sqrt
    /// mapping), and shares it with its luxel (uv x size, truncated); then
    /// each thread keeps one of the shared samples landing in its luxel,
    /// uniformly over all rounds (a reservoir of weight 1).
    /// </summary>
    private void SmallCharts(int ox, int oy, int w, int h, uint[] ids, BlockGeometry g, uint rngX, uint rngY, LightmapSample[] samples)
    {
        var tilesX = w / 8;
        var tilesY = (h + 7) / 8;
        Parallel.For(0, tilesX * tilesY, new ParallelOptions { MaxDegreeOfParallelism = Threads }, tile =>
        {
            var start = g.TileStart[tile];
            var end = g.TileStart[tile + 1];
            if (start >= end)
                return;
            var gx = tile % tilesX * 8;
            var gy = tile / tilesX * 8;
            var state = new (uint X, uint Y)[64];
            var count = new float[64];
            var kept = new LightmapSample[64];
            var shared = new LightmapSample[64];
            var at = new (int X, int Y)[64];
            for (var i = 0; i < 64; i++)
                state[i] = ((uint)(gx + i % 8) + rngX, (uint)(gy + i / 8) + rngY);
            for (var round = 0; round < 16; round++)
            {
                for (var i = 0; i < 64; i++)
                {
                    var s = state[i];
                    var u = Tea(ref s);
                    var v = Tea(ref s);
                    state[i] = s;
                    var chosen = -1;
                    var total = 0f;
                    for (var t = start; t < end; t++)
                    {
                        var sum = total + g.Areas[t];
                        total = sum;
                        if (sum == 0f)
                            continue;
                        if (LightEval.Coin(g.Areas[t] / sum, ref v))
                            chosen = t;
                    }
                    if (chosen < 0)
                        continue;
                    var r = MathF.Sqrt(u);
                    var b1 = 1f - r;
                    var b2 = v * r;
                    var b0 = r - b2;
                    shared[i] = Point(g, chosen, b0, b1, b2);
                    at[i] = ((int)(shared[i].LightmapUv.X * Width), (int)(shared[i].LightmapUv.Y * Height));
                }
                for (var i = 0; i < 64; i++)
                {
                    var s = state[i];
                    var x = Tea(ref s);
                    _ = Tea(ref s);
                    state[i] = s;
                    var px = ox + gx + i % 8;
                    var py = oy + gy + i / 8;
                    for (var j = 0; j < 64; j++)
                    {
                        if (at[j].X != px || at[j].Y != py || shared[j].Position.W == 0f)
                            continue;
                        count[i] += 1f;
                        if (LightEval.Coin(1f / count[i], ref x))
                            kept[i] = shared[j];
                    }
                }
            }
            for (var i = 0; i < 64; i++)
            {
                var lx = gx + i % 8;
                var ly = gy + i / 8;
                if (lx >= w || ly >= h)
                    continue;
                var id = ids[ly * w + lx];
                ref var k = ref kept[i];
                if (k.Position.W == 0f || id == 0xffffffff)
                    continue;
                var n = Vector3.Normalize(LightEval.V3(k.InterpolatedNormal));
                var t = Vector3.Normalize(LightEval.V3(k.Tangent));
                samples[ly * w + lx] = new LightmapSample
                {
                    Position = k.Position,
                    ShadingNormal = new Vector4(n, 0f),
                    InterpolatedNormal = new Vector4(n, 0f),
                    FaceNormal = k.FaceNormal,
                    Tangent = new Vector4(t, MathF.Sign(k.Tangent.W)),
                    LightmapUv = k.LightmapUv,
                    SampleFlags = ChartInfo(id),
                };
            }
        });
    }

    // A point on a block triangle: the corners' frames decoded as the shaders do, every field lerped.
    private static LightmapSample Point(BlockGeometry g, int t, float b0, float b1, float b2)
    {
        var v0 = g.Vertices[t * 3];
        var v1 = g.Vertices[t * 3 + 1];
        var v2 = g.Vertices[t * 3 + 2];
        var (n0, t0) = PackedFrame.UnpackGpu(v0.Frame);
        var (n1, t1) = PackedFrame.UnpackGpu(v1.Frame);
        var (n2, t2) = PackedFrame.UnpackGpu(v2.Frame);
        return new LightmapSample
        {
            Position = new Vector4(v0.Position * b0 + v1.Position * b1 + v2.Position * b2, 1f),
            InterpolatedNormal = new Vector4(n0 * b0 + n1 * b1 + n2 * b2, 0f),
            FaceNormal = new Vector4(Vector3.Normalize(Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position)), 0f),
            Tangent = t0 * b0 + t1 * b1 + t2 * b2,
            LightmapUv = v0.Uv * b0 + v1.Uv * b1 + v2.Uv * b2,
        };
    }

    /// <summary>
    /// lightmap_rasterize over the block's whole vertex buffer: the window
    /// jittered by up to half a luxel, the depth TEA(primitive id), pixels of
    /// another chart discarded, the nearest triangle's sample kept.
    /// </summary>
    private void Rasterize(int ox, int oy, int w, int h, uint[] ids, BlockGeometry g, float jx, float jy, LightmapSample[] samples)
    {
        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        var owner = new int[w * h];
        Array.Fill(owner, -1);
        var bary = new Vector3[w * h];
        for (var t = 0; t < g.TriangleCount; t++)
        {
            var chart = g.Charts[t];
            var a = g.Vertices[t * 3].Uv;
            var b = g.Vertices[t * 3 + 1].Uv;
            var c = g.Vertices[t * 3 + 2].Uv;
            var s0 = new Vector2((a.X - jx) * Width - ox, (a.Y - jy) * Height - oy);
            var s1 = new Vector2((b.X - jx) * Width - ox, (b.Y - jy) * Height - oy);
            var s2 = new Vector2((c.X - jx) * Width - ox, (c.Y - jy) * Height - oy);
            var minX = (int)MathF.Max(0, MathF.Floor(MathF.Min(s0.X, MathF.Min(s1.X, s2.X))));
            var maxX = (int)MathF.Min(w - 1, MathF.Ceiling(MathF.Max(s0.X, MathF.Max(s1.X, s2.X))));
            var minY = (int)MathF.Max(0, MathF.Floor(MathF.Min(s0.Y, MathF.Min(s1.Y, s2.Y))));
            var maxY = (int)MathF.Min(h - 1, MathF.Ceiling(MathF.Max(s0.Y, MathF.Max(s1.Y, s2.Y))));
            if (minX > maxX || minY > maxY)
                continue;
            var area = Edge(s0, s1, s2);
            if (area == 0f)
                continue;
            var d = TeaDepth((uint)t);
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    if (ids[y * w + x] != chart || !(d < depth[y * w + x]))
                        continue;
                    var pc = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Edge(s1, s2, pc) / area;
                    var w1 = Edge(s2, s0, pc) / area;
                    var w2 = Edge(s0, s1, pc) / area;
                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                        continue;
                    depth[y * w + x] = d;
                    owner[y * w + x] = t;
                    bary[y * w + x] = new Vector3(w0, w1, w2);
                }
        }
        for (var p = 0; p < w * h; p++)
        {
            var t = owner[p];
            if (t < 0)
                continue;
            var k = Point(g, t, bary[p].X, bary[p].Y, bary[p].Z);
            var n = new Vector4(Vector3.Normalize(LightEval.V3(k.InterpolatedNormal)), 0f);
            samples[p] = new LightmapSample
            {
                Position = k.Position,
                ShadingNormal = n,
                InterpolatedNormal = n,
                FaceNormal = k.FaceNormal,
                Tangent = new Vector4(Vector3.Normalize(LightEval.V3(k.Tangent)), k.Tangent.W),
                LightmapUv = k.LightmapUv,
                SampleFlags = ChartInfo(g.Charts[t]),
            };
        }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    // lightmap_generate_small_chart_samples' TEA, six rounds; a float in [0, 1) from the low 20 bits.
    private static float Tea(ref (uint X, uint Y) s)
    {
        var (x, y) = (s.X, s.Y);
        var sum = 0u;
        for (var i = 0; i < 6; i++)
        {
            sum += 0x9e3779b9;
            var nx = x + ((((y << 4) + 2738958700u) ^ (y + sum)) ^ ((y >> 5) + 3355524772u));
            var ny = y + ((((x << 4) + 2911926141u) ^ (x + sum)) ^ ((x >> 5) + 2123724318u));
            (x, y) = (nx, ny);
        }
        s = (x, y);
        return (x & 0xfffff) * 9.536743e-07f;
    }

    // lightmap_rasterize's geometry shader: the primitive's depth from TEA(primitive id, 0).
    private static float TeaDepth(uint primitive)
    {
        var s = (primitive, 0u);
        return Tea(ref s);
    }

    /// <summary>rtx_vrad3_lightmap_sample_validation: samples inside geometry move out, or are dropped.</summary>
    private void Validate(LightmapSample[] samples)
    {
        Parallel.For(0, samples.Length, new ParallelOptions { MaxDegreeOfParallelism = Threads }, i =>
        {
            if (samples[i].Position.W == 0f)
                return;
            samples[i] = ValidateOne(samples[i]);
        });
    }

    private Payload Trace(Vector3 o, Vector3 d, float tMin, float tMax, uint flags, uint mask, float rng)
    {
        var p = Payload.Initial(rng);
        var r = rng;
        var hit = scene.Rays.Trace(o, d, tMin, tMax, flags, mask, (tri, t, u, v, front) => BakeHits.StochasticOpacity(_hits.Opacity(tri, u, v), ref r));
        p.OpacityRng = r;
        if (hit.Found)
            _hits.Shade(ref p, hit);
        return p;
    }

    private float Visibility(Vector3 o, Vector3 d, float tMax, uint mask)
    {
        if (!(tMax > 0f))
            return 1f;
        var vis = 1f;
        var hit = scene.Rays.Trace(o, d, 0f, tMax, RayScene.TerminateOnFirstHit, mask, (tri, t, u, v, front) =>
        {
            vis *= 1f - Math.Clamp(_hits.Opacity(tri, u, v), 0f, 1f);
            return vis <= 0f;
        });
        return hit.Found ? 0f : vis;
    }

    private LightmapSample ValidateOne(LightmapSample s)
    {
        var face = LightEval.V3(s.FaceNormal);
        var (t1, t2) = LightEval.Basis(face);
        var valid = s.Position.W != 0f;
        var reach = valid ? 12f : 0f;
        var o = s;
        var step = face * 0.0078125f;
        // Two probes along the face normal, from just below and just above.
        foreach (var (start, dir) in new[] { (LightEval.V3(s.Position) - step, face), (LightEval.V3(s.Position) + step, -face) })
        {
            var p = Trace(start, dir, 0f, reach, 0, 1, 0f);
            if (p.Instance == -1)
                continue;
            var hitFace = p.Flipped ? -p.FaceNormal : p.FaceNormal;
            if (!(Vector3.Dot(face, hitFace) > 0f))
                continue;
            var sign = p.Flipped ? -1f : 1f;
            var moved = (start + dir * p.HitT) + p.FaceNormal * ((Vector3.Abs(start) + new Vector3(MathF.Abs(p.HitT))) * 9.9999997e-05f);
            var dist = (moved - LightEval.V3(s.Position)).Length();
            if (!(dist < reach))
                continue;
            var inst = scene.Instances[p.Instance];
            var transmissive = Vector3.Zero;
            o.Position = new Vector4(moved, o.Position.W);
            o.ShadingNormal = new Vector4(p.Normal * sign, transmissive.X);
            o.InterpolatedNormal = new Vector4(p.InterpolatedNormal * sign, transmissive.Y);
            o.FaceNormal = new Vector4(p.FaceNormal * sign, transmissive.Z);
            o.InstanceFlags = (int)(inst.Packed & 0xffff);
            reach = dist;
        }
        // A short step out along the (new) face normal.
        {
            var pos = LightEval.V3(o.Position);
            var fn = LightEval.V3(o.FaceNormal);
            var p = Trace(pos, fn, 0f, 1f, 0, 1, 0f);
            if (p.Instance != -1)
                o.Position = new Vector4((pos + fn * p.HitT) + fn * ((Vector3.Abs(pos) + new Vector3(MathF.Abs(p.HitT))) * 9.9999997e-05f), o.Position.W);
        }
        // Eight hemisphere rays (Hammersley about the original face normal): more than two back faces seen invalidate the sample.
        var backfaces = 0;
        var position = LightEval.V3(o.Position);
        var faceNow = LightEval.V3(o.FaceNormal);
        for (var i = 0; i < 8 && valid; i++)
        {
            if (backfaces + (8 - i) <= 2)
                break;
            var u = new Vector2(i * 0.125f, ReverseBits((uint)i) * 2.3283064e-10f);
            var dir = LightEval.ToWorld(t1, t2, faceNow, LightEval.CosineSample(u));
            var p = Trace(position, dir, 0f, 56755.84f, 0, 16, 0f);
            var rng = p.OpacityRng;
            if (p.Instance == -1)
                break;
            var hitPos = (position + dir * p.HitT) + p.FaceNormal * ((Vector3.Abs(position) + new Vector3(MathF.Abs(p.HitT))) * 9.9999997e-05f);
            var hitNormal = p.FaceNormal;
            if (p.SmoothPosition.X != float.MaxValue)
            {
                var to = p.SmoothPosition - hitPos;
                var len = to.Length();
                if (Visibility(hitPos, to / len, len, 255) == 1f)
                    hitNormal = p.Normal;
            }
            var packed = scene.Instances[p.Instance].Packed;
            if (((packed >> 16) & 1) != 0 || (packed & 0x800) != 0)
                break;
            if (!(Vector3.Dot(dir, hitNormal) > 0f))
                break;
            if (((packed & 0xffff) & 6) == 0)
                break;
            var again = Trace(position, dir, p.HitT * 0.9999f, p.HitT * 1.0001f, RayScene.CullBackFacing, 16, rng);
            if (again.Instance != -1)
                break;
            backfaces++;
            if (backfaces > 2)
                valid = false;
        }
        return valid ? o : default;
    }

    private static uint ReverseBits(uint v)
    {
        v = (v << 16) | (v >> 16);
        v = ((v & 0x00ff00ff) << 8) | ((v & 0xff00ff00) >> 8);
        v = ((v & 0x0f0f0f0f) << 4) | ((v & 0xf0f0f0f0) >> 4);
        v = ((v & 0x33333333) << 2) | ((v & 0xcccccccc) >> 2);
        return ((v & 0x55555555) << 1) | ((v & 0xaaaaaaaa) >> 1);
    }

    private static void Readback(LightmapImage image, Luxel[] luxels, int ox, int oy, int w, int h)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                ref var l = ref luxels[y * w + x];
                var p = (oy + y) * image.Width + ox + x;
                if (!(0f < l.Irradiance.W))
                {
                    image.LuxelRecords[p * 12 + 8] &= 0xfe;
                    continue;
                }
                void Set(int c, Vector4 v)
                {
                    var d = image.Channels[c]!.Data;
                    (d[p * 4], d[p * 4 + 1], d[p * 4 + 2], d[p * 4 + 3]) = (v.X, v.Y, v.Z, v.W);
                }
                Set(LightmapImage.Ch.Irradiance, new Vector4(LightEval.V3(l.Irradiance), 0f));
                image.SetSampleCount(p, (uint)l.Irradiance.W);
                Set(LightmapImage.Ch.ShR, l.ShR);
                Set(LightmapImage.Ch.ShG, l.ShG);
                Set(LightmapImage.Ch.ShB, l.ShB);
                Set(LightmapImage.Ch.QuadraticR, l.QuadR);
                Set(LightmapImage.Ch.QuadraticG, l.QuadG);
                Set(LightmapImage.Ch.QuadraticB, l.QuadB);
                Set(LightmapImage.Ch.QuadraticFinal, new Vector4(LightEval.V3(l.QuadFinal), 0f));
                image.LuxelRecords[p * 12 + 8] |= 1;
                image.Channels[LightmapImage.Ch.AmbientOcclusion]!.Data[p * 4] = l.Ao.X;
                ReadOnlySpan<int> ids = [l.Id0, l.Id1, l.Id2, l.Id3];
                ReadOnlySpan<float> sums = [l.BakedShadow.X, l.BakedShadow.Y, l.BakedShadow.Z, l.BakedShadow.W];
                ReadOnlySpan<float> weights = [l.BakedShadowUnoccluded.X, l.BakedShadowUnoccluded.Y, l.BakedShadowUnoccluded.Z, l.BakedShadowUnoccluded.W];
                for (var k = 0; k < 4; k++)
                    if (0f < weights[k])
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(image.ShadowRecords.AsSpan(p * 20 + 4 + k * 4), sums[k] / weights[k]);
                        image.ShadowRecords[p * 20 + k] = (byte)ids[k];
                    }
                var n = LightEval.V3(l.Normal);
                // 180030100's readback: VectorNormalize of the normal, the tangent's length (x x + (y y + z z)).
                var (nn, nl) = PathTraceBounds.Normalize(n);
                var tv = LightEval.V3(l.Tangent);
                var tl = MathF.Sqrt(tv.X * tv.X + (tv.Y * tv.Y + tv.Z * tv.Z));
                Set(LightmapImage.Ch.Normal, new Vector4(nn, 0f));
                Set(LightmapImage.Ch.Tangent, l.Tangent);
                image.Channels[LightmapImage.Ch.NormalSpread]!.Data[p * 4] = Spread(nl);
                image.Channels[LightmapImage.Ch.TangentSpread]!.Data[p * 4] = Spread(tl);
            }
    }

    private static float Spread(float len) => len < 1f ? (len <= 0f ? float.MaxValue : MathF.Sqrt((1f - len) / len)) : 0f;

    /// <summary>
    /// The script's post-process: consolidate small charts (mip 1), fireflies
    /// (4), median (7), JFA dilation, the AHD encode, gutters (mip 1), the seam
    /// weld; then irradiance.exr, directional_irradiance.exr,
    /// direct_light_shadows.exr and lightmap_chart_color.exr (16 16).
    /// </summary>
    public static (Vrad3Exr Irradiance, Vrad3Exr Directional, Vrad3Exr Shadows, Vrad3Exr ChartColor) PostProcess(LightmapImage image, PackingGeometry packing, byte[] seams)
    {
        LightmapPost.ConsolidateSmallCharts(image, packing.Charts, 1);
        LightmapPost.RemoveFireflies(image, packing.Charts, 4f);
        LightmapPost.FilterMedian(image, packing.Charts, 7);
        LightmapPost.DilateOverInvalidJfa(image);
        LightmapAhd.Encode(image);
        LightmapPost.FillGutters(image, packing.Charts, 1);
        LightmapWeld.Weld(image, seams);
        return (ColourExr(image), LightmapPost.AhdDirectionExr(image), LightmapPost.DirectLightShadowsExr(image), LightmapPost.ChartColorExr(image, packing.Charts));
    }

    /// <summary>irradiance.exr: channel 14 as FloatBitMap_t writes it (R, G, B).</summary>
    public static Vrad3Exr ColourExr(LightmapImage image)
    {
        var exr = new Vrad3Exr(image.Width, image.Height, ["B", "G", "R"]);
        var c = image.Channels[14]!.Data;
        var r = exr.Plane("R");
        var g = exr.Plane("G");
        var b = exr.Plane("B");
        for (var p = 0; p < image.Width * image.Height; p++)
        {
            r[p] = LightmapImage.ToHalfAway(c[p * 4]);
            g[p] = LightmapImage.ToHalfAway(c[p * 4 + 1]);
            b[p] = LightmapImage.ToHalfAway(c[p * 4 + 2]);
        }
        return exr;
    }
}
