using System.Numerics;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// One markup volume of the RC nav input (CNavGenInput +0x90, 0x50 bytes, NavGen_AddMarkup 180f20720):
/// the entity's local bounds (+0, +0xc), its matrix (+0x18, 3x4 row major: the instance path's step
/// matrix times the entity's own), its tag's index in the tag table (+0x48; -1 for none) and the byte
/// at +0x4c (1 when added).
/// </summary>
public sealed record NavMarkup((float X, float Y, float Z) Min, (float X, float Y, float Z) Max, float[] Matrix, int Tag, byte Flag = 1);

/// <summary>
/// The markups resourcecompiler hands navsystem (NavGen_CollectMarkupVolumes 180f209f0, 20260923) and
/// the (tag, value) table they index (CNavGenInput +0x30, 0x80-byte entries: the tag at +0, the value
/// at +0x40, each copied with V_strncpy into 0x40 bytes; 1818c7010 adds a pair when no entry matches
/// both, case-insensitively).
/// </summary>
public sealed class NavMarkupInput
{
    public List<(string Tag, string Value)> Tags { get; } = [];

    public List<NavMarkup> Markups { get; } = [];

    public static NavMarkupInput Empty => new();

    /// <summary>18001ba90: a markup's tag value (+0x40 of its entry), null for -1 or past the table.</summary>
    public string? ValueOf(int tag) => tag >= 0 && tag < Tags.Count ? Tags[tag].Value : null;

