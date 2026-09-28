using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Maps;

/// <summary>
/// The scene the light precompute traces (the map world's ray tracing
/// environment at world +0x2bd0, resourcecompiler 0923): a two level
/// structure. Every raytrace owner (a world mesh, a static prop's model, a
/// cable, terrain, a referenced map's world) keeps its own environment in
/// its own space; the world's holds one instance of it per placement, with
/// the placement's 3x4 and its inverse (FUN_18100fd50, FUN_181c13ac0,
/// FUN_181c19970). The owner's flag word sits in front of its environment
/// and a ray's mask skips the whole object; each triangle carries its own
/// flag word. See <see cref="Trace(Vector3, Vector3, uint)"/> for the ray.
/// </summary>
public sealed class EditorTraceScene : ILightTracer
{
    /// <summary>The light precompute's first pass mask (LightPrecompute_TraceRay).</summary>
    public const uint LightMask = 0xc00060b1;

    /// <summary>One placement of an owner's environment.</summary>
    public sealed class Instance
    {
        /// <summary>Local to world (instance +0x10) and world to local (+0x40, FUN_18125ade0).</summary>
        public float[] ToWorld = [], ToLocal = [];

        /// <summary>The owner's flag word (the environment's first word).</summary>
        public uint ObjectFlags;

        /// <summary>Per triangle: the 13 float record (FUN_180118e90), its flag word.</summary>
        internal float[] Records = [];
        internal ushort[] Flags = [];

        /// <summary>Where it came from, for diagnostics.</summary>
        public string Source = "";

        internal Vector3 Mins, Maxs;
        internal Bvh? Tree;
        internal int Count => Flags.Length;
    }

    /// <summary>A hit: world distance along the ray and the triangle's flag word.</summary>
    public readonly record struct SceneHit(float Distance, ushort Flags, int Instance, int Triangle);

    private readonly List<Instance> _instances;
    private readonly Bvh _top;

    public IReadOnlyList<Instance> Instances => _instances;

    public EditorTraceScene(List<Instance> instances)
    {
        _instances = [.. instances.Where(i => i.Count > 0)];
        foreach (var inst in _instances)
        {
            inst.Tree = Bvh.Build(inst.Count, t => Box(inst, t));
            (inst.Mins, inst.Maxs) = WorldBox(inst);
        }
        _top = Bvh.Build(_instances.Count, i => (_instances[i].Mins, _instances[i].Maxs));
    }

    /// <summary>
    /// The scene of a map's own meshes (CMapMesh owners). Each mesh is one
    /// owner placed at its instance path times its own matrix with the scales
    /// on its columns (owner vf 0x10 = node vf 0xb0, FUN_18125bfe0). Its flag
    /// word (FUN_1810e11a0): 0x4000 under an entity, 0x20 when hidden,
    /// disableShadows 1 adds 0xa000 and 2 adds 0x2000. Its triangles are its
    /// faces in its own space (RayScene_AddFace, FUN_1813ccd20): a face with no
    /// material or with face flag 1 or 2 is left out, face flag 4 adds 0x20,
    /// the material's flag word (<see cref="TraceScene.MaterialFlags"/>) is
    /// the rest; the face is cut by <see cref="PolygonTriangulator"/>.
    /// Not added here: subdivided faces, static props (CModelHelper owners:
    /// flags 0x80b0000 for a class with static_prop metadata, else 0x80b4000,
    /// so only static props are traced), cables, terrain, and the second
    /// level a referenced map's world adds (MapWorld_AddChildWorld): its
    /// meshes are placed here with the composed path in one step.
    /// </summary>
    public static List<Instance> MapMeshInstances(DmxBinary.Document doc, Func<string, ushort> materialFlags)
    {
        var list = new List<Instance>();
        foreach (var mesh in MapMeshes.Read(doc))
        {
            var node = mesh.Element!;
            uint flags = 0;
            if (mesh.ParentType is "CMapEntity" or "CMapSmartProp")
                flags |= 0x4000;
            if (mesh.Hidden)
                flags |= 0x20;
            var shadows = node.GetValue<int>("disableShadows") ?? 0;
            if (shadows == 1)
                flags |= 0xa000;
            else if (shadows == 2)
                flags |= 0x2000;
            var local = MapMeshes.Local(node);
            var s = node.GetValue<Vector3>("scales") ?? Vector3.One;
            local[0] *= s.X; local[1] *= s.Y; local[2] *= s.Z;
            local[4] *= s.X; local[5] *= s.Y; local[6] *= s.Z;
            local[8] *= s.X; local[9] *= s.Y; local[10] *= s.Z;
            var toWorld = MapMeshes.Concat(mesh.Path, local);
            var triangles = LocalTriangles(node, materialFlags);
            list.Add(MakeInstance(triangles, toWorld, flags, $"mesh {mesh.NodeId}"));
        }
        return list;
    }

