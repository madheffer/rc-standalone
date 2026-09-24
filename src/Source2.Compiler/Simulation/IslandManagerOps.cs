namespace Source2.Compiler.Simulation;

/// <summary>
/// Rubikon's island manager (RnWorld +0x118, functions 0x1802ca000..0x1802cf000):
/// islands are persistent, grown when a contact starts touching (merge into
/// the larger island, ties to B's), marked when one stops (a removed body
/// leaves a hole) and split by flood fill every tenth step. Every list is a
/// swap-remove array, so the order bodies and contacts end up in, which is
/// the order the island solve runs them in, depends on all of it.
/// </summary>
/// <remarks>
/// Joints and the other constraint lists are not ported (a settle has none),
/// and neither is the graph colouring of big islands (an island that would
/// need one throws).
/// </remarks>
public static class IslandManagerOps
{
    /// <summary>Removes item index <paramref name="index"/> by moving the last one there.</summary>
    private static void SwapRemove<T>(List<T> list, int index, Action<T, int> setIndex)
    {
        var last = list[^1];
        list[index] = last;
        list.RemoveAt(list.Count - 1);
        setIndex(last, index);
    }

    private static List<IslandNode> ListOf(IslandManager m, IslandNode n)
        => n.Kind == 0 ? m.Free : ((RnIsland)n).Coloured ? m.Coloured : m.Serial;

    /// <summary>Appends a node to its solve list if it is in none (FUN_1802ce1e0).</summary>
    public static void RegisterNode(IslandManager m, IslandNode n)
    {
        if (n.ListIndex != -1)
            return;
        var list = ListOf(m, n);
        n.ListIndex = list.Count;
        list.Add(n);
    }

    /// <summary>Takes a node out of its solve list (FUN_1802ce6a0).</summary>
    public static void Unlist(IslandManager m, IslandNode n)
    {
        if (n.ListIndex == -1)
            return;
        var index = n.ListIndex;
        SwapRemove(ListOf(m, n), index, (x, i) => x.ListIndex = i);
        n.ListIndex = -1;
    }

    /// <summary>Adds a node to the manager, and to its solve list when awake (FUN_1802ce480).</summary>
    public static void AddNode(IslandManager m, IslandNode n)
    {
        n.ManagerIndex = m.All.Count;
        m.All.Add(n);
        if (n.Awake && n.ListIndex == -1)
            RegisterNode(m, n);
    }

    /// <summary>Takes a node out of the manager entirely (FUN_1802cf0d0).</summary>
    public static void RemoveNode(IslandManager m, IslandNode n)
    {
        Unlist(m, n);
        var index = n.ManagerIndex;
        SwapRemove(m.All, index, (x, i) => x.ManagerIndex = i);
        n.ManagerIndex = -1;
        if (n is RnIsland island && island.SplitIndex != -1)
        {
            SwapRemove(m.SplitPending, island.SplitIndex, (x, i) => x.SplitIndex = i);
            island.SplitIndex = -1;
        }
    }

    /// <summary>
    /// Enters the body in an island's map (FUN_1802ce2d0), proposing the next
    /// solver index. True when the body is new to the island; the index is
    /// the body's solver index there either way.
    /// </summary>
    private static bool MapRegister(RnBody b, RnIsland island, int proposed, out int index)
    {
        if (b.MapState == 1 && b.MapIsland == island)
        {
            b.MapRefs++;
            index = b.MapIndex;
            return false;
        }
        if (b.MapState != 0)
        {
            if (b.MapState == 1)
            {
                b.MapHash ??= [];
                b.MapHash[b.MapIsland!] = (b.MapIndex, b.MapRefs);
                b.MapIsland = null;
                b.MapIndex = -1;
                b.MapRefs = 0;
                b.MapState = 2;
            }
            var (i, refs) = b.MapHash!.TryGetValue(island, out var e) ? e : (0, 0);
            refs++;
            if (refs != 1)
            {
                b.MapHash[island] = (i, refs);
                index = i;
                return false;
            }
            b.MapHash[island] = (proposed, refs);
            index = proposed;
            return true;
        }
        b.MapIsland = island;
        b.MapIndex = proposed;
        b.MapRefs++;
        b.MapState = 1;
        index = proposed;
        return true;
    }

