using System.Numerics;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The ladders of CNavMesh::BuildFromNavGenPolys (rc 0923 1818d63c0, identical in 0924): after the
/// areas, one CNavLadder per RC ladder (1818e6970, ids from 1, the counter the mesh's constructor
/// 1818d4720 and reset 1818dc3b0 set), built by 1818dc620 with navsystem's seven-area record for it
/// (LadderConnect's, remapped by IslandRemoval). The traces go through the nav physics interface
/// (1818d2b40 -> 1818d2c10 -> 1818d1fa0): a sphere of radius 0.1 (shape type 1) cast through the
/// world's vfn 0x560 with layers 0x3021, group 4, static bodies only and flags 0x49, of which only
/// whether it hit, the nearest hit's normal and its shape's InteractsAs are read.
/// </summary>
public static class NavMeshLadders
{
    /// <summary>The trace: the nearest static hit of the 0.1 sphere from start by delta (fraction 1, no hit, when clear).</summary>
    public delegate NavSphereWorld.NearestHit Trace(Vector3 start, Vector3 delta);

    /// <summary>
    /// 1818d63c0's ladder loop: each input ladder (1818c78a0: top, bottom, normal, width) built
    /// (1818dc620) with its record and appended to the mesh's list, linking the areas it names.
    /// </summary>
    public static void Build(NavWriter.Mesh mesh, IReadOnlyList<NavLadder> ladders, IReadOnlyList<int[]> records, Trace trace)
    {
        if (records.Count != ladders.Count)
            throw new InvalidOperationException($"{ladders.Count} ladders but {records.Count} connectivity records");
        var id = 1u;
        for (var i = 0; i < ladders.Count; i++)
        {
            var l = new NavWriter.Ladder { Id = id++ };
            BuildLadder(mesh, l, ladders[i], records[i], trace);
            mesh.Ladders.Add(l);
        }
    }

    /// <summary>
    /// 1818dc620. The direction from the input normal (|nx| &gt; |ny|: 1 when nx &gt; 0 else 3; otherwise
    /// 2 when ny &gt; 0 else 0) and 1818e7300; then along d = (top - bottom) normalised, the bottom
    /// moves up in 10-unit steps to the first point whose trace to the point plus 32 n is clear, and
    /// the top down the same way; the length (|top - bottom|, summed (dy^2 + dz^2) + dx^2), 1818e7300
    /// again, the area slots cleared and 1818e69b0.
    /// </summary>
    private static void BuildLadder(NavWriter.Mesh mesh, NavWriter.Ladder l, NavLadder input, int[] record, Trace trace)
    {
        l.Top = input.Top;
        l.Bottom = input.Bottom;
        l.Width = input.Width;
        var n0 = input.Normal;
        int direction;
        if (MathF.Abs(n0.Y) < MathF.Abs(n0.X))
            direction = 0f < n0.X ? 1 : 3;
        else
            direction = 0f < n0.Y ? 2 : 0;
        SetDirection(l, direction, trace);
        var v = l.Top - l.Bottom;
        var len = MathF.Sqrt((v.Z * v.Z + v.Y * v.Y) + v.X * v.X);
        Vector3 d;
        if (len < 1e-17f || 1e17f < len)
        {
            if (len != 0f)
                throw new NotSupportedException("a ladder whose length is outside 1e-17 .. 1e17 (18125d000) is not ported");
            d = Vector3.Zero;
            len = 0f;
        }
        else
        {
            var inv = 1f / len;
            d = new Vector3(inv * v.X, inv * v.Y, inv * v.Z);
        }
        if (0f <= len)
        {
            var f = 0f;
            do
            {
                var p = new Vector3(d.X * f + l.Bottom.X, d.Y * f + l.Bottom.Y, d.Z * f + l.Bottom.Z);
                if (Clear(p, l.Normal, trace))
                {
                    l.Bottom = p;
                    break;
                }
                f += 10f;
            } while (f <= len);
            f = 0f;
            do
            {
                var p = new Vector3(l.Top.X - d.X * f, l.Top.Y - d.Y * f, l.Top.Z - d.Z * f);
                if (Clear(p, l.Normal, trace))
                {
                    l.Top = p;
                    break;
                }
                f += 10f;
            } while (f <= len);
        }
        float dx = l.Top.X - l.Bottom.X, dy = l.Top.Y - l.Bottom.Y, dz = l.Top.Z - l.Bottom.Z;
        l.Length = MathF.Sqrt((dy * dy + dz * dz) + dx * dx);
        SetDirection(l, l.Direction, trace);
        Array.Clear(l.Areas);
        ConnectAreas(mesh, l, record);
    }

