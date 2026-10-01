using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The half-edge mesh resourcecompiler bakes subdivision on (CPolygonMesh over
/// CHalfEdgeMesh, 0923), kept as Valve keeps it: each element kind in a dense
/// array with a handle table. A new element goes at the end of the dense
/// array and takes the handle at the head of the free list, or a new one
/// (181375e20); a removed element's slot takes the last element and its
/// handle goes to the free list's tail (1813995a0). The dense face order is
/// what the export walks, so every operation keeps these rules.
/// </summary>
internal sealed class HalfEdgeMesh
{
    public const int Null = -1;

    /// <summary>A dense array with a FIFO handle free list.</summary>
    public sealed class Container<T> where T : class
    {
        private readonly List<T> dense = [];
        private readonly List<int> handleOfDense = [];
        private readonly List<int> denseOfHandle = [];
        private readonly List<int> nextFree = [];
        private int freeHead = Null, freeTail = Null;

        public int Count => dense.Count;

        public int Add(T item)
        {
            int handle;
            if (freeHead != Null)
            {
                handle = freeHead;
                freeHead = nextFree[handle];
                if (freeHead == Null)
                    freeTail = Null;
            }
            else
            {
                handle = denseOfHandle.Count;
                denseOfHandle.Add(Null);
                nextFree.Add(Null);
            }
            nextFree[handle] = Null;
            denseOfHandle[handle] = dense.Count;
            dense.Add(item);
            handleOfDense.Add(handle);
            return handle;
        }

        public void Remove(int handle)
        {
            var at = denseOfHandle[handle];
            var last = dense.Count - 1;
            if (at != last)
            {
                dense[at] = dense[last];
                handleOfDense[at] = handleOfDense[last];
                denseOfHandle[handleOfDense[at]] = at;
            }
            dense.RemoveAt(last);
            handleOfDense.RemoveAt(last);
            denseOfHandle[handle] = Null;
            if (freeTail == Null)
                freeHead = handle;
            else
                nextFree[freeTail] = handle;
            freeTail = handle;
            nextFree[handle] = Null;
        }

        public T this[int handle] => dense[denseOfHandle[handle]];

        public bool Alive(int handle) => handle >= 0 && handle < denseOfHandle.Count && denseOfHandle[handle] != Null;

        /// <summary>Handles in dense order.</summary>
        public IEnumerable<int> Handles => handleOfDense;
    }

    public sealed class Vertex
    {
        public int Out = Null;
        public Vector3 Position;
        /// <summary>When a patch grid last wrote this vertex (-1 for never).</summary>
        public long Written = -1;
    }

    public sealed class HalfEdge
    {
        /// <summary>The vertex this half-edge points to.</summary>
        public int Vertex = Null;
        public int Twin = Null;
        public int Next = Null;
        public int Face = Null;
        /// <summary>The paint of the corner this half-edge ends at, in its face.</summary>
        public Vector4 Paint;
        /// <summary>The corner's other face-vertex streams, carried as Paint is (null when not asked for).</summary>
        public float[]? Data;
        /// <summary>The edge's flags (edgeData "flags": bit 0 hard, bit 1 soft), the same on both halves.</summary>
        public int Flags;
        /// <summary>Made by AddEdge (the bake's new edges, which it marks soft).</summary>
        public bool Added;
    }

    public sealed class Face
    {
        public int First = Null;
        /// <summary>The .vmap face it was cut from (material, face set).</summary>
        public int Source;
    }

    public readonly Container<Vertex> Vertices = new();
    public readonly Container<HalfEdge> HalfEdges = new();
    public readonly Container<Face> Faces = new();

    public HalfEdge He(int h) => HalfEdges[h];

    /// <summary>The half-edges of a face, from its first.</summary>
    public IEnumerable<int> Loop(int face)
    {
        var first = Faces[face].First;
        var h = first;
        do
        {
            yield return h;
            h = He(h).Next;
        }
        while (h != first);
    }

    /// <summary>The half-edge before <paramref name="h"/> in its loop.</summary>
    public int Previous(int h)
    {
        var p = h;
        while (He(p).Next != h)
            p = He(p).Next;
        return p;
    }

    /// <summary>1813937a0: the half-edge from vertex a to vertex b, circling a from its outgoing half-edge.</summary>
    public int Between(int a, int b)
    {
        var start = Vertices[a].Out;
        if (start == Null)
            return Null;
        var h = start;
        do
        {
            if (He(h).Vertex == b)
                return h;
            h = He(He(h).Twin).Next;
        }
        while (h != start);
        return Null;
    }

    /// <summary>18138be80: the half-edge of <paramref name="face"/> that ends at <paramref name="v"/>.</summary>
    public int Corner(int face, int v)
    {
        var start = Vertices[v].Out;
        var h = start;
        do
        {
            var t = He(h).Twin;
            if (He(t).Face == face)
                return t;
            h = He(t).Next;
        }
        while (h != start);
        return Null;
    }

    /// <summary>18137a900: a twin pair, the first allocated first.</summary>
    public (int A, int B) NewPair()
    {
        var a = HalfEdges.Add(new HalfEdge());
        var b = HalfEdges.Add(new HalfEdge());
        He(a).Twin = b;
        He(b).Twin = a;
        return (a, b);
    }

    /// <summary>
    /// 181379a40: h (A to B) now ends at a new vertex M; a new half-edge M to B
    /// follows it in h's face, and on the twin's side a new B to M goes before
    /// the twin. Faces keep their first half-edges.
    /// </summary>
    public int SplitEdge(int h)
    {
        var t = He(h).Twin;
        var prevT = Previous(t);
        var prevH = Previous(h);
        var b = He(h).Vertex;
        var (n1, n2) = NewPair();
        var m = Vertices.Add(new Vertex());
        He(n1).Vertex = b;
        He(n1).Next = He(h).Next;
        He(n1).Face = He(h).Face;
        Vertices[m].Out = n1;
        He(n2).Vertex = m;
        He(n2).Next = t;
        He(n2).Face = He(t).Face;
        Vertices[b].Out = n2;
        He(h).Vertex = m;
        He(h).Next = n1;
        He(prevT).Next = n2;
        // Corners: n1 takes h's old corner at B; n2 sits beside the twin's
        // corner at A. SplitCorners lerps the two corners at M.
        He(n1).Paint = He(h).Paint;
        He(n1).Data = He(h).Data;
        He(n1).Flags = He(n2).Flags = He(h).Flags;
        He(n1).Added = He(n2).Added = He(h).Added;
        splitSides = (prevH, h, n1, t, n2, prevT);
        return m;
    }

    private (int PrevH, int H, int N1, int T, int N2, int PrevT) splitSides;

    /// <summary>
    /// 181376810: an edge between the ends of corners <paramref name="ca"/>
    /// (ending at a) and <paramref name="cb"/> (ending at b) of one face. The
    /// face keeps the loop through the new a-to-b half-edge, which becomes its
    /// first; a new face (appended) takes the loop through b-to-a.
    /// </summary>
    public int AddEdge(int ca, int cb)
    {
        var face = He(ca).Face;
        var (n1, n2) = NewPair();
        He(n1).Vertex = He(cb).Vertex;
        He(n2).Vertex = He(ca).Vertex;
        He(n1).Next = He(cb).Next;
        He(n2).Next = He(ca).Next;
        He(n1).Paint = He(cb).Paint;
        He(n2).Paint = He(ca).Paint;
        He(n1).Data = He(cb).Data;
        He(n2).Data = He(ca).Data;
        He(n1).Added = He(n2).Added = true;
        He(ca).Next = n1;
        He(cb).Next = n2;
        He(n1).Face = face;
        Faces[face].First = n1;
        var added = Faces.Add(new Face { First = n2, Source = Faces[face].Source });
        var h = n2;
        do
        {
            He(h).Face = added;
            h = He(h).Next;
        }
        while (h != n2);
        return added;
    }

    /// <summary>1813a5740 with 1813a4a00: (b - a) * t + a, each axis.</summary>
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        => new(((b.X - a.X) * t) + a.X, ((b.Y - a.Y) * t) + a.Y, ((b.Z - a.Z) * t) + a.Z);

    public static Vector4 Lerp(Vector4 a, Vector4 b, float t)
        => new(((b.X - a.X) * t) + a.X, ((b.Y - a.Y) * t) + a.Y, ((b.Z - a.Z) * t) + a.Z, ((b.W - a.W) * t) + a.W);

    public static float[]? Lerp(float[]? a, float[]? b, float t)
        => a == null || b == null ? a ?? b : [.. a.Select((x, i) => ((b[i] - x) * t) + x)];

    /// <summary>18137a050: a vertex on edge a-b at t from a.</summary>
    public int AddVertexToEdge(int a, int b, float t)
    {
        var h = Between(a, b);
        if (h == Null)
            return Null;
        var m = SplitEdge(h);
        Vertices[m].Position = Lerp(Vertices[a].Position, Vertices[b].Position, t);
        // 1813a54d0 on each side: the corner at M lerps that face's corners at a and b.
        var (prevH, hh, n1, tt, n2, prevT) = splitSides;
        He(hh).Paint = Lerp(He(prevH).Paint, He(n1).Paint, t);
        He(n2).Paint = Lerp(He(tt).Paint, He(prevT).Paint, t);
        He(hh).Data = Lerp(He(prevH).Data, He(n1).Data, t);
        He(n2).Data = Lerp(He(tt).Data, He(prevT).Data, t);
        return m;
    }

    /// <summary>
    /// 1813ba570: the point at <paramref name="t"/> of the way along face
    /// <paramref name="face"/>'s boundary from a to b, by arc length. Within
    /// the sub-edge it falls on, a local t under 0.01 reuses the sub-edge's
    /// start vertex, over 0.99 its end, else a vertex is added there.
    /// </summary>
    public int SplitBetween(int face, int a, int b, float t)
    {
        var start = Corner(face, a);
        var stop = Corner(face, b);
        if (start == Null || stop == Null)
            return Null;
        float total = 0f;
        var count = 0;
        for (var h = start; h != stop; h = He(h).Next)
        {
            total += Length(He(h).Vertex, He(He(h).Next).Vertex);
            count++;
        }
        var span = total <= 0f ? count : total;
        float along = 0f;
        for (var h = start; ; h = He(h).Next)
        {
            if (h == stop)
                return Null;
            var u = He(h).Vertex;
            var w = He(He(h).Next).Vertex;
            var len = Length(u, w);
            float local;
            if (0f < total)
            {
                if (!(0f < len))
                {
                    along += len;
                    continue;
                }
            }
            else
            {
                len = 1f;
            }
            local = ((span * t) - along) / len;
            if (local <= 1f)
            {
                if (local < 0.01f)
                    return u;
                if (local > 0.99f)
                    return w;
                return AddVertexToEdge(u, w, local);
            }
            along += len;
        }
    }

    // The sub-edge length the split sums: z, y then x squared, square root.
    private float Length(int u, int w)
    {
        var p = Vertices[u].Position;
        var q = Vertices[w].Position;
        float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return MathF.Sqrt(((dz * dz) + (dy * dy)) + (dx * dx));
    }
}
