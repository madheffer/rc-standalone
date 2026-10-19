namespace Source2.Compiler.Lighting;

/// <summary>
/// tier1's CUtlHashtable as resourcecompiler's lightmap packer instantiates it
/// (0923 18022abf0 insert, 18022a7f0 bump, 180228a30 rehash): open
/// addressing over a power-of-two slot array, each slot a word whose bit 31
/// marks it free, bit 30 the last entry of its home's chain, and the low 30
/// bits the hash. A new key always takes its home slot; the entry there is
/// bumped to the first free slot past its chain's end, and the chain's last
/// flag follows. Rehashing walks the old slots from the last to the first.
/// The slot order is what callers iterate, so it is Valve's exactly.
/// </summary>
internal sealed class UtlHashtable<TKey, TValue> where TKey : IEquatable<TKey>
{
    private const uint Free = 0x80000000u, Last = 0x40000000u, HashMask = 0x3fffffffu;

    private uint[] _words = [];
    private TKey[] _keys = [];
    private TValue[] _values = [];
    private readonly int _minSize;

    public UtlHashtable(int minSize = 32) => _minSize = minSize;

    public int Count { get; private set; }

    public int Buckets => _words.Length;

    /// <summary>180228a30: at least <paramref name="n"/> slots, a power of two no smaller than the minimum.</summary>
    public void Reserve(int n)
    {
        if (n < _minSize)
            n = _minSize;
        var p = (uint)(n - 1);
        p |= p >> 1;
        p |= p >> 2;
        p |= p >> 4;
        p |= p >> 8;
        p = (p >> 16 | p) + 1;
        var buckets = (int)p;
        var (oldWords, oldKeys, oldValues) = (_words, _keys, _values);
        _words = new uint[buckets];
        _keys = new TKey[buckets];
        _values = new TValue[buckets];
        Array.Fill(_words, Free);
        var remaining = Count;
        Count = 0;
        for (var i = oldWords.Length - 1; i >= 0 && remaining > 0; i--)
        {
            var w = oldWords[i];
            if ((int)w < 0)
                continue;
            Count++;
            Place(w, oldKeys[i], oldValues[i]);
            remaining--;
        }
    }

    /// <summary>18022abf0: the slot holding <paramref name="key"/>, inserting it with a default value if absent.</summary>
    public int FindOrInsert(TKey key, uint hash, out bool inserted)
    {
        var at = Find(key, hash);
        inserted = at < 0;
        if (at >= 0)
            return at;
        if (Count * 4 + 4 > _words.Length * 3)
            Reserve((Count * 4 + 4) / 3);
        Count++;
        return Place(hash, key, default!);
    }

    public int Find(TKey key, uint hash)
    {
        if (Count == 0)
            return -1;
        var mask = (uint)_words.Length - 1;
        var home = hash & mask;
        if (Home(_words[home], mask) != home)
            return -1;
        for (var i = home; ; i = (i + 1) & mask)
        {
            var w = _words[i];
            if (Home(w, mask) != home)
                continue;
            if (((w ^ hash) & HashMask) == 0 && _keys[i].Equals(key))
                return (int)i;
            if ((w & Last) != 0)
                return -1;
        }
    }

    public ref TValue Value(int slot) => ref _values[slot];

    /// <summary>RemoveAll: every slot freed, the slot array kept (its size, and so the slot order of what comes next).</summary>
    public void Clear()
    {
        Array.Fill(_words, Free);
        Count = 0;
    }

    public TKey Key(int slot) => _keys[slot];

    /// <summary>The occupied slots in slot order.</summary>
    public IEnumerable<int> Slots()
    {
        for (var i = 0; i < _words.Length; i++)
            if ((int)_words[i] >= 0)
                yield return i;
    }

    // Home(w): the slot an entry belongs to, or all ones for a free slot.
    private static uint Home(uint w, uint mask) => (int)w < 0 ? uint.MaxValue : w & mask;

    private int Place(uint hash, TKey key, TValue value)
    {
        var mask = (uint)_words.Length - 1;
        var plain = hash & HashMask;
        var slot = hash & mask;
        var w = _words[slot];
        uint word;
        if (Home(w, mask) == slot)
        {
            Bump(slot);
            word = plain;
        }
        else
        {
            word = plain | Last;
            if ((int)w >= 0)
                Bump(slot);
        }
        _words[slot] = word;
        _keys[slot] = key;
        _values[slot] = value;
        return (int)slot;
    }

    // 18022a7f0: move the entry at <slot> to the first free slot past its
    // home chain's end, taking the chain's last flag along.
    private void Bump(uint slot)
    {
        var mask = (uint)_words.Length - 1;
        var moved = _words[slot] & 0x7fffffffu;
        var home = moved & mask;
        var i = home;
        while (true)
        {
            var w = _words[i];
            if (Home(w, mask) == home)
            {
                if ((w & Last) != 0)
                {
                    moved |= Last;
                    _words[i] = w & ~Last;
                }
                i = (i + 1) & mask;
                continue;
            }
            if ((int)w < 0)
                break;
            i = (i + 1) & mask;
        }
        if ((_words[slot] & Last) != 0)
        {
            var j = slot;
            while (true)
            {
                j = (mask + j) & mask;
                if (j == i)
                    break;
                var w = _words[j];
                if (Home(w, mask) == home)
                {
                    _words[j] = w | Last;
                    moved &= ~Last;
                    break;
                }
            }
        }
        _words[i] = moved;
        _keys[i] = _keys[slot];
        _values[i] = _values[slot];
        _words[slot] = Free;
    }
}
