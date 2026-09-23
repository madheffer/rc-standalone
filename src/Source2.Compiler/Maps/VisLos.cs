using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The line-of-sight files and the CLOS generator that replays them.
///
/// <para>The compile builds two paths: <c>los</c>, under <c>game\</c>, loaded
/// and verified against the scene, and <c>los_errors</c>,
/// <c>content/csgo_addons/&lt;addon&gt;/maps/&lt;map&gt;.los</c>, loaded
/// unverified. The deterministic build hands CLOS the <c>los_errors</c> set.
/// It never writes either: the scan driver clears its recording flag before
/// the generators run, so the writer is always handed an empty set, and nothing
/// in visbuilder writes <c>los_errors</c> at all.</para>
///
/// <para>What CLOS does with a supplied file follows from the binary and was
/// confirmed by feeding probe01 2,000 segments: nothing. An unverified hint's
/// flag stays 0, so its ray is type 0 with flag 0; <c>BatchTracer</c> emits a
/// segment for a miss only from a type 2 ray and for a hit only when the flag is
/// set, and the continuation ray a type 0 hit queues is freed with its list
/// unread. The matrix after CLOS was byte identical.</para>
/// </summary>
public static class VisLos
{
    /// <summary>One segment of a hint file: 24 bytes, start then end.</summary>
    public readonly record struct Segment(Vector3 Start, Vector3 End);

    /// <summary>
    /// <c>Logs_ErrorLoading</c>'s read: a u32 count, then that many segments. A
    /// count the rest of the file cannot hold loads nothing. A missing file loads
    /// nothing.
    /// </summary>
    public static Segment[] Read(string path)
    {
        if (!File.Exists(path))
            return [];
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4)
            return [];
        var count = BitConverter.ToUInt32(bytes, 0);
        if ((uint)(bytes.Length - 4) / 24 < count)
            return [];
        var found = new Segment[count];
        for (var i = 0; i < count; i++)
        {
            var at = 4 + (i * 24);
            found[i] = new Segment(V(bytes, at), V(bytes, at + 12));
        }
        return found;
    }

    /// <summary>The <c>los_errors</c> path the compile builds for a map.</summary>
    public static string ErrorsPath(string contentRoot, string addon, string map)
        => Path.Combine(contentRoot, "csgo_addons", addon, "maps", map + ".los");

    /// <summary>
    /// <c>CLOSRayGenerator</c>: one pass, one ray per hint from its start toward
    /// its end, type 0 and flagged only when the loader verified it (never, for
    /// the <c>los_errors</c> set). Returns the rays cast.
    /// </summary>
    public static int Replay(VisPvs.State s, RayTraceEnvironment rte, VisPvs.Matrix matrix,
                             IReadOnlyList<Segment> hints, IReadOnlyList<bool>? verified = null)
    {
        if (hints.Count == 0)
            return 0;
        var reach = VisPvs.Reach(rte);
        var sets = new int[hints.Count][];
        Parallel.For(0, hints.Count, i =>
        {
            var (start, end) = (hints[i].Start, hints[i].End);
            var ray = VisPvs.Toward(start, end);
            var flag = verified is not null && verified[i];
            var type = flag ? 1 : 0;
            if (VisPvs.Traced(rte, ray, type, flag, reach) is not { } to)
                return;
            // No type 2 ray in the batch, so the fold's other branch: step one
            // unit in from the start, and a blocker ends the walk.
            if (VisPvs.WalkSegment(s, ray.Origin, to, sight: false) is { Count: > 0 } ids)
            {
                var sorted = ids.Distinct().ToArray();
                Array.Sort(sorted);
                sets[i] = sorted;
            }
        });
        foreach (var ids in sets)
        {
            if (ids is not null)
                matrix.Or(ids);
        }
        return hints.Count;
    }

    private static Vector3 V(byte[] b, int at)
        => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
}
