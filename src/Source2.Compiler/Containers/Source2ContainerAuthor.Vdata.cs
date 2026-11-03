namespace Source2.Compiler;

public static partial class Source2ContainerAuthor
{
    /// <summary>
    /// The "VData ModTools Stripping Version" fingerprint (RC 1.41.9.2 and since, 18015adb0):
    /// 18015bc50 hashes the root-member strip rules the game's modtools.dll returns from
    /// Source2ModTools001 slot 16 (18002e190 in modtools 20261023 and 20261029). CS2's list is one
    /// rule out of scripts/weapons.vdata: weapon_m14 in 1.41.9.2 (2278732723), weapon_lar since
    /// 1.41.9.3 (2026-10-29: 55841844, RC's own compile of the RcReference probes on 11-03).
    /// Ported from research/map-authoring (f35010e, bcb19b5), where patch_check follows it.
    /// </summary>
    public static uint VDataModToolsFingerprint => ModToolsStripFingerprint([("scripts/weapons.vdata", 1, "weapon_lar")]);

    /// <summary>18015bc50: per rule {file, int, member}, FNV-1a of each string folded by
    /// (h &lt;&lt; 17 ^ h) + (h &gt;&gt; 21), the int through murmur3's finaliser, all combined boost-style.</summary>
    internal static uint ModToolsStripFingerprint(IEnumerable<(string File, uint Value, string Member)> rules)
    {
        static uint Fold(string s)
        {
            var h = 0x811c9dc5u;
            foreach (var c in System.Text.Encoding.UTF8.GetBytes(s))
                h = (h ^ c) * 0x1000193u;
            return ((h << 17) ^ h) + (h >> 21);
        }

        var hash = 0u;
        foreach (var (file, value, member) in rules)
        {
            var f = Fold(file);
            var v = (value ^ (value >> 16)) * 0x85ebca6bu;
            v = (v ^ (v >> 13)) * 0xc2b2ae35u;
            v ^= v >> 16;
            var m = Fold(member) + (v << 6) + (v >> 2) + 0x9e3779b9u;
            m = (m ^ v) + 0x9e3779b9u + (f << 6);
            var d = (((f >> 2) + m) ^ f) + (hash << 6) + 0x9e3779b9u;
            hash ^= (hash >> 2) + d;
        }
        return hash;
    }
}
