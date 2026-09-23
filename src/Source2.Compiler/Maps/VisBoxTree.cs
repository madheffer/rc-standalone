using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The dynamic AABB tree the candidate query runs on, which is
/// <c>18010a520</c> and the five functions around it.
///
/// <para>A node is the 48 byte record <c>18003d600</c> also reads: box, height
/// at <c>+0x18</c>, parent at <c>+0x1c</c>, two children at <c>+0x20</c> and
/// <c>+0x24</c>, payload at <c>+0x28</c>, and a child of -1 marking a leaf. The
/// pool starts at 32 nodes with a free list threaded through the first word,
/// and doubles when it runs out.</para>
///
/// <para>No box is ever widened: <c>18010ad30</c> copies the caller's 24 bytes
/// verbatim and so does <c>18010b510</c>, the only other writer, and
/// <c>18010bbb0</c> tests an exact inclusive overlap with no margin. So the tree
/// answers precisely the set of leaves whose boxes meet the query.</para>
///
/// <para>It IS what <see cref="VisMerge"/> builds its candidate lists from, the
/// way <c>1800337a0</c> does: every cluster gets a proxy with its own box before
/// any list is built, an absorb moves the survivor and destroys the other, and
/// <c>1800306e0</c> then queries it. Two earlier attempts to stand it up moved
/// probe01's first pass from 2,466 clusters to 1,138; both were reading a ray
/// trace that landed 27% of its rays on the wrong triangle, and with that fixed
/// the tree reproduces the linear scan's numbers to the cluster on all three
/// specimens. It is kept because the ORDER a query returns candidates in decides
/// which of two equally priced pairs a cluster holds, and only the tree gives
/// Valve's order.</para>
/// </summary>
public sealed class VisBoxTree
{
    /// <summary>Nodes the pool starts with, the <c>0x600</c> bytes <c>18010a520</c> takes.</summary>
    public const int FirstNodes = 32;

    private const int None = -1;

    private struct Node
    {
        public Vector3 Mins;
        public Vector3 Maxs;
        public int Height;
        public int Parent;
        public int Left;
        public int Right;
        public int Payload;
        public int Next;

        public readonly bool Leaf => Left == None;
    }

    private Node[] _nodes = new Node[FirstNodes];
    private int _root = None;
    private int _free;

    /// <summary>Start empty, with every node on the free list.</summary>
    public VisBoxTree()
    {
        Thread(0);
    }

    /// <summary>Leaves currently in the tree.</summary>
    public int Count { get; private set; }

    /// <summary>The root node, or -1.</summary>
    public int Root => _root;

    /// <summary>
    /// The root's height, which is what <c>18010a6e0</c> keeps logarithmic. It
    /// is the only observable difference a missing balance makes: every query
    /// still answers correctly, just in a different ORDER and more slowly.
    /// </summary>
    public int Height => _root == None ? 0 : _nodes[_root].Height;

    /// <summary>
    /// <c>18010ad30</c>: take a node off the free list, copy the box in as it
    /// stands, and insert it.
    /// </summary>
    /// <param name="mins">The box.</param>
    /// <param name="maxs">The box.</param>
    /// <param name="payload">What a query should hand back.</param>
    public int Create(Vector3 mins, Vector3 maxs, int payload)
    {
        var at = Take();
        _nodes[at].Mins = mins;
        _nodes[at].Maxs = maxs;
        _nodes[at].Height = 0;
        _nodes[at].Parent = None;
        _nodes[at].Left = None;
        _nodes[at].Right = None;
        _nodes[at].Payload = payload;
        Insert(at);
        Count++;
        return at;
    }

    /// <summary><c>18010b510</c>: pull the leaf out, rewrite its box, put it back.</summary>
    /// <param name="proxy">The leaf, as <see cref="Create"/> returned it.</param>
    /// <param name="mins">Its new box.</param>
    /// <param name="maxs">Its new box.</param>
    public void Move(int proxy, Vector3 mins, Vector3 maxs)
    {
        Remove(proxy);
        _nodes[proxy].Mins = mins;
        _nodes[proxy].Maxs = maxs;
        Insert(proxy);
    }

    /// <summary><c>18010ae00</c>: pull the leaf out and return its node to the pool.</summary>
    /// <param name="proxy">The leaf to drop.</param>
    public void Destroy(int proxy)
    {
        Remove(proxy);
        _nodes[proxy].Next = _free;
        _nodes[proxy].Height = None;
        _free = proxy;
        Count--;
    }

