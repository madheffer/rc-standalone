using System.Numerics;
using System.Runtime.CompilerServices;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The CPU stand-in for vrad3's ray tracing acceleration structure: every
/// instance's triangles in world space under one binned-SAH BVH, traced with
/// the Vulkan ray tracing rules the shaders rely on (GL_EXT_ray_tracing):
/// <list type="bullet">
/// <item>an instance is tested when its mask meets the ray's cull mask;</item>
/// <item>a triangle is front facing when its vertices run counter-clockwise
/// as seen from the ray origin (scenesystem's meshes are counter-clockwise
/// with the face normal cross(v1 - v0, v2 - v0) outward), so ray flag 16
/// (cull back facing) skips triangles the ray meets from behind and 32 (cull
/// front facing) those it meets from the front. The sample validation needs
/// this one: its backface count wants a hit both behind the face normal and
/// gone when back faces are culled.</item>
/// <item>hits are kept in [tMin, tMax]; a non-opaque instance's candidates
/// go through the any-hit callback, which may ignore them; the closest
/// accepted hit wins, ties to the first found;</item>
/// <item>barycentrics are those of vertices 1 and 2.</item>
/// </list>
/// Triangles are intersected with the watertight test of Woop, Benthin and
/// Wald; traversal order is front to back, so ties may resolve unlike the
/// GPU's.
/// </summary>
public sealed class RayScene
{
    public const uint CullBackFacing = 16, CullFrontFacing = 32, TerminateOnFirstHit = 4, Opaque = 1;


    /// <summary>One placed triangle: the instance it belongs to and its index inside that instance's mesh.</summary>
    public readonly record struct Triangle(int Instance, int Primitive);

    /// <summary>An instance: its 8-bit mask, whether its candidates skip the any-hit test, and whether facing culls ignore it (the TLAS instance's facing-cull-disable bit).</summary>
    public sealed record Instance(byte Mask, bool Opaque, bool CullDisable = false);

    private readonly Vector3[] _v0, _v1, _v2;
    private readonly Triangle[] _tris;
    private readonly Instance[] _instances;

    /// <summary>The instances, in scene order (the GPU host's TLAS takes their masks and flags).</summary>
    public IReadOnlyList<Instance> Instances => _instances;
    private Node[] _nodes = [];
    private int[] _order = [];

    private struct Node
    {
        public Vector3 Min, Max;
        // Leaf: first triangle and count (count > 0); inner: left child (right = left + 1), count 0.
        public int Start, Count;
    }

    /// <summary>A hit: distance, barycentrics of vertices 1 and 2, the triangle and whether it was met from the front.</summary>
    public struct Hit
    {
        public float T, U, V;
        public int Triangle;
        public bool FrontFacing;
        public readonly bool Found => Triangle >= 0;
    }

