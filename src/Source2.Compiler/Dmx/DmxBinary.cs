using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Source2.Compiler;

/// <summary>
/// Reads a binary DMX file, which is what a Hammer <c>.vmap</c> source is.
///
/// <para>The layout, verified by parsing a real published <c>.vmap</c> to its last
/// byte (27,856 of 27,856, no remainder):</para>
///
/// <code>
///   "&lt;!-- dmx encoding binary 9 format vmap 40 --&gt;\n\0"
///   u32 prefixElementCount
///     per element: u32 attrCount, then attributes with INLINE names and strings
///   u32 stringCount, then that many NUL-terminated strings
///   u32 elementCount
///     per element: i32 typeIndex, i32 nameIndex, byte[16] guid
///   per element, in the same order: u32 attrCount
///     per attribute: i32 nameIndex, u8 type, value
/// </code>
///
/// <para>Two rules are not guessable from the shape. An attribute type above
/// <see cref="ArrayTypeBase"/> is an ARRAY of the type below it, and a string
/// inside an array is written inline even though a scalar string in the same
/// section is a string-table index.</para>
/// </summary>
public static class DmxBinary
{
    /// <summary>Array types are the scalar type plus this.</summary>
    public const byte ArrayTypeBase = 32;

    /// <summary>A parsed DMX file.</summary>
    /// <param name="Encoding">Encoding name, e.g. <c>binary</c>.</param>
    /// <param name="EncodingVersion">Encoding version; 9 for a CS2 map.</param>
    /// <param name="Format">Format name, e.g. <c>vmap</c>.</param>
    /// <param name="FormatVersion">Format version; 35 to 40 across shipped ZE ports.</param>
    /// <param name="Elements">Every element, in file order. The first is the root.</param>
    /// <param name="Prefix">The prefix elements, which carry the file's own metadata
    /// (a map's editor thumbnail and its asset reference list).</param>
    /// <param name="TrailingBytes">Bytes left after the last attribute. A Valve
    /// file has none, so anything here means the reader misjudged a length.</param>
    public sealed record Document(
        string Encoding,
        int EncodingVersion,
        string Format,
        int FormatVersion,
        IReadOnlyList<Element> Elements,
        IReadOnlyList<Element> Prefix,
        int TrailingBytes)
    {
        /// <summary>The root element, or null for an empty file.</summary>
        public Element? Root => Elements.Count > 0 ? Elements[0] : null;

        /// <summary>Every element of a given type, in file order.</summary>
        public IEnumerable<Element> OfType(string type)
            => Elements.Where(e => string.Equals(e.Type, type, StringComparison.Ordinal));
    }

    /// <summary>One element: its type, its name, and its attributes.</summary>
    public sealed class Element
    {
        internal Element(string type, string name, Guid id) => (Type, Name, Id) = (type, name, id);

        /// <summary>Element class, e.g. <c>CMapEntity</c>.</summary>
        public string Type { get; }

        /// <summary>Element name; often empty.</summary>
        public string Name { get; }

        /// <summary>The element's GUID as stored.</summary>
        public Guid Id { get; }

        /// <summary>Attributes by name, in file order.</summary>
        public Dictionary<string, object?> Attributes { get; } = [];

        /// <summary>An attribute value, or null when absent or of another type.</summary>
        public T? Get<T>(string name) where T : class
            => Attributes.TryGetValue(name, out var v) ? v as T : null;

        /// <summary>A value-typed attribute, or null when absent or of another type.</summary>
        public T? GetValue<T>(string name) where T : struct
            => Attributes.TryGetValue(name, out var v) && v is T t ? t : null;

        /// <summary>The elements an element-array attribute points at.</summary>
        public IEnumerable<Element> GetElements(string name)
            => Get<object?[]>(name)?.OfType<Element>() ?? [];

        /// <inheritdoc/>
        public override string ToString() => $"{Type} \"{Name}\" ({Attributes.Count} attrs)";
    }

    /// <summary>Parse <paramref name="bytes"/>. Throws <see cref="InvalidDataException"/>
    /// on anything it cannot read, rather than returning a half-parsed graph.</summary>
    public static Document Read(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes);
        var (encoding, encodingVersion, format, formatVersion) = ReadHeader(ref r);
        if (encodingVersion is < 9 or > 9)
            throw new InvalidDataException(
                $"DMX binary encoding {encodingVersion} is not supported (this reads 9, what CS2 writes).");

        // The prefix section names its attributes inline and has no string table
        // to reference, so its strings are inline too.
        var prefix = new List<Element>();
        var prefixCount = r.Count();
        for (var i = 0; i < prefixCount; i++)
        {
            var element = new Element("$prefix_element$", "", Guid.Empty);
            var attrCount = r.Count();
            for (var a = 0; a < attrCount; a++)
            {
                var name = r.InlineString();
                element.Attributes[name] = ReadValue(ref r, r.Byte(), [], inlineStrings: true, elements: null);
            }
            prefix.Add(element);
        }

        var stringCount = r.Count();
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
            strings[i] = r.InlineString();

        var elementCount = r.Count();
        var elements = new Element[elementCount];
        for (var i = 0; i < elementCount; i++)
        {
            var type = strings[r.Index(strings.Length)];
            var name = strings[r.Index(strings.Length)];
            elements[i] = new Element(type, name, new Guid(r.Bytes(16)));
        }

