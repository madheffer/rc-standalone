using System.Numerics;
using Source2.Compiler.Lighting.Bake.Glsl;
using Source2.Compiler.Lighting.Bake.Glsl.Raygen;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// One light probe volume's GPU half of vrad3's compute_lpv routine, on the
/// CPU with Valve's own ray generation shader (rtx_vrad3_lpv, both files of
/// combo 0, transpiled by tools/re/glsl_to_cs.py):
/// <list type="number">
/// <item><c>lpv_init</c> / <c>lpv_image_gpu</c> (180082340, 18002afc0): the
/// volume's grid, and a bordered one with a cell more on every side, its box
/// grown by (max - min) / cells each way.</item>
/// <item><c>lpv_voxelize_gpu</c> (180032850): 32 passes of file 0 over the
/// bordered grid. Pass i has m_nEstimate i, three draws of SFMT-19937 seeded
/// 0xea213b00 as the cell offset (x is the third, z the first, each times
/// 2^-32), the bordered box and the local-to-world matrix. A cell is solid once
/// a pass's 32 rays see more than 12.8 back faces; 18002a320 reads bit 0 back.</item>
/// <item><c>lpv_flood_fill</c> (1800323f0): the bordered cell holding the local
/// origin (floor((0 - min) / cell size), clamped); 180217b50 walks occupied
/// cells breadth first (+x, +y, +z, -x, -y, -z, Manhattan distance up to 8)
/// to the first empty one; 180217110 starts from everything occupied and
/// clears the empty cells reachable from it. The result is the solid mask.</item>
/// <item><c>lpv_compute_block_gpu</c> (180035e80): file 1 over the region,
/// passes in command lists of 16 up to maxspp, each with the pass's Halton
/// volume, three new SFMT draws (a second generator, same seed), the region's
/// start and the grid's cell counts packed 16:16, filter radius 1 and the
/// region's box. The convergence test (lightmap_test_converged, the LPV
/// combo) opens each 16-pass command list: pass 0, then each list that
/// starts at or past minspp.</item>
/// <item>The readback: each cell's six ambient cube faces (RGB, at least 0)
/// into the ambient cube image at column z X + x, row face Y + y; the baked
/// shadow slots as sum / weight with their light index.</item>
/// </list>
/// The post-process (ambient occlusion, fireflies, resolve, median) is the CPU
/// half and is separate.
/// </summary>
public sealed class LightProbeVolumeBaker(BakeScene scene, BakeHits hits, ushort[] haltonPermutations)
{
    /// <summary>A compute_lpv routine's volume: lpv_cells, lpv_box (local) and lpv_local_to_world (3 x 4, row major).</summary>
    public sealed record Volume(int CellsX, int CellsY, int CellsZ, Vector3 BoxMin, Vector3 BoxMax, float[] LocalToWorld);

    /// <summary>The GPU half's output.</summary>
    public sealed class Result
    {
        /// <summary>The ambient cube image, (X Z) by (Y 7), RGBA interleaved; the six face bands filled.</summary>
        public required float[] AmbientCube { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>Per cell (z Y + y) X + x: four light indices (255 empty) and four shadow values.</summary>
        public required byte[] ShadowIds { get; init; }
        public required float[] Shadow { get; init; }
        /// <summary>The bordered grid's solid mask after the flood fill (1 solid).</summary>
        public required uint[] SolidMask { get; init; }
        public int SolidAfterVoxelize { get; init; }
    }

    public int Threads { get; init; } = Environment.ProcessorCount;
    public Action<string>? Log { get; init; }

    private const uint Seed = 0xea213b00;
    // The draw scale vrad3 multiplies the SFMT words by (0x1802a7e88, read at 1800328aa and 1800361e2):
    // 0x2f7ffffd, three ulps under 2^-32, so a draw of 0xffffffff gives 0.9999998, never 1.
    private static readonly float DrawScale = BitConverter.UInt32BitsToSingle(0x2f7ffffd);

