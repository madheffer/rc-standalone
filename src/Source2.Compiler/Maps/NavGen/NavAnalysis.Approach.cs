namespace Source2.Compiler.Maps.NavGen;

public sealed partial class NavAnalysis
{
    /// <summary>
    /// The server's nav trace (CSNavMesh +0x50 vfn 8, 1814eb500): a sphere of radius 0.1 from the start
    /// to the end (see <see cref="NavSphereWorld"/>); its least fraction, 1 when nothing is hit. The
    /// trace's end position is start + (end - start) * fraction.
    /// </summary>
    public Func<V, V, float>? Trace;

    /// <summary>nav_approach_points_area_size_threshold (200): far areas with a side shorter than this are skipped.</summary>
    public float ApproachSizeThreshold = 200f;

    /// <summary>nav_potentially_visible_dot_tolerance (0.98).</summary>
    public float VisibleDotTolerance = 0.98f;

    /// <summary>An approach entry (CCSNavArea +0x108, 0x38 bytes each, at most 16).</summary>
    public sealed record ApproachEntry(Area? Prev, Area Here, byte HereBackEdge, Area Next, byte NextBackEdge, ushort PrevToHereHow,
                                       ushort HereToNextHow);

    private float TraceFraction(V start, V end)
        => (Trace ?? throw new InvalidOperationException("the nav analysis needs a trace")).Invoke(start, end);

    /// <summary>
    /// 1814b22d0 with the direction (0, 0, -1) (18029a0d0): down 10000 from 35.501 above the point; a hit
    /// counts when the start is clear and the trace either hits nothing or ends at least 35.5 from its
    /// start, else it tries again from 35.501 above where it ended (its start when it started solid) (16 tries, while the end is less than
    /// 100 above the point). The ground's z, or null.
    /// </summary>
    public float? GroundHeight(V pos)
    {
        const float dx = 0f, dy = 0f, dz = -1f;
        var start = new V(pos.X - dx * 35.501f, pos.Y - dy * 35.501f, pos.Z - dz * 35.501f);
        var end = new V(dx * 10000f + pos.X, dy * 10000f + pos.Y, dz * 10000f + pos.Z);
        var dot = (end.Z - pos.Z) * dz + (end.Y - pos.Y) * dy + (end.X - pos.X) * dx;
        for (var i = 0; -dot < 100f && i < 16; i++)
        {
            // A start-solid trace (its flag at +0x2b) goes on from its start (1814b2585 picks the start over
            // the end position); its fraction is 0, so the end position below is that start and the
            // retry is the same as for a hit closer than 35.5.
            var f = TraceFraction(start, end);
            var hit = new V((end.X - start.X) * f + start.X, (end.Y - start.Y) * f + start.Y, (end.Z - start.Z) * f + start.Z);
            if (f == 1f || 1260.25f <= (start.Y - hit.Y) * (start.Y - hit.Y) + (start.X - hit.X) * (start.X - hit.X) + (start.Z - hit.Z) * (start.Z - hit.Z))
                return hit.Z;
            start = new V(hit.X - dx * 35.501f, hit.Y - dy * 35.501f, hit.Z - dz * 35.501f);
            end = hit;
            dot = (end.Z - pos.Z) * dz + (end.Y - pos.Y) * dy + (end.X - pos.X) * dx;
        }
        return null;
    }

    /// <summary>
    /// The 1e-17 .. 1e17 normalisation: zero stays zero, any other length outside the range goes
    /// through server 18146bed0, instruction for instruction rc's VectorNormalize_Slow (18125d000).
    /// Its callers here (180398290 twice, 180391710's spot loop) read only the vector.
    /// </summary>
    private static V Normalize(V v)
    {
        var len = MathF.Sqrt(v.Z * v.Z + v.Y * v.Y + v.X * v.X);
        if (len < 1e-17f || 1e+17f < len)
        {
            if (len == 0f)
                return new V(0f, 0f, 0f);
            var n = VectorNormalizeSlow.Normalise(new System.Numerics.Vector3(v.X, v.Y, v.Z));
            return new V(n.X, n.Y, n.Z);
        }
        var inv = 1f / len;
        return new V(inv * v.X, inv * v.Y, inv * v.Z);
    }

