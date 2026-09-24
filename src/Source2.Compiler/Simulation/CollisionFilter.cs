namespace Source2.Compiler.Simulation;

/// <summary>Whether two shapes may make a contact: the attribute filter and the body veto.</summary>
public static class CollisionFilter
{
    /// <summary>
    /// FUN_1802faf00: the pair's contact flags from the group table, or 0 for
    /// no contact. The layers must not exclude each other; unless either group
    /// is 0 the layers must meet (and the 0x10 flag refuses a layer bit 1);
    /// hierarchy ids and ids to ignore are checked; each function mask must
    /// hold the other's index; a group entry with bit 0 needs both shapes'
    /// flag 1. With flag 4 on both and different entity ids, bits 2-4 stay,
    /// plus 8 when either has flag 0x20; otherwise bits 2-4 are cleared.
    /// </summary>
    public static ushort Flags(ushort[] table, in CollisionAttributes a, in CollisionAttributes b)
    {
        if ((a.InteractsExclude & b.InteractsAs) != 0 || (b.InteractsExclude & a.InteractsAs) != 0)
            return 0;
        if (a.Group != 0 && b.Group != 0)
        {
            if ((a.InteractsWith & 2) != 0 && (b.Flags & 0x10) != 0)
                return 0;
            if ((b.InteractsWith & 2) != 0 && (a.Flags & 0x10) != 0)
                return 0;
            if (((b.InteractsAs & a.InteractsWith) | (a.InteractsAs & b.InteractsWith)) == 0)
                return 0;
        }
        if ((ushort)(a.HierarchyId - 1) <= 0xfffd)
        {
            if (a.HierarchyId == b.HierarchyId && (a.Flags & 0x40) == 0 && (b.Flags & 0x40) == 0
                && !((a.Flags & 0x48) != 0 && (b.Flags & 0x48) != 0 && (a.Flags & 1) != 0 && (b.Flags & 1) != 0))
                return 0;
            if ((ushort)(b.HierarchyId - 1) <= 0xfffd && a.HierarchyId != b.HierarchyId
                && ((a.Flags & 0x80) != 0 || (b.Flags & 0x80) != 0))
                return 0;
        }
        if (b.EntityId != -1 && b.EntityId == a.OwnerId)
            return 0;
        if (a.EntityId != -1 && a.EntityId == b.OwnerId)
            return 0;
        var maskA = a.MaskIsDirect == 1 ? a.FunctionMask : (ushort)~a.FunctionMask;
        if (((maskA >> (b.FunctionIndex & 31)) & 1) == 0)
            return 0;
        var maskB = b.MaskIsDirect == 1 ? b.FunctionMask : (ushort)~b.FunctionMask;
        if (((maskB >> (a.FunctionIndex & 31)) & 1) == 0)
            return 0;
        var entry = table[a.Group * 64 + b.Group];
        if ((entry & 1) != 0 && ((a.Flags & 1) == 0 || (b.Flags & 1) == 0))
            return 0;
        if ((a.Flags & 4) == 0 || (b.Flags & 4) == 0 || a.EntityId == b.EntityId)
            return (ushort)(entry & 0xffe3);
        if ((entry & 0x1c) == 0)
            return entry;
        if (((a.Flags | b.Flags) & 0x20) == 0)
            return entry;
        return (ushort)(entry | 8);
    }

    /// <summary>
    /// FUN_1801bdea0: shapes of one body never collide. Two bodies neither of
    /// them dynamic collide only as two moving non-static bodies, with a solid
    /// (1) or no bit 2/4 flag refused, and their layers (as/with, no exclude)
    /// meeting. Otherwise a joint that disables collision (bits 0 and 1 clear)
    /// or a no-collide link between the bodies refuses the pair. The joint
    /// list walked is the dynamic body's (of the two, the one with fewer
    /// shapes; the static one's partner when one is static).
    /// </summary>
    public static bool BodiesMayCollide(BroadphaseShape shapeA, BroadphaseShape shapeB, ushort flags)
    {
        var a = shapeA.Body;
        var b = shapeB.Body;
        if (a == b)
            return false;
        var ta = a.State.BodyType;
        var tb = b.State.BodyType;
        BroadphaseBody walk = a, other = b;
        if (ta == 2)
        {
            if (tb != 0 && b.Shapes.Count < a.Shapes.Count)
                (walk, other) = (b, a);
        }
        else if (tb != 2)
        {
            if ((flags & 1) != 0 || ta == 0 || tb == 0 || (flags & 0x14) == 0)
                return false;
            ref readonly var x = ref shapeA.Attributes;
            ref readonly var y = ref shapeB.Attributes;
            if ((y.InteractsWith & x.InteractsAs) == 0 && (x.InteractsWith & y.InteractsAs) == 0)
                return false;
            return (y.InteractsExclude & x.InteractsAs) == 0 && (x.InteractsExclude & y.InteractsAs) == 0;
        }
        else if (ta != 0)
        {
            if (b.Shapes.Count < a.Shapes.Count)
                (walk, other) = (b, a);
        }
        else
            (walk, other) = (b, a);

        var disabled = false;
        foreach (var (body, jointFlags) in walk.Joints)
            if (body == other && (jointFlags & 2) == 0 && (jointFlags & 1) == 0)
                disabled = true;
        if (disabled)
            return false;
        if (a.NoCollide.Contains(b) || b.NoCollide.Contains(a))
            return false;
        return true;
    }
}
