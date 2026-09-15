using System.Text;

namespace Source2.Compiler;

/// <summary>
/// The <c>SHEET</c> extra-data payload of a compiled texture: the sprite-sheet
/// sequences a particle system animates through, and the UV rect of every frame.
///
/// <para>The animation lives HERE, not in the particle, which only picks a sequence and
/// a rate. Version 8 layout, in which every offset is relative to ITS OWN POSITION:</para>
///
/// <code>
///   u32 version = 8; u32 numSequences
///   sequence header x numSequences, 32 B: u32 id; u8 clamp, alphaCrop, noColor, noAlpha;
///     u32 framesOffset; u32 numFrames; f32 totalTime;
///     u32 nameOffset; u32 floatParamsOffset; u32 floatParamsCount
///   then per sequence, in order:
///     name, null-terminated UTF-8, padded to 4 B
///     frame x numFrames, 12 B: f32 displayTime; u32 imageOffset; u32 imageCount
///     image x sum(imageCount), 32 B: f32 cropped min/max xy, then uncropped min/max xy
/// </code>
/// </summary>
public static class SpriteSheet
{
    /// <summary>The only version CS2 reads, and the only one this writes.</summary>
    public const uint Version = 8;

    /// <summary>What Valve's own sheets call their sequences; VRF surfaces it verbatim.</summary>
    public const string DefaultSequenceName = "CDmeSheetSequence";

    /// <summary>One frame's rectangle in the atlas, in UV space (0..1).</summary>
    /// <param name="DisplayTime">Relative duration. Valve's sheets use 1.0 per ordinary frame.</param>
    /// <param name="Min">Top-left UV.</param>
    /// <param name="Max">Bottom-right UV.</param>
    /// <param name="CroppedMin">Tighter UV used for alpha-cropped frames; defaults to <paramref name="Min"/>.</param>
    /// <param name="CroppedMax">Tighter UV used for alpha-cropped frames; defaults to <paramref name="Max"/>.</param>
    public sealed record Frame(
        float DisplayTime,
        (float X, float Y) Min,
        (float X, float Y) Max,
        (float X, float Y)? CroppedMin = null,
        (float X, float Y)? CroppedMax = null);

    /// <summary>One animation sequence: an ordered run of frames a particle plays.</summary>
    /// <param name="Frames">In play order.</param>
    /// <param name="Clamp">True holds the last frame; false loops. An <c>.mks</c> <c>LOOP</c> line means false.</param>
    /// <param name="AlphaCrop">The frames carry tighter cropped rects.</param>
    /// <param name="NoColor">Alpha-only sequence (<c>sequence-a</c> in an .mks).</param>
    /// <param name="NoAlpha">Colour-only sequence (<c>sequence-rgb</c> in an .mks).</param>
    /// <param name="Name">Sequence name. Valve writes <see cref="DefaultSequenceName"/> for all of them.</param>
    /// <param name="FloatParams">Optional named floats carried alongside the sequence.</param>
    /// <param name="Id">
    /// The sequence number a particle selects with <c>m_nSequenceMin</c>/<c>Max</c>,
    /// and the number the <c>.mks</c> <c>sequence N</c> line names. Null means
    /// "use the list position", which is right for a sheet numbered 0..n-1 but
    /// wrong for one that skips or reorders - so an .mks-driven build passes the
    /// parsed number through rather than letting position stand in for it.
    /// </param>
    public sealed record Sequence(
        IReadOnlyList<Frame> Frames,
        bool Clamp = true,
        bool AlphaCrop = false,
        bool NoColor = false,
        bool NoAlpha = false,
        string Name = DefaultSequenceName,
        IReadOnlyDictionary<string, float>? FloatParams = null,
        int? Id = null);