    /// <summary>
    /// 180398290: whether <paramref name="area"/> may be visible from <paramref name="eye"/>: the trace to
    /// its centre 53.25 up is clear, or to a corner raised 53.25 + 53.25 whose direction from the eye
    /// differs from the centre's by a dot below <see cref="VisibleDotTolerance"/>.
    /// </summary>
    public bool IsPotentiallyVisible(Area area, V eye)
    {
        var c = area.Center;
        var target = new V(c.X + 0f, c.Y + 0f, c.Z + 53.25f);
        if (1f <= TraceFraction(eye, target))
            return true;
        var toCentre = Normalize(new V(c.X + 0f - eye.X, c.Y + 0f - eye.Y, c.Z + 53.25f - eye.Z));
        foreach (var k in area.Corners)
        {
            float x = k.X + 0f, y = k.Y + 0f, z = k.Z + 53.25f;
            var d = Normalize(new V(x - eye.X, y - eye.Y, z - eye.Z));
            if (d.Z * toCentre.Z + d.Y * toCentre.Y + d.X * toCentre.X < VisibleDotTolerance
                && 1f <= TraceFraction(eye, new V(x + 0f, y + 0f, z + 53.25f)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 180392280, the approach-area cost (vfn 0x90 of 1817bee30): -1 for an area whose id is on the
    /// blocked list, else the distance between the two areas' record positions. The search's position
    /// variable is left alone, so every area it pushes takes the start's position.
    /// </summary>
    private sealed class ApproachCost(List<uint> blocked) : IPathCost
    {
        public float Cost(NavAnalysis nav, Area from, Area to, Link link, int edge, V goalPos, ref V pos)
            => blocked.Contains(to.Id) ? -1f : Dist(nav.RecordOf(from).Pos, nav.RecordOf(to).Pos);
    }

    /// <summary>
    /// 180393060, CCSNavArea::ComputeApproachAreas: from the eye over the area's centre (ground height,
    /// then 63.9 up, 31.95 when crouching: attribute bit 16), for each far area (both search directions,
    /// mesh order) at least the size threshold on each side and not potentially visible: the path between
    /// them (A* with <see cref="ApproachCost"/>, centres, heuristic 1); its first area that is not
    /// potentially visible (one back when that is the far area itself) is the approach's next area, the
    /// one before it here and the one before that prev. The next area goes on the blocked list and the
    /// path is rebuilt until it fails, is shorter than 3, the list holds 256 ids (the area is done) or
    /// 16 entries exist; an entry whose here is already listed is not added again.
    /// </summary>
    public List<ApproachEntry> ApproachAreas(Area area)
    {
        var entries = new List<ApproachEntry>();
        var ground = GroundHeight(area.Center);
        if (ground is not { } gz)
            return entries;
        var eye = new V(area.Center.X, area.Center.Y, gz + (((area.Attributes >> 16) & 1) != 0 ? 31.95f : 63.9f));
        var blocked = new List<uint>();
        var cost = new ApproachCost(blocked);
        var path = new Area?[256];
        for (var searchType = 0; searchType < 2; searchType++)
            foreach (var far in Areas)
            {
                blocked.Clear();
                if (ApproachSizeThreshold > far.Max.X - far.Min.X || ApproachSizeThreshold > far.Max.Y - far.Min.Y)
                    continue;
                if (IsPotentiallyVisible(far, eye) || entries.Count >= 16)
                    continue;
                while (true)
                {
                    var (from, to) = searchType == 0 ? (area, far) : (far, area);
                    if (!AStar(from, to, from.Center, to.Center, 1f, 0, cost).Found)
                        break;
                    long count = 0;
                    for (var a = to; a != null; a = RecordOf(a).Parent)
                        count++;
                    if (256 < count)
                        count = 256;
                    else if (count < 3)
                        break;
                    var n = (int)count;
                    var walk = (Area?)to;
                    if (searchType == 0)
                        for (var k = n - 1; 0 <= k && walk != null; k--, walk = RecordOf(walk).Parent)
                            path[k] = walk;
                    else
                        for (var k = 0; k < n && walk != null; k++, walk = RecordOf(walk).Parent)
                            path[k] = walk;
                    for (var i = 1; i < n; i++)
                    {
                        var cur = path[i]!;
                        if (IsPotentiallyVisible(cur, eye))
                            continue;
                        if (blocked.Count == 256)
                            return entries;
                        var j = cur == far ? i - 1 : i;
                        if (j == 0)
                            continue;
                        var next = path[j]!;
                        blocked.Add(next.Id);
                        var here = path[j - 1]!;
                        if (!entries.Any(e => e.Here == here))
                        {
                            var rh = RecordOf(here);
                            var rn = RecordOf(next);
                            entries.Add(new ApproachEntry(j >= 2 ? path[j - 2] : null, here, rh.BackEdge, next, rn.BackEdge, (ushort)rh.How,
                                                          (ushort)rn.How));
                        }
                        break;
                    }
                    if (15 < entries.Count)
                        break;
                }
            }
        return entries;
    }
}
