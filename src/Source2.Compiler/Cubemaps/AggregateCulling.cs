using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// How the game turns a world node aggregate into a view's indirect draws (docs/CUBEMAPS.md, "The per-view
/// culling"), float for float:
/// <list type="bullet">
/// <item>The view's frustum (client 10-15/10-21): 1817465f0 extracts six planes from the world-to-projection
/// matrix (+0x288 = projection x world-to-view, 18174f330), 18174fe70 moves them to the camera and 1817435e0
/// packs them as two SoA blocks of four (normals, d, sign masks, absolute normals); scenesystem's view holds a
/// copy at +0x430 (camera) / +0x460 (planes).</item>
/// <item>Each fragment (CAggregateSceneObject::AddFragment, scenesystem 1800380a0, called per AggregateMeshInfo_t
/// by worldrenderer 18001e920): its draw call's bounds through the fragment's transform (18049bfb0 when the
/// draw call has meshlets, as world nodes' do; worldrenderer's 1800ba270 is the same code otherwise), and a
/// transform slot shared with the previous fragment while transform, packed tint and draw field match.</item>
/// <item>The cull (scenesystem 18003c5a0 per view and aggregate, 18003d380 per fragment): fragments in order,
/// or through a BVH above sc_aggregate_bvh_threshold (128); a fragment is visible unless its camera-relative box
/// lies behind one of the eight planes.</item>
/// <item>The draws (DrawArray, scenesystem 180042330): each material's list of (fragment, visible position) in
/// visible order; a command per entry, extended over the following entries while their transform slot is the
/// same and their first index continues the running range (sc_aggregate_fragment_merging), first instance =
/// the view's instance base + the entry's visible position.</item>
/// </list>
/// Not ported yet: LOD cross-fades and forced LODs (FUN_1800450f0's other paths), the per-view PVS test (FUN_180036840), culling boxes, the
/// small-object fade (the view's +0x5724 / +0x572c) and the shadow views' own frustum setup; none of them changed a
/// draw in the three captures.
/// </summary>
public static class AggregateCulling
{
    /// <summary>sc_aggregate_bvh_threshold's default: an aggregate with more fragments gets a BVH.</summary>
    public const int BvhThreshold = 128;

    /// <summary>
    /// A view's culling frustum as scenesystem reads it: the camera and two blocks of four planes, each block 40
    /// floats (nx[4], ny[4], nz[4], d[4], mask x[4], mask y[4], mask z[4], |nx|[4], |ny|[4], |nz|[4]).
    /// </summary>
    public sealed record Frustum(Vector3 Origin, float[] Blocks);

