namespace Source2.Compiler.Maps;

/// <summary>
/// MSVC's <c>std::sort</c>, element for element: insertion sort at or below 32,
/// the median-of-three (Tukey's ninther above 40) fat partition, and heap sort
/// once the depth budget is spent. It is not stable, so where the compile
/// sorts with <c>std::sort</c> and equal keys carry different payloads, only
/// the same algorithm puts them in the same order. <c>SortPairs</c> in
/// visbuilder is this, instantiated over the pre-merge's pair records.
/// </summary>
internal static class MsvcSort
{
    private const int InsertionMax = 32;

    public static void Sort<T>(T[] items, Func<T, T, bool> less)
        => Sort(items, 0, items.Length, items.Length, less);

    private static void Sort<T>(T[] a, int first, int last, int ideal, Func<T, T, bool> less)
    {
        while (true)
        {
            if (last - first <= InsertionMax)
            {
                Insertion(a, first, last, less);
                return;
            }
            if (ideal <= 0)
            {
                MakeHeap(a, first, last, less);
                for (; last - first >= 2; last--)
                    PopHeap(a, first, last, less);
                return;
            }

            var (lo, hi) = Partition(a, first, last, less);
            ideal = (ideal >> 1) + (ideal >> 2);
            if (lo - first < last - hi)
            {
                Sort(a, first, lo, ideal, less);
                first = hi;
            }
            else
            {
                Sort(a, hi, last, ideal, less);
                last = lo;
            }
        }
    }

    private static void Insertion<T>(T[] a, int first, int last, Func<T, T, bool> less)
    {
        if (first == last)
            return;
        for (var mid = first + 1; mid != last; mid++)
        {
            var value = a[mid];
            if (less(value, a[first]))
            {
                Array.Copy(a, first, a, first + 1, mid - first);
                a[first] = value;
                continue;
            }
            var hole = mid;
            for (var prev = hole - 1; less(value, a[prev]); prev--)
            {
                a[hole] = a[prev];
                hole = prev;
            }
            a[hole] = value;
        }
    }

    private static void Median3<T>(T[] a, int first, int mid, int last, Func<T, T, bool> less)
    {
        if (less(a[mid], a[first]))
            (a[mid], a[first]) = (a[first], a[mid]);
        if (less(a[last], a[mid]))
        {
            (a[last], a[mid]) = (a[mid], a[last]);
            if (less(a[mid], a[first]))
                (a[mid], a[first]) = (a[first], a[mid]);
        }
    }

    private static void GuessMedian<T>(T[] a, int first, int mid, int last, Func<T, T, bool> less)
    {
        var count = last - first;
        if (40 < count)
        {
            var step = (count + 1) >> 3;
            var twoStep = step << 1;
            Median3(a, first, first + step, first + twoStep, less);
            Median3(a, mid - step, mid, mid + step, less);
            Median3(a, last - twoStep, last - step, last, less);
            Median3(a, first + step, mid, last - step, less);
        }
        else
        {
            Median3(a, first, mid, last, less);
        }
    }

    private static (int, int) Partition<T>(T[] a, int first, int last, Func<T, T, bool> less)
    {
        var mid = first + ((last - first) >> 1);
        GuessMedian(a, first, mid, last - 1, less);
        var pfirst = mid;
        var plast = pfirst + 1;

        while (first < pfirst && !less(a[pfirst - 1], a[pfirst]) && !less(a[pfirst], a[pfirst - 1]))
            pfirst--;
        while (plast < last && !less(a[plast], a[pfirst]) && !less(a[pfirst], a[plast]))
            plast++;

        var gfirst = plast;
        var glast = pfirst;
        while (true)
        {
            for (; gfirst < last; gfirst++)
            {
                if (less(a[pfirst], a[gfirst]))
                    continue;
                if (less(a[gfirst], a[pfirst]))
                    break;
                if (plast != gfirst)
                    (a[plast], a[gfirst]) = (a[gfirst], a[plast]);
                plast++;
            }
            for (; first < glast; glast--)
            {
                var prev = glast - 1;
                if (less(a[prev], a[pfirst]))
                    continue;
                if (less(a[pfirst], a[prev]))
                    break;
                if (--pfirst != prev)
                    (a[pfirst], a[prev]) = (a[prev], a[pfirst]);
            }

            if (glast == first && gfirst == last)
                return (pfirst, plast);

            if (glast == first)
            {
                if (plast != gfirst)
                    (a[pfirst], a[plast]) = (a[plast], a[pfirst]);
                plast++;
                (a[pfirst], a[gfirst]) = (a[gfirst], a[pfirst]);
                pfirst++;
                gfirst++;
            }
            else if (gfirst == last)
            {
                if (--glast != --pfirst)
                    (a[glast], a[pfirst]) = (a[pfirst], a[glast]);
                plast--;
                (a[pfirst], a[plast]) = (a[plast], a[pfirst]);
            }
            else
            {
                glast--;
                (a[gfirst], a[glast]) = (a[glast], a[gfirst]);
                gfirst++;
            }
        }
    }

    private static void MakeHeap<T>(T[] a, int first, int last, Func<T, T, bool> less)
    {
        var bottom = last - first;
        for (var hole = bottom >> 1; hole > 0;)
        {
            hole--;
            PopHole(a, first, hole, bottom, a[first + hole], less);
        }
    }

    private static void PopHeap<T>(T[] a, int first, int last, Func<T, T, bool> less)
    {
        if (last - first < 2)
            return;
        last--;
        var value = a[last];
        a[last] = a[first];
        PopHole(a, first, 0, last - first, value, less);
    }

    private static void PopHole<T>(T[] a, int first, int hole, int bottom, T value, Func<T, T, bool> less)
    {
        var top = hole;
        var idx = hole;
        var maxNonLeaf = (bottom - 1) >> 1;
        while (idx < maxNonLeaf)
        {
            idx = (2 * idx) + 2;
            if (less(a[first + idx], a[first + idx - 1]))
                idx--;
            a[first + hole] = a[first + idx];
            hole = idx;
        }
        if (idx == maxNonLeaf && bottom % 2 == 0)
        {
            a[first + hole] = a[first + bottom - 1];
            hole = bottom - 1;
        }
        for (var parent = (hole - 1) >> 1; top < hole && less(a[first + parent], value); parent = (hole - 1) >> 1)
        {
            a[first + hole] = a[first + parent];
            hole = parent;
        }
        a[first + hole] = value;
    }
}
