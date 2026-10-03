using System.Globalization;
using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The cubemap and light probe volumes the late entity export hands the world
/// renderer builder (EntityLump_ExportNodeLate 180240a60: one 0x8c0-byte
/// record per env_cubemap, env_cubemap_box and light probe volume, cubemap
/// ones appended to builder +0x48, probe ones to +0x30), and the pick that
/// gives each node entry its cubemap (+0xa0) and light probe (+0xa4).
/// A record: the AngleMatrix of the entity's angles with its origin (+0),
/// influenceradius (+0x30, env_cubemap), box_mins and box_maxs (+0x34,
/// +0x40), whether it is a box (+0x4c: all but env_cubemap), the handshake
/// (+0x50), indoor_outdoor_level as the priority (+0x54), moveable (+0x58:
/// skipped by the pick) and the targetname (+0x60).
/// </summary>
public static class EnvVolumes
{
    public sealed record Volume(float[] Matrix, float Radius, bool Box, Vector3 Mins, Vector3 Maxs, int Handshake, int Priority,
                                bool Moveable, string Name);

    /// <summary>The two lists, each in export order.</summary>
    public sealed record Set(IReadOnlyList<Volume> Cubemaps, IReadOnlyList<Volume> Probes)
    {
        public static readonly Set Empty = new([], []);
    }

    private static readonly HashSet<string> Cubemap = new(StringComparer.OrdinalIgnoreCase)
    {
        "env_cubemap", "env_cubemap_box", "env_combined_light_probe_volume", "func_combined_light_probe_volume",
    };

    private static readonly HashSet<string> Probe = new(StringComparer.OrdinalIgnoreCase)
    {
        "env_light_probe_volume", "env_combined_light_probe_volume", "func_combined_light_probe_volume",
    };

    /// <summary>
    /// The record of one exported entity, or null for a class that leaves
    /// none. <paramref name="key"/> reads the entity's exported keys (null when
    /// absent: the export's defaults are 0, false and the zero vector).
    /// </summary>
    internal static (Volume Volume, bool Cubemap, bool Probe)? Of(string className, Vector3 origin, Vector3 angles, int handshake,
                                                               Func<string, string?> key)
    {
        var cubemap = Cubemap.Contains(className);
        var probe = Probe.Contains(className);
        if (!cubemap && !probe)
            return null;
        var sphere = className.Equals("env_cubemap", StringComparison.OrdinalIgnoreCase);
        var matrix = MapMeshes.AngleMatrix(angles);
        (matrix[3], matrix[7], matrix[11]) = (origin.X, origin.Y, origin.Z);
        Vector3 Vec(string name) => key(name) is { } v && CNumbers.FloatArray(v, 3) is var f ? new Vector3(f[0], f[1], f[2]) : Vector3.Zero;
        var volume = new Volume(matrix,
            sphere && key("influenceradius") is { } r ? CNumbers.ToFloat32(r) : 0f,
            !sphere,
            sphere ? Vector3.Zero : Vec("box_mins"),
            sphere ? Vector3.Zero : Vec("box_maxs"),
            handshake,
            key("indoor_outdoor_level") is { } level ? (int)CNumbers.ToFloat32(level) : 0,
            key("moveable") is { } m && (m == "1" || m.Equals("true", StringComparison.OrdinalIgnoreCase)),
            key("targetname") ?? "");
        return (volume, cubemap, probe);
    }