    /// <summary>The step's trace: from p to p + 32 n (the end summed n * 32 + p), clear when nothing is hit.</summary>
    private static bool Clear(Vector3 p, Vector3 n, Trace trace)
    {
        var end = new Vector3(n.X * 32f + p.X, n.Y * 32f + p.Y, n.Z * 32f + p.Z);
        return !Cast(trace, p, end).Hit;
    }

    /// <summary>1818d2c10's delta (end - start) and 1801f4e40's sphere centre (the shape's 0 plus the start).</summary>
    private static NavSphereWorld.NearestHit Cast(Trace trace, Vector3 start, Vector3 end)
        => trace(new Vector3(0f + start.X, 0f + start.Y, 0f + start.Z), new Vector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z));

    /// <summary>
    /// 1818e7300: the normal from the direction (0: -y, 1: +x, 2: +y, 3: -x); a trace from the centre
    /// ((bottom + top) * 0.5 + 5 n) to the centre less 32 n takes the hit's normal when it hits a shape
    /// whose InteractsAs has bit 8.
    /// </summary>
    private static void SetDirection(NavWriter.Ladder l, int direction, Trace trace)
    {
        l.Direction = direction;
        l.Normal = direction switch
        {
            0 => new Vector3(0f, -1f, 0f),
            1 => new Vector3(1f, 0f, 0f),
            2 => new Vector3(0f, 1f, 0f),
            3 => new Vector3(-1f, 0f, 0f),
            _ => Vector3.Zero,
        };
        var n = l.Normal;
        var start = new Vector3((l.Bottom.X + l.Top.X) * 0.5f + n.X * 5f, (l.Bottom.Y + l.Top.Y) * 0.5f + n.Y * 5f,
                                (l.Bottom.Z + l.Top.Z) * 0.5f + n.Z * 5f);
        var end = new Vector3(start.X - n.X * 32f, start.Y - n.Y * 32f, start.Z - n.Z * 32f);
        var hit = Cast(trace, start, end);
        if (hit.Hit && ((hit.InteractsAs >> 8) & 1) != 0)
        {
            if (float.IsNaN(hit.Normal.X))
                throw new NotSupportedException("a ladder's direction trace hit a hull or started inside a closed mesh (its normal is not ported)");
            l.Normal = hit.Normal;
        }
    }

    /// <summary>
    /// 1818e69b0: the bottom areas (record 4 .. 6; entry 4 at -1 keeps the slot) and the top areas
    /// (0 .. 3) into the slots; each area gets the ladder (1818ea300) on list 0 for a bottom, list 1
    /// for a top, bottoms first; then the top's z becomes the highest closest point (1818e4490) of the
    /// top areas when one lies above the bottom's z + 5.
    /// </summary>
    private static void ConnectAreas(NavWriter.Mesh mesh, NavWriter.Ladder l, int[] record)
    {
        NavWriter.Area? At(int k) => record[k] == -1 ? null : mesh.Areas[record[k]];
        // Slots in save order: +0x20, +0x28, +0x30, +0x38 (tops), +0x40, +0x48, +0x50 (bottoms).
        for (var k = 0; k < 7; k++)
            if (record[k] != -1)
                l.Areas[k] = At(k);
        foreach (var k in (int[])[4, 5, 6])
            if (l.Areas[k] is { } a)
                AddLadder(a, l, 0);
        foreach (var k in (int[])[0, 1, 2, 3])
            if (l.Areas[k] is { } a)
                AddLadder(a, l, 1);
        var z = l.Bottom.Z + 5f;
        var raised = false;
        foreach (var k in (int[])[0, 1, 2, 3])
        {
            if (l.Areas[k] is not { } a)
                continue;
            var c = ClosestPoint(mesh, a, l.Top);
            if (z < c.Z)
            {
                raised = true;
                z = c.Z;
            }
        }
        if (raised)
            l.Top = l.Top with { Z = z };
    }

    /// <summary>1818ea300: the ladder leaves both of the area's lists (1818eb970, the first entry of each), then joins <paramref name="list"/>.</summary>
    private static void AddLadder(NavWriter.Area a, NavWriter.Ladder l, int list)
    {
        a.Ladders[0].Remove(l.Id);
        a.Ladders[1].Remove(l.Id);
        a.Ladders[list].Add(l.Id);
    }

    /// <summary>
    /// 1818e4490 over the area's corners (1818dace0): for k from n - 1 down to 2, the closest point
    /// on (c0, ck, c(k-1)) (18129b060); a point replaces the best only when its squared distance,
    /// summed (dy^2 + dx^2) + dz^2, is below it. (0, 0, 0) for an area of fewer than three corners.
    /// </summary>
    private static Vector3 ClosestPoint(NavWriter.Mesh mesh, NavWriter.Area a, Vector3 p)
    {
        var poly = mesh.Polygons[a.Polygon];
        Vector3 C(int k) => new(mesh.Corners[poly[k]].X, mesh.Corners[poly[k]].Y, mesh.Corners[poly[k]].Z);
        var result = Vector3.Zero;
        var best = float.MaxValue;
        for (var k = poly.Length - 1; k >= 2; k--)
        {
            var c = ClosestPointOnTriangle(p, C(0), C(k), C(k - 1));
            float dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z;
            var dist = (dy * dy + dx * dx) + dz * dz;
            if (dist < best)
            {
                result = c;
                best = dist;
            }
        }
        return result;
    }

    /// <summary>
    /// 18129b060: the closest point to <paramref name="p"/> on the triangle (a, b, c), Ericson's
    /// regions in floats, each dot product summed as the disassembly does.
    /// </summary>
    internal static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
        float acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
        float apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
        var d1 = (apy * aby + apz * abz) + apx * abx;
        var d2 = (apz * acz + apy * acy) + apx * acx;
        if (d1 <= 0f && d2 <= 0f)
            return a;
        float bpx = p.X - b.X, bpy = p.Y - b.Y, bpz = p.Z - b.Z;
        var d3 = (bpz * abz + bpy * aby) + bpx * abx;
        var d4 = (bpz * acz + bpy * acy) + bpx * acx;
        if (0f <= d3 && d4 <= d3)
            return b;
        var vc = d4 * d1 - d3 * d2;
        if (vc <= 0f && 0f <= d1 && d3 <= 0f && !(d1 == 0f && d3 == 0f))
        {
            var v = d1 / (d1 - d3);
            return new Vector3(abx * v + a.X, aby * v + a.Y, abz * v + a.Z);
        }
        float cpx = p.X - c.X, cpy = p.Y - c.Y, cpz = p.Z - c.Z;
        var d5 = (cpz * abz + cpy * aby) + cpx * abx;
        var d6 = (cpz * acz + cpy * acy) + cpx * acx;
        if (0f <= d6 && d5 <= d6)
            return c;
        var vb = d5 * d2 - d6 * d1;
        if (vb <= 0f && 0f <= d2 && d6 <= 0f)
        {
            var w = d2 / (d2 - d6);
            return new Vector3(w * acx + a.X, w * acy + a.Y, w * acz + a.Z);
        }
        var va = d6 * d3 - d5 * d4;
        float e43 = d4 - d3, e56 = d5 - d6;
        if (va <= 0f && 0f <= e43 && 0f <= e56)
        {
            var w = e43 / (e56 + e43);
            return new Vector3((c.X - b.X) * w + b.X, (c.Y - b.Y) * w + b.Y, (c.Z - b.Z) * w + b.Z);
        }
        var denom = 1f / ((va + vb) + vc);
        float vv = denom * vb, ww = denom * vc;
        return new Vector3((abx * vv + a.X) + acx * ww, (aby * vv + a.Y) + acy * ww, (abz * vv + a.Z) + acz * ww);
    }
}
