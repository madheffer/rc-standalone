namespace Source2.Compiler.Simulation;

/// <summary>
/// The lists the collide pass fills (world +0x220 +0x7c8) and the post-pass
/// that consumes them (FUN_1801dceb0): lost pairs are destroyed, pairs with
/// both bodies asleep leave the active contacts, contacts that began or
/// stopped touching move between their shapes' lists and join or leave
/// islands, and size estimate changes reach the islands.
/// </summary>
public sealed class CollideLists
{
    /// <summary>+0x00: the pair's proxies no longer overlap.</summary>
    public readonly List<RnContact> Lost = [];

    /// <summary>+0x38: both bodies asleep.</summary>
    public readonly List<RnContact> BothAsleep = [];

    /// <summary>+0x70: a manifold, and the contact was not touching.</summary>
    public readonly List<RnContact> Began = [];

    /// <summary>+0xa8: no manifold, and the contact was touching.</summary>
    public readonly List<RnContact> Ended = [];

    /// <summary>+0xe0: the size estimate changed.</summary>
    public readonly List<RnContact> Resized = [];
}

/// <summary>Contact and body bookkeeping of the step: waking, the touch lists, destroying, the collide post-pass.</summary>
public static class ContactLifecycle
{
    private static void SwapRemove<T>(List<T> list, int index, Action<T, int> setIndex)
    {
        var last = list[^1];
        list[index] = last;
        list.RemoveAt(list.Count - 1);
        setIndex(last, index);
    }