    /// <summary>
    /// FUN_180254d10: a named entry takes the first volume (not moveable) of
    /// that targetname. Otherwise each volume scores the point in its own
    /// frame: a sphere contains it when radius squared is over its squared
    /// distance (score that distance), else scores (distance - radius)
    /// squared; a box contains it when the box distance is 0 (score the
    /// squared distance to the origin), else scores the box distance. A
    /// containing volume beats one that does not; between containing ones
    /// the higher priority wins; otherwise the lower score, a tie going to the
    /// later. The handshake of the winner, 0 without one.
    /// </summary>
    internal static int Pick(Vector3 p, string name, IReadOnlyList<Volume> volumes)
    {
        if (name.Length > 0)
        {
            foreach (var v in volumes)
                if (!v.Moveable && string.Equals(v.Name, name, StringComparison.Ordinal))
                    return v.Handshake;
            // "Unable to find EnvMap/LPV with entity name == %s", then the spatial pick.
        }
        var best = -1;
        var bestInside = false;
        var bestPriority = int.MinValue;
        var bestScore = float.MaxValue;
        for (var i = 0; i < volumes.Count; i++)
        {
            var v = volumes[i];
            if (v.Moveable)
                continue;
            var m = v.Matrix;
            float dx = p.X - m[3], dy = p.Y - m[7], dz = p.Z - m[11];
            var local = new Vector3((dz * m[8]) + ((dy * m[4]) + (dx * m[0])),
                                    (dz * m[9]) + ((dy * m[5]) + (dx * m[1])),
                                    (dz * m[10]) + ((dy * m[6]) + (dx * m[2])));
            var score = ((local.X * local.X) + (local.Y * local.Y)) + (local.Z * local.Z);
            bool inside;
            if (!v.Box)
            {
                inside = v.Radius * v.Radius > score;
                if (!inside)
                {
                    var s = MathF.Sqrt(score) - v.Radius;
                    score = s * s;
                }
            }
            else
            {
                var box = BoxDistance(v.Mins, v.Maxs, local);
                inside = box == 0f;
                if (!inside)
                    score = box;
            }
            bool take;
            if (inside != bestInside)
                take = inside;
            else if (inside && v.Priority != bestPriority)
                take = v.Priority > bestPriority;
            else
                take = !(score > bestScore);
            if (take)
                (best, bestInside, bestPriority, bestScore) = (i, inside, v.Priority, score);
        }
        return best < 0 ? 0 : volumes[best].Handshake;
    }

    // FUN_181257020: per axis (mins - p)+ + (p - maxs)+, squared, summed z, y, x.
    private static float BoxDistance(Vector3 mins, Vector3 maxs, Vector3 p)
    {
        static float Pos(float x) => x > 0f ? x : 0f;
        var x = Pos(mins.X - p.X) + Pos(p.X - maxs.X);
        var y = Pos(p.Y - maxs.Y) + Pos(mins.Y - p.Y);
        var z = Pos(p.Z - maxs.Z) + Pos(mins.Z - p.Z);
        return ((z * z) + (y * y)) + (x * x);
    }

    /// <summary>
    /// FUN_180255040 (with Resourcecompiler/BakedLighting/UseStaticLightProbes,
    /// default on) for one entry: its point is the entry's origin unless that
    /// is FLT_MAX on all three axes, else the centre of its mesh's bounds; an
    /// entry with attribute bit 20 picks a light probe, with bit 21 a cubemap.
    /// </summary>
    internal static (int Cubemap, int Probe) ForEntry(Vector3 origin, string name, ulong attributes, Func<(Vector3 Min, Vector3 Max)> bounds, Set set)
    {
        var p = origin;
        if (origin.X == float.MaxValue && origin.Y == float.MaxValue && origin.Z == float.MaxValue)
        {
            var (min, max) = bounds();
            p = new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);
        }
        var probe = (attributes >> 20 & 1) != 0 ? Pick(p, name, set.Probes) : 0;
        var cubemap = (attributes >> 21 & 1) != 0 ? Pick(p, name, set.Cubemaps) : 0;
        return (cubemap, probe);
    }

    internal static string Format(Volume v) => string.Create(CultureInfo.InvariantCulture,
        $"{v.Handshake} box {v.Box} r {v.Radius} {v.Mins} {v.Maxs} prio {v.Priority} moveable {v.Moveable} '{v.Name}'");
}
