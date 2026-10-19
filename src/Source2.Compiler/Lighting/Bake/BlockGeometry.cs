using System.Buffers.Binary;
using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// lightmap_load_block_gpu's "Clip packing geometry to block (CPU)" (18002d530):
/// the triangles the block's two sampling passes draw.
/// <list type="number">
/// <item>Every triangle in the cordon (ComputeLightmapPackingGeometryCordonMask,
/// 180045400: its world box overlaps the cordon) whose lightmap uv box overlaps
/// the block becomes a polygon of three vertices: position, uv and the packed
/// tangent frame (<see cref="PackedFrame.Pack"/>, the tangent normalised with
/// w its sign); zero-area triangles are dropped.</item>
/// <item>std::partition (18003d8e0) puts the polygons of large charts (chart
/// flags &amp; 0x30000000 clear) first; those stay whole.</item>
/// <item>Each small-chart polygon is split at the next 8 x 8 tile line after
/// its uv minimum (x first, then y; epsilon 2^-16) until it fits one tile:
/// vertices within the epsilon of the line go to both halves, crossing edges
/// get a vertex interpolated by d0 / (d0 - d1), its frame unpacked, lerped,
/// normalised and repacked. The split coordinate is snapped to the line;
/// when the polygon also crossed a y line, its v is set to the same value
/// (vrad3 does this; the x line's value lands in v).</item>
/// <item>Each small-chart polygon's tile is the one under its uv centroid
/// (0x7fffffff outside the block); they are sorted by tile and the outside
/// ones dropped.</item>
/// <item>All polygons are fanned (v0, vk, vk+1) into the vertex buffer
/// (PackingGeometryVertices, 24 bytes a vertex), with the chart of each
/// triangle (ChartIDs), its world area (TriangleArea) and, for the small
/// charts, the per-tile first triangle (the tile list for each
/// 8 x 8 thread group).</item>
/// </list>
/// The tile sort is MSVC's std::sort move for move (<see cref="Maps.MsvcSort"/>), so triangles within a
/// tile keep vrad3's (unstable) order.
/// </summary>
public sealed class BlockGeometry
{
    public readonly record struct Vertex(Vector3 Position, Vector2 Uv, uint Frame);

    /// <summary>Three vertices a triangle: the large charts' whole triangles, then the small charts' clipped ones by tile.</summary>
    public required Vertex[] Vertices { get; init; }

    /// <summary>Each triangle's chart.</summary>
    public required uint[] Charts { get; init; }

    /// <summary>The first small-chart triangle (the large charts' triangles come before it).</summary>
    public required int SmallStart { get; init; }

    /// <summary>Each triangle's world area (the sampler reads the small charts').</summary>
    public required float[] Areas { get; init; }

    /// <summary>Per 8 x 8 tile (row major over the block), its first triangle; one more entry ends the last.</summary>
    public required int[] TileStart { get; init; }

    public int TilesX { get; init; }

    public int TriangleCount => Charts.Length;

    private const float Eps = 1.5258789e-05f;

    private sealed class Polygon
    {
        public List<Vertex> Vertices = [];
        public uint Chart;
        public int Tile;
    }

