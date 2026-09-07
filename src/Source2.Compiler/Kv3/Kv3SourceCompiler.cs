using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// Compile a KV3 text source file (e.g. <c>.vsndevts</c>, <c>.vdata</c>,
/// <c>.vpcf</c>, <c>.vagrp</c>) into its <c>_c</c> binary counterpart that
/// CS2 actually reads.
///
/// How this works (donor-free since 2026-07-26):
///
///   1. <see cref="ValveKeyValue.KVSerializer"/> parses the user's KV3 text
///      into a <see cref="KVDocument"/>. The text format's <c>&lt;!-- kv3 ... --&gt;</c>
///      header populates <c>Header.Format</c> with the per-resource-type
///      schema GUID (e.g. <c>7412167c-...</c> for "generic" used by vsndevts /
///      vdata) so we don't have to hard-code one.
///
///   2. <see cref="Source2ContainerAuthor"/> authors the compiled container
///      FROM SCRATCH: resource version, RED2 (real input dependency + CRC of
///      the user's source, the correct compiler identity for the type, real
///      subasset definitions), RERL when the tree carries <c>resource:</c>
///      references, and the user's tree as the DATA block. Every structural
///      fact mirrors CS2's own resourcecompiler output (see the author's doc
///      + <c>tools/rc-oracle.ps1</c>). No donor template is involved, so no
///      donor metadata (input paths, compiler identities, subasset lists,
///      header versions) can leak into the output — the defect class the old
///      embedded-template approach had.
///
/// Round-trip semantics: take any KV3-backed <c>_c</c>, decompile it via the
/// decompile direction, edit the text, recompile here, and the result
/// will re-parse via VRF and carry the same data tree (byte-equality is
/// not promised — VRF's serializer rebuilds the string table and may
/// reorder type bytes).
///
/// <c>.vsnd</c> is deliberately REJECTED here: a real <c>vsnd_c</c> is an
/// audio container (resver 5, CTRL sound-shape + the encoded audio appended
/// after the block section) — KV3 text alone cannot author one, and the old
/// behavior of wrapping the text in a sound-EVENT skeleton produced a file
/// with the wrong resource version and compiler identity that no engine path
/// could meaningfully load. Sounds ship through the audio route
/// (<see cref="ResourceBuilder.BuildSound"/> / <c>RebuildSound</c>).
///
/// What this does NOT do (deliberate simplification):
///
///   • Validate the parsed tree against a per-resource-type schema. We
///     trust the user's text header to declare its own format GUID and
///     trust them to write a tree the engine will accept. The decompile
///     side's KV3 output is the canonical reference for what shape the
///     text should have.
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
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the parsed text has no Format header (no
    /// <c>&lt;!-- kv3 encoding:... format:... --&gt;</c> comment), when the
    /// input isn't parseable KV3, or for a <c>.vsnd</c> source (see class doc).
    /// </exception>
    public static byte[] Compile(byte[] textBytes, string? sourceExtension = null, string? sourceFileName = null)
    {
        if (sourceExtension is { Length: > 0 } && sourceExtension[0] != '.')
            sourceExtension = "." + sourceExtension;
        if (textBytes.Length == 0)
            throw new InvalidOperationException("Empty input — KV3 text expected.");

        if (string.Equals(sourceExtension, ".vsnd", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A .vsnd_c is an audio container (sound shape + encoded audio), not a KV3 document — " +
                "it cannot be authored from KV3 text alone. Upload the audio file instead (the sound " +
                "route compiles WAV/MP3 into a proper vsnd_c).");
        }

        // 1. Parse user text → KVDocument. ValveKeyValue's KV3 reader handles
        //    the comment-style header, type/format flags, multiline strings,
        //    nested objects, arrays of mixed type, binary blobs, etc. — much
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
        return Source2ContainerAuthor.AuthorKv3Resource(userDoc, ext, textBytes, sourceFileName);
    }
}
