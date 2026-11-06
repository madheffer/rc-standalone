using Source2.Compiler.Io;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// What resourcecompiler's shared KV3 compile (CResourceCompilerKV3::vf0 1800fa170 -> 1800f9db0,
/// rc 20260923) does with a type's compiled tree before saving it, for every KV3 text type:
/// <list type="number">
/// <item>1800f8950: every non-empty <c>resource:</c> or <c>resource_name:</c> string is fixed up in
/// place (181c1e960 -> FixupResourceName_NoExtension 181c1dca0, <see cref="ResourcePath"/>: dot
/// segments resolved, lower case, forward slashes) and keeps its flag. One FixupResourceName
/// refuses (an absolute or rooted path) is "Bad resource reference", an error.</item>
/// <item>1800f8b70: a <c>soundevent:</c> string is a <c>soundevent</c> subasset reference (count 1
/// each, AddSubassetReference 180175420); a <c>resource:</c> string a strong reference (context vf0
/// 180166b40) and a <c>resource_name:</c> string a weak one (vf1 180166c10), each a set keyed by
/// the resource id.</item>
/// <item>1800f8d90 turns <c>panorama:</c> strings that name images into references; not ported, so
/// such a string is refused.</item>
/// </list>
/// The edit info finalize (18016c650) then sorts the weak references with the comparator RERL
/// uses (std_sort_ResourceRefs 18017a5a0, V_stricmp_fast of the name), and RERL's one writer
/// (18016c0e0) sorts the strong ones the same way.
/// </summary>
public static class Kv3References
{
    /// <summary>The fixed-up tree, the strong and weak references (each sorted as RC lists them),
    /// and the soundevent references in the order the walk met them, with their counts.</summary>
    public sealed record Result(KVObject Data, IReadOnlyList<string> Strong, IReadOnlyList<string> Weak,
        IReadOnlyList<(string Name, int Count)> SoundEvents);

    /// <summary>Apply the shared KV3 compile passes to <paramref name="root"/>.</summary>
    /// <exception cref="InvalidDataException">A reference RC calls bad.</exception>
    public static Result Apply(KVObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var strong = new Dictionary<ulong, string>();
        var weak = new Dictionary<ulong, string>();
        var sounds = new List<(string Name, int Count)>();
        var data = Walk(root, strong, weak, sounds);
        return new Result(data, EditInfo.Red2Sorted(strong.Values, s => s), EditInfo.Red2Sorted(weak.Values, s => s), sounds);
    }

    private static KVObject Walk(KVObject node, Dictionary<ulong, string> strong, Dictionary<ulong, string> weak,
        List<(string Name, int Count)> sounds)
    {
        if (node.IsCollection)
        {
            var table = KVObject.Collection();
            foreach (var child in node.Children)
                table.Add(child.Key, Walk(child.Value, strong, weak, sounds));
            return table;
        }
        if (node.IsArray)
            return KVObject.Array(node.Values.Select(v => Walk(v, strong, weak, sounds)).ToList());
        if (node.ValueType != KVValueType.String || (string)node is not { Length: > 0 } text)
            return node;

        switch (node.Flag)
        {
            case KVFlag.Resource or KVFlag.ResourceName:
            {
                var fixedUp = Fixup(text);
                if (fixedUp.Length == 0)
                    return new KVObject("") { Flag = node.Flag };
                (node.Flag == KVFlag.Resource ? strong : weak).TryAdd(Source2ResourceId.ForPath(fixedUp), fixedUp);
                return new KVObject(fixedUp) { Flag = node.Flag };
            }
            case KVFlag.SoundEvent:
            {
                var at = sounds.FindIndex(s => Tier0Strings.EqualsIgnoreCase(s.Name, text));
                if (at < 0)
                    sounds.Add((text, 1));
                else
                    sounds[at] = (sounds[at].Name, sounds[at].Count + 1);
                return node;
            }
            case KVFlag.Panorama:
                throw new InvalidOperationException(
                    $"A panorama: string (\"{text}\") is not ported: resourcecompiler turns image names into references (1800f8d90).");
            default:
                return node;
        }
    }

    private static string Fixup(string text)
    {
        if (text.StartsWith('\\') || text.StartsWith('/') || (text.Length > 1 && text[1] == ':'))
            throw new InvalidDataException($"Bad resource reference \"{text}\"!");
        return ResourcePath.Fixup(text, "");
    }

    /// <summary>Add <paramref name="sounds"/> to a RED2 subasset reference table (null when there is
    /// none yet), under <c>soundevent</c>, as AddSubassetReference does: names keyed ignoring case,
    /// counts added.</summary>
    public static KVObject AddSoundEvents(KVObject references, IReadOnlyList<(string Name, int Count)> sounds)
    {
        if (sounds.Count == 0)
            return references;
        var result = KVObject.Collection();
        KVObject? soundTable = null;
        if (!references.IsNull)
            foreach (var child in references.Children)
            {
                if (soundTable is null && Tier0Strings.EqualsIgnoreCase(child.Key, "soundevent"))
                {
                    soundTable = KVObject.Collection();
                    foreach (var name in child.Value.Children)
                        soundTable.Add(name.Key, name.Value);
                    result.Add(child.Key, soundTable);
                }
                else
                    result.Add(child.Key, child.Value);
            }
        if (soundTable is null)
        {
            soundTable = KVObject.Collection();
            result.Add("soundevent", soundTable);
        }

        var rebuilt = KVObject.Collection();
        var counts = soundTable.Children.Select(c => (c.Key, Count: (long)c.Value)).ToList();
        foreach (var (name, count) in sounds)
        {
            var at = counts.FindIndex(c => Tier0Strings.EqualsIgnoreCase(c.Key, name));
            if (at < 0)
                counts.Add((name, count));
            else
                counts[at] = (counts[at].Key, counts[at].Count + count);
        }
        foreach (var (name, count) in counts)
            rebuilt.Add(name, new KVObject(count));

        var final = KVObject.Collection();
        foreach (var child in result.Children)
            final.Add(child.Key, ReferenceEquals(child.Value, soundTable) ? rebuilt : child.Value);
        return final;
    }
}