    /// <param name="vertices">World-space corners, three per triangle.</param>
    public RayScene(Vector3[] vertices, Triangle[] triangles, Instance[] instances)
    {
        var n = triangles.Length;
        _v0 = new Vector3[n];
        _v1 = new Vector3[n];
        _v2 = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            _v0[i] = vertices[i * 3];
            _v1[i] = vertices[i * 3 + 1];
            _v2[i] = vertices[i * 3 + 2];
        }
        _tris = triangles;
        _instances = instances;
        Build();
    }

    public int TriangleCount => _tris.Length;
    public Triangle TriangleAt(int i) => _tris[_order[i]];
    public (Vector3 A, Vector3 B, Vector3 C) Corners(int i) { var t = _order[i]; return (_v0[t], _v1[t], _v2[t]); }

    private void Build()
    {
        var n = _tris.Length;
        _order = new int[n];
        for (var i = 0; i < n; i++)
            _order[i] = i;
        if (n == 0)
        {
            _nodes = [new Node { Min = Vector3.Zero, Max = Vector3.Zero, Start = 0, Count = 0 }];
            return;
        }
        var centres = new Vector3[n];
        var lo = new Vector3[n];
        var hi = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            lo[i] = Vector3.Min(_v0[i], Vector3.Min(_v1[i], _v2[i]));
            hi[i] = Vector3.Max(_v0[i], Vector3.Max(_v1[i], _v2[i]));
            centres[i] = (lo[i] + hi[i]) * 0.5f;
        }
        var nodes = new List<Node>(2 * n);
        nodes.Add(default);
        var stack = new Stack<(int Node, int Start, int Count)>();
        stack.Push((0, 0, n));
        const int Bins = 16;
        Span<int> binCount = stackalloc int[Bins];
        var binLo = new Vector3[Bins];
        var binHi = new Vector3[Bins];
        while (stack.Count > 0)
        {
            var (index, start, count) = stack.Pop();
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            var cmin = new Vector3(float.MaxValue);
            var cmax = new Vector3(float.MinValue);
            for (var i = start; i < start + count; i++)
            {
                var t = _order[i];
                bmin = Vector3.Min(bmin, lo[t]);
                bmax = Vector3.Max(bmax, hi[t]);
                cmin = Vector3.Min(cmin, centres[t]);
                cmax = Vector3.Max(cmax, centres[t]);
            }
            var node = new Node { Min = bmin, Max = bmax, Start = start, Count = count };
            var split = -1;
            var axis = -1;
            if (count > 2)
            {
                var best = count * Area(bmin, bmax);
                for (var a = 0; a < 3; a++)
                {
                    var extent = Get(cmax, a) - Get(cmin, a);
                    if (!(extent > 0f))
                        continue;
                    binCount.Clear();
                    for (var b = 0; b < Bins; b++)
                    {
                        binLo[b] = new Vector3(float.MaxValue);
                        binHi[b] = new Vector3(float.MinValue);
                    }
                    var scale = Bins / extent;
                    for (var i = start; i < start + count; i++)
                    {
                        var t = _order[i];
                        var b = Math.Min(Bins - 1, (int)((Get(centres[t], a) - Get(cmin, a)) * scale));
                        binCount[b]++;
                        binLo[b] = Vector3.Min(binLo[b], lo[t]);
                        binHi[b] = Vector3.Max(binHi[b], hi[t]);
                    }
                    Span<float> leftCost = stackalloc float[Bins];
                    var accLo = new Vector3(float.MaxValue);
                    var accHi = new Vector3(float.MinValue);
                    var accN = 0;
                    for (var b = 0; b < Bins - 1; b++)
                    {
                        accN += binCount[b];
                        if (binCount[b] > 0)
                        {
                            accLo = Vector3.Min(accLo, binLo[b]);
                            accHi = Vector3.Max(accHi, binHi[b]);
                        }
                        leftCost[b] = accN == 0 ? 0f : accN * Area(accLo, accHi);
                    }
                    accLo = new Vector3(float.MaxValue);
                    accHi = new Vector3(float.MinValue);
                    accN = 0;
                    for (var b = Bins - 1; b > 0; b--)
                    {
                        accN += binCount[b];
                        if (binCount[b] > 0)
                        {
                            accLo = Vector3.Min(accLo, binLo[b]);
                            accHi = Vector3.Max(accHi, binHi[b]);
                        }
                        var cost = leftCost[b - 1] + (accN == 0 ? 0f : accN * Area(accLo, accHi));
                        if (cost < best)
                        {
                            best = cost;
                            axis = a;
                            split = b;
                        }
                    }
                }
            }
            if (axis < 0 && count > 4)
            {
                // No SAH win but too many to keep: halve on the widest centroid axis.
                var e = cmax - cmin;
                axis = e.X >= e.Y && e.X >= e.Z ? 0 : e.Y >= e.Z ? 1 : 2;
                if (Get(e, axis) > 0f)
                    split = Bins / 2;
                else
                    axis = -1;
            }
            if (axis < 0)
            {
                nodes[index] = node;
                continue;
            }
            var origin = Get(cmin, axis);
            var binScale = Bins / (Get(cmax, axis) - origin);
            int lo2 = start, hi2 = start + count - 1;
            while (lo2 <= hi2)
            {
                var t = _order[lo2];
                var b = Math.Min(Bins - 1, (int)((Get(centres[t], axis) - origin) * binScale));
                if (b < split)
                    lo2++;
                else
                {
                    _order[lo2] = _order[hi2];
                    _order[hi2] = t;
                    hi2--;
                }
            }
            var leftCount = lo2 - start;
            if (leftCount == 0 || leftCount == count)
            {
                nodes[index] = node;
                continue;
            }
            var left = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            node.Start = left;
            node.Count = 0;
            nodes[index] = node;
            stack.Push((left + 1, lo2, count - leftCount));
            stack.Push((left, start, leftCount));
        }
        _nodes = [.. nodes];
    }

    private static float Area(Vector3 lo, Vector3 hi)
    {
        var d = hi - lo;
        return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
    }

    private static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>The any-hit test for a candidate on a non-opaque instance: true accepts it.</summary>
    public delegate bool AnyHit(int triangle, float t, float u, float v, bool frontFacing);

    /// <summary>
    /// traceRayEXT's traversal: the closest accepted hit in [tMin, tMax],
    /// or, with TerminateOnFirstHit, the first. Triangle is -1 on a miss.
    /// </summary>
    public Hit Trace(Vector3 origin, Vector3 direction, float tMin, float tMax, uint flags, uint mask, AnyHit? anyHit = null)
    {
        var hit = new Hit { T = tMax, Triangle = -1 };
        if (_tris.Length == 0)
            return hit;
        var inv = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        // Watertight test setup (Woop et al. 2013).
        var kz = MaxDim(Vector3.Abs(direction));
        var kx = (kz + 1) % 3;
        var ky = (kx + 1) % 3;
        if (Get(direction, kz) < 0f)
            (kx, ky) = (ky, kx);
        var sx = Get(direction, kx) / Get(direction, kz);
        var sy = Get(direction, ky) / Get(direction, kz);
        var sz = 1f / Get(direction, kz);
        Span<int> stack = stackalloc int[128];
        var sp = 0;
        stack[sp++] = 0;
        var terminate = (flags & TerminateOnFirstHit) != 0;
        while (sp > 0)
        {
            ref var node = ref _nodes[stack[--sp]];
            if (!Slab(node.Min, node.Max, origin, inv, tMin, hit.T))
                continue;
            if (node.Count == 0)
            {
                var l = node.Start;
                var dl = Entry(_nodes[l], origin, inv);
                var dr = Entry(_nodes[l + 1], origin, inv);
                if (dl <= dr)
                {
                    stack[sp++] = l + 1;
                    stack[sp++] = l;
                }
                else
                {
                    stack[sp++] = l;
                    stack[sp++] = l + 1;
                }
                continue;
            }
            for (var i = node.Start; i < node.Start + node.Count; i++)
            {
                var tri = _order[i];
                var inst = _instances[_tris[tri].Instance];
                if ((inst.Mask & mask) == 0)
                    continue;
                if (!Intersect(_v0[tri], _v1[tri], _v2[tri], origin, kx, ky, kz, sx, sy, sz, out var t, out var u, out var v, out var det))
                    continue;
                if (!(t >= tMin && t <= hit.T))
                    continue;
                // det > 0: counter-clockwise from the origin, front facing.
                var front = det > 0f;
                if (!inst.CullDisable && (flags & CullBackFacing) != 0 && !front)
                    continue;
                if (!inst.CullDisable && (flags & CullFrontFacing) != 0 && front)
                    continue;
                if (!inst.Opaque && (flags & Opaque) == 0 && anyHit != null && !anyHit(i, t, u, v, front))
                    continue;
                hit = new Hit { T = t, U = u, V = v, Triangle = i, FrontFacing = front };
                if (terminate)
                    return hit;
            }
        }
        return hit;
    }

    private static int MaxDim(Vector3 v) => v.X > v.Y ? (v.X > v.Z ? 0 : 2) : (v.Y > v.Z ? 1 : 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Slab(Vector3 lo, Vector3 hi, Vector3 o, Vector3 inv, float tMin, float tMax)
    {
        var t0 = (lo - o) * inv;
        var t1 = (hi - o) * inv;
        var tn = Vector3.Min(t0, t1);
        var tf = Vector3.Max(t0, t1);
        var enter = MathF.Max(MathF.Max(tn.X, tn.Y), MathF.Max(tn.Z, tMin));
        var exit = MathF.Min(MathF.Min(tf.X, tf.Y), MathF.Min(tf.Z, tMax));
        // NaN from 0 * inf (an axis-parallel ray on a slab plane) counts as inside.
        return enter <= exit * 1.0000004f || float.IsNaN(enter) || float.IsNaN(exit);
    }

    private static float Entry(in Node n, Vector3 o, Vector3 inv)
    {
        var t0 = (n.Min - o) * inv;
        var t1 = (n.Max - o) * inv;
        var tn = Vector3.Min(t0, t1);
        return MathF.Max(MathF.Max(tn.X, tn.Y), tn.Z);
    }

    // Watertight ray/triangle: barycentrics u (v1) and v (v2), and the signed determinant.
    private static bool Intersect(Vector3 a, Vector3 b, Vector3 c, Vector3 o, int kx, int ky, int kz, float sx, float sy, float sz,
                                  out float t, out float u, out float v, out float det)
    {
        t = u = v = det = 0f;
        var A = a - o;
        var B = b - o;
        var C = c - o;
        var ax = Get(A, kx) - sx * Get(A, kz);
        var ay = Get(A, ky) - sy * Get(A, kz);
        var bx = Get(B, kx) - sx * Get(B, kz);
        var by = Get(B, ky) - sy * Get(B, kz);
        var cx = Get(C, kx) - sx * Get(C, kz);
        var cy = Get(C, ky) - sy * Get(C, kz);
        var U = cx * by - cy * bx;
        var V = ax * cy - ay * cx;
        var W = bx * ay - by * ax;
        if (U == 0f || V == 0f || W == 0f)
        {
            U = (float)((double)cx * by - (double)cy * bx);
            V = (float)((double)ax * cy - (double)ay * cx);
            W = (float)((double)bx * ay - (double)by * ax);
        }
        if ((U < 0f || V < 0f || W < 0f) && (U > 0f || V > 0f || W > 0f))
            return false;
        det = U + V + W;
        if (det == 0f)
            return false;
        var az = sz * Get(A, kz);
        var bz = sz * Get(B, kz);
        var cz = sz * Get(C, kz);
        var T = U * az + V * bz + W * cz;
        var invDet = 1f / det;
        t = T * invDet;
        u = V * invDet;
        v = W * invDet;
        return t >= 0f;
    }
}
