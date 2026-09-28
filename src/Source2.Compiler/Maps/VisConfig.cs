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

    /// <summary>
    /// The settings the world stage hands visibility, from the map itself:
    /// worldspawn's pvstype, and the sun (<see cref="SunOf"/>).
    /// </summary>
    public static VisConfig FromMap(IReadOnlyList<MapEntities.Entity> entities, FgdSchema schema)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(schema);
        var world = entities.FirstOrDefault(e => e.IsWorld);
        var pvs = world is null ? 10 : (int)CNumbers.Atoi(Key(world, "pvstype", schema) ?? "10");
        return new VisConfig(pvs, SunOf(entities, schema));
    }

    /// <summary>
    /// vDirToSun (EntityLump_ExportDriver, per entity in export order, the last
    /// one standing): an entity whose light description (Light_LegacyDescription)
    /// is directional, light_environment or light_directional, with castshadows
    /// set and a direct light mode of 2 or 3. The mode is directlight (2 when
    /// unset), and when baked_light_indexing is set and directlight is not 3, a
    /// mode 1 with indexing 1 becomes 3 and a mode 3 without it becomes 1. The
    /// direction is the light's forward axis, negated: the node's matrix under
    /// its parent's, back to angles (MatrixAngles), then AngleVectors.
    /// </summary>
    /// <remarks>WorldRenderer/DirectLightBaking below 1 forces the mode to 2;
    /// CS2's gameinfo does not set it, so it is 1. A light inside an instance
    /// takes its instance's matrix as the parent, which is not ported: such a
    /// light throws.</remarks>
    public static System.Numerics.Vector3? SunOf(IReadOnlyList<MapEntities.Entity> entities, FgdSchema schema)
    {
        System.Numerics.Vector3? sun = null;
        foreach (var e in entities)
        {
            if (!e.ClassName.Equals("light_environment", StringComparison.OrdinalIgnoreCase)
                && !e.ClassName.Equals("light_directional", StringComparison.OrdinalIgnoreCase))
                continue;
            int Int(string key, int fallback) => Key(e, key, schema) is { } text ? (int)CNumbers.Atoi(text) : fallback;
            var mode = (sbyte)Int("directlight", 2);
            if (Int("directlight", -1) != 3 && Int("baked_light_indexing", -1) is var indexing and not -1)
            {
                if (mode == 1 && indexing == 1)
                    mode = 3;
                else if (mode == 3 && indexing != 1)
                    mode = 1;
            }
            if ((sbyte)Int("castshadows", 0) == 0 || mode is not (2 or 3))
                continue;
            if (e.Instanced)
                throw new NotSupportedException($"{e.ClassName} {e.NodeId}: a sun inside an instance is not ported");
            var local = MapMeshes.AngleMatrix(e.Angles);
            local[3] = e.Origin.X;
            local[7] = e.Origin.Y;
            local[11] = e.Origin.Z;
            float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
            var angles = SettleWorld.MatrixAngles(MapMeshes.Concat(identity, local));
            const float Radians = 0.017453292f;
            float sp = MathF.Sin(angles.X * Radians), cp = MathF.Cos(angles.X * Radians);
            float sy = MathF.Sin(angles.Y * Radians), cy = MathF.Cos(angles.Y * Radians);
            sun = new System.Numerics.Vector3(-(cy * cp), -(sy * cp), -(-sp));
        }
        return sun;
    }

    /// <summary>An entity's key as the compile reads it: the map's value, else
    /// the FGD default the class fills in, else null.</summary>
    private static string? Key(MapEntities.Entity e, string key, FgdSchema schema)
        => e.Keys.FirstOrDefault(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value
           ?? schema.KeyOf(e.ClassName, key)?.Default;

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
