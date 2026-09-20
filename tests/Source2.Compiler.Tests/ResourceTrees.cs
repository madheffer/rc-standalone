using System.Buffers.Binary;
using System.Text;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Tests;

/// <summary>
/// Reads a compiled resource and renders its DATA block as a flat, diffable
/// listing.
///
/// <para>The listing carries each leaf's KV3 VALUE TYPE and flag, not just its
/// text, because that is where the failures hide: an integer widened to UInt64,
/// a resource reference written as a plain string, an empty collection written as
/// an empty array. A "does it parse" check sails past all three, and the first of
/// them shipped in every KV3 compile this project made until 2026-08-10.</para>
/// </summary>
internal static class ResourceTrees
{
    public static Resource Read(byte[] bytes, string name)
    {
        var res = new Resource { FileName = name };
        res.Read(new MemoryStream(bytes));
        return res;
    }

    /// <summary>
    /// The container's own block table, read from the bytes rather than from the
    /// parsed resource. VRF SKIPS a zero-size block when reading, so an empty DATA
    /// block - which is exactly what a map root ships - is invisible to
    /// <c>GetBlockByType</c> on Valve's file and ours alike.
    /// </summary>
    public static IReadOnlyList<(string FourCC, uint Size)> BlockTable(ReadOnlySpan<byte> file)
    {
        var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        var at = 8 + (int)blockOffset;
        var table = new List<(string, uint)>();
        for (var i = 0; i < count; i++, at += 12)
            table.Add((Encoding.ASCII.GetString(file.Slice(at, 4)),
                       BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 8)..])));
        return table;
    }

    public static string Render(Resource res)
    {
        var data = res.GetBlockByType(BlockType.DATA);
        var root = data is KeyValuesOrNTRO kvn ? kvn.Data : data!.AsKeyValueCollection();
        var sb = new StringBuilder();
        Render(root, "", sb);
        return sb.ToString();
    }

    private static void Render(KVObject? n, string path, StringBuilder sb)
    {
        if (n is null) { sb.AppendLine($"{path} = <null>"); return; }
        if (n.IsArray)
        {
            var i = 0;
            foreach (var c in n.Values)
                Render(c, $"{path}[{i++}]", sb);
            if (i == 0)
                sb.AppendLine($"{path} = []");
            return;
        }
        if (n.IsCollection)
        {
            var any = false;
            foreach (var c in n.Children) { any = true; Render(c.Value, $"{path}.{c.Key}", sb); }
            if (!any)
                sb.AppendLine($"{path} = {{}}");
            return;
        }
        var val = n.ValueType == KVValueType.String ? $"\"{(string)n}\"" : n.ToString();
        sb.AppendLine($"{path} = [{n.ValueType}{(n.Flag == KVFlag.None ? "" : "/" + n.Flag)}] {val}");
    }
}