    /// <summary>Drops one edge of the body in an island (FUN_1802ceef0); true when it left the island.</summary>
    private static bool MapRelease(RnBody b, RnIsland island)
    {
        if (b.MapState == 1)
        {
            if (--b.MapRefs != 0)
                return false;
            ResetMap(b);
            return true;
        }
        var (i, refs) = b.MapHash![island];
        if (--refs != 0)
        {
            b.MapHash[island] = (i, refs);
            return false;
        }
        b.MapHash.Remove(island);
        if (b.MapHash.Count == 0)
            ResetMap(b);
        return true;
    }

    private static void ResetMap(RnBody b)
    {
        b.MapIsland = null;
        b.MapIndex = -1;
        b.MapRefs = 0;
        b.MapState = 0;
    }

    /// <summary>
    /// Registers a contact's bodies in its island (FUN_1802cb890): a body new to
    /// the island is appended (its solver index is its position), the iteration
    /// maxima grow, and an awake body counts. The indices go to contact +0x6c/+0x70.
    /// </summary>
    private static void RegisterBodies(RnContact c, RnIsland island)
    {
        c.SolverA = Register(c.A.Body, island);
        c.SolverB = Register(c.B.Body, island);
    }

    private static int Register(RnBody b, RnIsland island)
    {
        if (MapRegister(b, island, island.Bodies.Count, out var index))
        {
            island.Bodies.Add(b);
            island.VelocityIterations = Math.Max(b.State.MinVelocityIterations, island.VelocityIterations);
            island.PositionIterations = Math.Max(b.State.MinPositionIterations, island.PositionIterations);
            if (b.ActiveIndex >= 0)
                island.AwakeCount++;
        }
        return index;
    }

    /// <summary>Appends a contact to an island (FUN_1802ca8e0).</summary>
    public static void AppendContact(RnIsland island, RnContact c)
    {
        var group = island.Contacts[c.Group];
        c.IslandIndex = group.Count;
        group.Add(c);
        c.Island = island;
        island.Sizes[c.Group] += c.Size88;
        RegisterBodies(c, island);
        island.EdgeCount++;
        if (island.Coloured)
            throw new NotSupportedException("graph-coloured islands (FUN_1802ca570) are not ported");
    }

    /// <summary>
    /// Empties an island's body list (FUN_1802cca70), resetting each body's
    /// map for this island, and returns the bodies in island order.
    /// </summary>
    private static List<RnBody> ResetBodies(RnIsland island)
    {
        var bodies = new List<RnBody>();
        foreach (var b in island.Bodies)
        {
            if (b == null)
                continue;
            if (b.MapState == 1)
                ResetMap(b);
            else if (b.MapHash != null && b.MapHash.Remove(island) && b.MapHash.Count == 0)
                ResetMap(b);
            bodies.Add(b);
        }
        island.Bodies.Clear();
        island.VelocityIterations = 0;
        island.PositionIterations = 0;
        island.AwakeCount = 0;
        return bodies;
    }

    /// <summary>Moves every contact of one island onto another, in order (FUN_1802cc790).</summary>
    private static void MoveContacts(RnIsland from, RnIsland to)
    {
        for (var g = 0; g < 2; g++)
        {
            foreach (var c in from.Contacts[g])
            {
                c.IslandIndex = -1;
                c.Island = null;
                AppendContact(to, c);
            }
            from.Contacts[g].Clear();
            from.Sizes[g] = 0;
        }
    }

