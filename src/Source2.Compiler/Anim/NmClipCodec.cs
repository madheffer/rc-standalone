using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Kv3;

namespace Source2.Compiler.Anim;

/// <summary>One bone's local transform in one frame.</summary>
public record struct TrackPose(Quaternion Rotation, Vector3 Translation, float Scale);

/// <summary>
/// Reads and writes the pose stream of a compiled NmClip (<c>.vnmclip_c</c> DATA) on a lossless
/// <see cref="Kv3Tree"/>, so everything the codec does not rewrite keeps Valve's encoding. A clip
/// node is the root or one of its <c>m_secondaryAnimations</c> (the weapon's own skeleton).
/// Per frame and per track the stream holds, as 16-bit words: rotation (3) unless static, then
/// translation (3) unless static, then scale (1) unless static. <c>m_nTrackReadOffset</c> is where
/// each track starts within a frame, and <c>m_compressedPoseOffsets</c> where each frame starts.
/// </summary>
public static class NmClipCodec
{
    /// <summary>Range length RC stores for a channel that does not move (its start is the value).</summary>
    public const double StaticRangeLength = 0.1;

    /// <summary>A moving channel whose span is below this stores the placeholder length instead.
    /// Fitted on stock clips: spans of 9.2e-5 get 0.1, spans of 1.1e-4 are stored as is.</summary>
    private const float StillSpan = 1e-4f;

    private static readonly float RotMin = -1f / MathF.Sqrt(2f);
    private static readonly float RotRange = 2f / MathF.Sqrt(2f);

    public static IEnumerable<Kv3Node> Secondaries(Kv3Tree t, Kv3Node clip) =>
        t.Get(clip, "m_secondaryAnimations")?.Items ?? [];

    public static int FrameCount(Kv3Tree t, Kv3Node clip) => (int)Kv3Tree.AsLong(t.Need(clip, "m_nNumFrames"));

    private readonly record struct Settings(
        double[] TStart, double[] TLen, double SStart, double SLen, Quaternion Constant, bool RotStatic, bool TransStatic, bool ScaleStatic);

