namespace Source2.Compiler.Physics;

/// <summary>
/// The open-addressed hash set the resource compiler's mesh code uses for
/// vertex and face handles, ported from its instances (realloc
/// <c>FUN_181103020</c> / <c>FUN_18012a510</c>, shift <c>FUN_181102e10</c> /
/// <c>FUN_18012b8b0</c>, insert-if-absent <c>FUN_1812e6810</c>, remove
/// <c>FUN_1812f90d0</c>). Callers read it back in slot order, so the slot
/// every key lands in is part of the output.
///
/// <para>A slot's flags word: bit 31 free, bit 30 last of its chain, the low
/// 30 bits the hash. A chain is every entry whose home slot (hash and mask) is
/// the same; its head sits in that home slot and the rest follow it in probe
/// order. A handle key is compared whole (index in the low 27 bits,
/// generation in the top 5).</para>
/// </summary>
internal sealed class ValveHashSet
{
    private const uint Free = 0x80000000;
    private const uint Last = 0x40000000;
    private const uint HashMask = 0x3fffffff;

    private uint[] _flags = [];
    private uint[] _keys = [];
    private int[] _values = [];
    private readonly int _minimum;

    public int Count { get; private set; }
    public int Size => _flags.Length;

    public ValveHashSet(int minimum = 32)
    {
        _minimum = minimum;
    }

    /// <summary>murmur3's fmix32, the hash every caller feeds in.</summary>
    public static uint Hash(uint key)
    {
        var h = (key >> 16 ^ key) * 0x85ebca6b;
        h = (h >> 13 ^ h) * 0xc2b2ae35;
        return h >> 16 ^ h;
    }

    private uint Ideal(uint flags) => (flags & (uint)(Size - 1)) | (uint)((int)flags >> 31);

    /// <summary>Realloc: the next power of two at or above max(n, minimum), and a rehash.</summary>
    public void Reserve(int n)
    {
        if (n < _minimum)
            n = _minimum;
        var p = (uint)n - 1;
        p |= p >> 1;
        p |= p >> 2;
        p |= p >> 4;
        p |= p >> 8;
        p = (p >> 16 | p) + 1;
        var oldFlags = _flags;
        var oldKeys = _keys;
        var oldValues = _values;
        var remaining = Count;
        _flags = new uint[p];
        _keys = new uint[p];
        _values = new int[p];
        Array.Fill(_flags, Free);
        Count = 0;
        // Re-inserted from the last slot down; each lands as its chain's head.
        for (var i = oldFlags.Length - 1; i >= 0 && remaining > 0; i--)
        {
            var f = oldFlags[i];
            if ((int)f < 0)
                continue;
            Count++;
            var mask = (uint)(Size - 1);
            var slot = mask & f;
            var bare = f & HashMask;
            var withLast = bare | Last;
            var occ = _flags[slot];
            var store = withLast;
            if (Ideal(occ) == slot)
            {
                Shift(slot);
                store = bare;
            }
            else if ((int)occ >= 0)
            {
                Shift(slot);
            }
            _flags[slot] = store;
            _keys[slot] = oldKeys[i];
            _values[slot] = oldValues[i];
            remaining--;
        }
    }

    // FUN_181102e10: move the entry at slot to the first free slot after its
    // chain's home, keeping the chain's last mark on its last member.
    private void Shift(uint slot)
    {
        var mask = (uint)(Size - 1);
        var moved = _flags[slot] & 0x7fffffff;
        var home = moved & mask;
        var i = home;
        while (true)
        {
            var e = _flags[i];
            if (Ideal(e) == home)
            {
                if ((e & Last) != 0)
                {
                    moved |= Last;
                    _flags[i] = e & ~Last;
                }
                i = (i + 1) & mask;
                continue;
            }
            if ((int)e < 0)
                break;
            i = (i + 1) & mask;
        }
        if ((_flags[slot] & Last) != 0)
        {
            var j = slot;
            while (true)
            {
                j = (mask + j) & mask;
                if (j == i)
                    goto Store;
                if (Ideal(_flags[j]) == home)
                    break;
            }
            _flags[j] |= Last;
            moved &= ~Last;
        }
    Store:
        _flags[i] = moved;
        _keys[i] = _keys[slot];
        _values[i] = _values[slot];
        _flags[slot] = Free;
    }

    /// <summary>The slot holding key, or -1.</summary>
    public int Find(uint key)
    {
        if (Count == 0)
            return -1;
        var h = Hash(key);
        var mask = (uint)(Size - 1);
        var home = mask & h;
        if (Ideal(_flags[home]) != home)
            return -1;
        var i = home;
        while (true)
        {
            var f = _flags[i];
            if (Ideal(f) == home)
            {
                if (((f ^ h) & HashMask) == 0 && _keys[i] == key)
                    return (int)i;
                if ((f & Last) != 0)
                    return -1;
            }
            i = (i + 1) & mask;
        }
    }

    /// <summary>Insert when absent (the growth check runs before the insert); the key's slot.</summary>
    public int Add(uint key, int value)
    {
        var found = Find(key);
        if (found >= 0)
            return found;
        var h = Hash(key);
        if (Size == 0 || (uint)(Size * 3) < (uint)(Count * 4 + 4))
            Reserve((Count * 4 + 4) / 3);
        Count++;
        var mask = (uint)(Size - 1);
        var slot = mask & h;
        var bare = h & HashMask;
        var store = bare | Last;
        var occ = _flags[slot];
        if (Ideal(occ) == slot)
        {
            Shift(slot);
            store = bare;
        }
        else if ((int)occ >= 0)
        {
            Shift(slot);
        }
        _flags[slot] = store;
        _keys[slot] = key;
        _values[slot] = value;
        return (int)slot;
    }

    public void Increment(int slot) => _values[slot]++;

    /// <summary>FUN_1812f90d0.</summary>
    public void Remove(uint key)
    {
        if (Count == 0)
            return;
        var h = Hash(key);
        var mask = (uint)(Size - 1);
        var home = mask & h;
        if (Ideal(_flags[home]) != home)
            return;
        var i = home;
        var prev = uint.MaxValue;
        while (true)
        {
            var f = _flags[i];
            if (Ideal(f) == home)
            {
                if (((f ^ h) & HashMask) == 0 && _keys[i] == key)
                    break;
                prev = i;
                if ((f & Last) != 0)
                    return;
            }
            i = (i + 1) & mask;
        }
        var flags = _flags[i];
        var ideal = Ideal(flags);
        _flags[i] = Free;
        Count--;
        var kind = (i == ideal ? 1u : 0u) | (flags & Last);
        if (kind == Last)
        {
            _flags[prev] |= Last;
            return;
        }
        if (kind == 1)
        {
            var j = i;
            do
            {
                j = (j + 1) & mask;
            } while (Ideal(_flags[j]) != (mask & h));
            _flags[i] = _flags[j];
            _keys[i] = _keys[j];
            _values[i] = _values[j];
            _flags[j] = Free;
        }
    }

    /// <summary>The occupied slots in slot order: (key, value).</summary>
    public IEnumerable<(uint Key, int Value)> InSlotOrder()
    {
        for (var i = 0; i < _flags.Length; i++)
        {
            if ((int)_flags[i] >= 0)
                yield return (_keys[i], _values[i]);
        }
    }

    /// <summary>The first occupied slot's key, if any.</summary>
    public uint? First()
    {
        for (var i = 0; i < _flags.Length; i++)
        {
            if ((int)_flags[i] >= 0)
                return _keys[i];
        }
        return null;
    }
}