    /// <summary>Sorts a flushed contact list by key (FUN_1801f8d90 with the sort on: MSVC std::sort, FUN_1801e6a70).</summary>
    public static void SortByKey(List<RnContact> list)
    {
        if (list.Count > 1)
            MsvcSort.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new KeyLess());
    }

    /// <summary>Sorts a flushed body list by body index (FUN_1801f8c70, std::sort FUN_1801e6170 on +0x14).</summary>
    public static void SortByIndex(List<RnBody> list)
    {
        if (list.Count > 1)
            MsvcSort.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), new IndexLess());
    }

    private readonly struct KeyLess : ILess<RnContact>
    {
        public bool Less(in RnContact a, in RnContact b) => a.Key < b.Key;
    }

    private readonly struct IndexLess : ILess<RnBody>
    {
        public bool Less(in RnBody a, in RnBody b) => a.Index < b.Index;
    }

    /// <summary>Puts a contact back in the world's active contacts if it is out (FUN_1801ef0c0).</summary>
    public static void Reactivate(RnWorld w, RnContact c)
    {
        if (c.ActiveIndex >= 0)
            return;
        c.ActiveIndex = w.ActiveContacts.Count;
        w.ActiveContacts.Add(c);
    }

    /// <summary>
    /// A body joins the awake bodies (FUN_1801eefd0): appended to world +0xa00,
    /// and its island (or its own node) counts it.
    /// </summary>
    public static void AddAwake(RnWorld w, RnBody b)
    {
        ref var s = ref b.State;
        if ((s.Flags4A & 4) != 0 || b.ActiveIndex >= 0 || s.BodyType == 0 || (s.Flags249 & 1) == 0)
            return;
        b.ActiveIndex = w.ActiveBodies.Count;
        w.ActiveBodies.Add(b);
        if (b.MapState == 0)
            IslandManagerOps.RegisterNode(w.Islands, b.Node);
        else
            IslandManagerOps.BodyWoke(w.Islands, b);
    }

    /// <summary>A body leaves the awake bodies (the sleep half of FUN_1801ff820, as FUN_1801f7200).</summary>
    public static void RemoveAwake(RnWorld w, RnBody b)
    {
        var index = b.ActiveIndex;
        if (index < 0)
            return;
        SwapRemove(w.ActiveBodies, index, (x, i) => x.ActiveIndex = i);
        b.ActiveIndex = -1;
        if (b.MapState == 0)
            IslandManagerOps.Unlist(w.Islands, b.Node);
        else
            IslandManagerOps.BodySlept(w.Islands, b);
    }

    /// <summary>Every contact on the body's shapes, touching list then not touching, each in link order (FUN_1801c0ff0 walks +0x80 and +0x88).</summary>
    public static IEnumerable<RnContact> ContactsOf(RnBody b)
    {
        foreach (var shape in b.Shapes)
            for (var state = 0; state < 2; state++)
                for (var e = shape.Heads[state]; e is { } edge; e = edge.Contact.Next[edge.Half])
                    yield return edge.Contact;
    }

    /// <summary>
    /// Wakes a body (FUN_1801c0ff0): a sleeping one leaves sleep, joins the
    /// awake bodies and puts every contact of its shapes back in the active
    /// contacts; an awake one only restarts its sleep timer if asked.
    /// While world +0xbc8 is set (inside a callback) the wake is queued
    /// instead; the step never sets it. FUN_180203fc0 then tells the
    /// world's +0xb30 listeners, which a bare world has none of.
    /// </summary>
    public static void Wake(RnWorld w, RnBody b, bool resetTimer)
    {
        ref var s = ref b.State;
        if ((s.Flags4A & 4) != 0 || (s.Flags249 & 1) == 0 || s.BodyType == 0)
            return;
        if ((s.Flags249 & 4) != 0)
        {
            s.Flags249 &= unchecked((byte)~4);
            s.SleepTimer = 0f;
            AddAwake(w, b);
            foreach (var c in ContactsOf(b))
                Reactivate(w, c);
        }
        else if (resetTimer)
        {
            s.SleepTimer = 0f;
        }
        s.Flags4A &= unchecked((ushort)~0x80);
    }

    /// <summary>Unlinks both halves of a contact from their shapes' lists (the first half of FUN_180306780, and FUN_180203980).</summary>
    private static void Unlink(RnContact c)
    {
        for (var h = 0; h < 2; h++)
        {
            var shape = c.Shape(h);
            var prev = c.Prev[h];
            var next = c.Next[h];
            if (prev is { } p)
            {
                p.Contact.Next[p.Half] = next;
                c.Prev[h] = null;
            }
            else
            {
                shape.Heads[c.TouchState] = next;
            }
            if (next is { } n)
            {
                n.Contact.Prev[n.Half] = prev;
                c.Next[h] = null;
            }
        }
    }

    /// <summary>
    /// Moves a contact to its shapes' list for a touch state (FUN_180306780):
    /// both halves are unlinked from the old list and pushed on the front of the new.
    /// </summary>
    public static void SetTouchState(RnContact c, int state)
    {
        Unlink(c);
        c.TouchState = state;
        for (var h = 0; h < 2; h++)
        {
            var shape = c.Shape(h);
            if (shape.Heads[state] is { } head)
            {
                c.Next[h] = head;
                head.Contact.Prev[head.Half] = new EdgeRef(c, h);
            }
            shape.Heads[state] = new EdgeRef(c, h);
        }
    }

    /// <summary>Whether a contact should be an island edge: alive, solid and touching.</summary>
    private static bool WantsEdge(RnContact c) => c.AllIndex != -1 && (c.Flags78 & 1) != 0 && c.TouchState == 0;

    private static void UpdateEdge(RnWorld w, RnContact c)
    {
        var want = WantsEdge(c);
        if (want == c.InIsland)
            return;
        if (want)
            IslandManagerOps.ActivateEdge(w.Islands, c);
        else
            IslandManagerOps.DeactivateEdge(w.Islands, c);
    }

    /// <summary>
    /// When a touching contact goes, a dynamic body asleep on it wakes if the
    /// other body is awake (FUN_1801dceb0 at 0x1801dcf53).
    /// </summary>
    private static void WakeLeftBehind(RnWorld w, RnContact c)
    {
        if (!c.InIsland)
            return;
        var a = c.A.Body;
        var b = c.B.Body;
        var aAwake = a.ActiveIndex >= 0;
        var bAwake = b.ActiveIndex >= 0;
        if (!aAwake && a.State.BodyType == 2 && bAwake)
            Wake(w, a, false);
        else if (!bAwake && b.State.BodyType == 2 && aAwake)
            Wake(w, b, false);
    }

    /// <summary>
    /// Destroys a contact (FUN_1801f7450): off its shapes' lists and out of
    /// the broadphase's pair set (FUN_180203980), out of the
    /// active and all-contacts lists, onto the destroyed list, out of its island.
    /// </summary>
    public static void Destroy(RnWorld w, RnContact c)
    {
        Unlink(c);
        w.Broadphase?.Pairs.Erase(c.A.Proxy.Handle, c.B.Proxy.Handle);
        if (c.ActiveIndex >= 0)
        {
            SwapRemove(w.ActiveContacts, c.ActiveIndex, (x, i) => x.ActiveIndex = i);
            c.ActiveIndex = -1;
        }
        if ((c.Flags74 & 4) != 0)
            throw new NotSupportedException("contact events (FUN_1801feca0) are not ported");
        var all = w.AllContacts[~c.Flags78 & 1];
        SwapRemove(all, c.AllIndex, (x, i) => x.AllIndex = i);
        c.AllIndex = -1;
        w.Destroyed.Add(c);
        UpdateEdge(w, c);
    }

    /// <summary>The collide post-pass (FUN_1801dceb0), the lists sorted by key when the world sorts.</summary>
    public static void Flush(RnWorld w, CollideLists lists)
    {
        if (w.SortLists)
            SortByKey(lists.Lost);
        foreach (var c in lists.Lost)
        {
            WakeLeftBehind(w, c);
            Destroy(w, c);
        }
        lists.Lost.Clear();

        if (w.SortLists)
            SortByKey(lists.BothAsleep);
        foreach (var c in lists.BothAsleep)
        {
            if (c.ActiveIndex < 0)
                continue;
            SwapRemove(w.ActiveContacts, c.ActiveIndex, (x, i) => x.ActiveIndex = i);
            c.ActiveIndex = -1;
        }
        lists.BothAsleep.Clear();

        if (w.SortLists)
            SortByKey(lists.Began);
        foreach (var c in lists.Began)
        {
            SetTouchState(c, 0);
            if ((c.Flags74 & 2) != 0)
                throw new NotSupportedException("touch events (FUN_1801fe920) are not ported");
            UpdateEdge(w, c);
        }
        lists.Began.Clear();

        if (w.SortLists)
            SortByKey(lists.Ended);
        foreach (var c in lists.Ended)
        {
            WakeLeftBehind(w, c);
            SetTouchState(c, 1);
            if ((c.Flags74 & 2) != 0)
                throw new NotSupportedException("touch events (FUN_1801feca0) are not ported");
            UpdateEdge(w, c);
        }
        lists.Ended.Clear();

        foreach (var c in lists.Resized)
        {
            if (c.Island is { } island)
            {
                island.Sizes[c.Group] += c.Size98 - c.Size88;
                if (island.Coloured)
                    throw new NotSupportedException("graph-coloured islands are not ported");
            }
            c.Size88 = c.Size98;
        }
        lists.Resized.Clear();
    }
    /// <summary>
    /// The collide pass (FUN_1801f6980): the worker over the active contacts,
    /// then the post-pass. Contact and touch events (FUN_1801f8fd0) are not
    /// ported; a contact that asks for them throws.
    /// </summary>
    public static void Collide(RnWorld w, CollideLists lists)
    {
        CollideContacts(w, lists);
        Flush(w, lists);
    }

    /// <summary>
    /// The collide worker (FUN_1801ecfb0) as one thread runs it: the pending
    /// splits first, then every active contact in order: a pair whose proxies
    /// no longer overlap is lost, the others get a new manifold, and the
    /// changes of touch and size go to the lists.
    /// </summary>
    public static void CollideContacts(RnWorld w, CollideLists lists)
    {
        if (IslandManagerOps.SplitPending(w.Islands, w.StepCount))
            w.StepFlags |= 2;
        foreach (var c in w.ActiveContacts)
        {
            if (!ProxiesOverlap(w, c))
            {
                lists.Lost.Add(c);
                continue;
            }
            var bodyA = c.A.Body;
            var bodyB = c.B.Body;
            if (bodyB.ActiveIndex < 0 && bodyA.ActiveIndex < 0)
                lists.BothAsleep.Add(c);
            Update(c, RnTransform.Of(bodyA.State), RnTransform.Of(bodyB.State));
            if (c.Manifolds.Count < 1)
            {
                if (c.TouchState == 0)
                    lists.Ended.Add(c);
            }
            else
            {
                if ((c.Flags78 & 1) != 0 && (c.A.Reports || c.B.Reports))
                    throw new NotSupportedException("contact reports (FUN_1801fc050) are not ported");
                if (c.TouchState != 0)
                    lists.Began.Add(c);
                else if ((c.Flags74 & 2) != 0)
                    throw new NotSupportedException("persisting touch events (FUN_1801ef380) are not ported");
            }
            if (c.Size98 != c.Size88)
                lists.Resized.Add(c);
        }
    }

    /// <summary>
    /// Whether the two proxies' fat boxes overlap (FUN_1801f17e0): apart when
    /// B's max is below A's min or A's max below B's min on some axis.
    /// </summary>
    private static bool ProxiesOverlap(RnWorld w, RnContact c)
    {
        if (c.ChildA != -1 || c.ChildB != -1)
            throw new NotSupportedException("compound children (FUN_1801fc0e0) are not ported");
        if (c.A.ProxyId == -1 || c.B.ProxyId == -1)
            throw new NotSupportedException("a shape without a proxy is not ported");
        var (aMin, aMax) = FatBox(w, c.A);
        var (bMin, bMax) = FatBox(w, c.B);
        var apart = bMax.X < aMin.X || bMax.Y < aMin.Y || bMax.Z < aMin.Z
                 || aMax.X < bMin.X || aMax.Y < bMin.Y || aMax.Z < bMin.Z;
        return !apart;
    }

    /// <summary>A proxy's fat box: tree id &amp; 7, node id &gt;&gt; 3.</summary>
    private static (Vec3 Min, Vec3 Max) FatBox(RnWorld w, RnShape shape)
    {
        if (w.Broadphase is not { } broadphase)
            return (shape.FatMin, shape.FatMax);
        var id = (uint)shape.ProxyId;
        ref readonly var node = ref broadphase.Trees[id & 7].Nodes.Nodes[id >> 3];
        return (node.Min, node.Max);
    }

    /// <summary>The contact's update (vtable slot 2): hull pairs through FUN_180307750, hull on mesh through FUN_180305460.</summary>
    private static void Update(RnContact c, in RnTransform xfA, in RnTransform xfB)
    {
        if (c.Mesh is { } mesh)
        {
            MeshCollision.Update(mesh, xfA, c.A.Hull, xfB, c.B.Mesh!, c.B.MeshScale);
            c.Manifolds = [.. mesh.Manifolds];
            c.Size98 = mesh.SizeEstimate;
            return;
        }
        if (c.A.Type != 2 || c.B.Type != 2)
            throw new NotSupportedException($"shape pair {c.A.Type}/{c.B.Type} is not ported");
        if ((c.Flags78 & 1) == 0)
            throw new NotSupportedException("sensor pairs (the overlap path of FUN_1802f2560) are not ported");
        if (!Finite(xfA) || !Finite(xfB))
            return;

        // The block's manifold is both the warm start and, in place, the output.
        CachedManifold result = default;
        var previous = c.Manifolds.Count > 0 ? c.Manifolds[0] : default;
        var old = c.Manifolds.Count > 0 ? new ReadOnlySpan<CachedManifold>(ref previous) : default;
        if (c.Manifolds.Count > 0)
            result = previous;
        var hit = HullCollision.Collide(old, ref result, xfA, c.A.Hull, xfB, c.B.Hull, ref c.Sat);
        c.Manifolds = hit ? [result] : [];
        c.Size98 = MeshCollision.SizeEstimate(c.Manifolds);
    }

    /// <summary>No component with exponent 0xff (FUN_180307750's guard).</summary>
    private static bool Finite(in RnTransform xf)
    {
        ReadOnlySpan<float> f =
        [
            xf.R.M0, xf.R.M1, xf.R.M2, xf.R.M3, xf.R.M4, xf.R.M5, xf.R.M6, xf.R.M7, xf.R.M8,
            xf.T.X, xf.T.Y, xf.T.Z,
        ];
        foreach (var v in f)
            if ((BitConverter.SingleToUInt32Bits(v) & 0x7f800000) == 0x7f800000)
                return false;
        return true;
    }
}