    private static Settings ReadSettings(Kv3Tree t, Kv3Node s)
    {
        double[] Range(string key) => [Kv3Tree.AsDouble(t.Need(t.Need(s, key), "m_flRangeStart")), Kv3Tree.AsDouble(t.Need(t.Need(s, key), "m_flRangeLength"))];
        var x = Range("m_translationRangeX");
        var y = Range("m_translationRangeY");
        var z = Range("m_translationRangeZ");
        var sc = Range("m_scaleRange");
        var q = t.Need(s, "m_constantRotation").Items!.Select(Kv3Tree.AsDouble).ToArray();
        return new Settings([x[0], y[0], z[0]], [x[1], y[1], z[1]], sc[0], sc[1],
            new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]),
            Kv3Tree.AsBool(t.Need(s, "m_bIsRotationStatic")), Kv3Tree.AsBool(t.Need(s, "m_bIsTranslationStatic")),
            Kv3Tree.AsBool(t.Need(s, "m_bIsScaleStatic")));
    }

    private static float Unorm(ushort v, double start, double len) => (float)(v / 65535.0f * len + start);

    private static Quaternion DecodeQuat(ushort a, ushort b, ushort c)
    {
        var m = RotRange / 0x7FFF;
        var v = new Vector3((a & 0x7FFF) * m + RotMin, (b & 0x7FFF) * m + RotMin, c * m + RotMin);
        var w = MathF.Sqrt(MathF.Max(0f, 1f - Vector3.Dot(v, v)));
        return (((a >> 14) & 2) | (b >> 15)) switch
        {
            0 => new Quaternion(w, v.X, v.Y, v.Z),
            1 => new Quaternion(v.X, w, v.Y, v.Z),
            2 => new Quaternion(v.X, v.Y, w, v.Z),
            _ => new Quaternion(v.X, v.Y, v.Z, w),
        };
    }

    /// <summary>Every frame's pose, [frame][track], decoded exactly as VRF (and the previewer) does.</summary>
    public static TrackPose[][] Decode(Kv3Tree t, Kv3Node clip)
    {
        var settings = t.Need(clip, "m_trackCompressionSettings").Items!.Select(s => ReadSettings(t, s)).ToArray();
        var data = t.Need(clip, "m_compressedPoseData").Blob ?? [];
        var offsets = t.Need(clip, "m_compressedPoseOffsets").Items!.Select(Kv3Tree.AsLong).ToArray();
        var words = new ushort[data.Length / 2];
        for (var i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));
        var frames = new TrackPose[offsets.Length][];
        for (var f = 0; f < offsets.Length; f++)
        {
            var p = (int)offsets[f];
            var pose = frames[f] = new TrackPose[settings.Length];
            for (var i = 0; i < settings.Length; i++)
            {
                var s = settings[i];
                var rot = s.Constant;
                var pos = new Vector3((float)s.TStart[0], (float)s.TStart[1], (float)s.TStart[2]);
                var scale = (float)s.SStart;
                if (!s.RotStatic) { rot = DecodeQuat(words[p], words[p + 1], words[p + 2]); p += 3; }
                if (!s.TransStatic)
                {
                    pos = new Vector3(Unorm(words[p], s.TStart[0], s.TLen[0]), Unorm(words[p + 1], s.TStart[1], s.TLen[1]), Unorm(words[p + 2], s.TStart[2], s.TLen[2]));
                    p += 3;
                }
                if (!s.ScaleStatic) { scale = Unorm(words[p], s.SStart, s.SLen); p++; }
                pose[i] = new TrackPose(rot, pos, scale);
            }
        }
        return frames;
    }

    /// <summary>
    /// Replace the clip's poses with <paramref name="frames"/> ([frame][track], same track count),
    /// recomputing the compression settings, the stream, both offset tables, and the frame counts.
    /// Duration is the caller's (<c>m_flDuration</c>); events and sync times are normalized and
    /// need no change for a retime.
    /// </summary>
    public static void Encode(Kv3Tree t, Kv3Node clip, TrackPose[][] frames)
    {
        var settingsNode = t.Need(clip, "m_trackCompressionSettings");
        var tracks = settingsNode.Items!.Count;
        if (frames.Length == 0 || frames.Any(f => f.Length != tracks))
            throw new ArgumentException($"expected {tracks} tracks in every frame");

        var words = new List<ushort>();
        var perTrack = new int[tracks];
        var stride = 0;
        var plans = new (bool Rot, bool Trans, bool Scale, float[] TStart, float[] TLen, float SStart, float SLen)[tracks];
        for (var i = 0; i < tracks; i++)
        {
            var q0 = Canonical(frames[0][i].Rotation);
            var rotStatic = frames.All(f => Canonical(f[i].Rotation) == q0);
            var t0 = frames[0][i].Translation;
            var transStatic = frames.All(f => f[i].Translation == t0);
            var s0 = frames[0][i].Scale;
            var scaleStatic = frames.All(f => f[i].Scale == s0);
            float[] tStart = [t0.X, t0.Y, t0.Z], tLen = [(float)StaticRangeLength, (float)StaticRangeLength, (float)StaticRangeLength];
            if (!transStatic)
                for (var a = 0; a < 3; a++)
                {
                    var lo = frames.Min(f => Axis(f[i].Translation, a));
                    var hi = frames.Max(f => Axis(f[i].Translation, a));
                    tStart[a] = lo;
                    // A near-still axis inside a moving track gets the static placeholder length.
                    tLen[a] = hi - lo < StillSpan ? (float)StaticRangeLength : hi - lo;
                }
            float sStart = s0, sLen = (float)StaticRangeLength;
            if (!scaleStatic)
            {
                sStart = frames.Min(f => f[i].Scale);
                var sHi = frames.Max(f => f[i].Scale);
                sLen = sHi - sStart < StillSpan ? (float)StaticRangeLength : sHi - sStart;
            }
            plans[i] = (rotStatic, transStatic, scaleStatic, tStart, tLen, sStart, sLen);
            perTrack[i] = stride;
            stride += (rotStatic ? 0 : 3) + (transStatic ? 0 : 3) + (scaleStatic ? 0 : 1);
        }

        foreach (var f in frames)
            for (var i = 0; i < tracks; i++)
            {
                var pl = plans[i];
                if (!pl.Rot) words.AddRange(EncodeQuat(f[i].Rotation));
                if (!pl.Trans)
                    for (var a = 0; a < 3; a++) words.Add(ToUnorm(Axis(f[i].Translation, a), pl.TStart[a], pl.TLen[a]));
                if (!pl.Scale) words.Add(ToUnorm(f[i].Scale, pl.SStart, pl.SLen));
            }

        for (var i = 0; i < tracks; i++)
        {
            var s = settingsNode.Items[i];
            var pl = plans[i];
            SetRange(t, t.Need(s, "m_translationRangeX"), pl.TStart[0], pl.TLen[0]);
            SetRange(t, t.Need(s, "m_translationRangeY"), pl.TStart[1], pl.TLen[1]);
            SetRange(t, t.Need(s, "m_translationRangeZ"), pl.TStart[2], pl.TLen[2]);
            SetRange(t, t.Need(s, "m_scaleRange"), pl.SStart, pl.SLen);
            t.Replace(s, "m_nTrackReadOffset", Kv3Tree.IntLike(t.Need(s, "m_nTrackReadOffset"), perTrack[i]));
            var q = pl.Rot ? frames[0][i].Rotation : Quaternion.Identity;
            var c = t.Need(s, "m_constantRotation");
            float[] comps = [q.X, q.Y, q.Z, q.W];
            for (var k = 0; k < 4; k++) c.Items![k].Bits = BitConverter.DoubleToUInt64Bits(comps[k]);
            t.Replace(s, "m_bIsRotationStatic", Kv3Tree.Bool(pl.Rot));
            t.Replace(s, "m_bIsTranslationStatic", Kv3Tree.Bool(pl.Trans));
            t.Replace(s, "m_bIsScaleStatic", Kv3Tree.Bool(pl.Scale));
        }

        var blob = new byte[words.Count * 2];
        for (var i = 0; i < words.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(i * 2), words[i]);
        t.Need(clip, "m_compressedPoseData").Blob = blob;

        var offsets = t.Need(clip, "m_compressedPoseOffsets");
        var like = offsets.ElementType;
        offsets.Items = Enumerable.Range(0, frames.Length).Select(f => new Kv3Node { Type = like, Bits = (ulong)(f * stride) }).ToList();
        t.Replace(clip, "m_nNumFrames", Kv3Tree.IntLike(t.Need(clip, "m_nNumFrames"), frames.Length));
        if (t.Get(clip, "m_rootMotion") is { } root && t.Get(root, "m_nNumFrames") is { } rn)
            t.Replace(root, "m_nNumFrames", Kv3Tree.IntLike(rn, frames.Length));
    }

    private static void SetRange(Kv3Tree t, Kv3Node range, float start, float len)
    {
        t.Replace(range, "m_flRangeStart", Kv3Tree.Double(start));
        t.Replace(range, "m_flRangeLength", Kv3Tree.Double(len));
    }

    private static float Axis(Vector3 v, int a) => a switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static ushort ToUnorm(float v, float start, float len) =>
        len <= 0 ? (ushort)0 : (ushort)Math.Clamp(MathF.Floor((v - start) / len * 65535f + 0.5f), 0, 65535);   // half up, as RC

    /// <summary>q and -q are one rotation; the encoding keeps the largest component positive.</summary>
    private static Quaternion Canonical(Quaternion q)
    {
        float[] c = [q.X, q.Y, q.Z, q.W];
        var big = 0;
        for (var i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[big])) big = i;
        return c[big] < 0 ? Quaternion.Negate(q) : q;
    }

    private static ushort[] EncodeQuat(Quaternion q)
    {
        q = Canonical(Quaternion.Normalize(q));
        float[] c = [q.X, q.Y, q.Z, q.W];
        var big = 0;
        for (var i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[big])) big = i;
        var rest = Enumerable.Range(0, 4).Where(i => i != big).Select(i => c[i]).ToArray();
        ushort Q(float v) => (ushort)Math.Clamp(MathF.Floor((v - RotMin) / RotRange * 0x7FFF + 0.5f), 0, 0x7FFF);
        var a = (ushort)(Q(rest[0]) | ((big & 2) << 14));
        var b = (ushort)(Q(rest[1]) | ((big & 1) << 15));
        return [a, b, Q(rest[2])];
    }
}
