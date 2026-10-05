using Source2.Compiler.Io;
using System.Globalization;
using System.Numerics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>What the settle leaves on one prop's node.</summary>
    /// <param name="Moved">Whether it was simulated, so its pose was written back.</param>
    /// <param name="Origin">The resting origin (the node's SetOrigin).</param>
    /// <param name="Angles">The resting angles (the node's SetAngles).</param>
    /// <param name="Asleep">Whether every body of the node is asleep at the
    /// end, a body left static counting as asleep, which sets its "Start
    /// asleep" spawnflag.</param>
    public sealed record Settlement(bool Moved, Vector3 Origin, Vector3 Angles, bool Asleep);

    /// <summary>
    /// The compile's physics settle (FUN_180f1e490), end to end: the settle
    /// world built from the map (<see cref="Build"/>, <see cref="Settled"/>,
    /// <see cref="CreateWorld"/>), 30 s at 1/90 s (PhysDoc_Simulate: 2700
    /// steps), then each simulated prop's pose written back
    /// (PhysObj_WriteBackTransform, <see cref="SettleWriteBack.EntityPose"/>).
    /// Keyed by the node id the body came from (as text), instance copies at
    /// their baked ids, a prefab map's nodes by id path ("108:3").
    /// </summary>
    /// <remarks>
    /// The bodies are made dynamic in build order; Valve's order is a hash
    /// map's over node pointers. Only the awake indices at the start differ
    /// with it, and every settled pose on atixref, c2m2 and Mako is the same.
    /// </remarks>
    public static Dictionary<string, Settlement> Run(DmxBinary.Document document, IModels models, FgdSchema schema, int createdOnLoad = 0,
                                                     Func<DmxBinary.Document, int>? createdOnLoadIn = null)
    {
        // A body the port cannot build refuses the settle only when there is
        // something to settle; it is judged eligible like any other body.
        var bodies = Build(document, models, schema, createdOnLoad, createdOnLoadIn);
        var result = new Dictionary<string, Settlement>();
        static string Key(BodyBuild b) => b.IdPath ?? b.NodeId.ToString(CultureInfo.InvariantCulture);
        if (Eligible(document, bodies, models, schema).Count == 0)
            return result;
        if (bodies.FirstOrDefault(b => b.Unsupported != null) is { } refused)
            throw new NotSupportedException($"node {refused.NodeId}: {refused.Unsupported}");
        var eligible = Eligible(document, bodies, models, schema);
        var settled = Settled(document, bodies, models, schema);
        var world = CreateWorld(bodies, settled);
        for (var step = 0; step < SettleSteps; step++)
            world.Step(1f / 90, step == 0);
        // FUN_180f1e490 after PhysDoc_StopSimulating: a node whose every
        // object PhysObj_IsAsleep calls asleep gets CMapEntity_SetStartAsleep.
        // Mako's two wood pallets, whose triangle meshes keep them static,
        // ship with it, so a static body answers asleep.
        foreach (var i in eligible.Except(settled))
        {
            var id = Key(bodies[i]);
            result[id] = new Settlement(false, default, default, result.GetValueOrDefault(id)?.Asleep ?? true);
        }
        foreach (var i in settled)
        {
            var node = bodies[i].Node!;
            var model = node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("model") ?? "";
            if (models.Physics(model) is { } phys && phys.BindPose.Length > 0)
                throw new NotSupportedException($"settling {model}, whose physics has a bind pose");
            ref var state = ref world.Bodies[i + 1].State;
            var pose = SettleWriteBack.EntityPose(state);
            // PhysObj_IsAsleep asks each body (vfn 0x408); the solver marks a
            // body it puts to sleep in Flags249 bit 4 (IslandSolver.PutToSleep).
            var asleep = (state.Flags249 & 4) != 0;
            var others = result.GetValueOrDefault(Key(bodies[i]))?.Asleep ?? true;
            if (bodies.Count(b => Key(b) == Key(bodies[i])) > 1)
                throw new NotSupportedException($"settling node {Key(bodies[i])} ({model}), which has several bodies");
            result[Key(bodies[i])] = new Settlement(true,
                new Vector3(pose.Origin.X, pose.Origin.Y, pose.Origin.Z),
                new Vector3(pose.Angles.X, pose.Angles.Y, pose.Angles.Z), asleep && others);
        }
        return result;
    }

    /// <summary>PhysDoc_Simulate(30 s, 1/90 s): (int)(30f / 0.0111111114f).</summary>
    private const int SettleSteps = 2700;

    /// <summary>
    /// An entity after the settle: a prop whose bodies all fell asleep goes
    /// through CMapEntity_SetStartAsleep(node, 1), which sets its class's
    /// "Start asleep" spawnflag, written back as "%d" (FUN_180f38bf0; nothing
    /// changes when the bit is already set or the class has no such flag),
    /// and "phys_start_asleep" "1" when the class declares that key. The
    /// node's table already holds every class key (SetClass), so a key the
    /// source lacks changes in its default's place, from the default's
    /// value. An awake prop keeps its keys.
    /// </summary>
    public static MapEntities.Entity SettledKeys(MapEntities.Entity entity, FgdSchema schema, bool asleep)
    {
        if (!asleep)
            return entity;
        var className = entity.ClassName;
        var list = entity.Keys.ToList();
        var defaults = entity.Defaults is { } had
            ? new Dictionary<string, string>(had, Tier0Strings.IgnoreCase)
            : new Dictionary<string, string>(Tier0Strings.IgnoreCase);
        string? Get(string key)
        {
            var at = list.FindIndex(k => k.Key.EqualsAscii(key));
            return at >= 0 ? list[at].Value
                 : defaults.TryGetValue(key, out var set) ? set
                 : schema.KeyOf(className, key)?.Default;
        }
        void Set(string key, string value)
        {
            var at = list.FindIndex(k => k.Key.EqualsAscii(key));
            if (at >= 0)
                list[at] = new(list[at].Key, value);
            else if (schema.KeyOf(className, key) is { } declared)
                defaults[declared.Name] = value;
            else
                list.Add(new(key, value));
        }
        var bit = (schema.KeyOf(className, "spawnflags")?.Flags ?? [])
            .FirstOrDefault(f => f.Name.EqualsAscii("Start asleep")).Bit;
        if (bit != 0)
        {
            var old = Get("spawnflags");
            var flags = old is { Length: > 0 } && int.TryParse(old, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var now = flags | (int)bit;
            if (now != flags)
                Set("spawnflags", now.ToString(CultureInfo.InvariantCulture));
        }
        if (schema.KeyOf(className, "phys_start_asleep") != null)
            Set("phys_start_asleep", "1");
        return entity with { Keys = list, Defaults = defaults.Count > 0 ? defaults : entity.Defaults };
    }
}
