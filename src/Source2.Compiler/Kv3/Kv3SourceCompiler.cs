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
        if (string.Equals(sourceExtension, ".vsnd", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A .vsnd_c is an audio container (sound shape + encoded audio), not a KV3 document - " +
                "it cannot be authored from KV3 text alone. Upload the audio file instead (the sound " +
                "route compiles PCM WAV into a proper vsnd_c).");
        }

        KVDocument userDoc;
        try
        {
            userDoc = Kv3Text.Parse(textBytes);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (userDoc.Header?.Format == null)
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
