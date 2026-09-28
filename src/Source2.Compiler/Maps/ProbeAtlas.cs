namespace Source2.Compiler.Maps;

/// <summary>
/// The light probe atlas (LightProbe_PackAtlas, resourcecompiler 0923
/// 1801f5a80): every probe volume's grid packed into one 3D texture by
/// tier0's CUtl3DAllocator, each volume's corner written back as its
/// light_probe_atlas_x, _Y and _Z (one-based, past the border texel).
/// </summary>
public static class ProbeAtlas
{
    /// <summary>A volume's grid (light_probe_size_x/y/z) and its place in the atlas.</summary>
    public readonly record struct Place(int X, int Y, int Z);

    /// <summary>
    /// The packing, one place per volume in the order given (the export's).
    /// <list type="number">
    /// <item>Each axis takes (size + 5) &amp; ~3 cells: the size plus a
    /// border texel either side, rounded up to 4 (-2 takes 4).</item>
    /// <item>The volumes are V_qsort-ed by size z, then y, then x, largest
    /// first.</item>
    /// <item>The atlas starts at the largest cell count per axis; while
    /// nothing has packed, every axis grows by 4.</item>
    /// <item>For each order of the three axes (0 1 2, 0 2 1, 1 0 2 ...) the
    /// first axis of the order shrinks by 1 after each pack that fits; the
    /// first that does not fit takes the shrink back and ends the order. The
    /// last fit's places stand.</item>
    /// </list>
    /// </summary>
    public static Place[] Pack(IReadOnlyList<Place> sizes)
    {
        if (sizes.Count == 0)
            return [];
        static int Cells(int size) => size == -2 ? 4 : (size + 5) & ~3;
        var order = Enumerable.Range(0, sizes.Count).ToList();
        CrtQsort.Sort(order, (a, b) => sizes[a].Z != sizes[b].Z ? sizes[b].Z - sizes[a].Z
                                     : sizes[a].Y != sizes[b].Y ? sizes[b].Y - sizes[a].Y
                                     : sizes[b].X - sizes[a].X);
        var dims = new int[3];
        foreach (var s in sizes)
        {
            dims[0] = Math.Max(dims[0], Cells(s.X));
            dims[1] = Math.Max(dims[1], Cells(s.Y));
            dims[2] = Math.Max(dims[2], Cells(s.Z));
        }
        var allocator = new Allocator3D();
        var rects = new Allocator3D.Rect[sizes.Count];
        Allocator3D.Rect[]? best = null;
        for (var a = 0; a < 3; a++)
            for (var b = 0; b < 3; b++)
                for (var c = 0; c < 3; c++)
                {
                    if (a == b || c == a || c == b)
                        continue;
                    int[] axes = [a, b, c];
                    var done = new bool[3];
                    // The loop runs while the first axis is not done and stops
                    // as soon as the second or third is (1801f5e10): only the
                    // first axis of an order ever shrinks.
                    while (!done[0])
                    {
                        if (done[1] || done[2])
                            break;
                        allocator.Reset(dims[0], dims[1], dims[2]);
                        var fits = true;
                        for (var i = 0; i < order.Count && fits; i++)
                        {
                            var s = sizes[order[i]];
                            fits = allocator.Allocate(Cells(s.X), Cells(s.Y), Cells(s.Z), out rects[i]);
                        }
                        if (!fits)
                        {
                            if (best == null)
                            {
                                dims[0] += 4;
                                dims[1] += 4;
                                dims[2] += 4;
                                continue;
                            }
                            var k = Array.IndexOf(done, false);
                            if (k >= 0)
                            {
                                done[k] = true;
                                dims[axes[k]]++;
                            }
                            continue;
                        }
                        best = (Allocator3D.Rect[])rects.Clone();
                        var j = Array.IndexOf(done, false);
                        if (j >= 0)
                        {
                            if (dims[axes[j]] <= 1)
                                done[j] = true;
                            else
                                dims[axes[j]]--;
                        }
                    }
                }
        var places = new Place[sizes.Count];
        for (var i = 0; i < order.Count; i++)
            places[order[i]] = new Place(best![i].X + 1, best[i].Y + 1, best[i].Z + 1);
        return places;
    }

    /// <summary>
    /// tier0's CUtl3DAllocator (Reset 180196680, Allocate3D 180196990 without
    /// its fallback, the split 180196780): free boxes in 32 lists by
    /// floor(log2(volume)), each new box pushed at its list's head.
    /// </summary>
    private sealed class Allocator3D
    {
        public readonly record struct Rect(int X, int Y, int Z, int W, int H, int D);

        private sealed class Box
        {
            public int X, Y, Z, W, H, D;
        }

        private readonly LinkedList<Box>[] _lists = Enumerable.Range(0, 32).Select(_ => new LinkedList<Box>()).ToArray();

        private static int Bucket(uint volume)
        {
            var i = 0;
            while ((volume >>= 1) != 0)
                i++;
            return i;
        }

        private void Push(Box box) => _lists[Bucket((uint)(box.W * box.H * box.D))].AddFirst(box);

        public void Reset(int w, int h, int d)
        {
            foreach (var list in _lists)
                list.Clear();
            Push(new Box { W = w, H = h, D = d });
        }

        /// <summary>
        /// The best fitting free box from the request's own list up: the one
        /// holding it with the least max(1,|dw|)*max(1,|dh|)*max(1,|dd|), the
        /// first met on a tie. The box's corner takes the request; what is left
        /// along x, then y, then z goes back as new boxes.
        /// </summary>
        public bool Allocate(int w, int h, int d, out Rect rect)
        {
            rect = default;
            var start = Bucket((uint)(w * h * d));
            LinkedListNode<Box>? best = null;
            var bestList = -1;
            var bestScore = int.MaxValue;
            if (start < 32)
            {
                for (var l = start; l < 32; l++)
                    for (var n = _lists[l].First; n != null; n = n.Next)
                    {
                        var b = n.Value;
                        if (w > b.W || h > b.H || d > b.D)
                            continue;
                        var score = Math.Max(1, Math.Abs(w - b.W)) * Math.Max(1, Math.Abs(h - b.H)) * Math.Max(1, Math.Abs(d - b.D));
                        if (score < bestScore)
                        {
                            (best, bestList, bestScore) = (n, l, score);
                        }
                    }
            }
            if (best == null)
                return false;
            var box = best.Value;
            _lists[bestList].Remove(best);
            int cw = Math.Min(w, box.W), ch = Math.Min(h, box.H), cd = Math.Min(d, box.D);
            rect = new Rect(box.X, box.Y, box.Z, cw, ch, cd);
            if (cw < box.W)
                Push(new Box { X = box.X + cw, Y = box.Y, Z = box.Z, W = box.W - cw, H = box.H, D = cd });
            if (ch < box.H)
                Push(new Box { X = box.X, Y = box.Y + ch, Z = box.Z, W = cw, H = box.H - ch, D = cd });
            if (cd < box.D)
            {
                box.Z += cd;
                box.D -= cd;
                Push(box);
            }
            return true;
        }
    }
}
