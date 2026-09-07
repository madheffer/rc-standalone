using System.Globalization;
using System.Text;

namespace Source2.Compiler;

/// <summary>
/// Parses an <c>.mks</c> sprite-sheet script: the plain-text source Valve's
/// <c>mksheet</c> takes, listing the sequences of an animated particle texture
/// and the image file behind each frame.
///
/// <para>The grammar handled here is the one VRF's own mks emitter produces when
/// it reconstructs a script out of a compiled sheet (<c>TextureExtract.TryGetMksData</c>),
/// which is the closest thing to a specification that exists:</para>
///
/// <code>
///   // comments run to end of line
///   packmode rgb+a          // optional, only meaningful with split sequences
///   sequence 0              // a sequence using both colour and alpha
///   LOOP                    // optional; without it the sequence clamps
///   frame flame_0.png 1
///   frame flame_1.png 1
///   sequence-rgb 1          // colour-only  (NoAlpha)
///   sequence-a 2            // alpha-only   (NoColor)
/// </code>
///
/// <para>Anything else is rejected with the offending line rather than skipped,
/// because a silently ignored directive would produce a sheet that animates
/// differently from what the author wrote.</para>
/// </summary>
public static class MksSource
{
    /// <summary>One parsed frame: the image to pack, and how long it shows for.</summary>
    public sealed record Frame(string ImagePath, float DisplayTime);

    /// <summary>One parsed sequence.</summary>
    public sealed record Sequence(int Index, IReadOnlyList<Frame> Frames, bool Clamp, bool NoColor, bool NoAlpha);

    /// <summary>A parsed script.</summary>
    /// <param name="Sequences">In file order.</param>
    /// <param name="PackModeRgbA">The script asked for <c>packmode rgb+a</c>.</param>
    public sealed record Script(IReadOnlyList<Sequence> Sequences, bool PackModeRgbA)
    {
        /// <summary>Every distinct image path referenced, in first-use order.</summary>
        public IReadOnlyList<string> ImagePaths => Sequences
            .SelectMany(s => s.Frames)
            .Select(f => f.ImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Parse <paramref name="text"/>. Throws on anything it does not understand.</summary>
    public static Script Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sequences = new List<Sequence>();
        var packModeRgbA = false;

        int? currentIndex = null;
        var currentFrames = new List<Frame>();
        var clamp = true;
        var noColor = false;
        var noAlpha = false;

        void Flush()
        {
            if (currentIndex is not { } idx) return;
            if (currentFrames.Count == 0)
                throw new InvalidOperationException($"Sequence {idx} declares no frames.");
            sequences.Add(new Sequence(idx, currentFrames.ToList(), clamp, noColor, noAlpha));
            currentFrames.Clear();
        }

        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "packmode":
                    // "flat" is the default (one image per frame); "rgb+a" means
                    // colour and alpha are packed from separate sequences.
                    if (parts.Length < 2)
                        throw new InvalidOperationException($"Line {lineNo}: packmode needs a value.");
                    packModeRgbA = parts[1].Equals("rgb+a", StringComparison.OrdinalIgnoreCase);
                    if (!packModeRgbA && !parts[1].Equals("flat", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Line {lineNo}: unknown packmode '{parts[1]}' (expected flat or rgb+a).");
                    break;

                case "sequence":
                case "sequence-rgb":
                case "sequence-a":
                    Flush();
                    if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                        throw new InvalidOperationException($"Line {lineNo}: '{parts[0]}' needs a sequence number.");
                    currentIndex = idx;
                    clamp = true;
                    noAlpha = verb == "sequence-rgb";
                    noColor = verb == "sequence-a";
                    break;

                case "loop":
                    if (currentIndex is null)
                        throw new InvalidOperationException($"Line {lineNo}: LOOP before any sequence.");
                    clamp = false;
                    break;

                case "frame":
                    if (currentIndex is null)
                        throw new InvalidOperationException($"Line {lineNo}: frame before any sequence.");
                    if (parts.Length < 2)
                        throw new InvalidOperationException($"Line {lineNo}: frame needs an image file.");
                    // "frame <image> [displayTime]". mksheet also accepts a second
                    // image for rgb+a pairs; that is not handled, and saying so is
                    // better than packing only the first half of the frame.
                    if (parts.Length > 3)
                        throw new InvalidOperationException(
                            $"Line {lineNo}: multi-image frames (rgb+a pairs) are not supported. " +
                            "Combine the colour and alpha images into one RGBA file first.");
                    var time = 1f;
                    if (parts.Length == 3 &&
                        !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out time))
                        throw new InvalidOperationException($"Line {lineNo}: '{parts[2]}' is not a display time.");
                    currentFrames.Add(new Frame(parts[1], time));
                    break;

                default:
                    throw new InvalidOperationException($"Line {lineNo}: unrecognised directive '{parts[0]}'.");
            }
        }

        Flush();

        if (sequences.Count == 0)
            throw new InvalidOperationException("The script declares no sequences.");

        return new Script(sequences, packModeRgbA);
    }

    /// <summary>Parse UTF-8 bytes.</summary>
    public static Script Parse(byte[] utf8) => Parse(Encoding.UTF8.GetString(utf8).Replace("\r\n", "\n"));
}
