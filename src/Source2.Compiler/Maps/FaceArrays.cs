using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A .vmap polygon mesh's arrays and the per-face patch data the subdivision
/// bake reads before it splits anything: each corner's patch grid of
/// positions (FUN_1813bda20 then FUN_1813bcc40), moved by displacement
/// (FUN_1813c7b90), and of paint (FUN_1813be320 then FUN_1813b82f0). The same
/// arithmetic as <see cref="MeshTessellation"/>.
/// </summary>
internal sealed class FaceArrays
{
    public readonly int[] Next, To, Opposite, EdgeFace, CornerData, First, VertexData;
    private readonly object?[] positions;
    private readonly int[] levels;
    private readonly bool hasSubdivision;
    public readonly object?[] Displacement;
    private readonly Dictionary<int, int> blockOf = [];
    private readonly object?[] gridPaint;
    private readonly object?[] paintStream;

    public FaceArrays(DmxBinary.Element data)
    {
        Next = Ints(data, "edgeNextIndices");
        To = Ints(data, "edgeVertexIndices");
        Opposite = Ints(data, "edgeOppositeIndices");
        EdgeFace = Ints(data, "edgeFaceIndices");
        CornerData = Ints(data, "edgeVertexDataIndices");
        First = Ints(data, "faceEdgeIndices");
        VertexData = Ints(data, "vertexDataIndices");
        positions = Stream(data.Get<DmxBinary.Element>("vertexData"), "position");
        var subdivision = data.Get<DmxBinary.Element>("subdivisionData");
        levels = subdivision?.Get<object?[]>("subdivisionLevels")?.Select(x => x is int i ? i : 0).ToArray() ?? [];
        hasSubdivision = subdivision != null && levels.Length > 0;
        Displacement = subdivision == null ? [] : Stream(subdivision, "displacement");
        gridPaint = subdivision == null ? [] : Stream(subdivision, "VertexPaintBlendParams");
        if (Displacement.Length > 0 || gridPaint.Length > 0)
        {
            var at = 0;
            for (var d = 0; d < levels.Length; d++)
                if (levels[d] > 0)
                {
                    blockOf[d] = at;
                    var side = (1 << (levels[d] - 1)) + 1;
                    at += side * side;
                }
        }
        paintStream = Stream(data.Get<DmxBinary.Element>("faceVertexData"), "VertexPaintBlendParams");
    }

    public int VertexCount => VertexData.Length;
    public bool WithPaint => paintStream.Length > 0;

    public Vector3 Pos(int v) => (Vector3)positions[VertexData[v]]!;

    public int Level(int h) => hasSubdivision && CornerData[h] >= 0 && CornerData[h] < levels.Length ? levels[CornerData[h]] : 0;

    public Vector4 Paint(int h) => WithPaint && CornerData[h] >= 0 && CornerData[h] < paintStream.Length && paintStream[CornerData[h]] is Vector4 p ? p : Vector4.Zero;

    /// <summary>A face's half-edges from its first.</summary>
    public List<int> Loop(int f)
    {
        var loop = new List<int>();
        var e = First[f];
        do
        {
            loop.Add(e);
            e = Next[e];
        } while (e != First[f] && loop.Count <= Next.Length);
        return loop;
    }

    /// <summary>Corner i's patch: n + 1 points a side, row r towards the previous side's midpoint, column c towards the next's.</summary>
    public Vector3[] Grid(List<int> hs, int i, int own)
    {
        var m = hs.Count;
        var inv = 1f / m;
        float cx = 0f, cy = 0f, cz = 0f;
        for (var k = 0; k < m; k++)
        {
            var v = Pos(To[hs[(i + k) % m]]);
            cx = (inv * v.X) + cx;
            cy = (v.Y * inv) + cy;
            cz = (v.Z * inv) + cz;
        }
        var p0 = Pos(To[hs[i]]);
        var pn = Pos(To[hs[(i + 1) % m]]);
        var pp = Pos(To[hs[(i + m - 1) % m]]);
        var c1 = new Vector3((p0.X + pn.X) * 0.5f, (pn.Y + p0.Y) * 0.5f, (pn.Z + p0.Z) * 0.5f);
        var c2 = new Vector3((pp.X + p0.X) * 0.5f, (pp.Y + p0.Y) * 0.5f, (pp.Z + p0.Z) * 0.5f);
        var c3 = new Vector3(cx, cy, cz);
        var n = 1 << (own - 1);
        var g = n + 1;
        var step = 1f / n;
        var points = new Vector3[g * g];
        for (var r = 0; r <= n; r++)
        {
            var v = r * step;
            var ax = ((c2.X - p0.X) * v) + p0.X;
            var ay = ((c2.Y - p0.Y) * v) + p0.Y;
            var az = ((c2.Z - p0.Z) * v) + p0.Z;
            var bz = ((c3.Z - c1.Z) * v) + c1.Z;
            var by = ((c3.Y - c1.Y) * v) + c1.Y;
            var dx = (((c3.X - c1.X) * v) + c1.X) - ax;
            for (var c = 0; c <= n; c++)
            {
                var u = c * step;
                points[(r * g) + c] = new Vector3((dx * u) + ax, ((by - ay) * u) + ay, ((bz - az) * u) + az);
            }
        }
        return points;
    }

