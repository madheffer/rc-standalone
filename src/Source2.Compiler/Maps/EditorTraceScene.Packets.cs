using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The light pass's rays as Valve traces them: in packets of four. RayBatch_Add (181c1bf10) files each segment by its
/// delta's sign octant and traces a packet when four have gathered;
/// RayBatch_Flush (181c1c530) traces what is left, octant by octant, short
/// packets padded with their first ray. A packet enters an instance when any
/// lane meets its box (RayScene_InstanceNodeHit 181c15d00), and a kd walk
/// tests every lane against each leaf it visits, so a ray can hit through
/// another lane's traversal. In an instance's own space the lanes may point
/// into different octants: the dispatch (181c02a20) then traces the lanes
/// one octant group at a time, each group's sub-packet keeping every lane's
/// origin and range and filling the others' directions with its first
/// lane's (only the group's results are kept).
/// </summary>
public sealed partial class EditorTraceScene
{
    LightTraceHit?[] ILightTracer.HitAll(IReadOnlyList<Vector3> starts, IReadOnlyList<Vector3> ends, uint mask)
    {
        var hits = new LightTraceHit?[starts.Count];
        var found = TraceBatch(starts, ends, mask);
        for (var k = 0; k < hits.Length; k++)
            hits[k] = found[k] is { } h ? new LightTraceHit(h.Distance, h.Flags) : null;
        return hits;
    }

    /// <summary>A batch of segments under one mask, packed and flushed as RayBatch does.</summary>
    internal SceneHit?[] TraceBatch(IReadOnlyList<Vector3> starts, IReadOnlyList<Vector3> ends, uint mask)
    {
        var result = new SceneHit?[starts.Count];
        var queues = new List<int>[8];
        for (var q = 0; q < 8; q++)
            queues[q] = new List<int>(4);
        var s = new Vector3[4];
        var e = new Vector3[4];
        var hits = new SceneHit?[4];
        void Run(List<int> lanes)
        {
            for (var l = 0; l < 4; l++)
            {
                var k = lanes[l < lanes.Count ? l : 0];
                (s[l], e[l]) = (starts[k], ends[k]);
            }
            TracePacket(s, e, mask, hits);
            for (var l = 0; l < lanes.Count; l++)
                result[lanes[l]] = hits[l];
            lanes.Clear();
        }
        for (var k = 0; k < starts.Count; k++)
        {
            float dx = ends[k].X - starts[k].X, dy = ends[k].Y - starts[k].Y, dz = ends[k].Z - starts[k].Z;
            var queue = queues[(dx < 0f ? 1 : 0) | (dy < 0f ? 2 : 0) | (dz < 0f ? 4 : 0)];
            queue.Add(k);
            if (queue.Count == 4)
                Run(queue);
        }
        foreach (var queue in queues)
            if (queue.Count > 0)
                Run(queue);
        return result;
    }

    /// <summary>
    /// One packet of four segments (RayBatch_TracePacket 181c1d010 into the
    /// instance pass 181c10ea0): each lane's direction the segment times the
    /// refined rcpps of its length, range 0 to that length; an instance is
    /// traced for the whole packet when any lane meets its box; per lane the
    /// nearest hit wins (strictly) and one beyond the lane's length is none.
    /// </summary>
    internal void TracePacket(Vector3[] starts, Vector3[] ends, uint mask, SceneHit?[] hits)
    {
        var o = new float[12];
        var d = new float[12];
        var from = new float[4];
        var to = new float[4];
        var inverse = new Vector3[4];
        for (var l = 0; l < 4; l++)
        {
            var delta = new Vector3(ends[l].X - starts[l].X, ends[l].Y - starts[l].Y, ends[l].Z - starts[l].Z);
            var length = MathF.Sqrt((delta.Z * delta.Z + delta.Y * delta.Y) + delta.X * delta.X);
            var guarded = MathF.Abs(length) < 1.17549435e-38f
                ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(length) | BitConverter.SingleToInt32Bits(1.1920929e-07f))
                : length;
            var scale = RayTraceEnvironment.Refined(guarded);
            var dir = new Vector3(delta.X * scale, delta.Y * scale, delta.Z * scale);
            (o[l], o[4 + l], o[8 + l]) = (starts[l].X, starts[l].Y, starts[l].Z);
            (d[l], d[4 + l], d[8 + l]) = (dir.X, dir.Y, dir.Z);
            to[l] = length;
            inverse[l] = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        }