    public static BlockGeometry Build(PackingGeometry packing, int width, int height, int bx, int by, int bw, int bh)
    {
        float fw = width, fh = height;
        var polys = new List<Polygon>();
        var stride = packing.VertexStride;
        var bytes = packing.Vertices;
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o));
        Vertex Load(uint index)
        {
            var o = (int)index * stride;
            var p = new Vector3(F(o), F(o + 4), F(o + 8));
            var n = new Vector3(F(o + 12), F(o + 16), F(o + 20));
            var t = new Vector4(F(o + 24), F(o + 28), F(o + 32), F(o + 36));
            var t3 = new Vector3(t.X, t.Y, t.Z);
            (t3, _) = PathTraceBounds.Normalize(t3);
            var tangent = new Vector4(t3, t.W < 0f ? -1f : 1f);
            return new Vertex(p, new Vector2(packing.AtlasUv[index * 2], packing.AtlasUv[index * 2 + 1]), PackedFrame.Pack(n, tangent));
        }
        var tris = packing.Indices.Length / 3;
        for (var t = 0; t < tris; t++)
        {
            var chart = packing.TriangleCharts[t];
            var i0 = packing.Indices[t * 3];
            var i1 = packing.Indices[t * 3 + 1];
            var i2 = packing.Indices[t * 3 + 2];
            var u0 = new Vector2(packing.AtlasUv[i0 * 2], packing.AtlasUv[i0 * 2 + 1]);
            var u1 = new Vector2(packing.AtlasUv[i1 * 2], packing.AtlasUv[i1 * 2 + 1]);
            var u2 = new Vector2(packing.AtlasUv[i2 * 2], packing.AtlasUv[i2 * 2 + 1]);
            var min = Vector2.Min(u0, Vector2.Min(u1, u2));
            var max = Vector2.Max(u0, Vector2.Max(u1, u2));
            if (!(bx / fw <= max.X && min.X <= (bx + bw) / fw && by / fh <= max.Y && min.Y <= (by + bh) / fh))
                continue;
            var poly = new Polygon { Chart = chart, Vertices = [Load(i0), Load(i1), Load(i2)] };
            if (TriangleArea(poly.Vertices[0].Position, poly.Vertices[1].Position, poly.Vertices[2].Position) != 0f)
                polys.Add(poly);
        }
        bool Small(Polygon p) => (packing.Charts[(int)p.Chart].Flags & 0x30000000) != 0;
        // std::partition: from the left the first small one, from the right the last large one, swapped.
        int first = 0, last = polys.Count;
        while (true)
        {
            while (first != last && !Small(polys[first]))
                first++;
            if (first == last)
                break;
            do
                last--;
            while (first != last && Small(polys[last]));
            if (first == last)
                break;
            (polys[first], polys[last]) = (polys[last], polys[first]);
            first++;
        }
        var smallStart = first;
        float tx8 = fw * 0.125f, ty8 = fh * 0.125f, ox = -bx * 0.125f, oy = -by * 0.125f;
        for (var i = smallStart; i < polys.Count; i++)
        {
            var poly = polys[i];
            var v = poly.Vertices;
            var lo = new Vector2(float.MaxValue);
            var hi = new Vector2(-float.MaxValue);
            foreach (var x in v)
            {
                lo = Vector2.Min(lo, x.Uv);
                hi = Vector2.Max(hi, x.Uv);
            }
            var maxV = hi.Y - Eps;
            var maxU = hi.X - Eps;
            var tileV = MathF.Floor(oy + (lo.Y + Eps) * ty8);
            var tileU = MathF.Floor(ox + (lo.X + Eps) * tx8);
            var lineV = (tileV + 1f - oy) / ty8;
            var lineU = (tileU + 1f - ox) / tx8;
            var splitU = lineU < maxU;
            var splitV = lineV < maxV;
            if (!splitU && !splitV)
                continue;
            var (nx, ny, d) = splitU ? (1f, 0f, lineU) : (0f, 1f, lineV);
            var n = v.Count;
            var dist = new float[n + 1];
            var side = new int[n + 1];
            for (var k = 0; k < n; k++)
            {
                dist[k] = ny * v[k].Uv.Y + nx * v[k].Uv.X - d;
                side[k] = dist[k] <= Eps ? (-Eps <= dist[k] ? 2 : 1) : 0;
            }
            dist[n] = dist[0];
            side[n] = side[0];
            var a = new List<Vertex>();
            var b = new List<Vertex>();
            for (var k = 0; k < n; k++)
            {
                var next = (k + 1) % n;
                if (side[k] == 2)
                {
                    a.Add(v[k]);
                    b.Add(v[k]);
                    continue;
                }
                if (side[k] == 0)
                    a.Add(v[k]);
                else
                    b.Add(v[k]);
                if (side[k + 1] == 2 || side[k + 1] == side[k])
                    continue;
                var t = dist[k] / (dist[k] - dist[k + 1]);
                var s = 1f - t;
                var c = v[k];
                var e = v[next];
                var p = new Vector3(t * e.Position.X + s * c.Position.X, t * e.Position.Y + s * c.Position.Y, t * e.Position.Z + s * c.Position.Z);
                var uv = new Vector2(s * c.Uv.X + t * e.Uv.X, s * c.Uv.Y + t * e.Uv.Y);
                if (splitU)
                    uv.X = d;
                if (splitV)
                    uv.Y = d;
                var (cn, ct) = PackedFrame.Unpack(c.Frame);
                var (en, et) = PackedFrame.Unpack(e.Frame);
                var ln = en * t + cn * s;
                (ln, _) = PathTraceBounds.Normalize(ln);
                var lt = new Vector3(ct.X * s + et.X * t, ct.Y * s + et.Y * t, ct.Z * s + et.Z * t);
                (lt, _) = PathTraceBounds.Normalize(lt);
                var w = ct.W * s + et.W * t < 0f ? -1f : 1f;
                var mid = new Vertex(p, uv, PackedFrame.Pack(ln, new Vector4(lt, w)));
                a.Add(mid);
                b.Add(mid);
            }
            poly.Vertices = a;
            polys.Add(new Polygon { Chart = poly.Chart, Vertices = b });
            i--;
        }
        var tilesX = (bw + 7) >> 3;
        var tilesY = (bh + 7) >> 3;
        for (var i = smallStart; i < polys.Count; i++)
        {
            var p = polys[i];
            float su = 0f, sv = 0f;
            foreach (var x in p.Vertices)
            {
                su += x.Uv.X;
                sv += x.Uv.Y;
            }
            var inv = 1f / p.Vertices.Count;
            var cv = ty8 * sv * inv;
            var tu = (int)MathF.Floor(ox + tx8 * su * inv);
            var tv = (int)MathF.Floor(oy + cv);
            p.Tile = tu >= 0 && tu < tilesX && tv >= 0 && tv < tilesY ? tilesX * tv + tu : int.MaxValue;
        }
        // std::sort by tile (std_Sort_32ByteElements, MSVC's introsort: not stable), then the trailing
        // polygons outside the block dropped.
        Maps.MsvcSort.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(polys)[smallStart..], (x, y) => x.Tile < y.Tile);
        while (polys.Count > smallStart && polys[^1].Tile == int.MaxValue)
            polys.RemoveAt(polys.Count - 1);
        var vertices = new List<Vertex>();
        var charts = new List<uint>();
        var areas = new List<float>();
        var tileStart = new int[tilesX * tilesY + 1];
        var smallTriangles = 0;
        for (var i = 0; i < polys.Count; i++)
        {
            var p = polys[i];
            var v = p.Vertices;
            if (i == smallStart)
                smallTriangles = charts.Count;
            for (var k = 1; k < v.Count - 1; k++)
            {
                var area = TriangleArea(v[0].Position, v[k].Position, v[k + 1].Position);
                vertices.Add(v[0]);
                vertices.Add(v[k]);
                vertices.Add(v[k + 1]);
                charts.Add(p.Chart);
                areas.Add(area);
            }
        }
        if (smallStart == polys.Count)
            smallTriangles = charts.Count;
        // The tile lists: for each tile the first triangle at or after it (empty tiles take the next one's start).
        var tri = smallTriangles;
        var tile = 0;
        for (var i = smallStart; i < polys.Count; i++)
        {
            while (tile <= polys[i].Tile)
                tileStart[tile++] = tri;
            tri += Math.Max(0, polys[i].Vertices.Count - 2);
        }
        while (tile < tileStart.Length)
            tileStart[tile++] = tri;
        return new BlockGeometry
        {
            Vertices = [.. vertices], Charts = [.. charts], Areas = [.. areas], SmallStart = smallTriangles, TileStart = tileStart, TilesX = tilesX,
        };
    }

    /// <summary>TriangleArea (1801f5110): half the cross product's length, vrad3's operation order.</summary>
    public static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        var y = (c.X - a.X) * (b.Z - a.Z) - (c.Z - a.Z) * (b.X - a.X);
        var x = (c.Z - a.Z) * (b.Y - a.Y) - (c.Y - a.Y) * (b.Z - a.Z);
        var z = (c.Y - a.Y) * (b.X - a.X) - (c.X - a.X) * (b.Y - a.Y);
        return MathF.Sqrt(z * z + y * y + x * x) * 0.5f;
    }
}

