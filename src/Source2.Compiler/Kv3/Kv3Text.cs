using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// KV3 text read the way this compiler reads a source: refused before parsing when it nests
/// deeper than <see cref="Kv3LineColumns.MaxDepth"/> or has a stray control character outside
/// strings (either takes ValveKeyValue's reader to a stack overflow or an endless allocation), and
/// a quoted <c>"false"</c> or <c>"12"</c> kept a string as resourcecompiler keeps it.
/// </summary>
public static class Kv3Text
{
    /// <summary>Parse <paramref name="text"/> (UTF-8), header included.</summary>
    /// <exception cref="InvalidDataException">The text is empty, unsafe or not KV3.</exception>
    public static KVDocument Parse(byte[] text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            throw new InvalidDataException("Empty input - KV3 text expected.");
        var decoded = System.Text.Encoding.UTF8.GetString(text);
        var (depth, controlAt) = Kv3LineColumns.PreParse(decoded);
        if (depth > Kv3LineColumns.MaxDepth)
            throw new InvalidDataException($"KV3 text nests {depth} levels deep; {Kv3LineColumns.MaxDepth} is the most this compiler accepts.");
        if (controlAt >= 0)
            throw new InvalidDataException(
                $"KV3 text has a stray character (a control character or ';') outside a string at offset {controlAt}; it is not valid KV3.");

        KVDocument document;
        try
        {
            using var stream = new MemoryStream(text, writable: false);
            document = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(stream);
        }
        catch (Exception ex) when (ex is KeyValueException or InvalidDataException or FormatException
                                       or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException(
                "Could not parse input as KV3 text. Make sure the file starts with a " +
                "<!-- kv3 encoding:... format:... --> header and that all braces / quotes balance. " +
                $"Underlying error: {ex.Message}", ex);
        }

        Kv3LineColumns.RestoreQuotedStrings(decoded, document.Root);
        return document;
    }
}