    /// <summary>
    /// Brings a body into the target island (FUN_1802ce050): a body in no
    /// island leaves the manager (its own node goes), a body in another heap
    /// island brings that whole island along.
    /// </summary>
    private static void Absorb(IslandManager m, RnIsland target, RnBody b)
    {
        if (b.MapState == 0)
        {
            RemoveNode(m, b.Node);
            return;
        }
        if (b.Static || b.MapState != 1 || b.MapIsland == target || b.MapIsland!.Kind != 1)
            return;
        var island = b.MapIsland;
        ResetBodies(island);
        MoveContacts(island, target);
        island.EdgeCount = 0;
        RemoveNode(m, island);
    }

    /// <summary>The colouring threshold on an island's lists (FUN_1802cad70 at 0x1802cae0x).</summary>
    private static bool NeedsColouring(IslandManager m, RnIsland island)
    {
        var limit = m.Colouring ? 25 : int.MaxValue;
        return limit <= island.Contacts[1].Count / 4 || limit <= island.Contacts[0].Count / 4;
    }

    /// <summary>
    /// A contact became an island edge (FUN_1802cad70): the target is the
    /// larger of the two bodies' heap islands (B's on a tie) or a new one;
    /// both bodies are absorbed, the contact appended, and the island listed
    /// if it is awake.
    /// </summary>
    public static void ActivateEdge(IslandManager m, RnContact c)
    {
        c.InIsland = true;
        RnIsland? target = null;
        foreach (var b in new[] { c.A.Body, c.B.Body })
            if (!b.Static && b.MapState == 1 && (target == null || target.Bodies.Count <= b.MapIsland!.Bodies.Count))
                target = b.MapIsland;
        if (target == null)
        {
            target = new RnIsland();
            AddNode(m, target);
        }
        Absorb(m, target, c.A.Body);
        Absorb(m, target, c.B.Body);
        AppendContact(target, c);
        if (!target.Coloured && NeedsColouring(m, target))
            throw new NotSupportedException("an island big enough for graph colouring (FUN_180333110)");
        if (target.Awake && target.ListIndex == -1)
            RegisterNode(m, target);
    }

    /// <summary>
    /// Takes a contact out of its island (FUN_1802ccce0). A body left with no
    /// edge there becomes a hole in the body list, and if it is in no island
    /// any more its own node is returned to be put back.
    /// </summary>
    private static void RemoveContact(RnIsland island, RnContact c, List<IslandNode> returned)
    {
        var group = island.Contacts[c.Group];
        SwapRemove(group, c.IslandIndex, (x, i) => x.IslandIndex = i);
        c.IslandIndex = -1;
        c.Island = null;
        island.Sizes[c.Group] -= c.Size88;
        foreach (var (b, slot) in new[] { (c.A.Body, c.SolverA), (c.B.Body, c.SolverB) })
        {
            if (!MapRelease(b, island))
                continue;
            if (b.ActiveIndex >= 0)
                island.AwakeCount--;
            island.Bodies[slot] = null;
            if (b.MapState == 0)
                returned.Add(b.Node);
        }
        island.EdgeCount--;
    }

    /// <summary>
    /// A contact stopped being an island edge (FUN_1802cd2b0). An island left
    /// without edges is deleted; otherwise it waits for a split. Bodies that
    /// dropped out of every island get their own node back.
    /// </summary>
    public static void DeactivateEdge(IslandManager m, RnContact c)
    {
        var returned = new List<IslandNode>();
        if (c.Island is { } island)
        {
            RemoveContact(island, c, returned);
            if (island.EdgeCount == 0)
            {
                RemoveNode(m, island);
            }
            else
            {
                if (island.AwakeCount == 0)
                    Unlist(m, island);
                if (island.SplitIndex == -1)
                {
                    island.SplitIndex = m.SplitPending.Count;
                    m.SplitPending.Add(island);
                }
                island.Flags |= 1;
            }
        }
        foreach (var n in returned)
        {
            n.ManagerIndex = m.All.Count;
            m.All.Add(n);
            if (n.Awake && n.ListIndex == -1)
                RegisterNode(m, n);
        }
        c.InIsland = false;
    }

