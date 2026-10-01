using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A map mesh's faces as triangles, the way the compile's mesh library cuts
/// them for physics (FUN_18139c100 -> FUN_18139c1f0): every triangle corner
/// gets a position, and equal positions are then welded into one vertex, in
/// the order they first appear (FUN_1813858d0).
/// </summary>
/// <remarks>
/// <para>A face with no subdivision level is a polygon: its corners in loop
/// order, and on each edge whose neighbour is subdivided the points that
/// neighbour puts there (2^level of them per edge, lerped), all cut by
/// <see cref="PolygonTriangulator"/>.</para>
/// <para>A subdivided face (meshData.subdivisionData, a level per face corner)
/// splits into one quad patch per corner (FUN_1813bda20: the corner, the two
/// edge midpoints, the face centre), each a bilinear grid of 2^(level-1)
/// cells a side (FUN_1813bcc40), triangulated with its outer edges stitched
/// to a finer neighbour (FUN_1813c26a0).</para>
/// <para>Displacement moves each patch point along the patch frame.</para>
/// </remarks>
public static class MeshTessellation
{
    /// <summary>The welded vertices, three indices a triangle, and each triangle's face.</summary>
    public sealed record Result(List<Vector3> Positions, List<int> Indices, List<int> Faces)
    {
        /// <summary>
        /// Builder order only, for a mesh with a VertexPaintBlendParams stream:
        /// each triangle corner's paint, index for index with <c>Indices</c>.
        /// </summary>
        public List<Vector4>? Paint { get; init; }

        /// <summary>Builder order only: when each position was last written by a patch grid (-1 for none).</summary>
        public List<long>? Written { get; init; }

        /// <summary>The half-edge bake only, when asked: each triangle corner's face-vertex streams, index for index with <c>Indices</c>.</summary>
        public List<float[]>? CornerData { get; init; }
    }

    public static Result Triangulate(DmxBinary.Element data) => Build(data, false).ToResult();

    /// <summary>
    /// The mesh as the editor's ray scene takes it (RayScene_AddFace,
    /// resourcecompiler 0923: 1813ccd20): face by face, unwelded, three
    /// corners a triangle, with each triangle's face.
    /// <list type="bullet">
    /// <item>A face whose first corner has no level is the polygon
    /// <see cref="Triangulate"/> cuts, with the points subdivided neighbours
    /// put on its edges.</item>
    /// <item>A subdivided face is one patch per corner from its first: the
    /// displaced grid of the corner's own level (FUN_1813c7b90), each cell,
    /// row by row, as [a, (r, c+1), b] then [a, b, (r+1, c)], a being (r, c)
    /// and b (r+1, c+1) (FUN_1813c3b60). Nothing is stitched to a finer
    /// neighbour, and a level above 5 gives no triangles (the 17 x 17 grid
    /// buffer).</item>
    /// </list>
    /// </summary>
    public static (List<Vector3> Corners, List<int> Faces) RayScene(DmxBinary.Element data)
    {
        var built = Build(data, false, rayScene: true);
        return (built.Positions, built.Faces);
    }

