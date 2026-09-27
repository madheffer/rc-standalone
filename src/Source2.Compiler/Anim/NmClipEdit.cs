using System.Buffers.Binary;
using Source2.Compiler.Kv3;

namespace Source2.Compiler.Anim;

/// <summary>
/// The edits the animation builder makes to a stock clip. Each touches only what it changes
/// (<see cref="NmClipCodec.Rebuild"/> copies untouched tracks' words verbatim), and a file is
/// rewritten with only its DATA block replaced, the way generated graphs ship (in game 2026-09-27).
/// Event and sync times are stored normalized, so a retime needs no change to them.
/// </summary>
public static class NmClipEdit
{
    /// <summary>
    /// <paramref name="vnmclipC"/> with its DATA edited by <paramref name="edit"/>. Everything before
    /// DATA stays byte-identical to Valve's file, header and block table included: only DATA's bytes,
    /// its size entry and the file size change. (A VRF re-serialize would shift every block by 16 bytes
    /// and add its own marker.) <paramref name="compression"/> overrides the KV3 compression.
    /// </summary>
    public static byte[] Rewrite(byte[] vnmclipC, Action<Kv3Tree> edit, uint? compression = null)
    {
        var blockTable = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(vnmclipC.AsSpan(8));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(vnmclipC.AsSpan(12));
        for (var i = 0; i < count; i++)
        {
            var entry = blockTable + i * 12;
            if (!vnmclipC.AsSpan(entry, 4).SequenceEqual("DATA"u8)) continue;
            var start = entry + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(vnmclipC.AsSpan(entry + 4));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(vnmclipC.AsSpan(entry + 8));
            if (start + size != vnmclipC.Length)
                throw new InvalidDataException("DATA is not the last block; refusing to rewrite a layout RC does not produce");
            var t = Kv3Tree.Read(vnmclipC.AsSpan(start, size));
            edit(t);
            var data = t.Write(compression);
            var output = new byte[start + data.Length];
            vnmclipC.AsSpan(0, start).CopyTo(output);
            data.CopyTo(output, start);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0), (uint)output.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(entry + 8), (uint)data.Length);
            return output;
        }
        throw new InvalidDataException("no DATA block");
    }

    /// <summary>The arms clip, then each secondary (weapon) clip.</summary>
    public static IReadOnlyList<Kv3Node> Clips(Kv3Tree t) => [t.Root, .. NmClipCodec.Secondaries(t, t.Root)];

    public static double Duration(Kv3Tree t) => Kv3Tree.AsDouble(t.Need(t.Root, "m_flDuration"));

    /// <summary>Play the same frames over <paramref name="seconds"/> (every sub-clip stays in step).</summary>
    public static void Retime(Kv3Tree t, double seconds)
    {
        if (!(seconds > 0) || seconds > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        foreach (var c in Clips(t)) SetDuration(t, c, seconds);
    }

    /// <summary>Keep frames <paramref name="first"/>..<paramref name="last"/> (inclusive). Duration
    /// shrinks with them at the same frame rate, and events move so they fire at the same moment of
    /// the motion; ones that end before the cut or start after it are dropped.</summary>
    public static void Trim(Kv3Tree t, int first, int last)
    {
        var n = NmClipCodec.FrameCount(t, t.Root);
        if (first < 0 || last >= n || last < first) throw new ArgumentOutOfRangeException(nameof(first), $"{first}..{last} of {n} frames");
        var oldDuration = Duration(t);
        var dt = n > 1 ? oldDuration / (n - 1) : 0;
        var newDuration = last > first ? dt * (last - first) : oldDuration;
        var sources = Enumerable.Range(first, last - first + 1).ToArray();
        foreach (var c in Clips(t))
        {
            NmClipCodec.Rebuild(t, c, sources, new Dictionary<int, TrackPose[]>());
            SliceRootMotion(t, c, n, first, last);
            RemapEvents(t, c, oldDuration, first * dt, newDuration);
            SetDuration(t, c, newDuration);
        }
    }

    /// <summary>Apply <paramref name="map"/> (frame, pose) to <paramref name="tracks"/> of clip
    /// <paramref name="clipIndex"/> (0 = arms, 1+ = the weapon sub-clips); others stay verbatim.</summary>
    public static void TransformTracks(Kv3Tree t, int clipIndex, IEnumerable<int> tracks, Func<int, TrackPose, TrackPose> map)
    {
        var clip = Clips(t)[clipIndex];
        var poses = NmClipCodec.Decode(t, clip);
        var replaced = tracks.Distinct().ToDictionary(i => i, i => poses.Select((f, k) => map(k, f[i])).ToArray());
        NmClipCodec.Rebuild(t, clip, Enumerable.Range(0, poses.Length).ToArray(), replaced);
    }

    private static void SetDuration(Kv3Tree t, Kv3Node clip, double seconds)
    {
        var like = t.Need(clip, "m_flDuration");
        t.Replace(clip, "m_flDuration", like.Type == Kv3Type.Float
            ? new Kv3Node { Type = Kv3Type.Float, Bits = BitConverter.SingleToUInt32Bits((float)seconds) }
            : Kv3Tree.Double((float)seconds));
    }

    private static void SliceRootMotion(Kv3Tree t, Kv3Node clip, int n, int first, int last)
    {
        if (t.Get(clip, "m_rootMotion") is not { } root || t.Get(root, "m_transforms") is not { Items: { } items } transforms) return;
        if (items.Count == n) transforms.Items = items.GetRange(first, last - first + 1);
    }

    private static void RemapEvents(Kv3Tree t, Kv3Node clip, double oldDuration, double cutStart, double newDuration)
    {
        if (t.Get(clip, "m_events") is not { Items: { } events } || events.Count == 0 || !(newDuration > 0)) return;
        var kept = new List<Kv3Node>();
        foreach (var e in events)
        {
            var start = t.Get(e, "m_flStartTime");
            var length = t.Get(e, "m_flDuration");
            if (start is null || length is null) { kept.Add(e); continue; }
            var s = Kv3Tree.AsDouble(t.Need(start, "m_flValue")) * oldDuration;
            var d = Kv3Tree.AsDouble(t.Need(length, "m_flValue")) * oldDuration;
            var ns = (s - cutStart) / newDuration;
            var nd = d / newDuration;
            if (ns + nd < 0 || ns > 1) continue;
            var cs = Math.Max(0, ns);
            t.Replace(start, "m_flValue", Kv3Tree.Double((float)cs));
            t.Replace(length, "m_flValue", Kv3Tree.Double((float)Math.Min(nd - (cs - ns), 1 - cs)));
            kept.Add(e);
        }
        t.Get(clip, "m_events")!.Items = kept;
    }
}