        var best = new SceneHit?[4];
        Span<int> stack = stackalloc int[256];
        var top = 0;
        if (_instances.Count > 0)
            stack[top++] = 0;
        while (top > 0)
        {
            var node = stack[--top];
            var enters = false;
            for (var l = 0; l < 4 && !enters; l++)
                enters = _top.Enters(node, starts[l], inverse[l], to[l]);
            if (!enters)
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
                if ((inst.ObjectFlags & mask) != 0 || !ValveBoxAny(inst, o, d, from, to))
                    continue;
                var found = TraceInstancePacket(inst, o, d, from, to, mask);
                for (var l = 0; l < 4; l++)
                    if (found[l] is { } h && (best[l] is not { } b || h.Distance < b.Distance))
                        best[l] = h with { Instance = index };
            }
        }
        for (var l = 0; l < 4; l++)
            hits[l] = best[l] is { } b && to[l] < b.Distance ? null : best[l];
    }

    /// <summary>
    /// The packet in one instance's space: per lane the origin and origin plus
    /// direction moved by the inverse, the direction normalised by a refined
    /// rsqrtps, the range scaled by the refined rcpps of that. A scene of
    /// triangles is walked through its kd tree (by octant group); a scene of
    /// scenes runs its own instance pass with the whole packet. Each lane's
    /// hit comes back as its distance from the lane's origin in the caller's
    /// space.
    /// </summary>
    private static SceneHit?[] TraceInstancePacket(Instance inst, float[] o, float[] d, float[] from, float[] to, uint mask)
    {
        var m = inst.ToLocal;
        var lo = new float[12];
        var ld = new float[12];
        var lfrom = new float[4];
        var lto = new float[4];
        var origins = new Vector3[4];
        for (var l = 0; l < 4; l++)
        {
            var origin = new Vector3(o[l], o[4 + l], o[8 + l]);
            var a = Xf(m, origin);
            var b = Xf(m, new Vector3(origin.X + d[l], origin.Y + d[4 + l], origin.Z + d[8 + l]));
            var v = new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var l2 = (v.Y * v.Y + v.X * v.X) + v.Z * v.Z;
            var r = Rsqrt(l2);
            var invLen = (r * (3f - (r * r) * l2)) * 0.5f;
            var c = Rcp(invLen);
            var s = (c + c) - (c * c) * invLen;
            (lo[l], lo[4 + l], lo[8 + l]) = (a.X, a.Y, a.Z);
            (ld[l], ld[4 + l], ld[8 + l]) = (v.X * invLen, v.Y * invLen, v.Z * invLen);
            lfrom[l] = s * from[l];
            lto[l] = s * to[l];
            origins[l] = origin;
        }

        if (inst.Kd?.WatchLog is { } watchLog)
            for (var l = 0; l < 4; l++)
                watchLog.Add($"enter lane {l}: o ({lo[l]:R} {lo[4 + l]:R} {lo[8 + l]:R}) d ({ld[l]:R} {ld[4 + l]:R} {ld[8 + l]:R}) range {lfrom[l]:R}..{lto[l]:R}");
        var t = new float[4];
        var tri = new int[4];
        var flags = new ushort[4];
        var hit = new bool[4];
        if (inst.Children is { } children)
        {
            var nested = new SceneHit?[4];
            var inverse = new Vector3[4];
            for (var l = 0; l < 4; l++)
                inverse[l] = new Vector3(1f / ld[l], 1f / ld[4 + l], 1f / ld[8 + l]);
            var tree = inst.Tree!;
            Span<int> nodes = stackalloc int[256];
            var depth = 0;
            nodes[depth++] = 0;
            while (depth > 0)
            {
                var node = nodes[--depth];
                var enters = false;
                for (var l = 0; l < 4 && !enters; l++)
                    enters = tree.Enters(node, new Vector3(lo[l], lo[4 + l], lo[8 + l]), inverse[l], lto[l]);
                if (!enters)
                    continue;
                var (first, count, left) = tree.Node(node);
                if (count == 0)
                {
                    nodes[depth++] = left;
                    nodes[depth++] = left + 1;
                    continue;
                }
                for (var k = first; k < first + count; k++)
                {
                    var child = children[tree.Items[k]];
                    if ((child.ObjectFlags & mask) != 0 || !ValveBoxAny(child, lo, ld, lfrom, lto))
                        continue;
                    var found = TraceInstancePacket(child, lo, ld, lfrom, lto, mask);
                    for (var l = 0; l < 4; l++)
                        if (found[l] is { } h && (nested[l] is not { } b || h.Distance < b.Distance))
                            nested[l] = h;
                }
            }
            for (var l = 0; l < 4; l++)
                if (nested[l] is { } h)
                    (t[l], tri[l], flags[l], hit[l]) = (h.Distance, h.Triangle, h.Flags, true);
        }
        else
        {
            KdCastByOctant(inst, lo, ld, lfrom, lto, mask, t, tri);
            for (var l = 0; l < 4; l++)
                if (tri[l] >= 0)
                    (flags[l], hit[l]) = (inst.Flags[tri[l]], true);
        }

        var result = new SceneHit?[4];
        for (var l = 0; l < 4; l++)
        {
            if (!hit[l])
                continue;
            var local = new Vector3(t[l] * ld[l] + lo[l], t[l] * ld[4 + l] + lo[4 + l], t[l] * ld[8 + l] + lo[8 + l]);
            var w = Xf(inst.ToWorld, local);
            float dx = w.X - origins[l].X, dy = w.Y - origins[l].Y, dz = w.Z - origins[l].Z;
            result[l] = new SceneHit(MathF.Sqrt((dz * dz + dy * dy) + dx * dx), flags[l], -1, tri[l]);
        }
        return result;
    }

    /// <summary>
    /// The mode 2 kd walk over an instance's triangles for a packet in its
    /// own space (181c02a20): one walk when every lane shares each direction
    /// sign bit (so -0 counts as negative), else one per octant group in lane
    /// order.
    /// </summary>
    private static void KdCastByOctant(Instance inst, float[] lo, float[] ld, float[] lfrom, float[] lto, uint mask, float[] t, int[] tri)
    {
        EnsureKd(inst);
        int Octant(int l) => (BitConverter.SingleToInt32Bits(ld[l]) < 0 ? 1 : 0) | (BitConverter.SingleToInt32Bits(ld[4 + l]) < 0 ? 2 : 0)
                             | (BitConverter.SingleToInt32Bits(ld[8 + l]) < 0 ? 4 : 0);
        Span<float> best = stackalloc float[4];
        Span<int> found = stackalloc int[4];
        var octant = Octant(0);
        if (Octant(1) == octant && Octant(2) == octant && Octant(3) == octant)
        {
            inst.Kd!.Cast(lo, ld, lfrom, lto, octant, (ushort)(mask & 0xffff), best, found);
            for (var l = 0; l < 4; l++)
                (t[l], tri[l]) = (best[l], found[l]);
            return;
        }
        Span<bool> pending = [true, true, true, true];
        var dirs = new float[12];
        for (var l = 0; l < 4; l++)
        {
            if (!pending[l])
                continue;
            var group = Octant(l);
            for (var p = 0; p < 4; p++)
                (dirs[p], dirs[4 + p], dirs[8 + p]) = (ld[l], ld[4 + l], ld[8 + l]);
            pending[l] = false;
            Span<bool> member = [false, false, false, false];
            member[l] = true;
            for (var j = l + 1; j < 4; j++)
                if (pending[j] && Octant(j) == group)
                {
                    (dirs[j], dirs[4 + j], dirs[8 + j]) = (ld[j], ld[4 + j], ld[8 + j]);
                    pending[j] = false;
                    member[j] = true;
                }
            inst.Kd!.Cast(lo, dirs, lfrom, lto, group, (ushort)(mask & 0xffff), best, found);
            for (var p = 0; p < 4; p++)
                if (member[p])
                    (t[p], tri[p]) = (best[p], found[p]);
        }
    }

    /// <summary>Diagnostics: log the kd walk's visits and tests of one triangle of a top-level instance (null to stop).</summary>
    internal void WatchTriangle(int instance, int triangle, List<string>? log)
    {
        var inst = _instances[instance];
        EnsureKd(inst);
        inst.Kd!.Watch = log is null ? -1 : triangle;
        log?.Add($"bounds {inst.Kd.Bounds.Mins:R} .. {inst.Kd.Bounds.Maxs:R}, {inst.Kd.Nodes.Count} nodes, root axis {inst.Kd.Nodes[0].Axis} split {inst.Kd.Nodes[0].Split:R}");
        if (log is not null)
        {
            void Paths(int node, string path)
            {
                var n = inst.Kd.Nodes[node];
                if (n.Axis == 3)
                {
                    if (n.Slots.Contains(triangle))
                        log.Add($"leaf {node} [{string.Join(",", n.Slots)}] via {path}");
                    return;
                }
                Paths(n.Lower, $"{path} {"xyz"[n.Axis]}<{n.Split:R}");
                Paths(n.Lower + 1, $"{path} {"xyz"[n.Axis]}>{n.Split:R}");
            }
            Paths(0, "");
            void Dump(int node)
            {
                var n = inst.Kd.Nodes[node];
                if (n.Axis == 3)
                {
                    log.Add("tree leaf " + string.Join(" ", n.Slots));
                    return;
                }
                log.Add($"tree {n.Axis} {n.Split:R}");
                Dump(n.Lower);
                Dump(n.Lower + 1);
            }
            Dump(0);
            var c = inst.Records.AsSpan(triangle * 22 + 13, 9);
            log.Add($"corners ({c[0]:R} {c[1]:R} {c[2]:R}) ({c[3]:R} {c[4]:R} {c[5]:R}) ({c[6]:R} {c[7]:R} {c[8]:R})");
        }
        inst.Kd.WatchLog = log;
    }

    /// <summary>Diagnostics: every triangle-level scene, nested ones too, as (path, kind, local box, our scene flag word).</summary>
    internal IEnumerable<(string Path, (Vector3 Mins, Vector3 Maxs) Box, uint Flags)> TriangleScenes()
    {
        var list = new List<(string, (Vector3, Vector3), uint)>();
        void Walk(Instance inst, string path)
        {
            if (inst.Children is { } kids)
            {
                list.Add((path + " (container)", inst.ValveBox ?? default, inst.ObjectFlags));
                foreach (var k in kids)
                    Walk(k, path + "/" + k.Source);
                return;
            }
            list.Add((path, new InstanceTriangles(inst).TracedBounds, inst.SceneFlags));
        }
        foreach (var inst in _instances)
            Walk(inst, inst.Source);
        return list;
    }

    private static void EnsureKd(Instance inst)
    {
        if (inst.Kd is not null)
            return;
        var source = new InstanceTriangles(inst);
        var (mins, maxs) = source.TracedBounds;
        inst.Kd = new TracerKd(source, [.. Enumerable.Range(0, inst.Flags.Length)], source.Corners, mins, maxs,
                               (inst.SceneFlags & 0x10000) != 0 ? TracerKd.WideLeafSize : TracerKd.LeafSize) { CullBackFaces = true };
    }

    /// <summary>
    /// RayScene_InstanceNodeHit (181c15d00) for a packet: any lane meeting
    /// the instance's box. The inverse direction is an exact divide (zero
    /// either sign gives +infinity), the near plane by the sign of 0 &lt;= d;
    /// a lane meets it when its start &lt; far, near &lt;= far and near &lt;
    /// its range.
    /// </summary>
    private static bool ValveBoxAny(Instance inst, float[] o, float[] d, float[] from, float[] to)
    {
        var (lo, hi) = inst.ValveBox!.Value;
        for (var l = 0; l < 4; l++)
            if (BoxLane(lo, hi, new Vector3(o[l], o[4 + l], o[8 + l]), new Vector3(d[l], d[4 + l], d[8 + l]), from[l], to[l]))
                return true;
        return false;
    }

    private static bool BoxLane(Vector3 lo, Vector3 hi, Vector3 o, Vector3 d, float from, float to)
    {
        static float Inv(float x) => 1f / (x == 0f ? 0f : x);
        static float Near(float l, float h, float oo, float dd) => Inv(dd) * (0f <= dd ? l - oo : h - oo);
        static float Far(float l, float h, float oo, float dd) => Inv(dd) * (0f <= dd ? h - oo : l - oo);
        static float Max(float a, float b) => a > b ? a : b;
        static float Min(float a, float b) => a < b ? a : b;
        var near = Max(Near(lo.X, hi.X, o.X, d.X), Max(Near(lo.Y, hi.Y, o.Y, d.Y), Near(lo.Z, hi.Z, o.Z, d.Z)));
        var far = Min(Far(lo.X, hi.X, o.X, d.X), Min(Far(lo.Y, hi.Y, o.Y, d.Y), Far(lo.Z, hi.Z, o.Z, d.Z)));
        return from < far && near <= far && near < to;
    }
}