/// <summary>
/// The packed tangent frame's decodes (32 bits: bit 0 the tangent sign, bits
/// 1-11 the tangent's angle about the normal over 2047 turns, bits 12-21 and
/// 22-31 the octahedral normal). vrad3's PackTangentFrame (1801f5970, the
/// unsearched encode) is resourcecompiler's CompressTangentFrame,
/// <see cref="Maps.TangentFrame.Compress"/>.
/// </summary>
public static class PackedFrame
{
    public static uint Pack(Vector3 n, Vector4 t) => Maps.TangentFrame.Compress(n, t);

    /// <summary>UnpackTangentFrame (1801f5680): the normal and the tangent (w the sign).</summary>
    public static (Vector3 Normal, Vector4 Tangent) Unpack(uint frame)
    {
        var n = Decode(frame >> 12 & 0x3ff, frame >> 22);
        var (b1, _) = Maps.TangentFrame.Basis(n);
        var a = (frame >> 1 & 0x7ff) / 2047f * 6.2831855f;
        var (s, c) = MathF.SinCos(a);
        var t = new Vector3((n.Y * b1.Z - b1.Y * n.Z) * s + b1.X * c, (n.Z * b1.X - b1.Z * n.X) * s + b1.Y * c, (b1.Y * n.X - n.Y * b1.X) * s + b1.Z * c);
        return (n, new Vector4(t, (frame & 1) != 0 ? 1f : -1f));
    }