    public Result Bake(Volume v, int minSpp = 512, int maxSpp = 1024, float error = 0.02f)
    {
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
        var instances = RaygenHost.Instances(scene);
        var globals = new[] { RaygenHost.Globals(scene) };
        var lights = scene.Lights.Select(RaygenHost.GpuLight).ToArray();
        var bins = RaygenHost.LightBins(scene);

        // lpv_voxelize_gpu
        var bordered = bn[0] * bn[1] * bn[2];
        var mask = new uint[bordered];
        var rng = new Sfmt19937(Seed);
        for (var pass = 0; pass < 32; pass++)
        {
            var d1 = rng.Next();
            var d2 = rng.Next();
            var d3 = rng.Next();
            var push = new RtxVrad3LpvS0F0.LpvPushConstants_t_natural(pass, 4, 1, 0, default, Offset(d1, d2, d3),
                new vec4(bmin[0], bmin[1], bmin[2], 0f), new vec4(bmax[0], bmax[1], bmax[2], 0f), localToWorld);
            Parallel.For(0, bn[2] * bn[1], new ParallelOptions { MaxDegreeOfParallelism = Threads },
                () => new SolidRaygen(scene, hits, mask, instances), (row, _, r) =>
                {
                    r.g_PushConstants = push;
                    r.LaunchSize = new uvec3((uint)bn[0], (uint)bn[1], (uint)bn[2]);
                    for (var x = 0; x < bn[0]; x++)
                        r.Run(new uvec3((uint)x, (uint)(row % bn[1]), (uint)(row / bn[1])));
                    return r;
                }, _ => { });
        }
        var occupied = new bool[bordered];
        for (var i = 0; i < bordered; i++)
            occupied[i] = (mask[i] & 1) != 0;
        var solidAfterVoxelize = occupied.Count(o => o);

        // lpv_flood_fill
        var seedCell = new int[3];
        for (var a = 0; a < 3; a++)
        {
            // 1800323f0: floorf, cvttss2si (NaN or out of range gives 0x80000000), then negative -> 0, else min(n - 1).
            var q = MathF.Floor((0f - bmin[a]) / ((bmax[a] - bmin[a]) / bn[a]));
            var i = float.IsNaN(q) || q >= 2147483648f || q < -2147483648f ? int.MinValue : (int)q;
            seedCell[a] = i < 0 ? 0 : Math.Min(i, bn[a] - 1);
        }
        var seed = (seedCell[2] * bn[1] + seedCell[1]) * bn[0] + seedCell[0];
        var start = NearestEmpty(occupied, bn, seed, 8);
        if (start < 0)
            Log?.Invoke("Cannot flood fill light probe volume - no empty space near center");
        else
        {
            var filled = FloodFill(occupied, bn, start);
            if (filled.All(o => o))
                filled[start] = false;
            occupied = filled;
        }
        for (var i = 0; i < bordered; i++)
            mask[i] = occupied[i] ? 1u : 0u;

        // lpv_compute_block_gpu 0 0 0 X Y Z minspp maxspp err
        var cells = n[0] * n[1] * n[2];
        var luxels = new LpvLuxel_t_natural[cells];
        for (var i = 0; i < cells; i++)
            luxels[i] = new LpvLuxel_t_natural(new _Array_natural_vector_float_4_6 { data = new vec4[6] }, new _Array_natural_vector_float_4_3 { data = new vec4[3] },
                default, default, new ivec4(255));
        var converged = new uint[((n[0] + 2) / 3) * ((n[1] + 2) / 3) * ((n[2] + 2) / 3)];
        var cell = new float[3];
        for (var a = 0; a < 3; a++)
            cell[a] = (max[a] - min[a]) / n[a];
        var regionMin = new vec4(0f * cell[0] + min[0], 0f * cell[1] + min[1], 0f * cell[2] + min[2], 0f);
        var regionMax = new vec4(n[0] * cell[0] + min[0], n[1] * cell[1] + min[1], n[2] * cell[2] + min[2], 0f);
        var packed = new uvec4((uint)n[0] << 16, (uint)n[1] << 16, (uint)n[2] << 16, 0u);
        rng = new Sfmt19937(Seed);
        var shared = new ComputeRaygen(scene, hits, mask, luxels, converged, n, instances, globals, lights, bins);
        for (var pass = 0; pass < maxSpp; pass++)
        {
            // The test opens each command list of up to 16 passes (180036258: pass 0, or a list
            // starting at or past minspp); the passes inside a list (180036830..1800369c3) skip it.
            if (pass % 16 == 0 && (pass == 0 || minSpp <= pass))
                TestConverged(luxels, converged, n, pass == 0 ? 0f : error);
            var random = HaltonSamples.Generate(haltonPermutations, (uint)pass);
            var d1 = rng.Next();
            var d2 = rng.Next();
            var d3 = rng.Next();
            var push = new RtxVrad3LpvS0F1.LpvPushConstants_t_natural(pass, 4, 1, 0, packed, Offset(d1, d2, d3), regionMin, regionMax, localToWorld);
            Parallel.For(0, n[2] * n[1], new ParallelOptions { MaxDegreeOfParallelism = Threads },
                () => new ComputeRaygen(shared), (row, _, r) =>
                {
                    r.Pass(push, random);
                    for (var x = 0; x < n[0]; x++)
                        r.Run(new uvec3((uint)x, (uint)(row % n[1]), (uint)(row / n[1])));
                    return r;
                }, _ => { });
            if (pass % 16 == 15)
                Log?.Invoke($"lpv pass {pass + 1} of {maxSpp}");
        }

        // read back
        int w = n[0] * n[2], h = n[1] * 7;
        var image = new float[w * h * 4];
        var ids = Enumerable.Repeat((byte)255, cells * 4).ToArray();
        var shadow = new float[cells * 4];
        for (var z = 0; z < n[2]; z++)
            for (var y = 0; y < n[1]; y++)
                for (var x = 0; x < n[0]; x++)
                {
                    var c = (z * n[1] + y) * n[0] + x;
                    ref var l = ref luxels[c];
                    var column = z * n[0] + x;
                    for (var face = 0; face < 6; face++)
                    {
                        var f = l.vAmbientCube.data[face];
                        var at = ((face * n[1] + y) * w + column) * 4;
                        image[at] = f.x <= 0f ? 0f : f.x;
                        image[at + 1] = f.y <= 0f ? 0f : f.y;
                        image[at + 2] = f.z <= 0f ? 0f : f.z;
                    }
                    for (var k = 0; k < 4; k++)
                        if (0f < l.vBakedShadowUnoccluded[k])
                        {
                            shadow[c * 4 + k] = l.vBakedShadow[k] / l.vBakedShadowUnoccluded[k];
                            ids[c * 4 + k] = (byte)l.vBakedShadowLightId[k];
                        }
                }
        return new Result { AmbientCube = image, Width = w, Height = h, ShadowIds = ids, Shadow = shadow, SolidMask = mask, SolidAfterVoxelize = solidAfterVoxelize };
    }