    /// <summary>1818c7010: the index of (tag, value), added at the end when new.</summary>
    public int TagIndex(string tag, string value)
    {
        tag = Truncate(tag);
        value = Truncate(value);
        for (var i = 0; i < Tags.Count; i++)
            if (Tags[i].Tag.Equals(tag, StringComparison.OrdinalIgnoreCase) && Tags[i].Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                return i;
        Tags.Add((tag, value));
        return Tags.Count - 1;
    }

    // V_strncpy into 0x40 bytes: at most 63 characters (ASCII keys).
    private static string Truncate(string s) => s.Length > 63 ? s[..63] : s;

    /// <summary>The markups whose tag value is NONAV (V_stricmp_fast), as 1800217b0 gathers them for the extraction.</summary>
    public List<NavMarkup> NoNav()
        => [.. Markups.Where(m => ValueOf(m.Tag) is { } v && v.Equals("NONAV", StringComparison.OrdinalIgnoreCase))];

    private const string Prefix = "navProperty_";

    /// <summary>
    /// 180f209f0 on a map: every func_nav_markup (or a class deriving from it, 180f2de60) the map's node
    /// list holds and that is not hidden (180f1ef70 with flags 3), in walk order. Per entity, its keys
    /// as CEntityKeyValues iterates them (the node's key table backwards, as the lump writes it: see
    /// <see cref="EntityLumpAuthor.KeyTable"/>), each named navProperty_&lt;tag&gt; (V_stristr_fast at the
    /// start) giving (tag, value) pairs (180f21d70): a string key (type 5) its value when not empty, a
    /// tag_list (0x22) one pair per token of its value (180f30580: split at commas and white space). A
    /// key the FGD does not declare is neither (s2c_navmarkup's navProperty_Custom adds nothing).
    /// Every pair is registered in the tag table; the first one adds the entity's markup (180f20720),
    /// later ones only register. An entity that added none gets one with tag -1 when it has a non-empty
    /// navProperty_NavAttributes tag list (0x22 or 0x23, 180f22330).
    /// The markup's frame and bounds are the entity's first mesh's (its matrix and vertex bounds; the
    /// entity's own angles do not count). Markups inside instances or prefabs (several instance paths,
    /// sub-worlds) are not ported and throw.
    /// </summary>
    public static NavMarkupInput Collect(DmxBinary.Document document, FgdSchema schema)
    {
        var input = new NavMarkupInput();
        var nodes = new Dictionary<int, DmxBinary.Element>();
        foreach (var e in document.Elements)
            if (e.Type is "CMapEntity" && e.GetValue<int>("nodeID") is { } id)
                nodes.TryAdd(id, e);
        foreach (var entity in MapEntities.From(document))
        {
            if (entity.IsWorld || entity.Hidden || !IsMarkup(entity.ClassName, schema))
                continue;
            if (entity.Instanced || entity.Prefabs.Length > 0)
                throw new NotSupportedException("a func_nav_markup inside an instance or prefab (its instance paths, 180f88460) is not ported");
            if (!nodes.TryGetValue(entity.NodeId, out var node))
                continue;
            var added = false;
            foreach (var (key, value) in EntityLumpAuthor.KeyTable(entity, schema).AsEnumerable().Reverse())
            {
                if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var tag = key[Prefix.Length..];
                foreach (var token in Values(schema.KeyOf(entity.ClassName, key)?.TypeId ?? -1, value))
                {
                    var index = input.TagIndex(tag, token);
                    if (!added)
                        added = input.AddMarkup(node, index);
                }
            }
            if (!added && EntityLumpAuthor.KeyTable(entity, schema).Any(k => k.Key.Equals(Prefix + "NavAttributes", StringComparison.OrdinalIgnoreCase)
                    && (schema.KeyOf(entity.ClassName, k.Key)?.TypeId ?? -1) is 0x22 or 0x23 && Tokens(k.Value).Any()))
                input.AddMarkup(node, -1);
        }
        return input;
    }

    private static bool IsMarkup(string className, FgdSchema schema)
        => className.Equals("func_nav_markup", StringComparison.OrdinalIgnoreCase) || schema.Inherits(className, "func_nav_markup");

    // 180f21d70's two key types: a string (5) is one value when not empty; a tag list (0x22) is its tokens.
    private static IEnumerable<string> Values(int type, string value) => type switch
    {
        5 => value.Length > 0 ? [value] : [],
        0x22 => Tokens(value),
        _ => [],
    };

    // 180f30580: the tokens of a tag list value, commas and white space between them.
    private static IEnumerable<string> Tokens(string value)
        => value.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    // 180f20720 for a node of the map's own (one identity instance path): the entity's first CMapMesh
    // below it (the child iterator 180f8b7c0 / 180f8bd70, depth first), its own matrix (vfn 0xa0:
    // AngleMatrix of its angles, its origin) and its local bounds (+0x14c / +0x158: its vertex positions),
    // then ConcatTransforms(identity, that matrix). An entity without a mesh adds nothing.
    private bool AddMarkup(DmxBinary.Element node, int tag)
    {
        if (FirstMesh(node) is not { } mesh)
            return false;
        if ((mesh.GetValue<Vector3>("scales") ?? Vector3.One) != Vector3.One)
            throw new NotSupportedException("a func_nav_markup whose mesh is scaled (the node bounds +0x14c) is not ported");
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var v in MapMeshes.LocalFaces(mesh).Corners.SelectMany(c => c))
        {
            minX = MathF.Min(minX, v.X);
            minY = MathF.Min(minY, v.Y);
            minZ = MathF.Min(minZ, v.Z);
            maxX = MathF.Max(maxX, v.X);
            maxY = MathF.Max(maxY, v.Y);
            maxZ = MathF.Max(maxZ, v.Z);
        }
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        Markups.Add(new NavMarkup((minX, minY, minZ), (maxX, maxY, maxZ), MapMeshes.Concat(identity, MapMeshes.Local(mesh)), tag));
        return true;
    }

    private static DmxBinary.Element? FirstMesh(DmxBinary.Element node)
    {
        foreach (var child in node.GetElements("children"))
        {
            if (child.Type == "CMapMesh")
                return child;
            if (FirstMesh(child) is { } below)
                return below;
        }
        return null;
    }
}

/// <summary>The markup stages' parameters (the stage list's bound convars, 180022b60; captured).</summary>
public static class NavMarkupSettings
{
    /// <summary>RemovePolysMarkedNoNav (003, 009): the pad added to the hull radius (180134b10).</summary>
    public const double NoNavPad = 0.75;

    /// <summary>RemovePolysMarkedNoNav's plane tolerance for a markup with its +0x4c byte set and without.</summary>
    public const double NoNavEps = 1.0, NoNavEpsPlain = 1.0;

    /// <summary>SplitPolysByMarkup (019): the growth (AVOID adds the hull radius) and the plane tolerances.</summary>
    public const double SplitGrow = 2.0, SplitEps = 1.0, SplitEpsPlain = 8.0;
}

