namespace Source2.Compiler.Maps.NavGen;

public sealed partial class NavAnalysis
{
    /// <summary>A hiding spot seen along an encounter path, at <paramref name="T"/> of the way.</summary>
    public sealed record SpotOrder(HidingSpot Spot, float T);

    /// <summary>
    /// A spot encounter (0x60 bytes, 180391710): from one connection of the area (+0 area, +8 its edge
    /// back, +0x10 the edge on this area) to another (+0x18, +0x20, +0x28), the path between the two
    /// portals' midpoints (+0x2c / +0x38) and the spots seen along it (+0x48 / +0x50).
    /// </summary>
    public sealed record SpotEncounter(Area From, int FromDir, Area To, int ToDir, V Start, V End, List<SpotOrder> Spots);

    /// <summary>Per area, its spot encounters (CCSNavArea +0xc0).</summary>
    public readonly Dictionary<Area, List<SpotEncounter>> Encounters = [];

    private int _encounterMarker;
    private readonly Dictionary<HidingSpot, int> _spotMarker = [];

    /// <summary>
    /// 180393ea0: per area, its connections (1814e1950 with flags 1) taken in every ordered pair of two
    /// different areas (<see cref="Encounter"/>).
    /// </summary>
    public void ComputeEncounterSpots()
    {
        foreach (var area in Areas)
        {
            var list = new List<SpotEncounter>();
            Encounters[area] = list;
            var entries = new List<(Area To, int How, byte Back)>();
            for (var e = 0; e < area.Links.Length; e++)
                foreach (var link in area.Links[e])
                    entries.Add((link.To, e, (byte)link.BackEdge));
            foreach (var a in entries)
                foreach (var b in entries)
                    if (a.To != b.To && Encounter(area, a, b) is { } enc)
                        list.Add(enc);
        }
    }

    /// <summary>
    /// 180391710: the path from the midpoint of the portal to <paramref name="from"/> to the midpoint of
    /// the portal to <paramref name="to"/>, each 62 above its area's z there; along it every 25 (and at
    /// its end), each in-cover hiding spot (all of the analysis's, in creation order) not yet seen on
    /// this path within 2000 of the point (spot 62 up) with a clear trace to 35.5 above it is seen; it
    /// counts when the direction to it makes a dot with the path's strictly between -0.7071 and 0.7071
    /// and the point is past the start, at the fraction of the path's length. Connections with no edge
    /// back (0xff) make none.
    /// </summary>
    private SpotEncounter? Encounter(Area area, (Area To, int How, byte Back) from, (Area To, int How, byte Back) to)
    {
        if (from.Back == 0xff || to.Back == 0xff)
            return null;
        var (pa0, pa1) = Portal(area, from.To, from.How, from.Back);
        var (pb0, pb1) = Portal(area, to.To, to.How, to.Back);
        float ax = (pa1.X - pa0.X) * 0.5f + pa0.X, ay = (pa1.Y - pa0.Y) * 0.5f + pa0.Y;
        float bx = (pb1.X - pb0.X) * 0.5f + pb0.X, by = (pb1.Y - pb0.Y) * 0.5f + pb0.Y;
        var start = new V(ax, ay, ZAt(from.To, ax, ay) + 62f);
        var end = new V(bx, by, ZAt(to.To, bx, by) + 62f);
        float dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z;
        var len = MathF.Sqrt(dz * dz + dy * dy + dx * dx);
        V dir;
        if (len < 1e-17f || 1e+17f < len)
        {
            if (len != 0f)
            {
                // 18146bed0 (180391a0c): the length is its return value, which the walk below runs to.
                var n = VectorNormalizeSlow.Normalise(new System.Numerics.Vector3(dx, dy, dz), out len);
                dir = new V(n.X, n.Y, n.Z);
            }
            else
            {
                dir = new V(0f, 0f, 0f);
                len = 0f;
            }
        }
        else
        {
            var inv = 1f / len;
            dir = new V(inv * dx, inv * dy, inv * dz);
        }
        var marker = ++_encounterMarker;
        var seen = new List<SpotOrder>();
        var t = 0f;
        var last = false;
        do
        {
            if (len <= t)
            {
                last = true;
                t = len;
            }
            var eye = new V(dir.X * t + start.X, dir.Y * t + start.Y, dir.Z * t + start.Z);
            foreach (var spot in Areas.SelectMany(a => Spots.TryGetValue(a, out var s) ? s : []).OrderBy(s => s.Id))
            {
                if ((spot.Flags & 1) == 0 || _spotMarker.GetValueOrDefault(spot) == marker)
                    continue;
                var sx = spot.Pos.X - eye.X;
                var sy = spot.Pos.Y - eye.Y;
                var sz = spot.Pos.Z + 62f - eye.Z;
                if (!(sy * sy + sx * sx + sz * sz <= 4000000f))
                    continue;
                if (TraceFraction(eye, new V(spot.Pos.X, spot.Pos.Y, spot.Pos.Z + 35.5f)) != 1f)
                    continue;
                var n = Normalize(new V(sx, sy, sz));
                var dot = dir.Z * n.Z + dir.Y * n.Y + dir.X * n.X;
                if (dot < 0.7071f && -0.7071f < dot && 0f < t)
                    seen.Add(new SpotOrder(spot, t / len));
                _spotMarker[spot] = marker;
            }
            t += 25f;
        } while (!last);
        return new SpotEncounter(from.To, from.How, to.To, to.How, start, end, seen);
    }
}
