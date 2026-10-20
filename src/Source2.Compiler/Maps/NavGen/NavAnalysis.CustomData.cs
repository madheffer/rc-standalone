using ValveKeyValue;

namespace Source2.Compiler.Maps.NavGen;

public sealed partial class NavAnalysis
{
    /// <summary>Each area's earliest occupy times (CCSNavArea +0xc8 T, +0xcc CT) once <see cref="Analyze"/> ran.</summary>
    public (float T, float CT)[] OccupyTimes = [];

    /// <summary>Each area's approach entries once <see cref="Analyze"/> ran.</summary>
    public readonly Dictionary<Area, List<ApproachEntry>> Approaches = [];

    /// <summary>
    /// 1803906a0, CS "Compute AI Data", in its order: earliest occupy times, hiding spots, encounter spots,
    /// then each area's approach areas. A map with a trigger_survival_playarea skips all but the times
    /// left at 120 and no approach areas (<paramref name="survival"/>).
    /// </summary>
    public void Analyze(IReadOnlyList<V> terroristSpawns, IReadOnlyList<V> ctSpawns, bool survival = false)
    {
        if (survival)
            throw new NotSupportedException("the survival play area path of the nav analysis is not ported");
        OccupyTimes = EarliestOccupyTimes(terroristSpawns, ctSpawns);
        ComputeHidingSpots();
        ComputeEncounterSpots();
        foreach (var area in Areas)
            Approaches[area] = ApproachAreas(area);
    }

    /// <summary>
    /// The .nav's CS custom data (CSNavMesh vfn 0x78, 18039ac00: arealist, 18039ad80 per area):
    /// hidingspotdata (id, pos, flags), spotencounterdata (from, fromdir, to, todir; the area's order
    /// array, rewritten by each encounter, keeps the last one's spots: id, t), approachdata (prev, here,
    /// herebackedge, next, nextbackedge, prevtoherehow, heretonexthow; -1 for no area) and
    /// earliestoccupytime (T, CT). The from / to and order ids are the low dwords of heap pointers in
    /// Valve's file; <paramref name="areaPointer"/> and <paramref name="spotPointer"/> give them (by
    /// default the area id and the spot id).
    /// </summary>
    /// <summary>tier0's KV3 integer as the save sets it (181693c70): 0 and 1 take the int64 zero / one types.</summary>
    private static KVObject Int(int v) => v is 0 or 1 ? new KVObject((long)v) : new KVObject(v);

    public KVObject CustomData(Func<Area, int>? areaPointer = null, Func<HidingSpot, int>? spotPointer = null)
    {
        areaPointer ??= a => (int)a.Id;
        spotPointer ??= s => (int)s.Id;
        var list = KVObject.Array();
        for (var i = 0; i < Areas.Count; i++)
        {
            var area = Areas[i];
            var entry = KVObject.Collection();
            var spots = KVObject.Array();
            foreach (var s in Spots.GetValueOrDefault(area) ?? [])
            {
                var spot = KVObject.Collection();
                spot.Add("id", Int((int)s.Id));
                var pos = KVObject.Array();
                pos.Add(new KVObject(s.Pos.X));
                pos.Add(new KVObject(s.Pos.Y));
                pos.Add(new KVObject(s.Pos.Z));
                spot.Add("pos", pos);
                spot.Add("flags", Int(s.Flags));
                spots.Add(spot);
            }
            entry.Add("hidingspotdata", spots);
            var encounters = KVObject.Array();
            KVObject? order = null;
            foreach (var e in Encounters.GetValueOrDefault(area) ?? [])
            {
                var enc = KVObject.Collection();
                enc.Add("from", Int(areaPointer(e.From)));
                enc.Add("fromdir", Int(e.FromDir));
                enc.Add("to", Int(areaPointer(e.To)));
                enc.Add("todir", Int(e.ToDir));
                encounters.Add(enc);
                order = KVObject.Array();
                foreach (var o in e.Spots)
                {
                    var item = KVObject.Collection();
                    item.Add("id", Int(spotPointer(o.Spot)));
                    item.Add("t", new KVObject((double)o.T));
                    order.Add(item);
                }
            }
            entry.Add("spotencounterdata", encounters);
            if (order != null)
                entry.Add("order", order);
            var approaches = KVObject.Array();
            foreach (var a in Approaches.GetValueOrDefault(area) ?? [])
            {
                var item = KVObject.Collection();
                item.Add("prev", Int(a.Prev is null ? -1 : (int)a.Prev.Id));
                item.Add("here", Int((int)a.Here.Id));
                item.Add("herebackedge", Int((sbyte)a.HereBackEdge));
                item.Add("next", Int((int)a.Next.Id));
                item.Add("nextbackedge", Int((sbyte)a.NextBackEdge));
                item.Add("prevtoherehow", Int(a.PrevToHereHow));
                item.Add("heretonexthow", Int(a.HereToNextHow));
                approaches.Add(item);
            }
            entry.Add("approachdata", approaches);
            var times = KVObject.Array();
            var (t, ct) = i < OccupyTimes.Length ? OccupyTimes[i] : (0f, 0f);
            foreach (var time in new[] { t, ct })
            {
                var item = KVObject.Collection();
                item.Add("time", new KVObject((double)time));
                times.Add(item);
            }
            entry.Add("earliestoccupytime", times);
            list.Add(entry);
        }
        var root = KVObject.Collection();
        root.Add("arealist", list);
        return root;
    }
}
