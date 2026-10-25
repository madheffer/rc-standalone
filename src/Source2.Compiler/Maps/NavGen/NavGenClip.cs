namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The physics world ClipPolysToHullClearance casts boxes through: world vfn 0x558 (vphysics2
/// 180065360 -> CRnWorld::CastShape 1801f5080) with a type-2 shape (an axis-aligned box from
/// <c>mins</c> to <c>maxs</c> about the start) moved by <c>delta</c>; the result's fraction
/// (+0x34), 1 when nothing is hit. The query is navsystem's (18004b370): interacts with 0xc3021,
/// excludes 0x400000, queries as 0x80000, static bodies, group 4, flags 0x49.
/// </summary>
public interface INavBoxWorld
{
    float CastBox((float X, float Y, float Z) mins, (float X, float Y, float Z) maxs, (float X, float Y, float Z) start,
                  (float X, float Y, float Z) delta);
}

public sealed partial class NavGenPolys
{
    // A polygon clipped against a plane (the 0x628-byte buffers 18004fe60 works on: up to 0x34 corners).
    private sealed class ClipPoly
    {
        public readonly List<(double X, double Y, double Z)> Corners = [];
        public double Nx, Ny, Nz;
    }

    // DAT_180129488: the "no point" triple.
    private static readonly (double X, double Y, double Z) NoPoint = (double.MaxValue, double.MaxValue, double.MaxValue);

    /// <summary>
    /// ClipPolysToHullClearance (022, 18006c6b0) for one hull: skipped (the polygons pass through) for
    /// the projected meshes (+0x61), an oriented mesh (input +0x128) or a hull that can crawl; the
    /// height is the short height when that is enabled. 1800544e0 copies the polygons into a work
    /// list and runs 180054ae0 over it until it is empty, each pass's split pieces making the next
    /// list; after five passes that do not shrink it, it warns and stops (what is left is dropped).
    /// </summary>
    public void ClipPolysToHullClearance(NavHull hull, INavBoxWorld world, bool orientedMesh)
    {
        if (orientedMesh || hull.CrawlEnabled)
            return;
        var height = hull.ShortHeightEnabled ? hull.ShortHeight : hull.Height;
        double climb = hull.MaxClimb, radius = hull.Radius, h = height;
        var output = new List<NavPoly>();
        var work = new List<NavPoly>(Polys);
        var passes = 5;
        while (passes >= 1 && work.Count != 0)
        {
            var next = new List<NavPoly>();
            ClipProcessList(output, work, next, world, climb, radius, h);
            if (work.Count <= next.Count && --passes < 1)
            {
                // Warning("ClipPolysToHullClearanceInternal - too many non-reducing passes")
            }
            work = next;
        }
        Polys = output;
    }

    // 180054ae0.
    private void ClipProcessList(List<NavPoly> output, List<NavPoly> work, List<NavPoly> next, INavBoxWorld world,
                                 double climbD, double radiusD, double heightD)
    {
        var sqrt2 = Math.Sqrt(2.0);
        var climb = (float)climbD;
        var height = (float)heightD;
        var reach = (float)(radiusD / sqrt2);
        foreach (var p in work)
        {
            var n = p.Indices.Count;
            var clear = true;
            for (var i = 0; i < n; i++)
                clear = clear && EdgeClear(p, i, 0f, climb, reach, height, world);
            if (clear)
            {
                output.Add(p);
                continue;
            }
            // The first clear point along the edges, 0.2 apart.
            var startEdge = -1;
            var startT = -1.0;
            for (var e = 0; e < n && startEdge == -1; e++)
            {
                var a = Corner(p, e);
                var b = Corner(p, (e + 1) % n);
                for (var t = 0.0; startEdge == -1 && t < 1.0; t += 0.20000000298023224)
                {
                    var tf = (double)(float)t;
                    var q = ((b.X - a.X) * tf + a.X, (b.Y - a.Y) * tf + a.Y, (b.Z - a.Z) * tf + a.Z);
                    if (1f <= BoxFraction(q, q, climb, reach, height, world))
                    {
                        startEdge = e;
                        startT = t;
                    }
                }
            }
            if (startEdge == -1)
                continue; // "clip section failed": the polygon is dropped.

            // Forward from the clear point to the first blocked stretch.
            var hitEdge = -1;
            var hitT = 0.0;
            for (var k = 0; k <= n; k++)
            {
                var e = (k + n + startEdge) % n;
                var a = Corner(p, e);
                var b = Corner(p, (k + n + startEdge + 1) % n);
                var t0 = k != 0 ? 0.0 : startT;
                var t1 = k != n ? 1.0 : startT;
                var f0 = (double)(float)t0;
                var p0 = (f0 * (b.X - a.X) + a.X, f0 * (b.Y - a.Y) + a.Y, f0 * (b.Z - a.Z) + a.Z);
                var f1 = (double)(float)t1;
                var p1 = ((b.X - a.X) * f1 + a.X, (b.Y - a.Y) * f1 + a.Y, (b.Z - a.Z) * f1 + a.Z);
                var f = BoxFraction(p0, p1, climb, reach, height, world);
                if (f < 1f)
                {
                    hitEdge = e;
                    hitT = (t1 - t0) * f + t0;
                    break;
                }
            }
            if (hitEdge == -1)
            {
                output.Add(p);
                continue;
            }
            // Backward from the clear point to the first blocked stretch.
            var backEdge = -1;
            var backT = 0.0;
            for (var k = 0; k <= n; k++)
            {
                var e = (n - k + startEdge) % n;
                var a = Corner(p, e);
                var b = Corner(p, (n - k + startEdge + 1) % n);
                var t0 = k == 0 ? startT : 1.0;
                var t1 = k != n ? 0.0 : startT;
                var f0 = (double)(float)t0;
                var p0 = (f0 * (b.X - a.X) + a.X, f0 * (b.Y - a.Y) + a.Y, f0 * (b.Z - a.Z) + a.Z);
                var f1 = (double)(float)t1;
                var p1 = ((b.X - a.X) * f1 + a.X, (b.Y - a.Y) * f1 + a.Y, f1 * (b.Z - a.Z) + a.Z);
                var f = BoxFraction(p0, p1, climb, reach, height, world);
                if (f < 1f)
                {
                    backEdge = e;
                    backT = (t1 - t0) * f + t0;
                    break;
                }
            }
            if (backEdge == -1)
            {
                output.Add(p);
                continue;
            }
            ClipSection(p, output, next, world, climb, reach, height, backEdge, backT, hitEdge, hitT);
        }
    }

    // The tail of 180054ae0 once the clear stretch runs from (backEdge, backT) to (hitEdge, hitT).
    private void ClipSection(NavPoly p, List<NavPoly> output, List<NavPoly> next, INavBoxWorld world, float climb, float reach, float height,
                             int backEdge, double backT, int hitEdge, double hitT)
    {
        var split = false;
        var length = BoundaryLength(p, backEdge, backT, hitEdge, hitT);
        if (36f < (float)length)
        {
            var mid = BoundaryPoint(p, climb, reach, height, world, (double)((float)length * 0.5f), backEdge, backT, hitEdge, hitT, true);
            if (mid != NoPoint)
                split = true;
        }
        var a = Corner(p, backEdge);
        var b = Corner(p, backEdge + 1);
        var tb = (double)(float)backT;
        var bx = (b.X - a.X) * tb + a.X;
        var bz = (b.Z - a.Z) * tb + a.Z;
        var by = (b.Y - a.Y) * tb + a.Y;
        a = Corner(p, hitEdge);
        b = Corner(p, hitEdge + 1);
        var tf = (double)(float)hitT;
        var fx = (b.X - a.X) * tf + a.X;
        var fy = (b.Y - a.Y) * tf + a.Y;
        var fz = (b.Z - a.Z) * tf + a.Z;
        var dx = fx - bx;
        var dy = fy - by;
        if (split)
        {
            var dz = fz - bz;
            var len = Math.Sqrt(dy * dy + dx * dx + dz * dz);
            double nx = dx, ny = dy, nz = dz;
            if (1.1920928955078125e-07 < len)
            {
                var inv = 1.0 / len;
                nz = dz * inv;
                nx = dx * inv;
                ny = dy * inv;
            }
            var d = (fy + by) * 0.5 * ny + (fx + bx) * 0.5 * nx + (fz + bz) * 0.5 * nz;
            var (front, back) = SplitByPlane(ClipSource(p), (nx, ny, nz, d, 0.5));
            if (front.Corners.Count < 3 || back.Corners.Count < 3)
                split = false;
            else
            {
                AddClipped(front, p, next);
                AddClipped(back, p, next);
            }
        }
        if (split)
            return;
        double px = -dy, py = dx, pz = 0.0;
        var plen = Math.Sqrt(dx * dx + -dy * -dy + 0.0);
        if (1.1920928955078125e-07 < plen)
        {
            var inv = 1.0 / plen;
            px = inv * -dy;
            py = inv * dx;
            pz = inv * 0.0;
        }
        if (!FindClipDistance(p, (px, py, pz), climb, reach, height, world))
            return; // "clip to clip section boundaries failed": dropped.
        var plane = (px, py, pz, ((by * py + bx * px + bz * pz) - 0.1) - 0.5, 0.1);
        var (_, kept) = SplitByPlane(ClipSource(p), plane);
        if (kept.Corners.Count == 0)
            return; // "poly eliminated by clipping to clip section".
        AddClipped(kept, p, output);
    }

    private ClipPoly ClipSource(NavPoly p)
    {
        var c = new ClipPoly { Nx = p.Nx, Ny = p.Ny, Nz = p.Nz };
        foreach (var k in p.Indices)
            c.Corners.Add(Pool[k]);
        return c;
    }

    // 180052f40: the edge pulled in toward the centroid by min(inset, its distance), cast end to end
    // with the floor 2.5 higher.
    private bool EdgeClear(NavPoly p, int i, float inset, float climb, float reach, float height, INavBoxWorld world)
    {
        var a = Corner(p, i);
        var b = Corner(p, i + 1);
        var n = EdgeOutward(p, i);
        var c = Centroid(p);
        var dist = (float)((c.Y - a.Y) * -n.Y + (c.X - a.X) * -n.X + (c.Z - a.Z) * -n.Z);
        if (dist <= inset)
            inset = dist;
        var s = (double)inset;
        var ox = -n.X * s;
        var oy = -n.Y * s;
        var oz = -n.Z * s;
        var p0 = (a.X + ox, a.Y + oy, a.Z + oz);
        var p1 = (b.X + ox, b.Y + oy, b.Z + oz);
        return 1f <= BoxFraction(p0, p1, climb + 2.5f, reach, height, world);
    }

    // 18004f280: the largest (y n.y + x n.x) + z n.z over the corners.
    private double Support(NavPoly p, (double X, double Y, double Z) n)
    {
        var best = -double.MaxValue;
        foreach (var k in p.Indices)
        {
            var v = Pool[k];
            var d = n.Y * v.Y + n.X * v.X + n.Z * v.Z;
            if (best <= d)
                best = d;
        }
        return best;
    }

    // 18004b370 with 18004b580: the box (-reach, -reach, climb) .. (reach, reach, height) cast from p0
    // to p1, both lifted by the ground under the path: four zero-width box drops (half reach / 2, from
    // min(climb + 5, height - reach / 2) above, at 0, 1/3, 2/3 and 1 along it), the deepest stop.
    private static float BoxFraction((double X, double Y, double Z) p0, (double X, double Y, double Z) p1, float climb, float reach,
                                     float height, INavBoxWorld world)
    {
        if (reach == -reach && height == climb)
            throw new NotSupportedException("the nav ray cast (a zero box) is not ported");
        var s = reach * 0.5f;
        var drop = height - s;
        if (climb + 5f <= height - s)
            drop = climb + 5f;
        var lift = 0f;
        for (var i = 0; i < 4; i++)
        {
            var t = (double)((float)i / 3f);
            var start = ((float)((p1.X - p0.X) * t + p0.X) + 0f, (float)((p1.Y - p0.Y) * t + p0.Y) + 0f, (float)((p1.Z - p0.Z) * t + p0.Z) + drop);
            var f = world.CastBox((-s, -s, 0f), (s, s, 1f), start, (-0f, -0f, -drop));
            var up = (1f - f) * drop;
            if (lift < up)
                lift = up;
        }
        var sx = (float)p0.X + 0f;
        var sy = (float)p0.Y + 0f;
        var sz = (float)p0.Z + lift;
        var delta = (((float)p1.X + 0f) - sx, ((float)p1.Y + 0f) - sy, ((float)p1.Z + lift) - sz);
        return world.CastBox((-reach, -reach, climb), (reach, reach, height), (sx, sy, sz), delta);
    }

    // 180053e40: the boundary's length from (back edge, back t) forward to (hit edge, hit t).
    private double BoundaryLength(NavPoly p, int backEdge, double backT, int hitEdge, double hitT)
    {
        var n = p.Indices.Count;
        if (backEdge != hitEdge || backT <= hitT)
        {
            var a = Corner(p, hitEdge);
            var e = (hitEdge + 1) % n;
            var b = Corner(p, e);
            var tf = (double)(float)hitT;
            var ry = ((b.Y - a.Y) * tf + a.Y) - b.Y;
            var rx = ((b.X - a.X) * tf + a.X) - b.X;
            var rz = ((b.Z - a.Z) * tf + a.Z) - b.Z;
            var total = Math.Sqrt(ry * ry + rx * rx + rz * rz) + 0.0;
            while (e != backEdge)
            {
                a = Corner(p, e);
                e = (e + 1) % n;
                b = Corner(p, e);
                total += Math.Sqrt((b.Y - a.Y) * (b.Y - a.Y) + (b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            }
            a = Corner(p, backEdge);
            b = Corner(p, (backEdge + 1) % n);
            var tb = (double)(float)backT;
            var qy = a.Y - ((b.Y - a.Y) * tb + a.Y);
            var qx = a.X - ((b.X - a.X) * tb + a.X);
            var qz = a.Z - ((b.Z - a.Z) * tb + a.Z);
            return Math.Sqrt(qx * qx + qy * qy + qz * qz) + total;
        }
        var c = Corner(p, backEdge);
        var d = Corner(p, (backEdge + 1) % n);
        var th = (double)(float)hitT;
        var tb2 = (double)(float)backT;
        var gz = ((d.Z - c.Z) * tb2 + c.Z) - ((d.Z - c.Z) * th + c.Z);
        var gy = ((d.Y - c.Y) * tb2 + c.Y) - ((d.Y - c.Y) * th + c.Y);
        var gx = ((d.X - c.X) * tb2 + c.X) - (th * (d.X - c.X) + c.X);
        return Math.Sqrt(gx * gx + gy * gy + gz * gz);
    }

    // 18005c130: walking the boundary from the hit point forward to the back point in fifths of each
    // edge, the clear sample (when testing) whose walked distance is nearest the target.
    private (double X, double Y, double Z) BoundaryPoint(NavPoly p, float climb, float reach, float height, INavBoxWorld world, double target,
                                                         int backEdge, double backT, int hitEdge, double hitT, bool test)
    {
        var n = p.Indices.Count;
        var result = NoPoint;
        var best = double.MaxValue;
        bool Clear((double X, double Y, double Z) q) => !test || 1f <= BoxFraction(q, q, climb, reach, height, world);
        void Consider(double walked, (double X, double Y, double Z) q)
        {
            var off = Math.Abs(walked - target);
            if (off <= best && best != off)
            {
                result = q;
                best = off;
            }
        }
        if (backEdge != hitEdge || backT <= hitT)
        {
            var walked = 0.0;
            var a = Corner(p, hitEdge);
            var e = (hitEdge + 1) % n;
            var b = Corner(p, e);
            var th = (double)(float)hitT;
            var sy = (b.Y - a.Y) * th + a.Y;
            var sx = (b.X - a.X) * th + a.X;
            var sz = (b.Z - a.Z) * th + a.Z;
            var seg = Math.Sqrt((sx - b.X) * (sx - b.X) + (sy - b.Y) * (sy - b.Y) + (sz - b.Z) * (sz - b.Z)) * 0.20000000298023224;
            for (var t = 0.20000000298023224; t < 1.0; t += 0.20000000298023224)
            {
                walked += seg;
                var f = (double)(float)t;
                var q = ((b.X - sx) * f + sx, (b.Y - sy) * f + sy, (b.Z - sz) * f + sz);
                if (Clear(q))
                    Consider(walked, q);
            }
            while (e != backEdge)
            {
                a = Corner(p, e);
                e = (e + 1) % n;
                b = Corner(p, e);
                var step = Math.Sqrt((a.Y - b.Y) * (a.Y - b.Y) + (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)) * 0.20000000298023224;
                for (var t = 0.20000000298023224; t < 1.0; t += 0.20000000298023224)
                {
                    walked += step;
                    var f = (double)(float)t;
                    var q = ((b.X - a.X) * f + a.X, (b.Y - a.Y) * f + a.Y, (b.Z - a.Z) * f + a.Z);
                    if (Clear(q))
                        Consider(walked, q);
                }
            }
            a = Corner(p, backEdge);
            b = Corner(p, (backEdge + 1) % n);
            var tb = (double)(float)backT;
            var ex = (b.X - a.X) * tb + a.X;
            var ey = (b.Y - a.Y) * tb + a.Y;
            var ez = (b.Z - a.Z) * tb + a.Z;
            var last = Math.Sqrt((a.X - ex) * (a.X - ex) + (a.Y - ey) * (a.Y - ey) + (a.Z - ez) * (a.Z - ez)) * 0.20000000298023224;
            for (var t = 0.20000000298023224; t < 1.0; t += 0.20000000298023224)
            {
                walked += last;
                var f = (double)(float)t;
                var q = ((ex - a.X) * f + a.X, (ey - a.Y) * f + a.Y, (ez - a.Z) * f + a.Z);
                if (Clear(q))
                    Consider(walked, q);
            }
            return result;
        }
        {
            var c = Corner(p, backEdge);
            var d = Corner(p, (backEdge + 1) % n);
            var th = (double)(float)hitT;
            var hx = (d.X - c.X) * th + c.X;
            var hy = (d.Y - c.Y) * th + c.Y;
            var hz = th * (d.Z - c.Z) + c.Z;
            var tb = (double)(float)backT;
            var gx = (tb * (d.X - c.X) + c.X) - hx;
            var gy = (tb * (d.Y - c.Y) + c.Y) - hy;
            var gz = (tb * (d.Z - c.Z) + c.Z) - hz;
            var len = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            var walked = 0.0;
            for (var t = 0.20000000298023224; t < 1.0; t += 0.20000000298023224)
            {
                walked += len * 0.20000000298023224;
                var f = (double)(float)t;
                var q = (f * gx + hx, f * gy + hy, f * gz + hz);
                if (Clear(q))
                    Consider(walked, q);
            }
            return result;
        }
    }

    // 18004ea70 with 180052b10: the first inset 0, 0.3, ... below 24 at which the polygon's part
    // beyond the plane (normal n, the support distance less the inset) has room for the box along
    // its on-plane extent.
    private bool FindClipDistance(NavPoly p, (double X, double Y, double Z) n, float climb, float reach, float height, INavBoxWorld world)
    {
        for (var f = 0f; f < 24f; f += 0.3f)
            if (ClipDirectionClear(p, n, f, climb, reach, height, world))
                return true;
        return false;
    }

    // 180052b10 (TestBoxHullForClipDir).
    private bool ClipDirectionClear(NavPoly p, (double X, double Y, double Z) n, double inset, float climb, float reach, float height,
                                    INavBoxWorld world)
    {
        var support = Support(p, n);
        var c = Centroid(p);
        if (!(inset <= support - (n.X * c.X + n.Y * c.Y + c.Z * n.Z)))
            return false;
        var d = support - inset;
        var (_, back) = SplitByPlane(ClipSource(p), (n.X, n.Y, n.Z, d, 0.10000000149011612));
        if (back.Corners.Count == 0)
            return true; // Warning("TestBoxHullForClipDir - polyBack is empty")
        var hi = -double.MaxValue;
        var lo = double.MaxValue;
        var pMax = NoPoint;
        var pMin = NoPoint;
        foreach (var v in back.Corners)
        {
            if (!(Math.Abs((n.Y * v.Y + n.X * v.X + n.Z * v.Z) - d) < 0.20000000298023224))
                continue;
            var along = v.X * -n.Y + v.Y * n.X + v.Z * 0.0;
            if (hi < along)
            {
                pMax = v;
                hi = along;
            }
            if (along < lo)
            {
                pMin = v;
                lo = along;
            }
        }
        return 1f <= BoxFraction(pMin, pMax, climb, reach, height, world);
    }

    // 18004fe60: corners in front of the plane (beyond eps) and on it go to front, behind and on it
    // to back, with the crossings on both; a side with fewer than three corners is emptied.
    private static (ClipPoly Front, ClipPoly Back) SplitByPlane(ClipPoly src, (double X, double Y, double Z, double D, double Eps) plane)
    {
        var front = new ClipPoly { Nx = src.Nx, Ny = src.Ny, Nz = src.Nz };
        var back = new ClipPoly { Nx = src.Nx, Ny = src.Ny, Nz = src.Nz };
        var n = src.Corners.Count;
        int Class((double X, double Y, double Z) v)
        {
            var dist = (v.Y * plane.Y + v.X * plane.X + v.Z * plane.Z) - plane.D;
            if (dist <= plane.Eps)
                return -plane.Eps <= dist ? 2 : 1;
            return 0;
        }
        (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        {
            // 180080820.
            var t = (plane.D - (plane.Y * a.Y + plane.X * a.X + plane.Z * a.Z))
                    / (plane.Y * (b.Y - a.Y) + plane.X * (b.X - a.X) + plane.Z * (b.Z - a.Z));
            return ((b.X - a.X) * t + a.X, (b.Y - a.Y) * t + a.Y, (b.Z - a.Z) * t + a.Z);
        }
        var prev = src.Corners[n - 1];
        var cls = Class(prev);
        var prevPrev = 3;
        for (var i = 0; i < n; i++)
        {
            var cur = src.Corners[i];
            var prevCls = cls;
            cls = Class(cur);
            if (cls == 1)
            {
                if (prevCls == 0)
                {
                    var x = Cross(prev, cur);
                    front.Corners.Add(x);
                    back.Corners.Add(x);
                }
                else if (prevCls == 2 && prevPrev != 1)
                    back.Corners.Add(prev);
                back.Corners.Add(cur);
            }
            else if (cls == 2)
            {
                front.Corners.Add(cur);
                var add = prevCls == 1;
                if (add)
                {
                    var j = (i + 1) % n;
                    if (j < i)
                    {
                        var v = src.Corners[j];
                        var dist = (v.Y * plane.Y + v.X * plane.X + v.Z * plane.Z) - plane.D;
                        if (dist <= plane.Eps && dist <= -plane.Eps && -plane.Eps != dist)
                            add = false;
                    }
                }
                if (add)
                    back.Corners.Add(cur);
            }
            else
            {
                if (prevCls == 1)
                {
                    var x = Cross(cur, prev);
                    front.Corners.Add(x);
                    back.Corners.Add(x);
                }
                front.Corners.Add(cur);
            }
            prevPrev = prevCls;
            prev = cur;
        }
        if (front.Corners.Count < 3)
            front.Corners.Clear();
        if (back.Corners.Count < 3)
            back.Corners.Clear();
        return (front, back);
    }

    // 18004c280 -> 18004bbe0: the clipped polygon fanned from its first corner into quads (0, k, k + 1,
    // k + 2) and a last triangle, each piece's corners appended to the pool afresh; kind, +0x62 and +0x64
    // from the source, flags 0, the normal of its first three corners. Returns the pieces added.
    private int AddClipped(ClipPoly c, NavPoly src, List<NavPoly> to)
    {
        var count = c.Corners.Count;
        if (count - 1 < 2)
            return 0;
        var added = 0;
        for (var k = 1; k < count - 1; k += 2)
        {
            var size = k + 2 < count ? 4 : 3;
            var idx = new List<int>();
            idx.Add(Pool.Count);
            Pool.Add(c.Corners[0]);
            for (var m = 0; m < size - 1; m++)
            {
                idx.Add(Pool.Count);
                Pool.Add(c.Corners[k + m]);
            }
            var p0 = Pool[idx[0]];
            var p1 = Pool[idx[1]];
            var p2 = Pool[idx[2]];
            double e1x = p1.X - p0.X, e1y = p1.Y - p0.Y, e1z = p1.Z - p0.Z;
            double e2y = p2.Y - p0.Y, e2z = p2.Z - p0.Z, e2x = p2.X - p0.X;
            var nx = e2z * e1y - e2y * e1z;
            var ny = e2x * e1z - e2z * e1x;
            var nz = e2y * e1x - e2x * e1y;
            var len = Math.Sqrt(ny * ny + nx * nx + nz * nz);
            if (1.1920928955078125e-07 < len)
            {
                var inv = 1.0 / len;
                nz *= inv;
                ny *= inv;
                nx *= inv;
            }
            if (len == 0.0)
                nx = ny = nz = 0.0;
            to.Add(new NavPoly
            {
                Indices = idx, Kind = src.Kind, Flags = 0, Nx = nx, Ny = ny, Nz = nz, Field62 = src.Field62, Field64 = src.Field64,
            });
            added++;
        }
        return added;
    }
}
