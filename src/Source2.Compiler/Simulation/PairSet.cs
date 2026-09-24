using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// The broadphase's set of shape pairs that already have a contact
/// (CBroadphase+0x2B8): Valve's CUtlHashtable of {shape A, shape B} with a
/// symmetric match, ported with its probing, chain bits, displacement, growth
/// and erase, so its slots come out as Valve's do.
///
/// <para>A slot is 0x18 bytes: a 32-bit word, then the two shape handles. The
/// word is 0x80000000 when empty; otherwise bits 0-29 are the hash, and bit 30
/// marks the last slot of its home bucket's chain. A lookup walks from the
/// home bucket, skipping slots of other homes, until the chain's last slot.</para>
/// </summary>
public sealed class PairSet
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Slot
    {
        public uint Word;
        public uint Pad;
        public ulong A;
        public ulong B;
    }

    private const uint Empty = 0x80000000;
    private const uint Last = 0x40000000;

    /// <summary>The slots; only the first <see cref="Buckets"/> are used.</summary>
    public Slot[] Slots = [];

    /// <summary>Live entries (+0x10).</summary>
    public int Count;

    /// <summary>Bucket count, a power of two (+0x14); 0 before the first insert.</summary>
    public int Buckets;

    /// <summary>The smallest table it grows to (+0x18, 32 in the broadphase ctor).</summary>
    public int MinimumSize = 32;

    /// <summary>
    /// FUN_1801c6540: a Murmur2-style mix of the two handles, the lower
    /// handle (as unsigned 64-bit) taken as the first half, so (a, b) and
    /// (b, a) hash alike.
    /// </summary>
    public static uint Hash(ulong a, ulong b)
    {
        var (lo, hi) = a < b ? (a, b) : (b, a);
        var h1 = Mix(hi);
        var h2 = Mix(lo);
        h2 ^= h2 >> 15;
        return (h2 * 0x40 + 0x9e3779b9 + (h1 ^ (h1 >> 15)) + (h2 >> 2)) ^ h2;
    }

    /// <summary>((M(hi32) ^ (M(lo32) * m ^ 0x59b8c22c) * m)) then (x >> 13 ^ x) * m, with M(k) = (k*m >> 24 ^ k*m) * m.</summary>
    private static uint Mix(ulong v)
    {
        const uint m = 0x5bd1e995;
        var lo = (uint)v * m;
        var hi = (uint)(v >> 32) * m;
        var x = ((hi >> 24) ^ hi) * m ^ (((lo >> 24) ^ lo) * m ^ 0x59b8c22c) * m;
        return ((x >> 13) ^ x) * m;
    }

    private uint Home(uint word) => (word & (uint)(Buckets - 1)) | (uint)((int)word >> 31);

    /// <summary>FUN_1801fa710 / the search in FUN_1801db2e0: the slot holding (a, b) in either order, or -1.</summary>
    public int Find(ulong a, ulong b) => Find(a, b, Hash(a, b));

    private int Find(ulong a, ulong b, uint hash)
    {
        if (Count == 0)
            return -1;
        var mask = (uint)(Buckets - 1);
        var home = hash & mask;
        if (Home(Slots[home].Word) != home)
            return -1;
        for (var i = home; ; i = (i + 1) & mask)
        {
            ref var s = ref Slots[i];
            if (Home(s.Word) != home)
                continue;
            if (((s.Word ^ hash) & 0x3fffffff) == 0 && ((s.A == a && s.B == b) || (s.A == b && s.B == a)))
                return (int)i;
            if ((s.Word & Last) != 0)
                return -1;
        }
    }

    public bool Contains(ulong a, ulong b) => Find(a, b) != -1;

    /// <summary>FUN_1801db2e0: inserts (a, b) unless present; returns whether it was inserted.</summary>
    public bool Insert(ulong a, ulong b)
    {
        var hash = Hash(a, b);
        if (Find(a, b, hash) != -1)
            return false;
        var need = (uint)(Count * 4 + 4);
        if ((uint)(Buckets * 3) < need)
            Grow((int)need / 3);
        var slot = Place(hash);
        Slots[slot].A = a;
        Slots[slot].B = b;
        return true;
    }

    /// <summary>FUN_1801a4a40 without the growth check: claims the home bucket, moving whatever sits there.</summary>
    private uint Place(uint hash)
    {
        Count++;
        var mask = (uint)(Buckets - 1);
        var value = (hash & 0x3fffffff) | Last;
        var slot = hash & mask;
        var word = Slots[slot].Word;
        if (Home(word) == slot)
        {
            Displace(slot);
            value = hash & 0x3fffffff;
        }
        else if ((int)word >= 0)
            Displace(slot);
        Slots[slot].Word = value;
        return slot;
    }

    /// <summary>
    /// FUN_18006b980: moves the entry in <paramref name="p"/> to the first
    /// empty slot after its chain's members, keeping the chain's last-bit on
    /// its last member.
    /// </summary>
    private void Displace(uint p)
    {
        var mask = (uint)(Buckets - 1);
        var moved = Slots[p].Word & 0x7fffffff;
        var home = moved & mask;
        var j = home;
        for (;;)
        {
            var w = Slots[j].Word;
            if (Home(w) == home)
            {
                if ((w & Last) != 0)
                {
                    moved |= Last;
                    Slots[j].Word = w & ~Last;
                }
            }
            else if ((int)w < 0)
                break;
            j = (j + 1) & mask;
        }
        if ((Slots[p].Word & Last) != 0)
        {
            var q = p;
            for (;;)
            {
                q = (mask + q) & mask;
                if (q == j)
                    break;
                var w = Slots[q].Word;
                if (Home(w) == home)
                {
                    Slots[q].Word = w | Last;
                    moved &= ~Last;
                    break;
                }
            }
        }
        Slots[j].Word = moved;
        Slots[j].A = Slots[p].A;
        Slots[j].B = Slots[p].B;
        Slots[p].Word = Empty;
    }

    /// <summary>
    /// FUN_1801a4e90: a table of the next power of two at or above
    /// max(<paramref name="request"/>, <see cref="MinimumSize"/>), refilled
    /// from the old table's last slot to its first.
    /// </summary>
    private void Grow(int request)
    {
        var old = Slots;
        var oldBuckets = Buckets;
        if (request < MinimumSize)
            request = MinimumSize;
        var size = (uint)request - 1;
        size |= size >> 1;
        size |= size >> 2;
        size |= size >> 4;
        size |= size >> 8;
        size = (size >> 16 | size) + 1;
        Slots = new Slot[size];
        for (var i = 0; i < Slots.Length; i++)
            Slots[i].Word = Empty;
        Buckets = (int)size;
        var live = Count;
        Count = 0;
        for (var i = oldBuckets - 1; i >= 0 && live > 0; i--)
        {
            if ((int)old[i].Word < 0)
                continue;
            var slot = Place(old[i].Word);
            Slots[slot].A = old[i].A;
            Slots[slot].B = old[i].B;
            live--;
        }
    }

    /// <summary>FUN_1801db8e0: removes (a, b); returns whether it was there.</summary>
    public bool Erase(ulong a, ulong b)
    {
        if (Count == 0)
            return false;
        var hash = Hash(a, b);
        var mask = (uint)(Buckets - 1);
        var home = hash & mask;
        if (Home(Slots[home].Word) != home)
            return false;
        var previous = -1;
        for (var i = home; ; i = (i + 1) & mask)
        {
            ref var s = ref Slots[i];
            if (Home(s.Word) != home)
                continue;
            if (((s.Word ^ hash) & 0x3fffffff) == 0 && ((s.A == a && s.B == b) || (s.A == b && s.B == a)))
            {
                var word = s.Word;
                var atHome = i == Home(word);
                s.Word = Empty;
                Count--;
                var code = (atHome ? 1u : 0u) | (word & Last);
                if (code == Last)
                    Slots[previous].Word |= Last;
                else if (code == 1)
                {
                    // The home slot emptied mid-chain: the chain's next member moves in.
                    var j = i;
                    do
                        j = (j + 1) & mask;
                    while (Home(Slots[j].Word) != home);
                    s.Word = Slots[j].Word;
                    s.A = Slots[j].A;
                    s.B = Slots[j].B;
                    Slots[j].Word = Empty;
                }
                return true;
            }
            previous = (int)i;
            if ((s.Word & Last) != 0)
                return false;
        }
    }
}