    private static vec4 Offset(uint d1, uint d2, uint d3) => new((float)d3 * DrawScale + 0f, (float)d2 * DrawScale + 0f, (float)d1 * DrawScale + 0f, 0f);

    private static readonly (int X, int Y, int Z)[] Neighbours = [(1, 0, 0), (0, 1, 0), (0, 0, 1), (-1, 0, 0), (0, -1, 0), (0, 0, -1)];

    private static int Neighbour(int[] n, int cell, (int X, int Y, int Z) d)
    {
        int x = cell % n[0] + d.X, y = cell / n[0] % n[1] + d.Y, z = cell / n[0] / n[1] + d.Z;
        if (x < 0 || y < 0 || z < 0 || x >= n[0] || y >= n[1] || z >= n[2])
            return -1;
        return (z * n[1] + y) * n[0] + x;
    }

    /// <summary>180217b50: the cell itself when empty, else the first empty neighbour reached breadth first through occupied cells within the distance.</summary>
    internal static int NearestEmpty(bool[] occupied, int[] n, int start, int distance)
    {
        if (!occupied[start])
            return start;
        int sx = start % n[0], sy = start / n[0] % n[1], sz = start / n[0] / n[1];
        var visited = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var d in Neighbours)
            {
                var next = Neighbour(n, cell, d);
                if (next < 0)
                    continue;
                int x = next % n[0], y = next / n[0] % n[1], z = next / n[0] / n[1];
                if (Math.Abs(z - sz) + Math.Abs(y - sy) + Math.Abs(x - sx) > distance || !visited.Add(next))
                    continue;
                if (!occupied[next])
                    return next;
                queue.Enqueue(next);
            }
        }
        return -1;
    }

    /// <summary>180217110: every cell occupied except the empty ones reachable from the start.</summary>
    internal static bool[] FloodFill(bool[] occupied, int[] n, int start)
    {
        var result = Enumerable.Repeat(true, occupied.Length).ToArray();
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var d in Neighbours)
            {
                var next = Neighbour(n, cell, d);
                if (next < 0 || occupied[next] || !result[next])
                    continue;
                result[next] = false;
                queue.Enqueue(next);
            }
        }
        return result;
    }

    /// <summary>
    /// lightmap_test_converged's LPV combo: 3 x 3 x 3 cells per group, one
    /// group a wave. A tolerance of 0 or less writes 0. A group with no cell
    /// whose first face has a sample is converged, one with two or fewer is
    /// not; otherwise each face's luminance (0.2125, 0.7154, 0.0721) over the
    /// group must have a standard deviation within mean times the tolerance.
    /// Lanes past the region's width alias the next row, as the shader's index
    /// does; past the end they read zeros.
    /// </summary>
    internal static void TestConverged(LpvLuxel_t_natural[] luxels, uint[] converged, int[] n, float tolerance)
    {
        int gx = (n[0] + 2) / 3, gy = (n[1] + 2) / 3, gz = (n[2] + 2) / 3;
        Span<float> lum = stackalloc float[27];
        Span<bool> valid = stackalloc bool[27];
        Span<int> index = stackalloc int[27];
        for (var bz = 0; bz < gz; bz++)
            for (var by = 0; by < gy; by++)
                for (var bx = 0; bx < gx; bx++)
                {
                    var result = false;
                    if (tolerance > 0f)
                    {
                        var count = 0;
                        for (var k = 0; k < 27; k++)
                        {
                            int x = bx * 3 + k % 3, y = by * 3 + k / 3 % 3, z = bz * 3 + k / 9;
                            index[k] = (z * n[0] * n[1]) + y * n[0] + x;
                            valid[k] = index[k] < luxels.Length && luxels[index[k]].vAmbientCube.data[0].w > 0f;
                            if (valid[k])
                                count++;
                        }
                        if (count == 0)
                            result = true;
                        else if (count > 2)
                        {
                            result = true;
                            for (var face = 0; face < 6; face++)
                            {
                                float sum = 0f, sum2 = 0f;
                                for (var k = 0; k < 27; k++)
                                {
                                    var c = valid[k] ? luxels[index[k]].vAmbientCube.data[face] : default;
                                    lum[k] = c.x * 0.2125f + c.y * 0.7154f + c.z * 0.0721f;
                                    sum += lum[k];
                                    sum2 += lum[k] * lum[k];
                                }
                                var mean = sum / count;
                                if (MathF.Sqrt(MathF.Abs(sum2 / count - mean * mean)) > mean * tolerance)
                                    result = false;
                            }
                        }
                    }
                    converged[(bz * gy + by) * gx + bx] = result ? 1u : 0u;
                }
    }

    /// <summary>File 0's host: the solid mask, nothing else bound.</summary>
    private sealed class SolidRaygen : RtxVrad3LpvS0F0
    {
        private readonly BakeScene _scene;
        private readonly BakeHits _hits;

        public SolidRaygen(BakeScene scene, BakeHits hits, uint[] mask, PackedRtxInstanceData_t_natural[] instances)
        {
            _scene = scene;
            _hits = hits;
            g_SolidMask._member0 = mask;
            g_instanceBuffer._member0 = instances;
        }

        public void Run(uvec3 id)
        {
            LaunchID = id;
            Main();
        }

        protected override void TraceRay(uint flags, uint mask, uint sbtOffset, uint sbtStride, uint missIndex, vec3 origin, float tMin, vec3 direction, float tMax, int payload) =>
            RaygenHost.Trace(_scene, _hits, ref p, ref p_1, flags, mask, origin, tMin, direction, tMax, payload);

        protected override vec4 ImageLoadRandom(ivec3 c) => default;
        protected override uvec4 ImageLoadConverged(ivec3 c) => default;
        protected override uvec4 TexelFetchChartIDs(ivec2 c) => default;
        protected override vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod) => new(1f);
        protected override vec4 TextureLodCube(int texture, vec3 direction, float lod) => RaygenHost.IblCube(_scene, direction);
    }

    /// <summary>File 1's host: the luxels, the solid mask, the converged image and the scene's lights.</summary>
    private sealed class ComputeRaygen : RtxVrad3LpvS0F1
    {
        private readonly BakeScene _scene;
        private readonly BakeHits _hits;
        private readonly uint[] _converged;
        private readonly int[] _n;
        private float[] _random = [];

        public ComputeRaygen(BakeScene scene, BakeHits hits, uint[] mask, LpvLuxel_t_natural[] luxels, uint[] converged, int[] n,
            PackedRtxInstanceData_t_natural[] instances, SceneGlobalInfoGPU_t_natural[] globals, PathTraceBarnLightGPU_t_natural[] lights, uint[] bins)
        {
            _scene = scene;
            _hits = hits;
            _converged = converged;
            _n = n;
            g_Luxels._member0 = luxels;
            g_SolidMask_1._member0 = mask;
            g_instanceBuffer_1._member0 = instances;
            g_SceneGlobalInfo._member0 = globals;
            g_Lights_1._member0 = lights;
            g_LightBins_1._member0 = bins;
            SubsurfaceProfilesConstantBuffer_t = RaygenHost.NoSubsurface();
            RandomSamplesSize = new ivec3(HaltonSamples.Size, HaltonSamples.Size, HaltonSamples.Layers);
            LaunchSize = new uvec3((uint)n[0], (uint)n[1], (uint)n[2]);
        }

        public ComputeRaygen(ComputeRaygen other) : this(other._scene, other._hits, other.g_SolidMask_1._member0, other.g_Luxels._member0, other._converged, other._n,
            other.g_instanceBuffer_1._member0, other.g_SceneGlobalInfo._member0, other.g_Lights_1._member0, other.g_LightBins_1._member0)
        {
        }

        public void Pass(in LpvPushConstants_t_natural push, float[] random)
        {
            g_PushConstants = push;
            _random = random;
        }

        public void Run(uvec3 id)
        {
            LaunchID = id;
            Main();
        }

        protected override void TraceRay(uint flags, uint mask, uint sbtOffset, uint sbtStride, uint missIndex, vec3 origin, float tMin, vec3 direction, float tMax, int payload) =>
            RaygenHost.Trace(_scene, _hits, ref p, ref p_1, flags, mask, origin, tMin, direction, tMax, payload);

        protected override vec4 ImageLoadRandom(ivec3 c) => RaygenHost.Random(_random, c);

        protected override uvec4 ImageLoadConverged(ivec3 c)
        {
            int gx = (_n[0] + 2) / 3, gy = (_n[1] + 2) / 3, gz = (_n[2] + 2) / 3;
            if (c.x < 0 || c.y < 0 || c.z < 0 || c.x >= gx || c.y >= gy || c.z >= gz)
                return default;
            return new uvec4(_converged[(c.z * gy + c.y) * gx + c.x], 0u, 0u, 0u);
        }

        protected override uvec4 TexelFetchChartIDs(ivec2 c) => default;
        protected override vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod) => new(1f);
        protected override vec4 TextureLodCube(int texture, vec3 direction, float lod) => RaygenHost.IblCube(_scene, direction);
    }
}