    /// <summary>The shaders' decode (lightmap_rasterize, the small-chart sampler): normal, tangent and sign.</summary>
    public static (Vector3 Normal, Vector4 Tangent) UnpackGpu(uint frame)
    {
        var x = (frame >> 12 & 1023u) * 0.0019550342f - 1f;
        var y = (frame >> 22 & 1023u) * 0.0019550342f - 1f;
        var z = 1f - MathF.Abs(x) - MathF.Abs(y);
        var t = Math.Clamp(-z, 0f, 1f);
        x += x >= 0f ? -t : t;
        y += y >= 0f ? -t : t;
        var n = Vector3.Normalize(new Vector3(x, y, z));
        var s = n.Z >= 0f ? 1f : -1f;
        var k = -1f / (s + n.Z);
        var b1 = new Vector3(1f + s * n.X * n.X * k, s * (n.X * n.Y * k), -s * n.X);
        var a = (frame >> 1 & 2047u) * 0.0030694604f;
        var tan = b1 * MathF.Cos(a) + Vector3.Cross(n, b1) * MathF.Sin(a);
        return (n, new Vector4(tan, (frame & 1) == 0 ? -1f : 1f));
    }

    // The host decode: octahedral, folded below the equator, normalised (1801f51e0's slow path not modelled).
    private static Vector3 Decode(uint qx, uint qy)
    {
        var x = qx / 1023f;
        var y = qy / 1023f;
        x = x + x - 1f;
        y = y + y - 1f;
        var ax = MathF.Abs(x);
        var z = 1f - (ax + MathF.Abs(y));
        if (z < 0f)
        {
            x = (1f - MathF.Abs(y)) * (0f <= x ? 1f : -1f);
            y = (1f - ax) * (0f <= y ? 1f : -1f);
        }
        var len = MathF.Sqrt(y * y + z * z + x * x);
        if (len < 1e-17f || 1e17f < len)
            return len == 0f ? Vector3.Zero : Vector3.Normalize(new Vector3(x, y, z));
        var inv = 1f / len;
        return new Vector3(x * inv, y * inv, inv * z);
    }
}
