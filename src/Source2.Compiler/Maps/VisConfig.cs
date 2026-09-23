using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// The per-map <c>.viscfg</c> the world stage writes beside the <c>.rte</c>: a
/// binary KV3 with worldspawn's <c>pvstype</c>, the sun direction and the map's
/// visibility hints. Only what the build reads is exposed.
/// </summary>
public sealed record VisConfig(int PvsType, System.Numerics.Vector3? DirToSun = null)
{
    /// <summary>
    /// <c>pvstype</c> 1 runs only the cluster-centre generator; any other value
    /// runs the full set (the flag <c>SampleVisForClusters</c> takes as
    /// <c>pvstype == 1</c>).
    /// </summary>
    public bool CentresOnly => PvsType == 1;

    public static VisConfig Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        using var owner = new ValveResourceFormat.Resource();
        var kv = new BinaryKV3 { Resource = owner };
        kv.Read(reader);
        var root = kv.Data.Root;
        System.Numerics.Vector3? sun = null;
        if (root.ContainsKey("vDirToSun"))
        {
            var v = root.GetFloatArray("vDirToSun");
            if (v.Length >= 3)
                sun = new System.Numerics.Vector3(v[0], v[1], v[2]);
        }
        return new VisConfig(root.ContainsKey("pvstype") ? root.GetInt32Property("pvstype") : 0, sun);
    }
}
