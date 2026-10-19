using Source2.Compiler.Io;

namespace Source2.Compiler.Maps;

/// <summary>
/// The .vmap_c's m_SubassetReferences: the (type, name) pairs the map refers to, with counts.
/// resourcecompiler collects them in a hash table (the collector's +0x30, entries of 0x20 bytes:
/// flags and hash, type, name, count; 180fc0ac0), copies it into a fresh one (180fbf9f0) and
/// reports the copy slot by slot (1800fee50), and the RED2 table keeps that order: each type a
/// sub-table in the order its first name appears. The .vmap's own map_subasset_reference_*
/// attributes are the same pairs sorted (180f71a50), not this.
/// </summary>
public static class MapSubassets
{
    private const uint Free = 0x80000000, Last = 0x40000000, HashMask = 0x3fffffff;

    /// <summary>
    /// The pairs in the RED2's order, as measured on all 24 of s2c_rc_probe's compiles
    /// (MapSubassetsTests): the walk is the main lump's (instance copies spliced in where their
    /// parent's subtree ends, the templates left out; EntityLumpSet.MainLump), except that a
    /// path's node entities come before the path. Each visible entity whose class the FGD knows
    /// (worldspawn aside) adds ("entity", class), then its soundevent-typed keys (FGD type
    /// sound, 0xd) in key-table order, class defaults included, each ("soundevent", value).
    /// </summary>
    public static List<(string Type, string Name, int Count)> References(IReadOnlyList<MapEntities.Entity> entities, FgdSchema schema,
                                                                       DmxBinary.Document document)
    {
        var (copies, templates) = MapInstances.Expand(document, entities, 0, prefabs: true);
        var pending = copies.GroupBy(c => c.EmitAt).ToDictionary(g => g.Key, g => g.ToList());
        var lump = new List<MapEntities.Entity>();
        for (var i = 0; i <= entities.Count; i++)
        {
            if (pending.TryGetValue(i, out var here))
                lump.AddRange(here.Select(c => entities[c.Template] with { Instanced = true }));
            if (i < entities.Count && !templates.Contains(i))
                lump.Add(entities[i]);
        }
        var walk = new List<MapEntities.Entity>(lump.Count);
        for (var i = 0; i < lump.Count; i++)
        {
            if (lump[i].PathNodes is null)
            {
                walk.Add(lump[i]);
                continue;
            }
            var j = i + 1;
            while (j < lump.Count && lump[j].PathNodes is null && lump[j].ClassName.StartsWith("path_node", StringComparison.OrdinalIgnoreCase))
                j++;
            walk.AddRange(lump.Skip(i + 1).Take(j - i - 1));
            walk.Add(lump[i]);
            i = j - 1;
        }
        var table = new Table();
        foreach (var e in walk)
        {
            if (e.IsWorld || e.Hidden || e.ClassName.EqualsAscii("worldspawn") || !schema.HasClass(e.ClassName))
                continue;
            table.Add("entity", e.ClassName);
            foreach (var (key, value) in EntityLumpAuthor.KeyTable(e, schema))
                if (value.Length > 0 && schema.KeyOf(e.ClassName, key) is { TypeId: 0xd })
                    table.Add("soundevent", value);
        }
        // Each type is a sub-table, entity's first (measured, whatever the first slot holds).
        return [.. table.Copy().InSlotOrder().OrderBy(r => r.Type, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 180fbfc90: MurmurHash2 (seed 0x3501a674, the usual finish) of the type and of the name,
    /// each folded with h ^ h &gt;&gt; 15, xored.
    /// </summary>
    public static uint Hash(string type, string name)
    {
        static uint Murmur(string s)
        {
            const uint m = 0x5bd1e995;
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            var h = (uint)b.Length ^ 0x3501a674u;
            var i = 0;
            for (; b.Length - i >= 4; i += 4)
            {
                var k = BitConverter.ToUInt32(b, i) * m;
                k = (k >> 24 ^ k) * m;
                h = h * m ^ k;
            }
            switch (b.Length - i)
            {
                case 3:
                    h ^= (uint)b[i + 2] << 16;
                    goto case 2;
                case 2:
                    h ^= (uint)b[i + 1] << 8;
                    goto case 1;
                case 1:
                    h = (b[i] ^ h) * m;
                    break;
            }
            return (h >> 13 ^ h) * m;
        }
        var (a, c) = (Murmur(type), Murmur(name));
        return c >> 15 ^ a >> 15 ^ a ^ c;
    }

    /// <summary>
    /// tier1's CUtlHashtable as these functions use it: open addressing over a power-of-two
    /// table (at least 32), a new key at the head of its bucket's run, FLAG_LAST on a run's last
    /// entry, growth when 3 slots per 4 used would be passed.
    /// </summary>
    internal sealed class Table
    {
        private (uint Flags, string Type, string Name, int Count)[] _slots = [];
        private int _used;

        public void Add(string type, string name)
        {
            var h = Hash(type, name);
            if (_slots.Length > 0)
            {
                var mask = (uint)_slots.Length - 1;
                var home = h & mask;
                if (Home(_slots[home].Flags, mask) == home)
                    for (var j = home; ; j = (j + 1) & mask)
                    {
                        var e = _slots[j];
                        if (Home(e.Flags, mask) != home)
                            continue;
                        if (((e.Flags ^ h) & HashMask) == 0 && e.Type.EqualsAscii(type) && e.Name.EqualsAscii(name))
                        {
                            _slots[j].Count++;
                            return;
                        }
                        if ((e.Flags & Last) != 0)
                            break;
                    }
            }
            if (_slots.Length * 3 < _used * 4 + 4)
                Resize((_used * 4 + 4) / 3);
            Place(h, type, name, 1);
        }

        /// <summary>180fbf9f0: a fresh table sized for the entries, filled from the last slot back.</summary>
        public Table Copy()
        {
            var t = new Table();
            if (_used > 0)
                t.Resize(_used * 4 / 3);
            for (var i = _slots.Length - 1; i >= 0; i--)
                if ((_slots[i].Flags & Free) == 0)
                    t.Place(_slots[i].Flags, _slots[i].Type, _slots[i].Name, _slots[i].Count);
            return t;
        }

        public List<(string Type, string Name, int Count)> InSlotOrder()
            => [.. _slots.Where(s => (s.Flags & Free) == 0).Select(s => (s.Type, s.Name, s.Count))];

        private static uint Home(uint flags, uint mask) => (flags & Free) != 0 ? uint.MaxValue : flags & mask;

        // 180fbff60 / the inline insert: at the hash's slot, moving what is there (180fbfe10).
        private void Place(uint hash, string type, string name, int count)
        {
            _used++;
            var mask = (uint)_slots.Length - 1;
            var idx = hash & mask;
            var plain = hash & HashMask;
            uint flags;
            var e = _slots[idx].Flags;
            if (Home(e, mask) == idx)
            {
                Shift(idx);
                flags = plain;
            }
            else
            {
                if ((e & Free) == 0)
                    Shift(idx);
                flags = plain | Last;
            }
            _slots[idx] = (flags, type, name, count);
        }

        // 180fbfe10: the entry at p moves to the first free slot after its run, taking FLAG_LAST.
        private void Shift(uint p)
        {
            var mask = (uint)_slots.Length - 1;
            var src = _slots[p];
            var f = src.Flags & 0x7fffffff;
            var home = f & mask;
            var j = home;
            while (true)
            {
                var e = _slots[j].Flags;
                if (Home(e, mask) == home)
                {
                    if ((e & Last) != 0)
                    {
                        f |= Last;
                        _slots[j].Flags = e & ~Last;
                    }
                    j = (j + 1) & mask;
                    continue;
                }
                if ((e & Free) != 0)
                    break;
                j = (j + 1) & mask;
            }
            if ((_slots[p].Flags & Last) != 0)
                for (var q = p; ;)
                {
                    q = (q + mask) & mask;
                    if (q == j)
                        break;
                    var e = _slots[q].Flags;
                    if (Home(e, mask) == home)
                    {
                        _slots[q].Flags = e | Last;
                        f &= ~Last;
                        break;
                    }
                }
            _slots[j] = (f, src.Type, src.Name, src.Count);
            if (j != p)
                _slots[p] = (Free, "", "", 0);
        }

        // 180fc0110: the next power of two of max(n, 32), the old entries re-placed from the last slot back.
        private void Resize(int n)
        {
            var size = 1;
            while (size < Math.Max(n, 0x20))
                size <<= 1;
            var old = _slots;
            _slots = new (uint, string, string, int)[size];
            for (var i = 0; i < size; i++)
                _slots[i] = (Free, "", "", 0);
            _used = 0;
            for (var i = old.Length - 1; i >= 0; i--)
                if ((old[i].Flags & Free) == 0)
                    Place(old[i].Flags, old[i].Type, old[i].Name, old[i].Count);
        }
    }
}
