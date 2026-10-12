using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// Compile a KV3 text source file (e.g. <c>.vsndevts</c>, <c>.vdata</c>,
/// <c>.vpcf</c>, <c>.vagrp</c>) into its <c>_c</c> binary counterpart that
/// CS2 actually reads.
///
/// <para>The text's <c>&lt;!-- kv3 ... --&gt;</c> header names the per-type schema
/// GUID, so it never has to be hard-coded, and
/// <see cref="Source2ContainerAuthor"/> authors the container around the parsed
/// tree. Decompiling a KV3-backed <c>_c</c>, editing the text and recompiling
/// here round-trips the data tree. Compiling Valve's own source gives Valve's
/// bytes when the source path and search path are the ones RC saw (the
/// RcReference probes, <c>Source2ContainerAuthorTests</c>).</para>
///
/// <para>The tree is not validated against a per-type schema. The header is
/// trusted to declare its own format, and the decompile direction's output is
/// the reference for what shape the text should take.</para>
///
/// <para><c>.vsnd</c> is rejected: a sound container holds a sound shape and the
/// encoded audio, which KV3 text cannot author. Use
/// <see cref="ResourceBuilder.BuildSound(byte[], string?)"/>.</para>
/// </summary>
public static class Kv3SourceCompiler
{
    /// <summary>
    /// Parse <paramref name="textBytes"/> as KV3 text and author its compiled
    /// binary form. <paramref name="sourceExtension"/> (e.g. <c>".vdata"</c>,
    /// with or without the leading dot) selects the container identity;
    /// null / unmapped extensions author the generic sound-event-style
    /// container (matching the legacy behavior for unknown KV3 types).
    /// <paramref name="sourceFileName"/>, when the caller knows it, becomes
    /// the RED2 input-dependency path (otherwise a neutral name is used).
    /// <paramref name="searchPath"/> is the content tree it was compiled from
    /// (<c>csgo_addons/&lt;addon&gt;</c>, or <c>csgo</c>), which RC records with
    /// every input dependency.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the parsed text has no Format header (no
    /// <c>&lt;!-- kv3 encoding:... format:... --&gt;</c> comment), when the
    /// input isn't parseable KV3, or for a <c>.vsnd</c> source (see class doc).
    /// </exception>
    public static byte[] Compile(byte[] textBytes, string? sourceExtension = null, string? sourceFileName = null, string searchPath = "csgo")
    {
        if (sourceExtension is { Length: > 0 } && sourceExtension[0] != '.')
            sourceExtension = "." + sourceExtension;
        if (textBytes.Length == 0)
            throw new InvalidOperationException("Empty input - KV3 text expected.");

        if (string.Equals(sourceExtension, ".vsnd", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A .vsnd_c is an audio container (sound shape + encoded audio), not a KV3 document - " +
                "it cannot be authored from KV3 text alone. Upload the audio file instead (the sound " +
                "route compiles PCM WAV into a proper vsnd_c).");
        }

        // Refuse what the reader cannot survive: hostile nesting (everything after
        // the parse recurses) and a stray control character (the reader then
        // allocates without end). See Kv3LineColumns.PreParse.
        var (depth, controlAt) = Kv3LineColumns.PreParse(System.Text.Encoding.UTF8.GetString(textBytes));
        if (depth > Kv3LineColumns.MaxDepth)
            throw new InvalidOperationException(
                $"KV3 text nests {depth} levels deep; {Kv3LineColumns.MaxDepth} is the most this compiler accepts.");
        if (controlAt >= 0)
            throw new InvalidOperationException(
                $"KV3 text has a stray character (a control character or ';') outside a string at offset {controlAt}; it is not valid KV3.");

        // 1. Parse user text → KVDocument. ValveKeyValue's KV3 reader handles
        //    the comment-style header, type/format flags, multiline strings,
        //    nested objects, arrays of mixed type, binary blobs, etc. - much
        //    more than a hand-rolled parser would cover.
        KVDocument userDoc;
        try
        {
            using var textStream = new MemoryStream(textBytes, writable: false);
            var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues3Text);
            userDoc = serializer.Deserialize(textStream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not parse input as KV3 text. Make sure the file starts with a " +
                "<!-- kv3 encoding:... format:... --> header and that all braces / quotes balance. " +
                $"Underlying error: {ex.Message}", ex);
        }

        // The reader types a quoted "false" or "12" by its spelling; RC keeps
        // it a string.
        Kv3LineColumns.RestoreQuotedStrings(System.Text.Encoding.UTF8.GetString(textBytes), userDoc.Root);

        if (userDoc?.Header?.Format == null)
        {
            throw new InvalidOperationException(
                "Parsed KV3 has no Format header. The first line must be a " +
                "<!-- kv3 encoding:text:version{...} format:<schema>:version{...} --> comment.");
        }

        // 2. Author the container from scratch. Unmapped/null extensions get
        //    the generic sound-event-style identity (legacy-compatible default
        //    for unknown KV3 types).
        var ext = sourceExtension is { Length: > 0 } && Source2ContainerAuthor.SpecByExtension.ContainsKey(sourceExtension)
            ? sourceExtension
            : ".vsndevts";
        return Source2ContainerAuthor.AuthorKv3Resource(userDoc, ext, textBytes, sourceFileName, searchPath);
    }
}
