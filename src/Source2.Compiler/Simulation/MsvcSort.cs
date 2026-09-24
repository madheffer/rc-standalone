namespace Source2.Compiler.Simulation;

/// <summary>A strict weak order: true when <paramref name="a"/> goes before <paramref name="b"/>.</summary>
public interface ILess<T>
{
    bool Less(in T a, in T b);
}

/// <summary>
/// Microsoft's std::sort (the MSVC STL's _Sort_unchecked), step for step, so
/// elements with equal keys end in the same order as in Valve's binaries:
/// introsort with a ninther above 40 elements, a three-way partition around
/// the median, insertion sort at 32 elements or fewer, and heap sort once the
/// depth budget runs out. vphysics2 instantiates it for the Morton rebuild
/// (FUN_180333af0, with FUN_180333970 as the median guess).
/// </summary>
public static class MsvcSort
{
    private const int InsertionSortMax = 32;

    public static void Sort<T, TLess>(Span<T> items, TLess less) where TLess : struct, ILess<T>
        => SortUnchecked(items, 0, items.Length, items.Length, less);

    private static void SortUnchecked<T, TLess>(Span<T> a, int first, int last, long ideal, TLess less)
        where TLess : struct, ILess<T>
    {
        for (;;)
        {
            if (last - first <= InsertionSortMax)
            {
                InsertionSort(a, first, last, less);
                return;
            }
            if (ideal <= 0)
            {
                MakeHeap(a, first, last, less);
                SortHeap(a, first, last, less);
                return;
            }
            var (pfirst, plast) = PartitionByMedianGuess(a, first, last, less);
            ideal = (ideal >> 1) + (ideal >> 2);
            if (pfirst - first < last - plast)
            {
                SortUnchecked(a, first, pfirst, ideal, less);
                first = plast;
            }
            else
            {
                SortUnchecked(a, plast, last, ideal, less);
                last = pfirst;
            }
        }
    }

    private static void InsertionSort<T, TLess>(Span<T> a, int first, int last, TLess less)
        where TLess : struct, ILess<T>
    {
        if (first == last)
            return;
        for (var mid = first + 1; mid != last; mid++)
        {
            var hole = mid;
            var val = a[mid];
            if (less.Less(val, a[first]))
            {
                a.Slice(first, mid - first).CopyTo(a.Slice(first + 1));
                a[first] = val;
            }
            else
            {
                for (var prev = hole - 1; less.Less(val, a[prev]); hole = prev, prev--)
                    a[hole] = a[prev];
                a[hole] = val;
            }
        }
    }

    private static void Swap<T>(Span<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static void Med3<T, TLess>(Span<T> a, int first, int mid, int last, TLess less)
        where TLess : struct, ILess<T>
    {
        if (less.Less(a[mid], a[first]))
            Swap(a, mid, first);
        if (less.Less(a[last], a[mid]))
        {
            Swap(a, last, mid);
            if (less.Less(a[mid], a[first]))
                Swap(a, mid, first);
        }
    }

    /// <summary>_Guess_median_unchecked; <paramref name="last"/> is inclusive.</summary>
    private static void GuessMedian<T, TLess>(Span<T> a, int first, int mid, int last, TLess less)
        where TLess : struct, ILess<T>
    {
        var count = last - first;
        if (40 < count)
        {
            var step = (count + 1) >> 3;
            var twoStep = step << 1;
            Med3(a, first, first + step, first + twoStep, less);
            Med3(a, mid - step, mid, mid + step, less);
            Med3(a, last - twoStep, last - step, last, less);
            Med3(a, first + step, mid, last - step, less);
        }
        else
            Med3(a, first, mid, last, less);
    }

    /// <summary>_Partition_by_median_guess_unchecked: returns [pfirst, plast), the run equal to the pivot.</summary>
    private static (int, int) PartitionByMedianGuess<T, TLess>(Span<T> a, int first, int last, TLess less)
        where TLess : struct, ILess<T>
    {
        var mid = first + ((last - first) >> 1);
        GuessMedian(a, first, mid, last - 1, less);
        var pfirst = mid;
        var plast = pfirst + 1;
        while (first < pfirst && !less.Less(a[pfirst - 1], a[pfirst]) && !less.Less(a[pfirst], a[pfirst - 1]))
            pfirst--;
        while (plast < last && !less.Less(a[plast], a[pfirst]) && !less.Less(a[pfirst], a[plast]))
            plast++;

        var gfirst = plast;
        var glast = pfirst;
        for (;;)
        {
            for (; gfirst < last; gfirst++)
            {
                if (less.Less(a[pfirst], a[gfirst]))
                    continue;
                if (less.Less(a[gfirst], a[pfirst]))
                    break;
                if (plast != gfirst)
                    Swap(a, plast, gfirst);
                plast++;
            }
            for (; first < glast; glast--)
            {
                if (less.Less(a[glast - 1], a[pfirst]))
                    continue;
                if (less.Less(a[pfirst], a[glast - 1]))
                    break;
                if (--pfirst != glast - 1)
                    Swap(a, pfirst, glast - 1);
            }
            if (glast == first && gfirst == last)
                return (pfirst, plast);
            if (glast == first)
            {
                if (plast != gfirst)
                    Swap(a, pfirst, plast);
                plast++;
                Swap(a, pfirst, gfirst);
                pfirst++;
                gfirst++;
            }
            else if (gfirst == last)
            {
                if (--glast != --pfirst)
                    Swap(a, glast, pfirst);
                Swap(a, pfirst, --plast);
            }
            else
                Swap(a, gfirst++, --glast);
        }
    }

    private static void PushHeapByIndex<T, TLess>(Span<T> a, int first, long hole, long top, T val, TLess less)
        where TLess : struct, ILess<T>
    {
        for (var idx = (hole - 1) >> 1; top < hole && less.Less(a[first + (int)idx], val); idx = (hole - 1) >> 1)
        {
            a[first + (int)hole] = a[first + (int)idx];
            hole = idx;
        }
        a[first + (int)hole] = val;
    }

    private static void PopHeapHoleByIndex<T, TLess>(Span<T> a, int first, long hole, long bottom, T val, TLess less)
        where TLess : struct, ILess<T>
    {
        var top = hole;
        var idx = hole;
        var maxNonLeaf = (bottom - 1) >> 1;
        while (idx < maxNonLeaf)
        {
            idx = 2 * idx + 2;
            if (less.Less(a[first + (int)idx], a[first + (int)(idx - 1)]))
                idx--;
            a[first + (int)hole] = a[first + (int)idx];
            hole = idx;
        }
        if (idx == maxNonLeaf && bottom % 2 == 0)
        {
            a[first + (int)hole] = a[first + (int)(bottom - 1)];
            hole = bottom - 1;
        }
        PushHeapByIndex(a, first, hole, top, val, less);
    }

    private static void MakeHeap<T, TLess>(Span<T> a, int first, int last, TLess less)
        where TLess : struct, ILess<T>
    {
        long bottom = last - first;
        for (var hole = bottom >> 1; hole > 0;)
        {
            hole--;
            PopHeapHoleByIndex(a, first, hole, bottom, a[first + (int)hole], less);
        }
    }

    private static void SortHeap<T, TLess>(Span<T> a, int first, int last, TLess less)
        where TLess : struct, ILess<T>
    {
        for (; last - first >= 2; last--)
        {
            var val = a[last - 1];
            a[last - 1] = a[first];
            PopHeapHoleByIndex(a, first, 0, last - 1 - first, val, less);
        }
    }
}