    /// <summary>
    /// Splits the pending islands (FUN_1802cee50), on steps that are multiples
    /// of 10, last pending first. True when it ran.
    /// </summary>
    public static bool SplitPending(IslandManager m, int step)
    {
        if (m.SplitPending.Count == 0 || !(step < 0 || step % 10 == 0))
            return false;
        while (m.SplitPending.Count > 0)
        {
            var island = m.SplitPending[^1];
            m.SplitPending.RemoveAt(m.SplitPending.Count - 1);
            island.SplitIndex = -1;
            Split(m, island);
        }
        return true;
    }

    /// <summary>
    /// Rebuilds an island by flood fill (FUN_1802ce7b0). The old island is
    /// emptied and reused for the first part; each part grows from the next
    /// unassigned body in the old order, depth first through the bodies'
    /// touching contacts in shape list order.
    /// </summary>
    private static void Split(IslandManager m, RnIsland island)
    {
        if ((island.Flags & 2) != 0)
        {
            if ((island.Flags & 1) == 0)
            {
                island.VelocityIterations = 0;
                island.PositionIterations = 0;
                foreach (var b in island.Bodies)
                {
                    if (b == null)
                        continue;
                    island.VelocityIterations = Math.Max(b.State.MinVelocityIterations, island.VelocityIterations);
                    island.PositionIterations = Math.Max(b.State.MinPositionIterations, island.PositionIterations);
                }
            }
            island.Flags &= ~2;
        }
        if ((island.Flags & 1) == 0)
            return;

        var bodies = ResetBodies(island);
        for (var g = 0; g < 2; g++)
        {
            foreach (var c in island.Contacts[g])
            {
                c.Island = null;
                c.IslandIndex = -1;
            }
            island.Contacts[g].Clear();
            island.Sizes[g] = 0;
        }
        island.EdgeCount = 0;
        island.Coloured = false;
        Unlist(m, island);
        SwapRemove(m.All, island.ManagerIndex, (x, i) => x.ManagerIndex = i);
        island.ManagerIndex = -1;

        RnIsland? reuse = island;
        var stack = new List<RnBody>();
        foreach (var seed in bodies)
        {
            if (seed.MapState != 0 || seed.Static)
                continue;
            var current = reuse ?? new RnIsland();
            reuse = null;
            stack.Add(seed);
            while (stack.Count > 0)
            {
                var body = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                foreach (var shape in body.Shapes)
                {
                    for (var e = shape.Heads[0]; e is { } edge; e = edge.Contact.Next[edge.Half])
                    {
                        var c = edge.Contact;
                        if (!c.InIsland || c.Island != null)
                            continue;
                        foreach (var other in new[] { c.A.Body, c.B.Body })
                            if (other.MapState == 0 && !other.Static && other != seed)
                                stack.Add(other);
                        AppendContact(current, c);
                    }
                }
            }
            if (NeedsColouring(m, current))
                throw new NotSupportedException("an island big enough for graph colouring (FUN_180333110)");
            AddNode(m, current);
        }
        island.Flags &= ~1;
    }

    /// <summary>A body in islands woke (FUN_1801dc0a0): each of its islands counts it and is listed.</summary>
    public static void BodyWoke(IslandManager m, RnBody b)
    {
        if (b.MapState == 1)
        {
            RegisterNode(m, b.MapIsland!);
            b.MapIsland!.AwakeCount++;
        }
        else if (b.MapState == 2)
        {
            throw new NotSupportedException("waking a body in several islands (a static body cannot wake)");
        }
    }

    /// <summary>A body in islands fell asleep (FUN_1801dc1e0): an island with no awake body leaves its list.</summary>
    public static void BodySlept(IslandManager m, RnBody b)
    {
        if (b.MapState == 1)
        {
            if (--b.MapIsland!.AwakeCount == 0)
                Unlist(m, b.MapIsland);
        }
        else if (b.MapState == 2)
        {
            throw new NotSupportedException("a body in several islands fell asleep (a static body cannot sleep)");
        }
    }
}
