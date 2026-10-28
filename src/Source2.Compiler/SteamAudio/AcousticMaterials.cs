using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The scene's material table, one entry per game surface property in the surface property system's order
/// (surfaceproperties.vsurf_c's SurfacePropertiesList), built as steamaudio 1023 builds it:
/// <list type="bullet">
/// <item>CSurfacePropDataTable&lt;CSteamAudioMaterial&gt;::Parse (180128710): every entry starts as the default
/// (180084a40: absorption 0.3, scattering 0.5, transmission 0 in each of 11 bands); in surface order an entry
/// copies its base's entry (180084910), then, when scripts/surfaceproperties_steamaudio.txt names it, takes that
/// entry's coefficients (180084720: missing arrays keep what they had);</item>
/// <item>the scene build (180084d90) reduces each to Steam Audio's three bands: absorption and transmission
/// take bands 0, 7 and 9 (the indices steamaudio sets at 180063adc), scattering the 11 bands summed in order
/// from 0 (18007d0b0) over 11.</item>
/// </list>
/// </summary>
public static class AcousticMaterials
{
    private static readonly int[] BandIndices = [0, 7, 9];

    private sealed class Entry
    {
        public float[] Absorption = Enumerable.Repeat(0.3f, 11).ToArray();
        public float[] Scattering = Enumerable.Repeat(0.5f, 11).ToArray();
        public float[] Transmission = new float[11];

        public Entry Copy() => new() { Absorption = (float[])Absorption.Clone(), Scattering = (float[])Scattering.Clone(), Transmission = (float[])Transmission.Clone() };
    }

    /// <summary>The table, and each surface property's name (index = the material index a triangle carries).</summary>
    public static (AcousticMaterial[] Materials, string[] Names) Build(KVObject vsurf, string steamAudioTxt)
    {
        var list = vsurf.GetArray("SurfacePropertiesList").ToList();
        var names = list.Select(e => e.GetStringProperty("surfacePropertyName")).ToArray();
        var index = list.Select((e, i) => ((uint)e.GetUInt32Property("m_nameHash"), i)).ToDictionary(x => x.Item1, x => x.i);
        var txt = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(steamAudioTxt)));
        var byName = new Dictionary<int, KVObject>();
        foreach (var e in txt.Root.GetArray("SurfacePropertiesList"))
        {
            var name = e.ContainsKey("surfacePropertyName") ? (string)e["surfacePropertyName"] : "";
            // The property is found by its token (MurmurHash2 of the lowercased name, seed 0x31415926, 180094f40).
            var i = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (name.Length > 0 && i >= 0)
                byName[i] = e;
        }
        // Every entry is a default first (the table's resize); a base later in the list is still one when copied.
        var entries = Enumerable.Range(0, list.Count).Select(_ => new Entry()).ToArray();
        for (var i = 0; i < list.Count; i++)
        {
            var entry = entries[i];
            var baseHash = (uint)list[i].GetUInt32Property("m_baseNameHash");
            if (index.TryGetValue(baseHash, out var b) && b != i)
                entry = entries[b].Copy();
            if (byName.TryGetValue(i, out var t))
            {
                Read(t, "absorptionCoefficients", entry.Absorption);
                Read(t, "scatteringCoefficients", entry.Scattering);
                Read(t, "transmissionCoefficients", entry.Transmission);
            }
            entries[i] = entry;
        }
        var materials = new AcousticMaterial[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var sum = 0.0f;
            foreach (var s in e.Scattering)
                sum += s;
            materials[i] = new AcousticMaterial(e.Absorption[BandIndices[0]], e.Absorption[BandIndices[1]], e.Absorption[BandIndices[2]],
                                                sum / 11.0f, e.Transmission[BandIndices[0]], e.Transmission[BandIndices[1]], e.Transmission[BandIndices[2]]);
        }
        return (materials, names);
    }

    // 1801deab0: a float array member; a missing one leaves the values as they are.
    private static void Read(KVObject entry, string key, float[] into)
    {
        if (!entry.ContainsKey(key))
            return;
        var values = entry.GetArray(key).ToList();
        if (values.Count != into.Length)
            throw new NotSupportedException($"surfaceproperties_steamaudio.txt: {key} with {values.Count} values (11 expected) is not ported");
        for (var i = 0; i < into.Length; i++)
            into[i] = (float)(double)values[i];
    }
}
