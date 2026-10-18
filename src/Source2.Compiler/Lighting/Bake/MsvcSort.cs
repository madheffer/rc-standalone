namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// MSVC's std::sort (the STL's _Sort_unchecked: introsort with insertion sort at 32 elements or fewer,
/// _Partition_by_median_guess_unchecked with Tukey's ninther above 40, heap sort after 1.5 log2(N)
/// divisions), as vrad3's std_Sort_32ByteElements (18003eaa0) runs it. It is not stable, so equal keys
/// come out in the order this exact sequence of moves leaves them; ported move for move.
/// </summary>
public static class MsvcSort
{
    public static void Sort<T>(IList<T> a, int first, int last, Func<T, T, bool> less)
        => SortUnchecked(a, first, last, last - first, less);

    private static void SortUnchecked<T>(IList<T> a, int first, int last, int ideal, Func<T, T, bool> less)
    {
        for (;;)
        {
            if (last - first <= 32)
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
            var (midFirst, midSecond) = PartitionByMedianGuess(a, first, last, less);
            ideal = (ideal >> 1) + (ideal >> 2);
            if (midFirst - first < last - midSecond)
            {
                SortUnchecked(a, first, midFirst, ideal, less);
                first = midSecond;
            }
            else
            {
                SortUnchecked(a, midSecond, last, ideal, less);
                last = midFirst;
            }
        }
    }

    private static void Swap<T>(IList<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static void InsertionSort<T>(IList<T> a, int first, int last, Func<T, T, bool> less)
    {
        if (first == last)
            return;
        for (var mid = first + 1; mid != last; mid++)
        {
            var hole = mid;
            var val = a[mid];
            if (less(val, a[first]))
            {
                for (var k = mid; k > first; k--)
                    a[k] = a[k - 1];
                a[first] = val;
            }
            else
            {
                for (var prev = hole - 1; less(val, a[prev]); hole = prev, prev--)
                    a[hole] = a[prev];
                a[hole] = val;
            }
        }
    }

    private static void Med3<T>(IList<T> a, int first, int mid, int last, Func<T, T, bool> less)
    {
        if (less(a[mid], a[first]))
            Swap(a, mid, first);
        if (less(a[last], a[mid]))
        {
            Swap(a, last, mid);
            if (less(a[mid], a[first]))
                Swap(a, mid, first);
        }
    }

    private static void GuessMedian<T>(IList<T> a, int first, int mid, int last, Func<T, T, bool> less)
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

    private static (int, int) PartitionByMedianGuess<T>(IList<T> a, int first, int last, Func<T, T, bool> less)
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
        for (;;)
        {
            for (; gfirst < last; gfirst++)
            {
                if (less(a[pfirst], a[gfirst]))
                    continue;
                if (less(a[gfirst], a[pfirst]))
                    break;
                if (plast != gfirst)
                    Swap(a, plast, gfirst);
                plast++;
            }
            for (; first < glast; glast--)
            {
                if (less(a[glast - 1], a[pfirst]))
                    continue;
                if (less(a[pfirst], a[glast - 1]))
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

    private static void MakeHeap<T>(IList<T> a, int first, int last, Func<T, T, bool> less)
    {
        var bottom = last - first;
        for (var hole = bottom >> 1; hole > 0;)
        {
            hole--;
            var val = a[first + hole];
            PopHeapHoleByIndex(a, first, hole, bottom, val, less);
        }
    }

    private static void PopHeapHoleByIndex<T>(IList<T> a, int first, int hole, int bottom, T val, Func<T, T, bool> less)
    {
        var top = hole;
        var idx = hole;
        var maxNonLeaf = (bottom - 1) >> 1;
        while (idx < maxNonLeaf)
        {
            idx = 2 * idx + 2;
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
        for (var i = (hole - 1) >> 1; top < hole && less(a[first + i], val); i = (hole - 1) >> 1)
        {
            a[first + hole] = a[first + i];
            hole = i;
        }
        a[first + hole] = val;
    }

    private static void SortHeap<T>(IList<T> a, int first, int last, Func<T, T, bool> less)
    {
        for (; last - first >= 2; last--)
        {
            var end = last - 1;
            var val = a[end];
            a[end] = a[first];
            PopHeapHoleByIndex(a, first, 0, end - first, val, less);
        }
    }
}