    /// <summary>
    /// The mesh as the map builder bakes it for export (BakeSubdivisionForFaces,
    /// resourcecompiler 0923: 1813baa40), in the order its faces end up: the
    /// same triangles as <see cref="Triangulate"/>, ordered by the builder's
    /// dense face array (measured against atixref's exported meshes).
    /// <list type="bullet">
    /// <item>Splitting a face (1813ca560) puts its last corner's patch in the
    /// face's slot and appends the other patches in corner order.</item>
    /// <item>Each patch splits the same way: the child at its fourth corner
    /// keeps the slot, the first three are appended, and the children recurse
    /// in corner order 0, 1, 3, 2.</item>
    /// <item>Each leaf cell, patch by patch and row by row, is cut on its
    /// diagonal: the cell keeps [b, (r+1, c), a] and the builder appends
    /// [a, (r, c+1), b], where a is grid (r, c) and b is grid (r+1, c+1).</item>
    /// </list>
    /// The result is each face's own slot in face order, then the appended
    /// faces. A face stitched to a finer neighbour is not covered: it keeps
    /// <see cref="Triangulate"/>'s cut in its slot, and <c>Covered</c> is false.
    /// </summary>
    /// <remarks>
    /// With a VertexPaintBlendParams stream the corners carry paint too, as
    /// the bake sets it (1813baa40 with FUN_1813b82f0): a subdivided patch
    /// takes the paint stored per grid point in subdivisionData when there is
    /// such a stream; otherwise its four corner
    /// values are the face corner, the midpoints towards the next and previous
    /// corners ((b - a) * 0.5 + a, FUN_181046990) and the face mean
    /// (FUN_1813be320: each corner times 1/m added in loop order from this
    /// corner), and each grid point lerps them the way the positions are
    /// lerped. Points a subdivided neighbour puts on a polygon face's edge get
    /// nested midpoints from the edge's start (the library's edge splits;
    /// not measured).
    /// </remarks>
    public static (Result Result, bool Covered) TriangulateBuilder(DmxBinary.Element data)
    {
        var result = Build(data, true);
        return (result.ToResult(), !result.Stitched);
    }

    private sealed record BuiltResult(List<Vector3> Positions, List<int> Indices, List<int> Faces, bool Stitched)
    {
        public List<Vector4>? Paint { get; set; }

        public List<long>? Written { get; set; }

        public Result ToResult() => new(Positions, Indices, Faces) { Paint = Paint, Written = Written };
    }