public sealed partial class NavGenPolys
{
    /// <summary>
    /// RemovePolysMarkedNoNav (180074590; stage 003 with <paramref name="shrink"/>, 009 without): for each
    /// markup whose tag value is NONAV or NONAVNONSOLID, its box grown by pad + the hull's radius (NONAV),
    /// by 1 in z only (NONAVNONSOLID), or shrunk by pad + radius on every axis (003), and its matrix with 1
    /// taken off the z translation. The polygons the two BVHs return for the box's world AABB (the
    /// polygons there at the start, then those the earlier markups added, 180060a60 / 1800610c0) are cut by
    /// the box's six planes in turn (the lambda at 1800ff838, 18006a000: a polygon both sides of a plane
    /// is replaced by its front and back pieces, appended, and the pieces join the candidates for the next
    /// plane); a polygon wholly behind a plane, and every piece, is listed, and each listed polygon whose
    /// corners are all within the tolerance inside all six planes is removed. The polygons left, in order,
    /// are the output.
    /// </summary>
    public void RemovePolysMarkedNoNav(NavMarkupInput input, double pad, double epsFlagged, double epsPlain, bool shrink, float hullRadius)
    {
        foreach (var p in Polys)
            p.Removed = false;
        var work = Polys;
        if (work.Count != 0 && work[0].Indices.Count > 0)
        {
            var initial = work.Count;
            var first = new PolyBvh(this, work, 0, initial);
            var grow = (float)pad + hullRadius;
            foreach (var m in input.Markups)
            {
                if (input.ValueOf(m.Tag) is not { } value)
                    continue;
                var noNav = value.Equals("NONAV", StringComparison.OrdinalIgnoreCase);
                if (!noNav && !value.Equals("NONAVNONSOLID", StringComparison.OrdinalIgnoreCase))
                    continue;
                float gxy, gz;
                if (shrink)
                    gxy = gz = -grow;
                else if (noNav)
                    gxy = gz = grow;
                else
                    (gxy, gz) = (0f, 1f);
                (float X, float Y, float Z) min = (m.Min.X - gxy, m.Min.Y - gxy, m.Min.Z - gz);
                (float X, float Y, float Z) max = (m.Max.X + gxy, m.Max.Y + gxy, m.Max.Z + gz);
                var matrix = (float[])m.Matrix.Clone();
                matrix[11] = m.Matrix[11] - 1f;
                var planes = BoxPlanes(matrix, min, max, m.Flag != 0 ? epsFlagged : epsPlain);
                var (wmin, wmax) = TransformAabb(matrix, min, max);
                var candidates = new List<int>();
                first.Query(wmin, wmax, work, candidates);
                new PolyBvh(this, work, initial, work.Count - initial).Query(wmin, wmax, work, candidates);
                var inside = new List<int>();
                foreach (var plane in planes)
                {
                    var newPieces = 0;
                    var end = candidates.Count;
                    for (var c = 0; c < end; c++)
                        newPieces += ClipByPlane(work, candidates[c], plane, null, inside);
                    for (var i = work.Count - newPieces; i < work.Count; i++)
                        candidates.Add(i);
                }
                foreach (var i in inside)
                {
                    var p = work[i];
                    if (p.Removed)
                        continue;
                    if (planes.All(plane => p.Indices.All(k => !Outside(Pool[k], plane))))
                        p.Removed = true;
                }
            }
        }
        Polys = [.. work.Where(p => !p.Removed)];
    }

    // 180074590's last test: a corner beyond the plane by more than its tolerance.
    private static bool Outside((double X, double Y, double Z) v, (double X, double Y, double Z, double D, double Eps) plane)
    {
        var d = (v.Y * plane.Y + v.X * plane.X + v.Z * plane.Z) - plane.D;
        return plane.Eps <= d && d != plane.Eps;
    }

