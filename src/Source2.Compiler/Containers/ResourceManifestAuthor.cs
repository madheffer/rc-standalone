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
/// in the survey corpus (docs/MAP_RESOURCES.md). Every one of them has exactly one
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