    /// <summary>
    /// 1817465f0 on a row-major world-to-projection: planes r3 - r0, r3 + r0, r3 - r1, r3 + r1, r3 + r2 (the near
    /// plane; just r2 when <paramref name="zeroToOneDepth"/>, the view's +0x180 byte), r3 - r2 (replaced by the
    /// near plane when its normal is degenerate), each (n, -w) and normalised by 1 / sqrt((x x + y y) + z z)
    /// when that length squared is at least 1e-20. Subtractions are r3 + (-r), the same value.
    /// </summary>
    public static float[] ExtractPlanes(float[] m, bool zeroToOneDepth = false)
    {
        var p = new float[24];
        void Set(int i, float x, float y, float z, float w) => (p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3]) = (x, y, z, -w);
        Set(0, m[12] + -m[0], m[13] + -m[1], m[14] + -m[2], m[15] + -m[3]);
        Set(1, m[12] + m[0], m[13] + m[1], m[14] + m[2], m[15] + m[3]);
        Set(2, m[12] + -m[4], m[13] + -m[5], m[14] + -m[6], m[15] + -m[7]);
        Set(3, m[12] + m[4], m[13] + m[5], m[14] + m[6], m[15] + m[7]);
        if (zeroToOneDepth)
            Set(4, m[8], m[9], m[10], m[11]);
        else
            Set(4, m[12] + m[8], m[13] + m[9], m[14] + m[10], m[15] + m[11]);
        Set(5, m[12] + -m[8], m[13] + -m[9], m[14] + -m[10], m[15] + -m[11]);
        if ((p[20] * p[20] + p[21] * p[21]) + p[22] * p[22] <= 1e-20f)
            Array.Copy(p, 16, p, 20, 4);
        for (var i = 0; i < 6; i++)
        {
            var (x, y, z) = (p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            var lengthSquared = (x * x + y * y) + z * z;
            if (lengthSquared >= 1e-20f)
            {
                var inverse = 1f / MathF.Sqrt(lengthSquared);
                (p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3]) = (x * inverse, y * inverse, z * inverse, inverse * p[i * 4 + 3]);
            }
        }
        return p;
    }

    /// <summary>18174fe70 after the extraction: each plane's d moved to the camera, d + ((nz (-oz) + ny (-oy)) + nx (-ox)).</summary>
    public static void ToCamera(float[] planes, Vector3 origin)
    {
        var (ox, oy, oz) = (-origin.X, -origin.Y, -origin.Z);
        for (var i = 0; i < 6; i++)
            planes[i * 4 + 3] += (planes[i * 4 + 2] * oz + oy * planes[i * 4 + 1]) + planes[i * 4] * ox;
    }

    /// <summary>1817435e0: planes 0 to 3 as the first block, planes 4 and 5 in the second block's first two lanes (the
    /// other two all zero, which never cull). A mask lane is all ones where the normal component is below zero.</summary>
    public static float[] Pack(float[] planes)
    {
        var b = new float[80];
        static float Mask(float v) => v < 0f ? BitConverter.UInt32BitsToSingle(0xffffffff) : 0f;
        for (var block = 0; block < 2; block++)
        {
            var count = block == 0 ? 4 : 2;
            for (var lane = 0; lane < count; lane++)
            {
                var i = (block * 4 + lane) * 4;
                for (var c = 0; c < 4; c++)
                    b[block * 40 + c * 4 + lane] = planes[i + c];
                for (var c = 0; c < 3; c++)
                {
                    b[block * 40 + 16 + c * 4 + lane] = Mask(planes[i + c]);
                    b[block * 40 + 28 + c * 4 + lane] = MathF.Abs(planes[i + c]);
                }
            }
        }
        return b;
    }

    /// <summary>A face view's frustum: +0x288 = 181746490(projection, world-to-view with the row (0, 0, 0, 1)).</summary>
    public static Frustum FaceFrustum(CubemapFaceViews.View v)
    {
        var w = v.WorldToView;
        float[] worldToView4 = [w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], w[8], w[9], w[10], w[11], 0f, 0f, 0f, 1f];
        var planes = ExtractPlanes(CubemapFaceViews.Mul4(v.Projection, worldToView4));
        ToCamera(planes, v.Origin);
        return new Frustum(v.Origin, Pack(planes));
    }

    /// <summary>
    /// A sun cascade's frustum, taken the face's way: planes from the identity jitter times the cascade's orthographic
    /// projection times its light view, moved to the cascade camera (the sun's normalised direction times the view's
    /// depth offset, as the per-view block uploads it). Checked against the captures' cascade draws, not read.
    /// </summary>
    public static Frustum CascadeFrustum(CubemapShadowCascades.Cascade c, Vector3 cameraDirection, bool zeroToOneDepth = false)
    {
        float[] identity = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];
        var planes = ExtractPlanes(CubemapFaceViews.Mul4(CubemapFaceViews.Mul4(identity, c.Projection), c.View), zeroToOneDepth);
        var offset = c.View[11];
        var camera = new Vector3(cameraDirection.X * offset, cameraDirection.Y * offset, cameraDirection.Z * offset);
        ToCamera(planes, camera);
        return new Frustum(camera, Pack(planes));
    }

    /// <summary>18049c3a0 through 18049dd60: a point through a row-major 3 x 4, x' = (y m1 + m3) + (x m0 + z m2).</summary>
    public static Vector3 TransformPoint(float[] m, Vector3 p)
    {
        float Row(int r) => (p.Y * m[r * 4 + 1] + m[r * 4 + 3]) + (p.X * m[r * 4] + p.Z * m[r * 4 + 2]);
        return new Vector3(Row(0), Row(1), Row(2));
    }

    /// <summary>
    /// 18049bfb0 (= worldrenderer 1800ba270): a box through a 3 x 4 by centre and extents. c = (max + min) 0.5,
    /// e = max - c, c' = M c, e'_i = (|e_y m_i1| + |e_x m_i0|) + |e_z m_i2|; the box is c' - e' .. e' + c'.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) TransformBox(float[] m, Vector3 min, Vector3 max)
    {
        var c = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
        var e = new Vector3(max.X - c.X, max.Y - c.Y, max.Z - c.Z);
        var t = TransformPoint(m, c);
        float Extent(int r) => (MathF.Abs(e.Y * m[r * 4 + 1]) + MathF.Abs(e.X * m[r * 4])) + MathF.Abs(e.Z * m[r * 4 + 2]);
        var x = new Vector3(Extent(0), Extent(1), Extent(2));
        return (new Vector3(t.X - x.X, t.Y - x.Y, t.Z - x.Z), new Vector3(x.X + t.X, x.Y + t.Y, x.Z + t.Z));
    }

    /// <summary>worldrenderer 1800b9a60: a b for row-major 3 x 4s, each element (a2 b2j + a1 b1j) + a0 b0j + (0 or a's translation).</summary>
    public static float[] Concat(float[] a, float[] b)
    {
        var o = new float[12];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 4; c++)
                o[r * 4 + c] = a[r * 4 + 2] * b[8 + c] + a[r * 4 + 1] * b[4 + c] + a[r * 4] * b[c] + (c == 3 ? a[r * 4 + 3] : 0f);
        return o;
    }

    /// <summary>The identity 3 x 4.</summary>
    public static readonly float[] Identity = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f];

    /// <summary>One fragment as the cull and the draws read it.</summary>
    /// <param name="DrawCall">Its draw call (fragment +0x18).</param>
    /// <param name="StartIndex">The draw call's first index (its 0x120-byte record +0x5c).</param>
    /// <param name="IndexCount">The draw call's index count (+0x60).</param>
    /// <param name="BaseVertex">The draw call's vertex offset (+0x54).</param>
    /// <param name="TransformSlot">Fragment +0x20.</param>
    /// <param name="Min">Its bounds (fragment bounds +0).</param>
    /// <param name="Max">Its bounds (+0x10).</param>
    /// <param name="LodSetup">Its LOD setup (fragment +0x22, AggregateMeshInfo_t's m_nLODSetupIndex), -1 for none.</param>
    /// <param name="LodMask">The LODs it belongs to (fragment +0x24, m_nLODGroupMask).</param>
    public sealed record Fragment(int DrawCall, int StartIndex, int IndexCount, int BaseVertex, int TransformSlot, Vector3 Min, Vector3 Max,
        int LodSetup = -1, int LodMask = 0)
    {
        /// <summary>Fragment bounds +0x1c (AddFragment): sqrt((dz dz + dy dy) + dx dx) 0.5 over the box's diagonal.</summary>
        public float Radius => Half(Min, Max, zFirst: true);

        /// <summary>(max + min) 0.5 per axis, as 18003d380 hands the centre to the size metric.</summary>
        public Vector3 Centre => new((Max.X + Min.X) * 0.5f, (Max.Y + Min.Y) * 0.5f, (Max.Z + Min.Z) * 0.5f);
    }

    // Half a box's diagonal: AddFragment sums (z z + y y) + x x, the small prepass layer's aggregate (x x + y y) + z z.
    private static float Half(Vector3 min, Vector3 max, bool zFirst)
    {
        var (dx, dy, dz) = (min.X - max.X, min.Y - max.Y, min.Z - max.Z);
        var squared = zFirst ? (dz * dz + dy * dy) + dx * dx : (dx * dx + dy * dy) + dz * dz;
        return MathF.Sqrt(squared) * 0.5f;
    }

    /// <summary>A draw call of the aggregate's model, as AddFragment reads it.</summary>
    public sealed record DrawCall(int StartIndex, int IndexCount, int BaseVertex, Vector3 BoundsMin, Vector3 BoundsMax, Vector4 Tint);

    /// <summary>A fragment's on-disk record (AggregateMeshInfo_t) as far as the culling needs it.</summary>
    public sealed record MeshInfo(int DrawCall, float[]? Transform, uint TintRgb, int LodSetup, int LodMask = 0);

    /// <summary>An aggregate's LOD setup (m_lodSetups; scenesystem's 0x34-byte record: origin, scale, count, distances).</summary>
    public sealed record LodSetup(Vector3 Origin, float MaxObjectScale, float[] SwitchDistances);

    /// <summary>
    /// scenesystem 1800450f0 without cross-fading or a forced LOD: the setup's size in the LOD view (as
    /// <see cref="ScreenSize"/>, with the setup's scale for radius, 1 when the camera is within it), f = 50 / (size x
    /// the view's height in pixels), and the LOD is the highest index whose switch distance is below f (0 if none).
    /// </summary>
    public static int SelectLod(LodSetup setup, Vector3 camera, float cotHalfFov, int viewHeight)
    {
        var size = ScreenSize(camera, cotHalfFov, setup.Origin, setup.MaxObjectScale);
        var f = 50f / (size * viewHeight);
        var lod = setup.SwitchDistances.Length - 1;
        while (lod > 0 && !(setup.SwitchDistances[lod] < f))
            lod--;
        return Math.Max(lod, 0);
    }

    /// <summary>
    /// The fragments of one aggregate in AddFragment's order. The transform is the aggregate's times the fragment's
    /// (identity when it has none); the bounds are the draw call's through it. A new transform slot starts unless the
    /// current slot's transform equals this one (no element more than 1e-5 apart, 18049ad90), its packed tint is this
    /// fragment's and its draw call's vertex offset is this one's; world fragments carry no instance stream, which
    /// would also force one.
    /// </summary>
    public static List<Fragment> Fragments(IReadOnlyList<MeshInfo> meshes, IReadOnlyList<DrawCall> draws, float[]? aggregateTransform = null)
    {
        var result = new List<Fragment>();
        float[]? slotTransform = null;
        uint slotTint = 0;
        var slotBaseVertex = 0;
        var slot = -1;
        foreach (var mesh in meshes)
        {
            var transform = Concat(aggregateTransform ?? Identity, mesh.Transform ?? Identity);
            var draw = draws[mesh.DrawCall];
            var tint = PackTint(mesh.TintRgb, draw.Tint);
            if (slotTransform == null || draw.BaseVertex != slotBaseVertex || tint != slotTint || !Equal(slotTransform, transform, 1e-5f))
            {
                slot++;
                (slotTransform, slotTint, slotBaseVertex) = (transform, tint, draw.BaseVertex);
            }
            var (min, max) = TransformBox(transform, draw.BoundsMin, draw.BoundsMax);
            result.Add(new Fragment(mesh.DrawCall, draw.StartIndex, draw.IndexCount, draw.BaseVertex, slot, min, max, mesh.LodSetup, mesh.LodMask));
        }
        return result;
    }

    /// <summary>
    /// AddFragment's packed tint: the fragment's colour bytes made linear (18049ca70, as <see cref="Lighting.SceneLights.SrgbToLinear"/>)
    /// times the draw call's tint, then 18049c8d0: each colour channel clamped to [0, 1], back to sRGB
    /// (powf(c, 1 / 2.4) 1.055 - 0.055 above 0.0031308, else c 12.92), times 255 clamped to [0, 255] and rounded to
    /// nearest even; alpha is the draw call's times 255, rounded, unclamped. Bytes r, g, b, a from the low end.
    /// </summary>
    public static uint PackTint(uint rgb, Vector4 drawTint)
    {
        var linear = Lighting.SceneLights.SrgbToLinear((int)(rgb & 0xff), (int)(rgb >> 8 & 0xff), (int)(rgb >> 16 & 0xff));
        static uint Channel(float c)
        {
            c = 0f <= c ? (c <= 1f ? c : 1f) : 0f;
            c = 0.0031308f < c ? Maps.LightPow.Pow(c, BitConverter.UInt32BitsToSingle(0x3ed55555)) * 1.055f - 0.055f : c * 12.92f;
            c *= 255f;
            if (c <= 0f)
                c = 0f;
            if (255f <= c)
                c = 255f;
            return (uint)(int)MathF.Round(c) & 0xff;
        }
        var alpha = (uint)(int)MathF.Round(drawTint.W * 255f) & 0xff;
        return Channel(linear.X * drawTint.X) | Channel(linear.Y * drawTint.Y) << 8 | Channel(linear.Z * drawTint.Z) << 16 | alpha << 24;
    }

    private static bool Equal(float[] a, float[] b, float tolerance)
    {
        for (var i = 0; i < 12; i++)
            if (MathF.Abs(a[i] - b[i]) > tolerance)
                return false;
        return true;
    }

    /// <summary>
    /// scenesystem 18004c390, the size a sphere covers in a perspective view: clamp(r cot (1 / sqrt(d d)), 0, 1) with
    /// d d = (dy dy + dx dx) + dz dz from the camera, and 1 when the camera is inside it (r r &gt; d d). 1 / sqrt is the
    /// CPU's rsqrtss, whose low bits differ between vendors; the game's value is the one on the machine it runs on.
    /// <paramref name="cotHalfFov"/> is the view's +0x410 (1 for a cubemap face).
    /// </summary>
    public static float ScreenSize(Vector3 camera, float cotHalfFov, Vector3 centre, float radius)
    {
        var (dx, dy, dz) = (centre.X - camera.X, centre.Y - camera.Y, centre.Z - camera.Z);
        var squared = (dy * dy + dx * dx) + dz * dz;
        var inverse = Sse.IsSupported ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(squared)).ToScalar() : 1f / MathF.Sqrt(squared);
        var size = MathF.Min(MathF.Max(radius * cotHalfFov * inverse, 0f), 1f);
        return radius * radius > squared ? 1f : size;
    }

    /// <summary>
    /// The aggregate's own size for the small prepass layer (scenesystem 180041a00): its bounds' centre and half
    /// diagonal ((x x + y y) + z z). The bounds are the union of the fragments' (worldrenderer's 1800ba270 boxes, the
    /// same values as the fragments').
    /// </summary>
    public static float AggregateSize(IReadOnlyList<Fragment> fragments, Vector3 camera, float cotHalfFov)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        foreach (var f in fragments)
        {
            min = Vector3.Min(min, f.Min);
            max = Vector3.Max(max, f.Max);
        }
        var centre = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
        return ScreenSize(camera, cotHalfFov, centre, Half(max, min, zFirst: false));
    }

    /// <summary>r_csgo_depth_prepass_cull_threshold's default over 100 (client 181201670): the size that splits the
    /// game view's "Depth Prepass Large" layer (+0x6d0 = 0.6) from "Depth Prepass Small" (-0.6).</summary>
    public const float PrepassSplit = 60f / 100f;

    /// <summary>
    /// 180041a00's size test for a layer with a split: a positive split keeps fragments whose size reaches it, a
    /// negative one fragments where the smaller of their size and the aggregate's stays below its magnitude; zero keeps all.
    /// </summary>
    public static bool InLayer(float split, float fragmentSize, float aggregateSize)
    {
        if (split > 0f && split > fragmentSize)
            return false;
        return !(split < 0f && -split <= MathF.Min(fragmentSize, aggregateSize));
    }

    /// <summary>18003d380's frustum test: the box relative to the camera, against each block's four planes; outside when
    /// (px nx + py ny) + pz nz &lt; d for any plane, p the box corner the plane's masks pick (the minimum where set).</summary>
    public static bool Outside(Frustum f, Vector3 min, Vector3 max)
    {
        var lo = new Vector3(min.X - f.Origin.X, min.Y - f.Origin.Y, min.Z - f.Origin.Z);
        var hi = new Vector3(max.X - f.Origin.X, max.Y - f.Origin.Y, max.Z - f.Origin.Z);
        var b = f.Blocks;
        for (var block = 0; block < 2; block++)
        {
            var o = block * 40;
            for (var lane = 0; lane < 4; lane++)
            {
                var px = BitConverter.SingleToUInt32Bits(b[o + 16 + lane]) >> 31 != 0 ? lo.X : hi.X;
                var py = BitConverter.SingleToUInt32Bits(b[o + 20 + lane]) >> 31 != 0 ? lo.Y : hi.Y;
                var pz = BitConverter.SingleToUInt32Bits(b[o + 24 + lane]) >> 31 != 0 ? lo.Z : hi.Z;
                if ((px * b[o + lane] + py * b[o + 4 + lane]) + pz * b[o + 8 + lane] < b[o + 12 + lane])
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The order the cull visits an aggregate's fragments: fragment order, or above <see cref="BvhThreshold"/> the
    /// BVH's. CAggregateSceneObject's setup (scenesystem 180038650) builds tinybvh over the fragment bounds
    /// (1804c9ad0 -> 1804c87c0, the build resourcecompiler links, with c_trav 16 and c_int 1) and reorders the
    /// fragments into left-first leaf order, which is the build's final primitive order.
    /// </summary>
    public static int[] CullOrder(IReadOnlyList<Fragment> fragments)
        => fragments.Count > BvhThreshold
            ? Lighting.TinyBvh.Order([.. fragments.Select(f => (f.Min, f.Max))], 16f, 1f)
            : [.. Enumerable.Range(0, fragments.Count)];

    /// <summary>
    /// The fragments a view keeps, in the order the cull visits them. Through the BVH a node whose box is outside
    /// skips its fragments, which would all fail the same test (their boxes lie inside it and the test is monotone
    /// in the corner), so the result is the visit order filtered by the per-fragment test.
    /// </summary>
    /// <remarks>
    /// After the frustum, a fragment with a LOD setup needs its LOD group mask to hold the LOD
    /// <see cref="SelectLod"/> picks in the LOD view (the face for its own cascades too: 18003c5a0 takes the root view).
    /// </remarks>
    public static List<int> Visible(Frustum f, IReadOnlyList<Fragment> fragments, IReadOnlyList<LodSetup>? lodSetups = null,
        Vector3 lodCamera = default, float cotHalfFov = 1f, int viewHeight = 1024)
    {
        if (fragments.Any(x => x.LodSetup >= 0) && lodSetups == null)
            throw new ArgumentException("fragments with LOD setups need the aggregate's setups", nameof(lodSetups));
        var lods = lodSetups?.Select(s => SelectLod(s, lodCamera, cotHalfFov, viewHeight)).ToArray();
        var visible = new List<int>();
        foreach (var i in CullOrder(fragments))
            if (!Outside(f, fragments[i].Min, fragments[i].Max)
                && (fragments[i].LodSetup < 0 || (fragments[i].LodMask >> lods![fragments[i].LodSetup] & 1) != 0))
                visible.Add(i);
        return visible;
    }

    /// <summary>VkDrawIndexedIndirectCommand.</summary>
    public readonly record struct Command(int IndexCount, int InstanceCount, int FirstIndex, int VertexOffset, int FirstInstance);

    /// <summary>
    /// DrawArray's commands for one material list of (fragment, visible position), in visible order. When every
    /// fragment has its own transform (transform count = fragment count) entries whose positions follow on and whose
    /// draw call is the first's become one instanced command; otherwise each command is extended over the next
    /// entries while their transform slot is the first's and their first index continues the running range. Either
    /// merge needs sc_aggregate_fragment_merging. The first instance is the view's base plus the first entry's position.
    /// </summary>
    public static List<Command> Commands(IReadOnlyList<(int Fragment, int Position)> list, IReadOnlyList<Fragment> fragments, int instanceBase,
        bool ownTransforms = false, bool merging = true)
    {
        var commands = new List<Command>();
        for (var i = 0; i < list.Count; i++)
        {
            var (start, position) = list[i];
            var first = fragments[start];
            if (ownTransforms)
            {
                var instances = 1;
                while (merging && i + 1 < list.Count && list[i + 1].Position == position + instances
                       && fragments[list[i + 1].Fragment].DrawCall == first.DrawCall)
                {
                    instances++;
                    i++;
                }
                commands.Add(new Command(first.IndexCount, instances, first.StartIndex, first.BaseVertex, position + instanceBase));
                continue;
            }
            var count = first.IndexCount;
            while (merging && i + 1 < list.Count && fragments[list[i + 1].Fragment] is { } next
                   && next.TransformSlot == first.TransformSlot && count + first.StartIndex == next.StartIndex)
            {
                count += next.IndexCount;
                i++;
            }
            commands.Add(new Command(count, 1, first.StartIndex, first.BaseVertex, position + instanceBase));
        }
        return commands;
    }
}
