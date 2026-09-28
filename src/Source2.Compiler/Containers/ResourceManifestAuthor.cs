using System.Buffers.Binary;
using System.Text;

namespace Source2.Compiler;

/// <summary>
/// Writes a <c>.vrman_c</c>, the list of resources the engine loads alongside
/// something bigger. A map has one beside its world, one beside each world node
/// and one beside its physics.
///
/// <para>Its DATA block is not KV3. It is a three level structure of self-relative
/// offsets, the same rule the container header uses, where every offset is added
/// to the address of the field holding it:</para>
///
/// <code>
///   i32 rel, u32 count     -> groups
///   i32 rel, u32 count     -> per group, its string pointer table
///   i32 rel                -> per string, 4 byte stride, points at NUL-terminated UTF-8
/// </code>
///
/// <para>Layout recovered from shipped maps and checked against all 301 manifests
/// in the survey corpus (docs/CONTAINERS.md). Every one of them has exactly one
/// group holding one to three strings.</para>
/// </summary>
public static class ResourceManifestAuthor
{
    /// <summary>Author the manifest for one group of resource paths.</summary>
    public static byte[] Author(IReadOnlyList<string> resources) => Author([resources]);

    /// <summary>Author a manifest of several groups.</summary>
    public static byte[] Author(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return Source2ContainerAuthor.AuthorChildResource(".vrman", BuildData(groups), groups.SelectMany(g => g));
    }

    /// <summary>
    /// A manifest RC compiles from a file it saved first (AddManifest,
    /// rc 1801f9550): a physics or world node manifest. RED2 names that file,
    /// <paramref name="relativeFilename"/> under <c>csgo_addons/&lt;addon&gt;</c>,
    /// with the CRC32 of <see cref="SourceText"/>.
    /// </summary>
    public static byte[] AuthorSaved(IReadOnlyList<string> resources, string relativeFilename, string addon)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var crc = System.IO.Hashing.Crc32.HashToUInt32(Encoding.UTF8.GetBytes(SourceText(resources)));
        return Source2ContainerAuthor.AuthorChildResource(".vrman", BuildData([resources]), resources,
            new SavedSource(relativeFilename, "csgo_addons/" + addon, crc));
    }

    /// <summary>
    /// The saved source: rc 181c1f980 appends each path to a flat
    /// <c>resourceManifest</c> array, 181c1fae0 writes it with SaveKV3 (text,
    /// generic), CRLF and no final newline. Its CRC matches Valve's on four maps.
    /// </summary>
    public static string SourceText(IReadOnlyList<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var text = new StringBuilder();
        text.Append("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\r\n");
        text.Append("{\r\n\tresourceManifest = \r\n\t[\r\n");
        foreach (var resource in resources)
            text.Append("\t\t\"").Append(resource).Append("\",\r\n");
        text.Append("\t]\r\n}");
        return text.ToString();
    }

    /// <summary>
    /// The DATA payload on its own, for a caller assembling the container itself
    /// and for the round-trip test that compares it against Valve's bytes.
    /// </summary>
    public static byte[] BuildData(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (var group in groups)
            ArgumentNullException.ThrowIfNull(group);

        // Sections in the order Valve writes them, so the offsets below are all
        // forward: the outer header, the group headers, each group's pointer
        // table, then the strings themselves.
        var groupHeaders = 8;
        var pointerTables = groupHeaders + groups.Count * 8;
        var strings = pointerTables + groups.Sum(g => g.Count) * 4;

        var bytes = new byte[strings + groups.SelectMany(g => g).Sum(s => Encoding.UTF8.GetByteCount(s) + 1)];
        var buffer = bytes.AsSpan();

        Write(buffer, 0, groupHeaders - 0);
        WriteCount(buffer, 4, groups.Count);

        var pointerAt = pointerTables;
        var stringAt = strings;
        for (var g = 0; g < groups.Count; g++)
        {
            var headerAt = groupHeaders + g * 8;
            Write(buffer, headerAt, pointerAt - headerAt);
            WriteCount(buffer, headerAt + 4, groups[g].Count);

            foreach (var resource in groups[g])
            {
                Write(buffer, pointerAt, stringAt - pointerAt);
                pointerAt += 4;
                stringAt += Encoding.UTF8.GetBytes(resource, buffer[stringAt..]) + 1;
            }
        }

        return bytes;
    }

    /// <summary>
    /// Read a manifest's DATA payload back into its groups of paths - the
    /// decompile direction, and what a round-trip test compares against.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadData(ReadOnlySpan<byte> data)
    {
        var groupsAt = At(data, 0);
        var groupCount = Count(data, 4);
        var groups = new List<IReadOnlyList<string>>(groupCount);

        for (var g = 0; g < groupCount; g++)
        {
            var headerAt = groupsAt + g * 8;
            var pointerAt = At(data, headerAt);
            var count = Count(data, headerAt + 4);
            var paths = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var stringAt = At(data, pointerAt + i * 4);
                var end = data[stringAt..].IndexOf((byte)0);
                if (end < 0)
                    throw new InvalidDataException($"Manifest string at {stringAt} is not terminated.");
                paths.Add(Encoding.UTF8.GetString(data.Slice(stringAt, end)));
            }
            groups.Add(paths);
        }
        return groups;
    }

    private static int At(ReadOnlySpan<byte> data, int at)
    {
        var target = at + BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        if ((uint)target >= (uint)data.Length)
            throw new InvalidDataException($"Manifest offset at {at} points outside the block.");
        return target;
    }

    private static int Count(ReadOnlySpan<byte> data, int at)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        // Each entry costs at least four bytes, so anything past that is a
        // corrupt or hostile payload rather than a big manifest.
        if (count > data.Length / 4)
            throw new InvalidDataException($"Manifest count {count} at {at} exceeds the block.");
        return (int)count;
    }

    private static void Write(Span<byte> buffer, int at, int selfRelative)
        => BinaryPrimitives.WriteInt32LittleEndian(buffer[at..], selfRelative);

    private static void WriteCount(Span<byte> buffer, int at, int count)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer[at..], (uint)count);
}