        string? last = null;
        foreach (var element in elements)
        {
            var attrCount = r.Count();
            for (var a = 0; a < attrCount; a++)
            {
                int index;
                try
                {
                    index = r.Index(strings.Length);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException($"{ex.Message} (element {element.Type} '{element.Name}', after attribute {last})", ex);
                }
                var name = strings[index];
                var type = r.Byte();
                last = $"{name} of type {type}";
                element.Attributes[name] = ReadValue(ref r, type, strings, inlineStrings: false, elements);
            }
        }

        return new Document(encoding, encodingVersion, format, formatVersion, elements, prefix, r.Remaining);
    }

    /// <summary>Parse a file, binary or keyvalues2 text (<see cref="DmxText"/>).</summary>
    public static Document ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DmxText.IsText(bytes) ? DmxText.Read(bytes) : Read(bytes);
    }

    /// <summary>An element for another reader of the format to fill.</summary>
    internal static Element NewElement(string type, string name, Guid id) => new(type, name, id);

    private static (string Encoding, int EncodingVersion, string Format, int FormatVersion) ReadHeader(ref Reader r)
    {
        var line = r.InlineString();
        // "<!-- dmx encoding binary 9 format vmap 40 -->"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var encoding = Array.IndexOf(parts, "encoding");
        var format = Array.IndexOf(parts, "format");
        if (parts.Length < 8 || encoding < 0 || format < 0 || encoding + 2 >= parts.Length || format + 2 >= parts.Length)
            throw new InvalidDataException($"Not a DMX header: '{line}'.");
        return (parts[encoding + 1], int.Parse(parts[encoding + 2]),
                parts[format + 1], int.Parse(parts[format + 2]));
    }

    private static object? ReadValue(ref Reader r, byte type, string[] strings, bool inlineStrings, Element[]? elements)
    {
        if (type > ArrayTypeBase)
        {
            var scalar = (byte)(type - ArrayTypeBase);
            var count = r.Count();
            var values = new object?[count];
            for (var i = 0; i < count; i++)
                // A string inside an array is inline in every section.
                values[i] = ReadScalar(ref r, scalar, strings, inlineStrings: scalar == 5, elements);
            return values;
        }
        return ReadScalar(ref r, type, strings, inlineStrings, elements);
    }

    private static object? ReadScalar(ref Reader r, byte type, string[] strings, bool inlineStrings, Element[]? elements)
        => type switch
        {
            1 => Reference(ref r, elements),
            2 => r.Int(),
            3 => r.Float(),
            4 => r.Byte() != 0,
            5 => inlineStrings ? r.InlineString() : strings[r.Index(strings.Length)],
            6 => r.Bytes(r.Count()).ToArray(),
            7 => TimeSpan.FromSeconds(r.Int() / 10000.0),
            8 => r.Bytes(4).ToArray(),
            9 => new Vector2(r.Float(), r.Float()),
            10 or 12 => new Vector3(r.Float(), r.Float(), r.Float()),
            11 => new Vector4(r.Float(), r.Float(), r.Float(), r.Float()),
            13 => new Quaternion(r.Float(), r.Float(), r.Float(), r.Float()),
            14 => ReadMatrix(ref r),
            15 => r.ULong(),
            16 => r.Byte(),
            _ => throw new InvalidDataException($"Unknown DMX attribute type {type}."),
        };

    // -1 is null and -2 an element outside the file, its GUID following as an
    // inline string (the binary serializer's ELEMENT_INDEX_EXTERNAL); neither
    // resolves here.
    private static object? Reference(ref Reader r, Element[]? elements)
    {
        var index = r.Int();
        if (index == -2)
        {
            r.InlineString();
            return null;
        }
        return elements is not null && index >= 0 && index < elements.Length ? elements[index] : null;
    }

    private static Matrix4x4 ReadMatrix(ref Reader r)
        => new(r.Float(), r.Float(), r.Float(), r.Float(),
               r.Float(), r.Float(), r.Float(), r.Float(),
               r.Float(), r.Float(), r.Float(), r.Float(),
               r.Float(), r.Float(), r.Float(), r.Float());

    /// <summary>
    /// A bounds-checked cursor. Every read is checked because these files come
    /// from strangers: a corrupt count would otherwise allocate wildly or walk
    /// off the end.
    /// </summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        /// <summary>Bytes not consumed, which a well-formed file leaves at zero.</summary>
        public readonly int Remaining => _data.Length - _at;

        public byte Byte() => Bytes(1)[0];

        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));

        public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Bytes(4));

        public ulong ULong() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));

        /// <summary>A count that must be able to fit in what is left, so a hostile
        /// value fails here rather than during a huge allocation.</summary>
        public int Count()
        {
            var n = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
            if (n > (uint)(_data.Length - _at))
                throw new InvalidDataException($"DMX count {n} exceeds the {_data.Length - _at} bytes left.");
            return (int)n;
        }

        public int Index(int limit)
        {
            var i = Int();
            if (i < 0 || i >= limit)
                throw new InvalidDataException($"DMX string index {i} is outside the table of {limit}.");
            return i;
        }

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || _at + n > _data.Length)
                throw new InvalidDataException($"DMX read of {n} bytes runs past the end.");
            var span = _data.Slice(_at, n);
            _at += n;
            return span;
        }

        public string InlineString()
        {
            var end = _data[_at..].IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException("DMX string is not terminated.");
            var s = Encoding.UTF8.GetString(_data.Slice(_at, end));
            _at += end + 1;
            return s;
        }
    }
}