    /// <summary>
    /// <c>18010bbb0</c>: every leaf whose box meets the query, by payload. The
    /// test is inclusive on both sides and carries no margin.
    /// </summary>
    /// <param name="mins">The query box.</param>
    /// <param name="maxs">The query box.</param>
    /// <param name="into">Collects the payloads.</param>
    public void Query(Vector3 mins, Vector3 maxs, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_root == None)
            return;

        var stack = new Stack<int>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            ref var node = ref _nodes[at];
            if (mins.X > node.Maxs.X || node.Mins.X > maxs.X
                || mins.Y > node.Maxs.Y || node.Mins.Y > maxs.Y
                || mins.Z > node.Maxs.Z || node.Mins.Z > maxs.Z)
                continue;
            if (node.Leaf)
            {
                into.Add(node.Payload);
                continue;
            }
            // 18010bbb0 writes child2 into the slot it just popped and child1
            // above it, so child1 comes off next. Candidate order decides which
            // of two equally priced pairs the merge takes, so it matters.
            stack.Push(node.Right);
            stack.Push(node.Left);
        }
    }

    /// <summary>
    /// Every leaf whose box the SEGMENT from <paramref name="from"/> to
    /// <paramref name="to"/> meets, by payload, which is the outer walk of
    /// <c>18003d600</c>.
    ///
    /// <para>The slab test is <c>t</c> in [0, 1] and the reciprocal is the plain
    /// one: a zero component becomes FLT_MAX rather than the guarded epsilon
    /// <see cref="VisVisibility.Reciprocal"/> uses, because that is what
    /// <c>18003d600</c> writes. The epsilon guard belongs to the per cell mask it
    /// calls afterwards, not to this.</para>
    /// </summary>
    /// <param name="from">Segment start.</param>
    /// <param name="to">Segment end.</param>
    /// <param name="into">Collects the payloads.</param>
    public void Crossed(Vector3 from, Vector3 to, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_root == None)
            return;

        var delta = to - from;
        var inverse = new Vector3(
            delta.X != 0f ? 1f / delta.X : float.MaxValue,
            delta.Y != 0f ? 1f / delta.Y : float.MaxValue,
            delta.Z != 0f ? 1f / delta.Z : float.MaxValue);

        var stack = new Stack<int>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            ref var node = ref _nodes[at];

            var low = (node.Mins - from) * inverse;
            var high = (node.Maxs - from) * inverse;
            var enter = MathF.Max(MathF.Max(MathF.Min(low.X, high.X), MathF.Min(low.Y, high.Y)),
                                  MathF.Max(MathF.Min(low.Z, high.Z), 0f));
            var leave = MathF.Min(MathF.Min(MathF.Max(low.X, high.X), MathF.Max(low.Y, high.Y)),
                                  MathF.Min(MathF.Max(low.Z, high.Z), 1f));
            if (enter > leave || enter > 1f)
                continue;

            if (node.Leaf)
            {
                into.Add(node.Payload);
                continue;
            }
            stack.Push(node.Right);
            stack.Push(node.Left);
        }
    }

    /// <summary>The chain from a leaf up to the root, as node index and box.</summary>
    /// <param name="proxy">The leaf to walk up from.</param>
    public List<(int Node, Vector3 Mins, Vector3 Maxs)> Path(int proxy)
    {
        var chain = new List<(int, Vector3, Vector3)>();
        for (var at = proxy; at != None; at = _nodes[at].Parent)
            chain.Add((at, _nodes[at].Mins, _nodes[at].Maxs));
        return chain;
    }

    /// <summary>The box a leaf is stored under, for checking it against its owner.</summary>
    /// <param name="proxy">The leaf.</param>
    public (Vector3 Mins, Vector3 Maxs) BoxOf(int proxy) => (_nodes[proxy].Mins, _nodes[proxy].Maxs);

    /// <summary>
    /// Walk the tree and report the first thing wrong with it, or null. Used to
    /// tell a stale box apart from a leaf that fell out of the structure.
    /// </summary>
    /// <param name="proxy">A leaf to confirm is reachable from the root.</param>
    public string? Validate(int proxy)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        if (_root != None)
            stack.Push(_root);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            if (!seen.Add(at))
                return $"node {at} reached twice";
            if (_nodes[at].Leaf)
                continue;
            foreach (var child in new[] { _nodes[at].Left, _nodes[at].Right })
            {
                if (_nodes[child].Parent != at)
                    return $"node {child} says its parent is {_nodes[child].Parent}, not {at}";
                if (_nodes[at].Mins.X > _nodes[child].Mins.X || _nodes[at].Mins.Y > _nodes[child].Mins.Y
                    || _nodes[at].Mins.Z > _nodes[child].Mins.Z
                    || _nodes[at].Maxs.X < _nodes[child].Maxs.X || _nodes[at].Maxs.Y < _nodes[child].Maxs.Y
                    || _nodes[at].Maxs.Z < _nodes[child].Maxs.Z)
                    return $"node {at} box {_nodes[at].Mins}..{_nodes[at].Maxs} does not contain"
                         + $" child {child} box {_nodes[child].Mins}..{_nodes[child].Maxs}";
                stack.Push(child);
            }
        }
        if (!seen.Contains(proxy))
            return $"leaf {proxy} is not reachable from the root";
        return null;
    }

    /// <summary>
    /// The sibling search: descend while it is cheaper to go down than to make a
    /// new parent here, by surface area with the inherited cost carried along.
    /// </summary>
    private void Insert(int leaf)
    {
        if (_root == None)
        {
            _root = leaf;
            _nodes[leaf].Parent = None;
            return;
        }

        var mins = _nodes[leaf].Mins;
        var maxs = _nodes[leaf].Maxs;
        var at = _root;
        while (!_nodes[at].Leaf)
        {
            var area = Area(_nodes[at].Mins, _nodes[at].Maxs);
            var merged = Area(Vector3.Min(_nodes[at].Mins, mins), Vector3.Max(_nodes[at].Maxs, maxs));
            var here = 2f * merged;
            var inherited = 2f * (merged - area);

            var left = Descend(_nodes[at].Left, mins, maxs, inherited);
            var right = Descend(_nodes[at].Right, mins, maxs, inherited);
            if (here < left && here < right)
                break;
            at = left < right ? _nodes[at].Left : _nodes[at].Right;
        }

        var sibling = at;
        var above = _nodes[sibling].Parent;
        var parent = Take();
        _nodes[parent].Parent = above;
        _nodes[parent].Payload = None;
        _nodes[parent].Mins = Vector3.Min(mins, _nodes[sibling].Mins);
        _nodes[parent].Maxs = Vector3.Max(maxs, _nodes[sibling].Maxs);
        _nodes[parent].Height = _nodes[sibling].Height + 1;
        _nodes[parent].Left = sibling;
        _nodes[parent].Right = leaf;
        _nodes[sibling].Parent = parent;
        _nodes[leaf].Parent = parent;

        if (above == None)
            _root = parent;
        else if (_nodes[above].Left == sibling)
            _nodes[above].Left = parent;
        else
            _nodes[above].Right = parent;

        Refit(_nodes[leaf].Parent);
    }

    private float Descend(int child, Vector3 mins, Vector3 maxs, float inherited)
    {
        var merged = Area(Vector3.Min(_nodes[child].Mins, mins), Vector3.Max(_nodes[child].Maxs, maxs));
        return _nodes[child].Leaf
            ? merged + inherited
            : merged - Area(_nodes[child].Mins, _nodes[child].Maxs) + inherited;
    }

    private void Remove(int leaf)
    {
        if (leaf == _root)
        {
            _root = None;
            return;
        }

        var parent = _nodes[leaf].Parent;
        var above = _nodes[parent].Parent;
        var sibling = _nodes[parent].Left == leaf ? _nodes[parent].Right : _nodes[parent].Left;

        if (above == None)
        {
            _root = sibling;
            _nodes[sibling].Parent = None;
        }
        else
        {
            if (_nodes[above].Left == parent)
                _nodes[above].Left = sibling;
            else
                _nodes[above].Right = sibling;
            _nodes[sibling].Parent = above;
            Refit(above);
        }

        _nodes[parent].Next = _free;
        _nodes[parent].Height = None;
        _free = parent;
    }

    /// <summary>
    /// <c>18010a5d0</c>: walk to the root, BALANCING each node on the way and
    /// then fixing its box and height.
    ///
    /// <para>The balance is not optional and it was missing here for a long time.
    /// <c>18010a5d0</c> opens every iteration with
    /// <c>FUN_18010a6e0(tree, node)</c> and continues from what it RETURNS, which
    /// is the new root of that subtree after a rotation. Leaving it out still
    /// answers every query correctly -- a query tests boxes, not shape -- so the
    /// tests never saw it. What it changes is the ORDER a query returns candidates
    /// in, and that decides which of two equally priced pairs a cluster holds.</para>
    /// </summary>
    private void Refit(int at)
    {
        while (at != None)
        {
            at = Balance(at);
            var left = _nodes[at].Left;
            var right = _nodes[at].Right;
            _nodes[at].Height = Math.Max(_nodes[left].Height, _nodes[right].Height) + 1;
            _nodes[at].Mins = Vector3.Min(_nodes[left].Mins, _nodes[right].Mins);
            _nodes[at].Maxs = Vector3.Max(_nodes[left].Maxs, _nodes[right].Maxs);
            at = _nodes[at].Parent;
        }
    }

    /// <summary>
    /// <c>18010a6e0</c>: one rotation when a node's two children differ in height
    /// by two, returning the subtree's new root. The taller child comes up and
    /// takes its own shorter grandchild's place, which is the standard single
    /// rotation of a dynamic AABB tree.
    /// </summary>
    private int Balance(int a)
    {
        if (_nodes[a].Leaf || _nodes[a].Height < 2)
            return a;

        var b = _nodes[a].Left;
        var c = _nodes[a].Right;
        var tilt = _nodes[c].Height - _nodes[b].Height;

        if (tilt > 1)
        {
            Raise(a, c, up: true);
            return c;
        }
        if (tilt < -1)
        {
            Raise(a, b, up: false);
            return b;
        }
        return a;
    }

    /// <summary>
    /// Swap <paramref name="child"/> above <paramref name="at"/> and hand one of
    /// its own children back down. <paramref name="up"/> says which side the
    /// child came from, which is the only thing that differs between the two
    /// halves of <c>18010a6e0</c>.
    /// </summary>
    private void Raise(int at, int child, bool up)
    {
        var other = up ? _nodes[at].Left : _nodes[at].Right;
        var first = _nodes[child].Left;
        var second = _nodes[child].Right;

        _nodes[child].Left = at;
        _nodes[child].Parent = _nodes[at].Parent;
        _nodes[at].Parent = child;

        var above = _nodes[child].Parent;
        if (above == None)
            _root = child;
        else if (_nodes[above].Left == at)
            _nodes[above].Left = child;
        else
            _nodes[above].Right = child;

        // The TALLER grandchild stays up with its parent; the shorter one comes
        // down to take the rotated node's place.
        var stays = _nodes[first].Height > _nodes[second].Height ? first : second;
        var drops = stays == first ? second : first;

        _nodes[child].Right = stays;
        if (up)
            _nodes[at].Right = drops;
        else
            _nodes[at].Left = drops;
        _nodes[drops].Parent = at;

        _nodes[at].Mins = Vector3.Min(_nodes[other].Mins, _nodes[drops].Mins);
        _nodes[at].Maxs = Vector3.Max(_nodes[other].Maxs, _nodes[drops].Maxs);
        _nodes[child].Mins = Vector3.Min(_nodes[at].Mins, _nodes[stays].Mins);
        _nodes[child].Maxs = Vector3.Max(_nodes[at].Maxs, _nodes[stays].Maxs);

        _nodes[at].Height = Math.Max(_nodes[other].Height, _nodes[drops].Height) + 1;
        _nodes[child].Height = Math.Max(_nodes[at].Height, _nodes[stays].Height) + 1;
    }

    private int Take()
    {
        if (_free == None)
        {
            var was = _nodes.Length;
            Array.Resize(ref _nodes, was * 2);
            Thread(was);
        }
        var at = _free;
        _free = _nodes[at].Next;
        return at;
    }

    private void Thread(int from)
    {
        for (var i = from; i < _nodes.Length; i++)
        {
            _nodes[i].Next = i + 1;
            _nodes[i].Height = None;
        }
        _nodes[^1].Next = None;
        _free = from;
    }

    private static float Area(Vector3 mins, Vector3 maxs)
    {
        var d = maxs - mins;
        return (d.X * d.Y) + (d.Y * d.Z) + (d.X * d.Z);
    }
}
