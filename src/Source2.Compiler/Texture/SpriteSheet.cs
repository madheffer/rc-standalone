using System.Buffers.Binary;
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
    /// <param name="MoreImages">Further rects of the same frame, after this one (stock sheets of
    /// multi-layer particles store up to four per frame); none for most sheets.</param>
    public sealed record Frame(
        float DisplayTime,
        (float X, float Y) Min,
        (float X, float Y) Max,
        (float X, float Y)? CroppedMin = null,
        (float X, float Y)? CroppedMax = null,
        IReadOnlyList<Image>? MoreImages = null)
    {
        /// <summary>The frame's rects in stored order: its own, then <see cref="MoreImages"/>.</summary>
        public IEnumerable<Image> Images => [new Image(Min, Max, CroppedMin, CroppedMax), .. MoreImages ?? []];
    }

    /// <summary>One rect of a frame, in UV space; a null cropped corner means the uncropped one.</summary>
    /// <param name="Min">Top-left UV.</param>
    /// <param name="Max">Bottom-right UV.</param>
    /// <param name="CroppedMin">Tighter top-left for alpha-cropped frames.</param>
    /// <param name="CroppedMax">Tighter bottom-right for alpha-cropped frames.</param>
    public sealed record Image(
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
            cursor += sequences[s].Frames.Sum(f => 1 + (f.MoreImages?.Count ?? 0)) * imageSize;

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
            var image = imagesPos[s];
            foreach (var frame in seq.Frames)
            {
                var at = (int)ms.Position;
                var images = 1 + (frame.MoreImages?.Count ?? 0);
                w.Write(frame.DisplayTime);
                w.Write((uint)(image - (at + 4)));
                w.Write((uint)images);
                image += images * imageSize;
            }

            ms.Position = imagesPos[s];
            foreach (var i in seq.Frames.SelectMany(f => f.Images))
            {
                var cMin = i.CroppedMin ?? i.Min;
                var cMax = i.CroppedMax ?? i.Max;
                w.Write(cMin.X);
                w.Write(cMin.Y);
                w.Write(cMax.X);
                w.Write(cMax.Y);
                w.Write(i.Min.X);
                w.Write(i.Min.Y);
                w.Write(i.Max.X);
                w.Write(i.Max.Y);
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

    /// <summary>
    /// Read a SHEET payload (a compiled texture's <see cref="Textures.VTexExtraData.SHEET"/> extra data).
    /// A cropped rect equal to the uncropped one reads as none.
    /// The stored total time is not kept: it is the sum of the display times, which <see cref="Write"/>
    /// recomputes.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload is not a version 8 sheet, or an offset or count runs past it.</exception>
    public static IReadOnlyList<Sequence> Read(ReadOnlySpan<byte> sheet)
    {
        if (sheet.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(sheet) != Version)
            throw new InvalidDataException($"A sprite sheet is version {Version}.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(sheet[4..]);
        Check(sheet, 8, count, 32);
        var sequences = new List<Sequence>((int)count);
        for (var s = 0; s < count; s++)
        {
            var at = 8 + 32 * s;
            var framesAt = Offset(sheet, at + 8);
            var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(sheet[(at + 12)..]);
            var name = String(sheet, Offset(sheet, at + 20));
            var paramsAt = Offset(sheet, at + 24);
            var paramCount = BinaryPrimitives.ReadUInt32LittleEndian(sheet[(at + 28)..]);

            Check(sheet, paramsAt, paramCount, 8);
            var floatParams = new Dictionary<string, float>();
            for (var p = 0; p < paramCount; p++)
            {
                var entry = (int)paramsAt + 8 * p;
                floatParams[String(sheet, Offset(sheet, entry))] = BinaryPrimitives.ReadSingleLittleEndian(sheet[(entry + 4)..]);
            }

            Check(sheet, framesAt, frameCount, 12);
            var frames = new Frame[frameCount];
            for (var f = 0; f < frameCount; f++)
            {
                var entry = (int)framesAt + 12 * f;
                var imageAt = Offset(sheet, entry + 4);
                var imageCount = BinaryPrimitives.ReadUInt32LittleEndian(sheet[(entry + 8)..]);
                if (imageCount == 0)
                    throw new InvalidDataException("A sheet frame has no image.");
                Check(sheet, imageAt, imageCount, 32);
                var images = new Image[imageCount];
                for (var i = 0; i < imageCount; i++)
                {
                    var image = sheet.Slice((int)imageAt + 32 * i, 32);
                    var (croppedMin, croppedMax, min, max) = (Point(image, 0), Point(image, 1), Point(image, 2), Point(image, 3));
                    var cropped = croppedMin != min || croppedMax != max;
                    images[i] = new Image(min, max, cropped ? croppedMin : null, cropped ? croppedMax : null);
                }
                frames[f] = new Frame(BinaryPrimitives.ReadSingleLittleEndian(sheet[entry..]), images[0].Min, images[0].Max,
                    images[0].CroppedMin, images[0].CroppedMax, imageCount > 1 ? images[1..] : null);
            }
            sequences.Add(new Sequence(frames, Clamp: sheet[at + 4] != 0, AlphaCrop: sheet[at + 5] != 0, NoColor: sheet[at + 6] != 0,
                NoAlpha: sheet[at + 7] != 0, Name: name, FloatParams: paramCount == 0 ? null : floatParams,
                Id: (int)BinaryPrimitives.ReadUInt32LittleEndian(sheet[at..])));
        }
        return sequences;

        static (float X, float Y) Point(ReadOnlySpan<byte> image, int i) =>
            (BinaryPrimitives.ReadSingleLittleEndian(image[(8 * i)..]), BinaryPrimitives.ReadSingleLittleEndian(image[(8 * i + 4)..]));

        // An offset field holds the distance from itself.
        static long Offset(ReadOnlySpan<byte> sheet, int field) => field + (long)BinaryPrimitives.ReadUInt32LittleEndian(sheet[field..]);

        static void Check(ReadOnlySpan<byte> sheet, long at, uint count, int size)
        {
            if (at < 0 || at + (long)count * size > sheet.Length)
                throw new InvalidDataException($"{count} sheet entries of {size} bytes at {at} run past the {sheet.Length}-byte sheet.");
        }

        static string String(ReadOnlySpan<byte> sheet, long at)
        {
            if (at >= sheet.Length)
                throw new InvalidDataException($"A sheet string at {at} is past the {sheet.Length}-byte sheet.");
            var rest = sheet[(int)at..];
            var end = rest.IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException("A sheet string is not terminated.");
            return Encoding.UTF8.GetString(rest[..end]);
        }
    }
}