    /// <summary>
    /// Serialize sequences into a SHEET payload, ready for
    /// <see cref="ResourceBuilder.TextureDef.SheetData"/>.
    /// </summary>
    public static byte[] Write(IReadOnlyList<Sequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        if (sequences.Count == 0)
            throw new ArgumentException("A sprite sheet needs at least one sequence.", nameof(sequences));
        for (var i = 0; i < sequences.Count; i++)
            if (sequences[i].Frames.Count == 0)
                throw new ArgumentException($"Sequence {i} has no frames.", nameof(sequences));

        // Ids are how a particle addresses a sequence, so two sequences sharing
        // one makes the second unreachable. Silently emitting that would look
        // like the sheet simply lost a sequence.
        var seenIds = new HashSet<uint>();
        for (var i = 0; i < sequences.Count; i++)
            if (!seenIds.Add((uint)(sequences[i].Id ?? i)))
                throw new ArgumentException(
                    $"Two sequences share id {sequences[i].Id ?? i}; ids must be distinct.", nameof(sequences));

        const int headerSize = 8;
        const int sequenceHeaderSize = 32;
        const int frameSize = 12;
        const int imageSize = 32;
        const int floatParamSize = 8;

        // Pass 1: fix every position. Each sequence's data is laid out
        // name, frames, images, float-param table, float-param names - in that
        // order, contiguously, in sequence order, exactly as Valve's own sheets
        // are (verified against explosion_blast_01_flame).
        var nameBytes = sequences.Select(s => Encoding.UTF8.GetBytes(s.Name)).ToArray();
        var paramNames = sequences
            .Select(s => (s.FloatParams ?? new Dictionary<string, float>())
                .Select(kv => (Key: kv.Key, Name: Encoding.UTF8.GetBytes(kv.Key), kv.Value)).ToArray())
            .ToArray();

        var namePos = new int[sequences.Count];
        var framesPos = new int[sequences.Count];
        var imagesPos = new int[sequences.Count];
        var paramsPos = new int[sequences.Count];
        var paramNamePos = new int[sequences.Count][];

        var cursor = headerSize + sequences.Count * sequenceHeaderSize;
        for (var s = 0; s < sequences.Count; s++)
        {
            namePos[s] = cursor;
            cursor = Align4(cursor + nameBytes[s].Length + 1);   // + null terminator

            framesPos[s] = cursor;
            cursor += sequences[s].Frames.Count * frameSize;

            imagesPos[s] = cursor;
            cursor += sequences[s].Frames.Count * imageSize;      // one image per frame

            paramsPos[s] = cursor;
            cursor += paramNames[s].Length * floatParamSize;

            paramNamePos[s] = new int[paramNames[s].Length];
            for (var p = 0; p < paramNames[s].Length; p++)
            {
                paramNamePos[s][p] = cursor;
                cursor = Align4(cursor + paramNames[s][p].Name.Length + 1);
            }
        }

        var buffer = new byte[cursor];
        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);

        w.Write(Version);
        w.Write((uint)sequences.Count);

        for (var s = 0; s < sequences.Count; s++)
        {
            var seq = sequences[s];
            var at = (int)ms.Position;

            w.Write((uint)(seq.Id ?? s));           // id
            w.Write(seq.Clamp);
            w.Write(seq.AlphaCrop);
            w.Write(seq.NoColor);
            w.Write(seq.NoAlpha);
            w.Write((uint)(framesPos[s] - (at + 8)));
            w.Write((uint)seq.Frames.Count);
            w.Write(seq.Frames.Sum(f => f.DisplayTime));            // totalTime
            w.Write((uint)(namePos[s] - (at + 20)));
            w.Write((uint)(paramNames[s].Length == 0 ? 0 : paramsPos[s] - (at + 24)));
            w.Write((uint)paramNames[s].Length);
        }

        for (var s = 0; s < sequences.Count; s++)
        {
            var seq = sequences[s];

            ms.Position = namePos[s];
            w.Write(nameBytes[s]);
            w.Write((byte)0);

            ms.Position = framesPos[s];
            for (var f = 0; f < seq.Frames.Count; f++)
            {
                var at = (int)ms.Position;
                w.Write(seq.Frames[f].DisplayTime);
                w.Write((uint)(imagesPos[s] + f * imageSize - (at + 4)));
                w.Write(1u);                                        // one image per frame
            }

            ms.Position = imagesPos[s];
            foreach (var f in seq.Frames)
            {
                var cMin = f.CroppedMin ?? f.Min;
                var cMax = f.CroppedMax ?? f.Max;
                w.Write(cMin.X);
                w.Write(cMin.Y);
                w.Write(cMax.X);
                w.Write(cMax.Y);
                w.Write(f.Min.X);
                w.Write(f.Min.Y);
                w.Write(f.Max.X);
                w.Write(f.Max.Y);
            }

            ms.Position = paramsPos[s];
            for (var p = 0; p < paramNames[s].Length; p++)
            {
                var at = (int)ms.Position;
                w.Write((uint)(paramNamePos[s][p] - at));
                w.Write(paramNames[s][p].Value);
            }
            for (var p = 0; p < paramNames[s].Length; p++)
            {
                ms.Position = paramNamePos[s][p];
                w.Write(paramNames[s][p].Name);
                w.Write((byte)0);
            }
        }

        w.Flush();
        return buffer;
    }

    private static int Align4(int n) => (n + 3) & ~3;
}
