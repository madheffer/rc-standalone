namespace Source2.Compiler.Maps;

/// <summary>
/// tier1's CUtlHashtable as resourcecompiler inlines it (insert 18028f100,
/// rehash insert 18022b530, bump 18022c170, rehash 18022bec0), kept for its
/// slot order: a walk over the slots is the order its callers see. Each slot
/// holds a word (bit 31 empty, bit 30 the last of its chain, the low 30
/// bits the hash) and a key. A new key takes its ideal slot, bumping what is
/// there along its chain; the table doubles (a power of two, at least 32)
/// when count * 4 + 4 passes capacity * 3, re-inserting from the last slot
/// to the first.
/// </summary>
internal sealed class UtlHashtable<TKey> where TKey : IEquatable<TKey>
{
    private uint[] words = [];
    private TKey[] keys = [];
    private int count;
    private const int MinimumSize = 0x20;

    /// <summary>The slots in order with their keys.</summary>
    public IEnumerable<(int Slot, TKey Key)> Slots()
    {
        for (var i = 0; i < words.Length; i++)
            if ((int)words[i] >= 0)
                yield return (i, keys[i]);
    }

    /// <summary>The key's slot, inserting it when absent.</summary>
    public int Insert(TKey key, uint hash)
    {
        if (count != 0)
        {
            var mask = (uint)words.Length - 1;
            var ideal = hash & mask;
            var at = ideal;
            if (Ideal(words[at], mask) == ideal)
                while (true)
                {
                    var w = words[at];
                    if (Ideal(w, mask) == ideal)
                    {
                        if (((w ^ hash) & 0x3fffffff) == 0 && keys[at].Equals(key))
                            return (int)at;
                        if ((w >> 30 & 1) != 0)
                            break;
                    }
                    at = (at + 1) & mask;
                }
        }
        if ((uint)(count * 4 + 4) > (uint)(words.Length * 3))
            Grow((count * 4 + 4) / 3);
        return Place(key, hash);
    }

    // 18022b530 without the growth test: count up, then the key at its ideal slot.
    private int Place(TKey key, uint hash)
    {
        count++;
        var mask = (uint)words.Length - 1;
        var plain = hash & 0x3fffffff;
        var chained = plain | 0x40000000;
        var slot = mask & hash;
        var w = words[slot];
        var word = chained;
        if (Ideal(w, mask) == slot)
        {
            Bump(slot);
            word = plain;
        }
        else if ((int)w >= 0)
        {
            Bump(slot);
            word = chained;
        }
        words[slot] = word;
        keys[slot] = key;
        return (int)slot;
    }

    // An occupied slot's ideal slot; an empty one reads as all ones.
    private static uint Ideal(uint word, uint mask) => (word & mask) | (uint)((int)word >> 31);

    // 18022c170: the entry at slot moves to the first free slot of its chain's walk.
    private void Bump(uint slot)
    {
        var mask = (uint)words.Length - 1;
        var moving = words[slot] & 0x7fffffff;
        var ideal = moving & mask;
        var at = ideal;
        while (true)
        {
            while (true)
            {
                var w = words[at];
                if (Ideal(w, mask) != ideal)
                {
                    if ((int)w < 0)
                        goto Found;
                    break;
                }
                if ((w >> 30 & 1) == 0)
                    break;
                moving |= 0x40000000;
                words[at] = w & 0xbfffffff;
                at = (at + 1) & mask;
            }
            at = (at + 1) & mask;
        }
    Found:
        if ((words[slot] & 0x40000000) != 0)
        {
            var back = slot;
            while (true)
            {
                back = (mask + back) & mask;
                if (back == at)
                    break;
                var w = words[back];
                if (Ideal(w, mask) == ideal)
                {
                    words[back] = w | 0x40000000;
                    moving &= 0xbfffffff;
                    break;
                }
            }
        }
        words[at] = moving;
        keys[at] = keys[slot];
        words[slot] = 0x80000000;
    }

    // 18022bec0: a power of two at least the request and 32, old slots re-placed last to first.
    private void Grow(int request)
    {
        if (request < MinimumSize)
            request = MinimumSize;
        var size = 1;
        while (size < request)
            size <<= 1;
        var (oldWords, oldKeys) = (words, keys);
        words = new uint[size];
        keys = new TKey[size];
        Array.Fill(words, 0x80000000);
        count = 0;
        for (var i = oldWords.Length - 1; i >= 0; i--)
            if ((int)oldWords[i] >= 0)
                Place(oldKeys[i], oldWords[i]);
    }
}