    private static List<(Vector3, Vector3, Vector3, ushort)> LocalTriangles(DmxBinary.Element node, Func<string, ushort> materialFlags)
    {
        var found = new List<(Vector3, Vector3, Vector3, ushort)>();
        var data = node.Get<DmxBinary.Element>("meshData");
        if (data is null)
            return found;
        object?[] Stream(string group, string name) => data.Get<DmxBinary.Element>(group)?.GetElements("streams")
            .FirstOrDefault(st => st.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
        int[] Ints(string name) => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();
        var positions = Stream("vertexData", "position");
        var materials = Stream("faceData", "materialindex").Select(x => x is int i ? i : -1).ToArray();
        var faceFlags = Stream("faceData", "flags").Select(x => x is int i ? i : 0).ToArray();
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var next = Ints("edgeNextIndices");
        var to = Ints("edgeVertexIndices");
        var first = Ints("faceEdgeIndices");
        var flagsOf = new Dictionary<int, ushort>();
        for (var f = 0; f < first.Length; f++)
        {
            var m = f < materials.Length ? materials[f] : -1;
            var ff = f < faceFlags.Length ? faceFlags[f] : 0;
            if (m < 0 || m >= names.Length || (ff & 3) != 0)
                continue;
            if (!flagsOf.TryGetValue(m, out var mf))
                flagsOf[m] = mf = materialFlags(names[m]);
            var tf = (ushort)((ff & 4) != 0 ? mf | 0x20 : mf);
            var corners = new List<Vector3>();
            var e = first[f];
            do
            {
                corners.Add((Vector3)positions[to[e]]!);
                e = next[e];
            }
            while (e != first[f] && corners.Count <= next.Length);
            var tri = PolygonTriangulator.Triangulate([.. corners]);
            for (var k = 0; k + 2 < tri.Length; k += 3)
                found.Add((corners[tri[k]], corners[tri[k + 1]], corners[tri[k + 2]], tf));
        }
        return found;
    }

    /// <summary>
    /// Adds an owner's triangles, given in its own space, placed by
    /// <paramref name="toWorld"/>. Degenerate triangles are dropped (the
    /// record cannot be made).
    /// </summary>
    public static Instance MakeInstance(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, ushort Flags)> triangles,
                                        float[] toWorld, uint objectFlags, string source)
    {
        var records = new List<float>();
        var flags = new List<ushort>();
        Span<float> corners = stackalloc float[9];
        Span<float> r = stackalloc float[13];
        foreach (var (a, b, c, f) in triangles)
        {
            corners[0] = a.X; corners[1] = a.Y; corners[2] = a.Z;
            corners[3] = b.X; corners[4] = b.Y; corners[5] = b.Z;
            corners[6] = c.X; corners[7] = c.Y; corners[8] = c.Z;
            r.Clear();
            if (!RayTraceEnvironment.RecordFromCorners(corners, r))
                continue;
            foreach (var x in r)
                records.Add(x);
            for (var k = 0; k < 9; k++)
                records.Add(corners[k]);
            flags.Add(f);
        }
        return new Instance
        {
            ToWorld = toWorld, ToLocal = Invert(toWorld), ObjectFlags = objectFlags,
            Records = [.. records], Flags = [.. flags], Source = source,
        };
    }