    private static BuiltResult Build(DmxBinary.Element data, bool builderOrder, bool rayScene = false)
    {
        var next = Ints(data, "edgeNextIndices");
        var to = Ints(data, "edgeVertexIndices");
        var opposite = Ints(data, "edgeOppositeIndices");
        var edgeFace = Ints(data, "edgeFaceIndices");
        var cornerData = Ints(data, "edgeVertexDataIndices");
        var first = Ints(data, "faceEdgeIndices");
        var vertexData = Ints(data, "vertexDataIndices");
        var positions = Stream(data.Get<DmxBinary.Element>("vertexData"), "position");
        var subdivision = data.Get<DmxBinary.Element>("subdivisionData");
        var levels = subdivision?.Get<object?[]>("subdivisionLevels")?.Select(x => x is int i ? i : 0).ToArray() ?? [];
        var hasSubdivision = subdivision != null && levels.Length > 0;
        // The displacement stream: a block of (2^(level-1) + 1)^2 offsets per
        // subdivided face corner, in corner data order.
        var displacement = subdivision == null ? [] : Stream(subdivision, "displacement");
        var blockOf = new Dictionary<int, int>();
        var gridPaint = subdivision == null || !builderOrder ? [] : Stream(subdivision, "VertexPaintBlendParams");
        if (displacement.Length > 0 || gridPaint.Length > 0)
        {
            var at = 0;
            for (var d = 0; d < levels.Length; d++)
                if (levels[d] > 0)
                {
                    blockOf[d] = at;
                    var side = (1 << (levels[d] - 1)) + 1;
                    at += side * side;
                }
            if (displacement.Length > 0 && at != displacement.Length)
                throw new InvalidDataException($"displacement stream of {displacement.Length}, levels ask {at}");
            if (gridPaint.Length > 0 && at != gridPaint.Length)
                throw new InvalidDataException($"subdivision paint stream of {gridPaint.Length}, levels ask {at}");
        }

        Vector3 Pos(int v) => (Vector3)positions[vertexData[v]]!;
        int Level(int h) => hasSubdivision && cornerData[h] >= 0 && cornerData[h] < levels.Length ? levels[cornerData[h]] : 0;
        // FUN_1813c7ae0: the level across a half-edge, 0 where no face is.
        int Across(int h) => hasSubdivision && opposite[h] >= 0 && edgeFace[opposite[h]] >= 0 ? Level(opposite[h]) : 0;

        // The paint per corner, when asked for (builder order) and present.
        var paintStream = builderOrder ? Stream(data.Get<DmxBinary.Element>("faceVertexData"), "VertexPaintBlendParams") : [];
        var withPaint = paintStream.Length > 0;
        Vector4 Paint(int h) => withPaint && cornerData[h] >= 0 && cornerData[h] < paintStream.Length && paintStream[cornerData[h]] is Vector4 p ? p : Vector4.Zero;
        var corners = new List<Vector3>();
        var cornerPaint = new List<Vector4>();
        var faces = new List<int>();
        // Builder order: each face's own slot, then the appended faces.
        var slotCorners = new List<Vector3>[first.Length];
        var slotPaint = new List<Vector4>[first.Length];
        var slotFaces = new List<int>[first.Length];
        var appended = new List<(int Face, Vector3[] Corners, Vector4[] Paint)>();
        var appendedSlots = new List<Slot>();
        var stitched = false;
        // Each grid point's last write, in the order the bake positions patches:
        // faces in builder order, a face's patches in corner order.
        var written = new Dictionary<(uint, uint, uint), long>();
        long writes = 0;
        void Write(Vector3[] grid)
        {
            foreach (var p in grid)
                written[(BitConverter.SingleToUInt32Bits(p.X), BitConverter.SingleToUInt32Bits(p.Y), BitConverter.SingleToUInt32Bits(p.Z))] = writes++;
        }
        // The builder (1813baa40) gathers the faces to split level by level,
        // 1 to 5, each level in face order, so a lower level's patches are
        // appended first; a face above level 5 is not split.
        bool Splits(int f) => Level(first[f]) is > 0 and var l && (!builderOrder || l <= 5);
        var order = Enumerable.Range(0, first.Length);
        if (builderOrder)
            order = [.. order.Where(f => !Splits(f)), .. order.Where(Splits).OrderBy(f => Level(first[f]))];
        foreach (var f in order)
        {
            if (builderOrder)
            {
                corners = slotCorners[f] = [];
                cornerPaint = slotPaint[f] = [];
                faces = slotFaces[f] = [];
            }
            var loop = new List<int>();
            var e = first[f];
            do
            {
                loop.Add(e);
                e = next[e];
            } while (e != first[f] && loop.Count <= next.Length);
            var level = Level(first[f]);
            if (Splits(f))
                Patches(loop, level);
            else
                Polygon(loop);

            void Polygon(List<int> hs)
            {
                var c = hs.Select(h => Pos(to[h])).ToArray();
                var points = new List<Vector3>();
                var paints = new List<Vector4>();
                for (var i = 0; i < c.Length; i++)
                {
                    var a = c[i];
                    var b = c[(i + 1) % c.Length];
                    var n = 1 << Across(hs[(i + 1) % hs.Count]);
                    var step = 1f / n;
                    for (var k = 0; k < n; k++)
                    {
                        var t = k * step;
                        points.Add(new Vector3(((b.X - a.X) * t) + a.X, ((b.Y - a.Y) * t) + a.Y, ((b.Z - a.Z) * t) + a.Z));
                        paints.Add(EdgePaint(Paint(hs[i]), Paint(hs[(i + 1) % hs.Count]), 0, n, k));
                    }
                }
                var cut = PolygonTriangulator.Triangulate([.. points]);
                foreach (var j in cut)
                {
                    corners.Add(points[j]);
                    cornerPaint.Add(paints[j]);
                }
                for (var t = 0; t < cut.Length / 3; t++)
                    faces.Add(f);
            }

            void Patches(List<int> hs, int faceLevel)
            {
                var m = hs.Count;
                var ratio = new int[m];
                for (var i = 0; i < m; i++)
                    ratio[i] = Math.Max(1, (1 << Across(hs[(i + 1) % m])) / (1 << faceLevel));
                if (builderOrder)
                {
                    if (ratio.All(x => x == 1) && Enumerable.Range(0, m).All(i => Level(hs[i]) == faceLevel))
                    {
                        BuilderPatches(hs, faceLevel);
                        return;
                    }
                    stitched = true;
                }
                for (var i = 0; i < m; i++)
                {
                    var own = Level(hs[i]);
                    if (own == 0 || (rayScene && own > 5))
                        continue;
                    var grid = Grid(hs, i, own);
                    if (displacement.Length > 0)
                        Displace(grid, hs, i, f);
                    Write(grid);
                    if (rayScene)
                    {
                        var cells = 1 << (own - 1);
                        var side = cells + 1;
                        for (var r = 0; r < cells; r++)
                            for (var c = 0; c < cells; c++)
                            {
                                int a = (r * side) + c, b = a + side + 1;
                                corners.AddRange([grid[a], grid[a + 1], grid[b], grid[a], grid[b], grid[b - 1]]);
                                faces.Add(f);
                                faces.Add(f);
                            }
                        continue;
                    }
                    var p = ratio[i];
                    var q = ratio[(i + m - 1) % m];
                    var stitched = Stitch(1 << (own - 1), p, q).ToList();
                    foreach (var (a, b, t) in stitched)
                    {
                        var pa = grid[a];
                        var pb = grid[b];
                        corners.Add(new Vector3(((pb.X - pa.X) * t) + pa.X, ((pb.Y - pa.Y) * t) + pa.Y, ((pb.Z - pa.Z) * t) + pa.Z));
                        // Stitched patches are not covered; their paint is not ported.
                        cornerPaint.Add(new Vector4(float.NaN));
                    }
                    for (var t = 0; t < stitched.Count / 3; t++)
                        faces.Add(f);
                }
            }

            // The builder's order for a face whose patches share its level and
            // meet no finer neighbour (see TriangulateBuilder).
            void BuilderPatches(List<int> hs, int level)
            {
                var m = hs.Count;
                var n = 1 << (level - 1);
                var g = n + 1;
                var grids = new Vector3[m][];
                var paintGrids = new Vector4[m][];
                for (var i = 0; i < m; i++)
                {
                    paintGrids[i] = PaintGrid(hs, i, level);
                    grids[i] = Grid(hs, i, level);
                    if (displacement.Length > 0)
                        Displace(grids[i], hs, i, f);
                    Write(grids[i]);
                }
                var faceSlot = new Slot();
                var patchSlot = new Slot[m];
                patchSlot[m - 1] = faceSlot;
                for (var i = 0; i < m - 1; i++)
                {
                    patchSlot[i] = new Slot();
                    appendedSlots.Add(patchSlot[i]);
                }
                // A region by its four corners as patch grid (row, col); a split
                // appends children 0..2, child 3 keeps the slot.
                var cellSlot = new Slot[m, n, n];
                for (var i = 0; i < m; i++)
                    Split(i, patchSlot[i], (0, 0), (0, n), (n, n), (n, 0), level - 1);

                void Split(int patch, Slot slot, (int R, int C) p0, (int R, int C) p1, (int R, int C) p2, (int R, int C) p3, int depth)
                {
                    if (depth == 0)
                    {
                        cellSlot[patch, Math.Min(Math.Min(p0.R, p1.R), Math.Min(p2.R, p3.R)), Math.Min(Math.Min(p0.C, p1.C), Math.Min(p2.C, p3.C))] = slot;
                        return;
                    }
                    (int R, int C)[] q = [p0, p1, p2, p3];
                    static (int R, int C) Mid((int R, int C) a, (int R, int C) b) => ((a.R + b.R) / 2, (a.C + b.C) / 2);
                    var slots = new Slot[4];
                    slots[3] = slot;
                    for (var k = 0; k < 3; k++)
                    {
                        slots[k] = new Slot();
                        appendedSlots.Add(slots[k]);
                    }
                    // FUN_1813ca560 recurses corners 0, 1, 3, 2; a child keeps its
                    // parent's corner order, parent corner k at its own index k, so
                    // its corner j lies between the parent's corners k and j.
                    foreach (var k in new[] { 0, 1, 3, 2 })
                        Split(patch, slots[k], Mid(q[k], q[0]), Mid(q[k], q[1]), Mid(q[k], q[2]), Mid(q[k], q[3]), depth - 1);
                }

                for (var i = 0; i < m; i++)
                {
                    var grid = grids[i];
                    var pg = paintGrids[i];
                    for (var r = 0; r < n; r++)
                    {
                        for (var c = 0; c < n; c++)
                        {
                            int a = (r * g) + c, b = ((r + 1) * g) + c + 1, below = ((r + 1) * g) + c, right = (r * g) + c + 1;
                            cellSlot[i, r, c].Corners = [grid[b], grid[below], grid[a]];
                            cellSlot[i, r, c].Paint = [pg[b], pg[below], pg[a]];
                            appendedSlots.Add(new Slot { Corners = [grid[a], grid[right], grid[b]], Paint = [pg[a], pg[right], pg[b]] });
                        }
                    }
                }
                corners.AddRange(faceSlot.Corners!);
                cornerPaint.AddRange(faceSlot.Paint!);
                faces.Add(f);
                foreach (var s in appendedSlots)
                    appended.Add((f, s.Corners!, s.Paint!));
                appendedSlots.Clear();
            }

            // FUN_1813c7b90: each patch point moved by its displacement in
            // the patch frame (FUN_1813bc230; Matrix3x4_Rotate, columns B, T, N).
            void Displace(Vector3[] grid, List<int> hs, int i, int face)
            {
                var m = hs.Count;
                var loopPoints = new Vector3[m];
                float cx = 0f, cy = 0f, cz = 0f;
                var inv = 1f / m;
                for (var k = 0; k < m; k++)
                {
                    var v = loopPoints[k] = Pos(to[hs[(i + k) % m]]);
                    cx = (inv * v.X) + cx;
                    cy = (v.Y * inv) + cy;
                    cz = (v.Z * inv) + cz;
                }
                var n = PolygonTriangulator.Newell(loopPoints);
                var e1 = first[face];
                var a = Pos(to[e1]);
                var b = Pos(to[next[e1]]);
                var dx = cx - ((b.X + a.X) * 0.5f);
                var dy = cy - ((a.Y + b.Y) * 0.5f);
                var dz = cz - ((b.Z + a.Z) * 0.5f);
                var t = Unit(new Vector3((dz * n.Y) - (dy * n.Z), (dx * n.Z) - (dz * n.X), (dy * n.X) - (dx * n.Y)));
                var bt = Unit(new Vector3((t.Y * n.Z) - (t.Z * n.Y), (t.Z * n.X) - (n.Z * t.X), (n.Y * t.X) - (t.Y * n.X)));
                var start = blockOf[cornerData[hs[i]]];
                for (var k = 0; k < grid.Length; k++)
                {
                    var d = (Vector3)displacement[start + k]!;
                    var rx = (d.Z * n.X) + ((d.Y * t.X) + (d.X * bt.X));
                    var ry = (d.Z * n.Y) + ((d.Y * t.Y) + (d.X * bt.Y));
                    var rz = (d.Z * n.Z) + ((d.Y * t.Z) + (d.X * bt.Z));
                    grid[k] = new Vector3(rx + grid[k].X, ry + grid[k].Y, rz + grid[k].Z);
                }
            }

            // FUN_1813be320 then FUN_1813b82f0: corner i's patch of paint, a
            // grid laid out like Grid's.
            Vector4[] PaintGrid(List<int> hs, int i, int own)
            {
                // A subdivided mesh stores paint per grid point (subdivisionData,
                // in the displacement's layout); the bake's lerp is scaled by 0
                // then and the stored value added (FUN_1813b82f0 with
                // FUN_1813c1cc0): every paint value ze_hold_em_paint's floor was
                // handed is one of them.
                if (gridPaint.Length > 0)
                {
                    var side = (1 << (own - 1)) + 1;
                    var start = blockOf[cornerData[hs[i]]];
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
                var s1 = Lerp(s0, Paint(hs[(i + 1) % m]), 0.5f);
                var s2 = Lerp(s0, Paint(hs[(i + m - 1) % m]), 0.5f);
                var n = 1 << (own - 1);
                var g = n + 1;
                var step = 1f / n;
                var points = new Vector4[g * g];
                for (var r = 0; r <= n; r++)
                {
                    var v = r * step;
                    var left = Lerp(s0, s2, v);
                    var right = Lerp(s1, mean, v);
                    for (var c = 0; c <= n; c++)
                        points[(r * g) + c] = Lerp(left, right, c * step);
                }
                return points;
            }

            // FUN_1813bda20 then FUN_1813bcc40: corner h's patch, a grid of
            // n + 1 points a side, row r from the corner towards the previous
            // edge's midpoint, column c towards the next edge's.
            Vector3[] Grid(List<int> hs, int i, int own)
            {
                var m = hs.Count;
                var inv = 1f / m;
                float cx = 0f, cy = 0f, cz = 0f;
                for (var k = 0; k < m; k++)
                {
                    var v = Pos(to[hs[(i + k) % m]]);
                    cx = (inv * v.X) + cx;
                    cy = (v.Y * inv) + cy;
                    cz = (v.Z * inv) + cz;
                }
                var p0 = Pos(to[hs[i]]);
                var pn = Pos(to[hs[(i + 1) % m]]);
                var pp = Pos(to[hs[(i + m - 1) % m]]);
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
        }

        if (builderOrder)
        {
            corners = [.. slotCorners.SelectMany(x => x), .. appended.SelectMany(x => x.Corners)];
            cornerPaint = [.. slotPaint.SelectMany(x => x), .. appended.SelectMany(x => x.Paint)];
            faces = [.. slotFaces.SelectMany(x => x), .. appended.Select(x => x.Face)];
        }

        if (rayScene)
            return new BuiltResult(corners, [], faces, stitched);

        // FUN_1813858d0: equal positions (bit for bit) are one vertex, numbered as first met.
        var weld = new Dictionary<(uint, uint, uint), int>();
        var result = new BuiltResult([], [], faces, stitched) { Paint = withPaint ? [] : null, Written = builderOrder ? [] : null };
        for (var i = 0; i < corners.Count; i++)
        {
            var c = corners[i];
            var key = (BitConverter.SingleToUInt32Bits(c.X), BitConverter.SingleToUInt32Bits(c.Y), BitConverter.SingleToUInt32Bits(c.Z));
            if (!weld.TryGetValue(key, out var index))
            {
                index = weld[key] = result.Positions.Count;
                result.Positions.Add(c);
                result.Written?.Add(written.TryGetValue(key, out var w) ? w : -1);
            }
            result.Indices.Add(index);
            result.Paint?.Add(cornerPaint[i]);
        }
        return result;
    }

    // A face slot in the builder's dense face array, holding the leaf it ends with.
    private sealed class Slot
    {
        public Vector3[]? Corners { get; set; }
        public Vector4[]? Paint { get; set; }
    }

    // FUN_181046990: (b - a) * t + a, component by component.
    private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
        => new(((b.X - a.X) * t) + a.X, ((b.Y - a.Y) * t) + a.Y, ((b.Z - a.Z) * t) + a.Z, ((b.W - a.W) * t) + a.W);

    // FUN_1813be320's mean: value * w + sum.
    private static Vector4 MulAdd(Vector4 value, float w, Vector4 sum)
        => new((value.X * w) + sum.X, (value.Y * w) + sum.Y, (value.Z * w) + sum.Z, (value.W * w) + sum.W);

    // The paint at point k of an edge halved and halved again from klo to
    // khi: nested midpoints, each from the lower end.
    private static Vector4 EdgePaint(Vector4 lo, Vector4 hi, int klo, int khi, int k)
    {
        while (k != klo)
        {
            var mid = (klo + khi) / 2;
            var m = Lerp(lo, hi, 0.5f);
            if (k < mid)
            {
                hi = m;
                khi = mid;
            }
            else
            {
                lo = m;
                klo = mid;
            }
        }
        return lo;
    }

    /// <summary>A vector over its length ((z^2 + y^2) + x^2); a length outside 1e-17..1e17 is not handled.</summary>
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

    /// <summary>
    /// FUN_1813c26a0: a patch of n cells a side as triangles of lerped grid
    /// points (a, b, t). The first row's cells take p segments each and the
    /// first column's q, fanned to the next row (column) in halves; the other
    /// cells are two triangles each.
    /// </summary>
    internal static IEnumerable<(int A, int B, float T)> Stitch(int n, int p, int q)
    {
        var g = n + 1;
        float fu = 1f / p, fv = 1f / q;
        int half = p / 2, halfQ = q / 2;
        for (var k = 0; k < p; k++)
        {
            yield return (0, 1, k * fu);
            yield return (0, 1, (k + 1) * fu);
            yield return (g + 1, g + 1, 0f);
        }
        for (var c = 2; c <= n; c++)
        {
            for (var k = 0; k < half; k++)
            {
                yield return (c - 1, c, k * fu);
                yield return (c - 1, c, (k + 1) * fu);
                yield return (c + n, c + n, 0f);
            }
            for (var k = half; k < p; k++)
            {
                yield return (c - 1, c, k * fu);
                yield return (c - 1, c, (k + 1) * fu);
                yield return (c + n + 1, c + n + 1, 0f);
            }
            yield return (c - 1, c, half * fu);
            yield return (c + n + 1, c + n + 1, 0f);
            yield return (c + n, c + n, 0f);
        }
        for (var k = 0; k < q; k++)
        {
            yield return (0, g, k * fv);
            yield return (g + 1, g + 1, 0f);
            yield return (0, g, (k + 1) * fv);
        }
        for (var r = 2; r <= n; r++)
        {
            int above = (r - 1) * g, here = r * g;
            for (var k = 0; k < halfQ; k++)
            {
                yield return (above, here, k * fv);
                yield return (above + 1, above + 1, 0f);
                yield return (above, here, (k + 1) * fv);
            }
            for (var k = halfQ; k < q; k++)
            {
                yield return (above, here, k * fv);
                yield return (here + 1, here + 1, 0f);
                yield return (above, here, (k + 1) * fv);
            }
            yield return (above, here, halfQ * fv);
            yield return (above + 1, above + 1, 0f);
            yield return (here + 1, here + 1, 0f);
        }
        for (var r = 1; r < n; r++)
            for (var c = 1; c < n; c++)
            {
                var p00 = (r * g) + c;
                yield return (p00, p00, 0f);
                yield return (p00 + 1, p00 + 1, 0f);
                yield return (p00 + g + 1, p00 + g + 1, 0f);
                yield return (p00, p00, 0f);
                yield return (p00 + g + 1, p00 + g + 1, 0f);
                yield return (p00 + g, p00 + g, 0f);
            }
    }

    private static int[] Ints(DmxBinary.Element data, string name)
        => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : -1).ToArray();

    private static object?[] Stream(DmxBinary.Element? holder, string name)
        => holder?.GetElements("streams").FirstOrDefault(s => s.Name.Split(':')[0] == name)?.Get<object?[]>("data") ?? [];
}