    /// <summary>
    /// SplitPolysByMarkup (180075f40, stage 019): each markup whose tag value is not WALKABLESEED, NONAV or
    /// NONAVNONSOLID (a markup without a tag included), its box grown by <paramref name="grow"/> (AVOID: plus
    /// the hull's radius); every polygon its grown box touches (vphysics2's point-set distance of the
    /// box, grown by 1 more and placed by the markup's matrix, to the polygon's corners at most 0.125,
    /// 180077cf0 / 180078360) is cut by the box's six planes in turn, over the polygons there when each
    /// plane starts. The polygons left, in order, are the output.
    /// </summary>
    public void SplitPolysByMarkup(NavMarkupInput input, double grow, double epsFlagged, double epsPlain, float hullRadius)
    {
        foreach (var p in Polys)
            p.Removed = false;
        var work = Polys;
        foreach (var m in input.Markups)
        {
            var value = input.ValueOf(m.Tag);
            var g = (float)grow;
            if (value is not null)
            {
                if (value.Equals("WALKABLESEED", StringComparison.OrdinalIgnoreCase) || value.Equals("NONAV", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("NONAVNONSOLID", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (value.Equals("AVOID", StringComparison.OrdinalIgnoreCase))
                    g = (float)grow + hullRadius;
            }
            (float X, float Y, float Z) min = (m.Min.X - g, m.Min.Y - g, m.Min.Z - g);
            (float X, float Y, float Z) max = (m.Max.X + g, m.Max.Y + g, m.Max.Z + g);
            var planes = BoxPlanes(m.Matrix, min, max, m.Flag != 0 ? epsFlagged : epsPlain);
            var columns = Columns(m.Matrix);
            bool Touches(NavPoly p) => MarkupTouches(min, max, columns, Corners(p));
            foreach (var plane in planes)
            {
                var count = work.Count;
                for (var i = 0; i < count; i++)
                    ClipByPlane(work, i, plane, Touches, null);
            }
        }
        Polys = [.. work.Where(p => !p.Removed)];
    }

    // The lambda at 1800ff838 (18006a000) for one polygon: skipped when removed or when the filter turns
    // it down; split by the plane (18004fe60); a polygon only behind the plane is listed, one both sides
    // is removed and its front, then back, pieces appended (18004c280), all of them listed. Returns the
    // pieces added.
    private int ClipByPlane(List<NavPoly> work, int index, (double X, double Y, double Z, double D, double Eps) plane,
                            Func<NavPoly, bool>? filter, List<int>? inside)
    {
        var p = work[index];
        if (p.Removed || filter?.Invoke(p) == false)
            return 0;
        var (front, back) = SplitByPlane(ClipSource(p), plane);
        if (front.Corners.Count == 0 || back.Corners.Count == 0)
        {
            if (inside is not null && back.Corners.Count != 0)
                inside.Add(index);
            return 0;
        }
        p.Removed = true;
        var n = AddClipped(front, p, work) + AddClipped(back, p, work);
        if (inside is not null)
            for (var i = work.Count - n; i < work.Count; i++)
                inside.Add(i);
        return n;
    }

    // The box's six planes in doubles (180074590 / 180075f40): axis k of the matrix (its column k) times
    // +1 then -1, each component a float product widened, the offset the face's coordinate (max, then
    // -min) plus the column's dot with the translation, summed ((t.y n.y + t.x n.x) + t.z n.z) + face.
    private static (double X, double Y, double Z, double D, double Eps)[] BoxPlanes(float[] m, (float X, float Y, float Z) min,
                                                                                   (float X, float Y, float Z) max, double eps)
    {
        var planes = new (double, double, double, double, double)[6];
        for (var u = 0; u < 6; u++)
        {
            var axis = u / 2;
            var s = (u & 1) == 0 ? 1f : -1f;
            double nx = m[axis] * s, ny = m[4 + axis] * s, nz = m[8 + axis] * s;
            var face = axis switch
            {
                0 => s > 0 ? max.X : -min.X,
                1 => s > 0 ? max.Y : -min.Y,
                _ => s > 0 ? max.Z : -min.Z,
            };
            var d = (((double)m[7] * ny + (double)m[3] * nx) + (double)m[11] * nz) + face;
            planes[u] = (nx, ny, nz, d, eps);
        }
        return planes;
    }

    /// <summary>
    /// TransformAABB (navsystem 1800ed7d0): the box's centre ((max + min) 0.5) through the matrix
    /// (1800ee160), its half extent (max - centre) through the absolute rotation, each row summed
    /// (|e.y m1| + |e.x m0|) + |e.z m2|.
    /// </summary>
    public static ((float X, float Y, float Z) Min, (float X, float Y, float Z) Max) TransformAabb(float[] m, (float X, float Y, float Z) min,
                                                                                                    (float X, float Y, float Z) max)
    {
        var cx = (max.X + min.X) * 0.5f;
        var cy = (max.Y + min.Y) * 0.5f;
        var cz = (max.Z + min.Z) * 0.5f;
        float ex = max.X - cx, ey = max.Y - cy, ez = max.Z - cz;
        var c = RecastInput.Transform(m, cx, cy, cz);
        var rx = (MathF.Abs(ey * m[1]) + MathF.Abs(ex * m[0])) + MathF.Abs(ez * m[2]);
        var ry = (MathF.Abs(ey * m[5]) + MathF.Abs(ex * m[4])) + MathF.Abs(ez * m[6]);
        var rz = (MathF.Abs(ey * m[9]) + MathF.Abs(ex * m[8])) + MathF.Abs(ez * m[10]);
        return ((c.X - rx, c.Y - ry, c.Z - rz), (rx + c.X, ry + c.Y, rz + c.Z));
    }

    // CVPhysics2Interface::PointSetDistance's transform layout (180073970): the 3x4 matrix as columns.
    private static float[] Columns(float[] m) => [m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10], m[3], m[7], m[11]];

    // 180078360: the box grown by 1, its eight corners (x fastest, then y, then z) placed by the
    // markup's matrix, against the polygon's corners; touching when the distance is 0 (at most 0.125).
    private static bool MarkupTouches((float X, float Y, float Z) min, (float X, float Y, float Z) max, float[] columns, (float X, float Y, float Z)[] polygon)
    {
        float x0 = min.X - 1f, y0 = min.Y - 1f, z0 = min.Z - 1f, x1 = max.X + 1f, y1 = max.Y + 1f, z1 = max.Z + 1f;
        (float X, float Y, float Z)[] box =
        [
            (x0, y0, z0), (x1, y0, z0), (x0, y1, z0), (x1, y1, z0),
            (x0, y0, z1), (x1, y0, z1), (x0, y1, z1), (x1, y1, z1),
        ];
        return NavGjk.PhysicsDistance(box, columns, polygon) <= 0f;
    }

    /// <summary>
    /// 180047aa0 / 180044fb0: the BVH's leaf key of a quantised box, the 30-bit Morton code of its
    /// centre ((min + max) >> 1, + 0x7fff, its top 10 bits), z, y, x from the top.
    /// </summary>
    internal static uint BvhKey(short minX, short maxX, short minY, short maxY, short minZ, short maxZ)
    {
        static uint Axis(short lo, short hi) => (uint)((((lo + hi) >> 1) + 0x7fff) & 0xffff) >> 6 & 0x3ff;
        static uint Spread(uint v)
        {
            v = (v | (v << 16)) & 0x030000ff;
            v = (v | (v << 8)) & 0x0300f00f;
            v = (v | (v << 4)) & 0x030c30c3;
            return (v | (v << 2)) & 0x09249249;
        }
        return (Spread(Axis(minZ, maxZ)) << 2) | (Spread(Axis(minY, maxY)) << 1) | Spread(Axis(minX, maxX));
    }

    /// <summary>
    /// The polygon BVH the markup stages query (navsystem 180069a40 build, 180060a60 / 1800610c0
    /// query): the polygons' bounds (their corners as floats) grown by 1 set an origin ((max - min)
    /// 0.5000076 + min) and a scale (65535 / (max - min)) per axis (18004a070); each polygon's box is
    /// quantised to int16 (floor and ceil of (v - origin) scale); the polygons are ordered by a 30-bit
    /// Morton key of their box centres ((min + max) >> 1, + 0x7fff, top 10 bits; z, y, x from the top,
    /// 180047aa0 / 180044fb0) and then their index (MSVC std::sort on key &lt;&lt; 32 | index, 1800ee550).
    /// A query box is quantised the same way, clamped to the int16 range; the result is every polygon
    /// still not removed whose quantised box meets it (inclusive), in key order, each index offset by
    /// the BVH's first polygon. The three-level tree (180047fb0, 18005f110) only prunes and visits its
    /// nodes and their 16 children in order, so it is not built.
    /// </summary>
    private sealed class PolyBvh
    {
        private readonly int _offset;
        private readonly int[] _order = [];
        private readonly short[] _boxes = [];
        private readonly float _ox, _oy, _oz, _sx, _sy, _sz;
        private readonly float _minX = float.MaxValue, _minY = float.MaxValue, _minZ = float.MaxValue;
        private readonly float _maxX = -float.MaxValue, _maxY = -float.MaxValue, _maxZ = -float.MaxValue;

        public PolyBvh(NavGenPolys g, List<NavPoly> polys, int start, int count)
        {
            _offset = start;
            if (count <= 0)
                return;
            var first = g.Pool[polys[start].Indices[0]];
            float minX = (float)first.X, minY = (float)first.Y, minZ = (float)first.Z;
            float maxX = minX, maxY = minY, maxZ = minZ;
            for (var i = start; i < start + count; i++)
                foreach (var k in polys[i].Indices)
                {
                    var v = g.Pool[k];
                    float x = (float)v.X, y = (float)v.Y, z = (float)v.Z;
                    if (x <= minX) minX = x;
                    if (y <= minY) minY = y;
                    if (z <= minZ) minZ = z;
                    if (maxY <= y) maxY = y;
                    if (maxX <= x) maxX = x;
                    if (maxZ <= z) maxZ = z;
                }
            // 18004a070.
            _minX = minX - 1f;
            _minY = minY - 1f;
            _minZ = minZ - 1f;
            _maxX = maxX + 1f;
            _maxY = maxY + 1f;
            _maxZ = maxZ + 1f;
            _sx = 65535f / (_maxX - _minX);
            _sy = 65535f / (_maxY - _minY);
            _sz = 65535f / (_maxZ - _minZ);
            const float Half = 0.5000076f;
            _ox = (_maxX - _minX) * Half + _minX;
            _oy = (_maxY - _minY) * Half + _minY;
            _oz = (_maxZ - _minZ) * Half + _minZ;
            var boxes = new short[count * 6];
            var keys = new long[count];
            for (var i = 0; i < count; i++)
            {
                var p = polys[start + i];
                var v0 = g.Pool[p.Indices[0]];
                float lx = (float)v0.X, ly = (float)v0.Y, lz = (float)v0.Z, hx = lx, hy = ly, hz = lz;
                for (var c = 1; c < p.Indices.Count; c++)
                {
                    var v = g.Pool[p.Indices[c]];
                    float x = (float)v.X, y = (float)v.Y, z = (float)v.Z;
                    if (x <= lx) lx = x;
                    if (y <= ly) ly = y;
                    if (hx <= x) hx = x;
                    if (hy <= y) hy = y;
                    if (hz <= z) hz = z;
                    if (z <= lz) lz = z;
                }
                boxes[i * 6] = (short)(int)MathF.Floor((lx - _ox) * _sx);
                boxes[i * 6 + 1] = (short)(int)MathF.Ceiling((hx - _ox) * _sx);
                boxes[i * 6 + 2] = (short)(int)MathF.Floor((ly - _oy) * _sy);
                boxes[i * 6 + 3] = (short)(int)MathF.Ceiling((hy - _oy) * _sy);
                boxes[i * 6 + 4] = (short)(int)MathF.Floor((lz - _oz) * _sz);
                boxes[i * 6 + 5] = (short)(int)MathF.Ceiling((hz - _oz) * _sz);
                keys[i] = ((long)MortonKey(boxes, i) << 32) | (uint)i;
            }
            if (count > 1)
                Array.Sort(keys);
            _order = new int[count];
            _boxes = new short[count * 6];
            for (var s = 0; s < count; s++)
            {
                var i = (int)(keys[s] & 0xffffffff);
                _order[s] = i;
                Array.Copy(boxes, i * 6, _boxes, s * 6, 6);
            }
        }

        private static uint MortonKey(short[] boxes, int i)
            => BvhKey(boxes[i * 6], boxes[i * 6 + 1], boxes[i * 6 + 2], boxes[i * 6 + 3], boxes[i * 6 + 4], boxes[i * 6 + 5]);

        public void Query((float X, float Y, float Z) min, (float X, float Y, float Z) max, List<NavPoly> polys, List<int> output)
        {
            var all = min.X <= _minX && min.Y <= _minY && min.Z <= _minZ && _maxX <= max.X && _maxY <= max.Y && _maxZ <= max.Z;
            int lx = 0, hx = 0, ly = 0, hy = 0, lz = 0, hz = 0;
            if (!all)
            {
                lx = Quantise((min.X - _ox) * _sx, floor: true);
                ly = Quantise((min.Y - _oy) * _sy, floor: true);
                lz = Quantise((min.Z - _oz) * _sz, floor: true);
                hx = Quantise((max.X - _ox) * _sx, floor: false);
                hy = Quantise((max.Y - _oy) * _sy, floor: false);
                hz = Quantise((max.Z - _oz) * _sz, floor: false);
            }
            for (var s = 0; s < _order.Length; s++)
            {
                if (!all && (_boxes[s * 6] > hx || lx > _boxes[s * 6 + 1] || _boxes[s * 6 + 2] > hy || ly > _boxes[s * 6 + 3]
                             || _boxes[s * 6 + 4] > hz || lz > _boxes[s * 6 + 5]))
                    continue;
                var index = _order[s] + _offset;
                if (!polys[index].Removed)
                    output.Add(index);
            }
        }

        // 180060a60: clamped to [-32768, 32767] as a float, floored or ceiled, then to int16.
        private static int Quantise(float v, bool floor)
        {
            if (32767f <= v)
                v = 32767f;
            if (!(-32768f <= v))
                v = -32768f;
            var i = (int)(floor ? MathF.Floor(v) : MathF.Ceiling(v));
            return i < -0x7fff ? -0x8000 : i > 0x7ffe ? 0x7fff : i;
        }
    }
}

public static class NavGenInputMarkups
{
    /// <summary>
    /// 1800217b0's pad for the extraction's NONAV boxes: 180134b10 (0.75), raised to it plus each hull's
    /// radius (the hull table at +0x170, 0x48 bytes each, the radius at +4) when that is larger.
    /// </summary>
    public static float NoNavPad(IEnumerable<float> hullRadii)
    {
        var pad = NavGenStages.RadiusPad;
        foreach (var r in hullRadii)
            if (pad <= NavGenStages.RadiusPad + r)
                pad = NavGenStages.RadiusPad + r;
        return pad;
    }

    /// <summary>
    /// 180048b20's body test: a body is left out when its AABB lies inside (inclusive) the world AABB of
    /// a NONAV box shrunk by the pad (TransformAABB of the markup's matrix).
    /// </summary>
    public static bool BodyInside(IReadOnlyList<NavMarkup> noNav, float pad, (float X, float Y, float Z) min, (float X, float Y, float Z) max)
    {
        foreach (var m in noNav)
        {
            var (bmin, bmax) = NavGenPolys.TransformAabb(m.Matrix, (m.Min.X + pad, m.Min.Y + pad, m.Min.Z + pad),
                                                         (m.Max.X - pad, m.Max.Y - pad, m.Max.Z - pad));
            if (bmin.X <= min.X && bmin.Y <= min.Y && bmin.Z <= min.Z && max.X <= bmax.X && max.Y <= bmax.Y && max.Z <= bmax.Z)
                return true;
        }
        return false;
    }

    /// <summary>
    /// NavGen_ApplyNoNavMarkups (180049210): each triangle whose three corners all lie in a NONAV box
    /// shrunk by the pad, in the markup's frame (the corner less the translation, then the rotation's
    /// transpose summed (d.y r1 + d.x r0) + d.z r2), inclusive, is dropped; the rest keep their order. The
    /// BVH the binary narrows the candidates with returns a superset of them, so every triangle is tested.
    /// </summary>
    public static void ApplyNoNavMarkups(List<Vector3> verts, List<int> indices, IReadOnlyList<NavMarkup> noNav, float pad)
    {
        if (verts.Count == 0 || indices.Count == 0 || noNav.Count == 0)
            return;
        var count = indices.Count / 3;
        var marked = new bool[count];
        foreach (var m in noNav)
        {
            var r = m.Matrix;
            float lx = m.Min.X + pad, ly = m.Min.Y + pad, lz = m.Min.Z + pad;
            float hx = m.Max.X - pad, hy = m.Max.Y - pad, hz = m.Max.Z - pad;
            bool Inside(Vector3 v)
            {
                float dx = v.X - r[3], dy = v.Y - r[7], dz = v.Z - r[11];
                var x = (dy * r[4] + dx * r[0]) + dz * r[8];
                var y = (dy * r[5] + dx * r[1]) + dz * r[9];
                var z = (dy * r[6] + dx * r[2]) + dz * r[10];
                return lx <= x && ly <= y && lz <= z && x <= hx && y <= hy && z <= hz;
            }
            for (var t = 0; t < count; t++)
                if (!marked[t] && Inside(verts[indices[t * 3]]) && Inside(verts[indices[t * 3 + 1]]) && Inside(verts[indices[t * 3 + 2]]))
                    marked[t] = true;
        }
        var kept = new List<int>(indices.Count);
        for (var t = 0; t < count; t++)
            if (!marked[t])
                kept.AddRange([indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2]]);
        indices.Clear();
        indices.AddRange(kept);
    }
}