    /// <summary>
    /// FUN_18125ade0: a general 3x4 inverse by the adjugate; identity when
    /// the determinant is below 1.17549435e-35 in size.
    /// </summary>
    public static float[] Invert(float[] m)
    {
        float m0 = m[0], m1 = m[1], m2 = m[2], m4 = m[4], m5 = m[5], m6 = m[6], m8 = m[8], m9 = m[9], m10 = m[10];
        var det = ((m6 * m1 - m5 * m2) * m8 + (m9 * m2 - m10 * m1) * m4) + (m5 * m10 - m6 * m9) * m0;
        if (!(1.17549435e-35f <= MathF.Abs(det)))
            return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        var k = 1f / det;
        var o = new float[12];
        o[0] = (m5 * m10 - m9 * m6) * k;
        o[1] = (m9 * m2 - m1 * m10) * k;
        o[2] = (m1 * m6 - m5 * m2) * k;
        o[4] = (m6 * m8 - m10 * m4) * k;
        o[5] = (m10 * m0 - m2 * m8) * k;
        o[6] = (m2 * m4 - m6 * m0) * k;
        o[8] = (m9 * m4 - m5 * m8) * k;
        o[9] = (m1 * m8 - m9 * m0) * k;
        o[10] = (m5 * m0 - m1 * m4) * k;
        float tx = -m[3], ty = -m[7], tz = -m[11];
        o[3] = (tz * o[2] + ty * o[1]) + tx * o[0];
        o[7] = (tz * o[6] + ty * o[5]) + tx * o[4];
        o[11] = (tz * o[10] + ty * o[9]) + tx * o[8];
        return o;
    }

    // The instance pass's point transform (181c10ea0, 181c12720): each row
    // t + ((x m0 + y m1) + z m2).
    private static Vector3 Xf(float[] m, Vector3 p) => new(
        m[3] + ((p.X * m[0] + p.Y * m[1]) + p.Z * m[2]),
        m[7] + ((p.X * m[4] + p.Y * m[5]) + p.Z * m[6]),
        m[11] + ((p.X * m[8] + p.Y * m[9]) + p.Z * m[10]));

    float? ILightTracer.Trace(Vector3 start, Vector3 end) => Trace(start, end, LightMask)?.Distance;

    LightTraceHit? ILightTracer.Hit(Vector3 start, Vector3 end, uint mask)
        => Trace(start, end, mask) is { } h ? new LightTraceHit(h.Distance, h.Flags) : null;

    /// <summary>
    /// One segment through the batch tracer in mode 2 (FUN_181c1d010 into
    /// 181c0a000 and the instance pass 181c10ea0). The direction is the
    /// segment times the refined rcpps of its length; every instance whose
    /// owner flags miss the mask is traced in its own space: the origin and
    /// origin + direction moved by the inverse, the local direction scaled by
    /// a refined rsqrtps, the range by the refined rcpps of that. A triangle
    /// counts when its flags miss the mask, its plane faces the ray (unless
    /// flag 2, render back faces) by more than 1e-10, 0 &lt; t, and the point
    /// is inside its edges. The local hit is moved back to the world and its
    /// distance from the origin measured; the nearest wins (strictly), and a
    /// hit beyond the segment's length is none.
    /// </summary>
    public SceneHit? Trace(Vector3 start, Vector3 end, uint mask)
    {
        var delta = new Vector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
        var length = MathF.Sqrt((delta.Z * delta.Z + delta.Y * delta.Y) + delta.X * delta.X);
        var guarded = MathF.Abs(length) < 1.17549435e-38f
            ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(length) | BitConverter.SingleToInt32Bits(1.1920929e-07f))
            : length;
        var scale = RayTraceEnvironment.Refined(guarded);
        var dir = new Vector3(delta.X * scale, delta.Y * scale, delta.Z * scale);

