namespace Source2.Compiler.Simulation;

/// <summary>
/// vphysics2's collision group table (0x18045b328, 64 x 64 u16, a pair's
/// contact flags; 0 means the groups never meet), as its startup fills it
/// (FUN_18006e1d0): cleared with group 0 meeting everything
/// (FUN_1802d7990 -> FUN_1802fa8b0 / FUN_1802fa8e0), then the pair rules of
/// FUN_1802d79a0, whose stripes (FUN_1802d7cd0) cover groups 4 to 24 and
/// whose copies (FUN_1802d7780) take another group's row and column.
/// </summary>
public static class CollisionGroupTable
{
    public const int Groups = 64;

    /// <summary>The table after startup.</summary>
    public static ushort[] Build()
    {
        var t = new ushort[Groups * Groups];
        Reset(t, 0, 0x15);
        Stripe(t, 0, 0x15);
        Stripe(t, 8, 0x15);
        Stripe(t, 7, 0x15);
        Stripe(t, 6, 0x15);
        Stripe(t, 9, 0x15);
        Stripe(t, 10, 0x15);
        Stripe(t, 14, 0x15);
        Stripe(t, 16, 0x15);
        Stripe(t, 18, 0x15);
        Stripe(t, 20, 0x15);
        Stripe(t, 2, 0x14);
        t[(2 * 64) + 0] = 0x14;
        t[(0 * 64) + 2] = 0x14;
        Stripe(t, 5, 0x0);
        Stripe(t, 19, 0x0);
        t[(5 * 64) + 20] = 0x15;
        t[(20 * 64) + 5] = 0x15;
        Stripe(t, 3, 0x15);
        t[(3 * 64) + 2] = 0x14;
        t[(3 * 64) + 3] = 0x15;
        t[(2 * 64) + 3] = 0x14;
        t[(7 * 64) + 6] = 0x0;
        t[(6 * 64) + 6] = 0x0;
        t[(6 * 64) + 7] = 0x0;
        t[(16 * 64) + 16] = 0x0;
        t[(9 * 64) + 9] = 0x0;
        t[(6 * 64) + 8] = 0x0;
        t[(8 * 64) + 6] = 0x0;
        Stripe(t, 4, 0x15);
        t[(4 * 64) + 8] = 0x15;
        t[(8 * 64) + 4] = 0x15;
        t[(4 * 64) + 2] = 0x14;
        t[(2 * 64) + 4] = 0x14;
        Stripe(t, 23, 0x0);
        t[(8 * 64) + 18] = 0x0;
        t[(18 * 64) + 8] = 0x0;
        t[(8 * 64) + 20] = 0x0;
        t[(20 * 64) + 8] = 0x0;
        t[(8 * 64) + 23] = 0x15;
        t[(23 * 64) + 8] = 0x15;
        Stripe(t, 13, 0x0);
        t[(13 * 64) + 16] = 0x15;
        t[(16 * 64) + 13] = 0x15;
        t[(16 * 64) + 14] = 0x0;
        t[(13 * 64) + 9] = 0x14;
        t[(9 * 64) + 13] = 0x14;
        t[(14 * 64) + 10] = 0x14;
        t[(10 * 64) + 14] = 0x14;
        t[(14 * 64) + 13] = 0x14;
        t[(13 * 64) + 14] = 0x14;
        t[(14 * 64) + 8] = 0x14;
        t[(8 * 64) + 14] = 0x14;
        t[(14 * 64) + 16] = 0x0;
        Copy(t, 12, 8);
        Stripe(t, 17, 0x0);
        t[(17 * 64) + 20] = 0x15;
        t[(20 * 64) + 17] = 0x15;
        t[(12 * 64) + 17] = 0x15;
        t[(12 * 64) + 18] = 0x15;
        t[(17 * 64) + 12] = 0x15;
        t[(18 * 64) + 12] = 0x15;
        t[(12 * 64) + 20] = 0x15;
        t[(20 * 64) + 12] = 0x15;
        t[(12 * 64) + 6] = 0x15;
        t[(6 * 64) + 12] = 0x15;
        t[(12 * 64) + 14] = 0x14;
        t[(14 * 64) + 12] = 0x14;
        t[(12 * 64) + 23] = 0x0;
        t[(23 * 64) + 12] = 0x0;
        Copy(t, 21, 12);
        Copy(t, 22, 12);
        Copy(t, 11, 8);
        t[(18 * 64) + 11] = 0x15;
        t[(11 * 64) + 18] = 0x15;
        t[(22 * 64) + 22] = 0x0;
        t[(22 * 64) + 12] = 0x0;
        t[(12 * 64) + 22] = 0x0;
        Copy(t, 24, 4);
        t[(24 * 64) + 13] = 0x15;
        t[(13 * 64) + 24] = 0x15;
        Stripe(t, 1, 0x0);
        return t;
    }

    /// <summary>
    /// FUN_1802fa8e0: group g meets every group with <paramref name="flags"/>,
    /// except that it meets group 0 with 0x15 and group 2 not at all.
    /// </summary>
    private static void Reset(ushort[] t, int g, ushort flags)
    {
        for (var b = 1; b < Groups; b++)
        {
            if (b == 2)
                continue;
            t[(g * Groups) + b] = flags;
            t[(b * Groups) + g] = flags;
        }
        t[(g * Groups) + 0] = 0x15;
        t[g] = 0x15;
        t[(g * Groups) + 2] = 0;
        t[(2 * Groups) + g] = 0;
    }

    /// <summary>FUN_1802d7cd0: group g meets groups 4 to 24 with <paramref name="flags"/>, both ways.</summary>
    private static void Stripe(ushort[] t, int g, ushort flags)
    {
        for (var b = 4; b <= 24; b++)
        {
            t[(g * Groups) + b] = flags;
            t[(b * Groups) + g] = flags;
        }
    }

    /// <summary>FUN_1802d7780: group <paramref name="to"/> takes group <paramref name="from"/>'s row, and each value as its column too, in order.</summary>
    private static void Copy(ushort[] t, int to, int from)
    {
        for (var b = 0; b < Groups; b++)
        {
            var v = t[(from * Groups) + b];
            t[(to * Groups) + b] = v;
            t[(b * Groups) + to] = v;
        }
    }
}
