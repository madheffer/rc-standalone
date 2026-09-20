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