    /// <summary>Each patch point moved by its displacement in the patch frame (columns B, T, N).</summary>
    public void Displace(Vector3[] grid, List<int> hs, int i, int face)
    {
        var m = hs.Count;
        var loopPoints = new Vector3[m];
        float cx = 0f, cy = 0f, cz = 0f;
        var inv = 1f / m;
        for (var k = 0; k < m; k++)
        {
            var v = loopPoints[k] = Pos(To[hs[(i + k) % m]]);
            cx = (inv * v.X) + cx;
            cy = (v.Y * inv) + cy;
            cz = (v.Z * inv) + cz;
        }
        var n = PolygonTriangulator.Newell(loopPoints);
        var e1 = First[face];
        var a = Pos(To[e1]);
        var b = Pos(To[Next[e1]]);
        var dx = cx - ((b.X + a.X) * 0.5f);
        var dy = cy - ((a.Y + b.Y) * 0.5f);
        var dz = cz - ((b.Z + a.Z) * 0.5f);
        var t = Unit(new Vector3((dz * n.Y) - (dy * n.Z), (dx * n.Z) - (dz * n.X), (dy * n.X) - (dx * n.Y)));
        var bt = Unit(new Vector3((t.Y * n.Z) - (t.Z * n.Y), (t.Z * n.X) - (n.Z * t.X), (n.Y * t.X) - (t.Y * n.X)));
        var start = blockOf[CornerData[hs[i]]];
        for (var k = 0; k < grid.Length; k++)
        {
            var d = (Vector3)Displacement[start + k]!;
            var rx = (d.Z * n.X) + ((d.Y * t.X) + (d.X * bt.X));
            var ry = (d.Z * n.Y) + ((d.Y * t.Y) + (d.X * bt.Y));
            var rz = (d.Z * n.Z) + ((d.Y * t.Z) + (d.X * bt.Z));
            grid[k] = new Vector3(rx + grid[k].X, ry + grid[k].Y, rz + grid[k].Z);
        }
    }

    /// <summary>Corner i's patch of paint: the stored grid when there is one, else the corner, side midpoints and mean lerped.</summary>
    public Vector4[] PaintGrid(List<int> hs, int i, int own)
    {
        if (gridPaint.Length > 0)
        {
            var side = (1 << (own - 1)) + 1;
            var start = blockOf[CornerData[hs[i]]];
            var stored = new Vector4[side * side];
            for (var k = 0; k < stored.Length; k++)
                stored[k] = (Vector4)gridPaint[start + k]!;
            return stored;
        }
        var m = hs.Count;
        var inv = 1f / m;
        var mean = Vector4.Zero;
        for (var k = 0; k < m; k++)
            mean = MulAdd(Paint(hs[(i + k) % m]), inv, mean);
        var s0 = Paint(hs[i]);
        var s1 = HalfEdgeMesh.Lerp(s0, Paint(hs[(i + 1) % m]), 0.5f);
        var s2 = HalfEdgeMesh.Lerp(s0, Paint(hs[(i + m - 1) % m]), 0.5f);
        var n = 1 << (own - 1);
        var g = n + 1;
        var step = 1f / n;
        var points = new Vector4[g * g];
        for (var r = 0; r <= n; r++)
        {
            var v = r * step;
            var left = HalfEdgeMesh.Lerp(s0, s2, v);
            var right = HalfEdgeMesh.Lerp(s1, mean, v);
            for (var c = 0; c <= n; c++)
                points[(r * g) + c] = HalfEdgeMesh.Lerp(left, right, c * step);
        }
        return points;
    }

    private static Vector4 MulAdd(Vector4 value, float w, Vector4 sum)
        => new((value.X * w) + sum.X, (value.Y * w) + sum.Y, (value.Z * w) + sum.Z, (value.W * w) + sum.W);

    private static Vector3 Unit(Vector3 v)
    {
        var length = MathF.Sqrt(((v.Z * v.Z) + (v.Y * v.Y)) + (v.X * v.X));
        if (length == 0f)
            return Vector3.Zero;
        if (length < 1e-17f || 1e17f < length)
            throw new NotSupportedException("a patch frame of extreme length");
        var inv = 1f / length;
        return new Vector3(v.X * inv, v.Y * inv, v.Z * inv);
    }

    private static int[] Ints(DmxBinary.Element data, string name)
        => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();

    private static object?[] Stream(DmxBinary.Element? holder, string name)
        => holder?.GetElements("streams").FirstOrDefault(s => s.Name.Split(':')[0] == name)?.Get<object?[]>("data") ?? [];
}