        SceneHit? best = null;
        var inverse = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        Span<int> stack = stackalloc int[256];
        var top = 0;
        if (_instances.Count > 0)
            stack[top++] = 0;
        while (top > 0)
        {
            var node = stack[--top];
            if (!_top.Enters(node, start, inverse, length))
                continue;
            var (first, count, left) = _top.Node(node);
            if (count == 0)
            {
                stack[top++] = left;
                stack[top++] = left + 1;
                continue;
            }
            for (var k = first; k < first + count; k++)
            {
                var index = _top.Items[k];
                var inst = _instances[index];
                if ((inst.ObjectFlags & mask) != 0)
                    continue;
                if (TraceInstance(inst, start, dir, length, mask) is not { } h)
                    continue;
                if (best is null || h.Distance < best.Value.Distance)
                    best = h with { Instance = index };
            }
        }
        return best is { } b && length < b.Distance ? null : best;
    }

    private static SceneHit? TraceInstance(Instance inst, Vector3 origin, Vector3 dir, float tmax, uint mask)
    {
        var m = inst.ToLocal;
        var lo = Xf(m, origin);
        var p2 = new Vector3(origin.X + dir.X, origin.Y + dir.Y, origin.Z + dir.Z);
        var lp = Xf(m, p2);
        var ld = new Vector3(lp.X - lo.X, lp.Y - lo.Y, lp.Z - lo.Z);
        var l2 = (ld.Y * ld.Y + ld.X * ld.X) + ld.Z * ld.Z;
        var r = Rsqrt(l2);
        var invLen = (r * (3f - (r * r) * l2)) * 0.5f;
        ld = new Vector3(ld.X * invLen, ld.Y * invLen, ld.Z * invLen);
        var c = Rcp(invLen);
        var len = (c + c) - (c * c) * invLen;
        var localMax = len * tmax;

        var best = float.NaN;
        var bestTriangle = -1;
        var inverse = new Vector3(1f / ld.X, 1f / ld.Y, 1f / ld.Z);
        var tree = inst.Tree!;
        Span<int> stack = stackalloc int[256];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = stack[--top];
            if (!tree.Enters(node, lo, inverse, localMax))
                continue;
            var (first, count, left) = tree.Node(node);
            if (count == 0)
            {
                stack[top++] = left;
                stack[top++] = left + 1;
                continue;
            }
            for (var k = first; k < first + count; k++)
            {
                var t = tree.Items[k];
                if ((inst.Flags[t] & mask) != 0)
                    continue;
                var limit = float.IsNaN(best) ? 1e23f : best;
                if (Meets(inst.Records.AsSpan(t * 22, 13), (inst.Flags[t] & 2) != 0, lo, ld, limit) is { } hit
                    && (float.IsNaN(best) || hit < best || (hit == best && t < bestTriangle)))
                    (best, bestTriangle) = (hit, t);
            }
        }
        if (float.IsNaN(best))
            return null;
        var local = new Vector3(best * ld.X + lo.X, best * ld.Y + lo.Y, best * ld.Z + lo.Z);
        var w = Xf(inst.ToWorld, local);
        var dx = w.X - origin.X;
        var dy = w.Y - origin.Y;
        var dz = w.Z - origin.Z;
        var distance = MathF.Sqrt((dz * dz + dy * dy) + dx * dx);
        return new SceneHit(distance, inst.Flags[bestTriangle], -1, bestTriangle);
    }

    // 181c0a000's triangle test (mode 2, back faces culled).
    private static float? Meets(ReadOnlySpan<float> r, bool twoSided, Vector3 o, Vector3 d, float best)
    {
        float nx = r[0], ny = r[1], nz = r[2], plane = r[3];
        var denom = (d.Z * nz + d.Y * ny) + d.X * nx;
        var ok = denom < -1.00000001e-10f || 1.00000001e-10f < denom;
        if (!twoSided)
            ok &= denom < 0f;
        if (!ok)
            return null;
        var t = (plane - ((o.Z * nz + o.Y * ny) + nx * o.X)) / denom;
        if (!(0f < t && t < best))
            return null;
        int u = (int)r[11], v = (int)r[12];
        var pu = t * Axis(d, u) + Axis(o, u);
        var pv = t * Axis(d, v) + Axis(o, v);
        var a = (r[5] * pu + r[6] * pv) + r[7];
        var b = (r[8] * pu + r[9] * pv) + r[10];
        return 0f <= a && 0f <= b && b + a <= 1f ? t : null;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static float Rsqrt(float x) => Sse.IsSupported
        ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalarUnsafe(x)).ToScalar() : 1f / MathF.Sqrt(x);

    private static float Rcp(float x) => Sse.IsSupported
        ? Sse.ReciprocalScalar(Vector128.CreateScalarUnsafe(x)).ToScalar() : 1f / x;

    private static (Vector3, Vector3) Box(Instance inst, int t)
    {
        var c = inst.Records.AsSpan(t * 22 + 13, 9);
        var a = new Vector3(c[0], c[1], c[2]);
        var b = new Vector3(c[3], c[4], c[5]);
        var e = new Vector3(c[6], c[7], c[8]);
        return (Vector3.Min(a, Vector3.Min(b, e)), Vector3.Max(a, Vector3.Max(b, e)));
    }

    private static (Vector3, Vector3) WorldBox(Instance inst)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (var t = 0; t < inst.Count; t++)
            for (var k = 0; k < 3; k++)
            {
                var c = inst.Records.AsSpan(t * 22 + 13 + k * 3, 3);
                var w = Xf(inst.ToWorld, new Vector3(c[0], c[1], c[2]));
                lo = Vector3.Min(lo, w);
                hi = Vector3.Max(hi, w);
            }
        return (lo, hi);
    }

    /// <summary>A bounding volume hierarchy over boxes, padded so a ray on a face is never cut.</summary>
    internal sealed class Bvh
    {
        private readonly List<(Vector3 Lo, Vector3 Hi, int First, int Count, int Left)> _nodes = [];
        public int[] Items { get; private set; } = [];

        public static Bvh Build(int count, Func<int, (Vector3, Vector3)> box)
        {
            var bvh = new Bvh();
            var items = Enumerable.Range(0, count).ToArray();
            var boxes = Enumerable.Range(0, count).Select(box).ToArray();
            bvh._nodes.Add(default);
            bvh.Split(0, items, 0, count, boxes);
            bvh.Items = items;
            return bvh;
        }

        private void Split(int node, int[] items, int first, int count, (Vector3 Lo, Vector3 Hi)[] boxes)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (var k = first; k < first + count; k++)
            {
                lo = Vector3.Min(lo, boxes[items[k]].Lo);
                hi = Vector3.Max(hi, boxes[items[k]].Hi);
            }
            var pad = new Vector3(1e-3f) + (Vector3.Abs(lo) + Vector3.Abs(hi)) * 1e-5f;
            lo -= pad;
            hi += pad;
            if (count <= 4)
            {
                _nodes[node] = (lo, hi, first, count, 0);
                return;
            }
            var size = hi - lo;
            var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            float Centre(int i) => axis == 0 ? boxes[i].Lo.X + boxes[i].Hi.X : axis == 1 ? boxes[i].Lo.Y + boxes[i].Hi.Y : boxes[i].Lo.Z + boxes[i].Hi.Z;
            Array.Sort(items, first, count, Comparer<int>.Create((a, b) => Centre(a).CompareTo(Centre(b))));
            var half = count / 2;
            var left = _nodes.Count;
            _nodes.Add(default);
            _nodes.Add(default);
            _nodes[node] = (lo, hi, 0, 0, left);
            Split(left, items, first, half, boxes);
            Split(left + 1, items, first + half, count - half, boxes);
        }

        public (int First, int Count, int Left) Node(int n) => (_nodes[n].First, _nodes[n].Count, _nodes[n].Left);

        public bool Enters(int n, Vector3 o, Vector3 inverse, float limit)
        {
            var (lo, hi, _, _, _) = _nodes[n];
            double t0 = 0, t1 = limit * 1.001 + 1;
            for (var a = 0; a < 3; a++)
            {
                double oo = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
                double inv = a == 0 ? inverse.X : a == 1 ? inverse.Y : inverse.Z;
                double l = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z;
                double h = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
                if (double.IsInfinity(inv))
                {
                    if (oo < l || oo > h)
                        return false;
                    continue;
                }
                var ta = (l - oo) * inv;
                var tb = (h - oo) * inv;
                if (ta > tb)
                    (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta);
                t1 = Math.Min(t1, tb);
                if (t0 > t1)
                    return false;
            }
            return true;
        }
    }
}
