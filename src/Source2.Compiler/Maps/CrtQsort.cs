namespace Source2.Compiler.Maps;

/// <summary>
/// The Microsoft CRT's <c>qsort</c>, which tier0 exports as <c>V_qsort</c>,
/// element for element. Above eight elements it partitions around a
/// median-of-three pivot and skips runs equal to it. At eight or fewer it
/// runs a selection sort that swaps the largest element to the end, the first
/// one found. That sort is not stable: on all-equal keys it swaps the first
/// and last element every pass. Where the compile sorts equal keys, only the
/// same algorithm leaves them in the same order.
/// </summary>
internal static class CrtQsort
{
    private const int Cutoff = 8;

    public static void Sort<T>(IList<T> a, Func<T, T, int> compare)
    {
        var n = a.Count;
        if (n < 2)
            return;
        var stack = new Stack<(int Lo, int Hi)>();
        int lo = 0, hi = n - 1;
        while (true)
        {
            var size = hi - lo + 1;
            if (size <= Cutoff)
            {
                ShortSort(a, lo, hi, compare);
            }
            else
            {
                var mid = lo + (size / 2);
                if (compare(a[lo], a[mid]) > 0)
                    Swap(a, lo, mid);
                if (compare(a[lo], a[hi]) > 0)
                    Swap(a, lo, hi);
                if (compare(a[mid], a[hi]) > 0)
                    Swap(a, mid, hi);
                int loGuy = lo, hiGuy = hi;
                while (true)
                {
                    if (mid > loGuy)
                    {
                        do
                            loGuy++;
                        while (loGuy < mid && compare(a[loGuy], a[mid]) <= 0);
                    }
                    if (mid <= loGuy)
                    {
                        do
                            loGuy++;
                        while (loGuy <= hi && compare(a[loGuy], a[mid]) <= 0);
                    }
                    do
                        hiGuy--;
                    while (hiGuy > mid && compare(a[hiGuy], a[mid]) > 0);
                    if (hiGuy < loGuy)
                        break;
                    Swap(a, loGuy, hiGuy);
                    if (mid == hiGuy)
                        mid = loGuy;
                }
                hiGuy++;
                if (mid < hiGuy)
                {
                    do
                        hiGuy--;
                    while (hiGuy > mid && compare(a[hiGuy], a[mid]) == 0);
                }
                if (mid >= hiGuy)
                {
                    do
                        hiGuy--;
                    while (hiGuy > lo && compare(a[hiGuy], a[mid]) == 0);
                }
                // The smaller side is sorted first; the larger waits on the stack.
                if (hiGuy - lo >= hi - loGuy)
                {
                    if (lo < hiGuy)
                        stack.Push((lo, hiGuy));
                    if (loGuy < hi)
                    {
                        lo = loGuy;
                        continue;
                    }
                }
                else
                {
                    if (loGuy < hi)
                        stack.Push((loGuy, hi));
                    if (lo < hiGuy)
                    {
                        hi = hiGuy;
                        continue;
                    }
                }
            }
            if (stack.Count == 0)
                return;
            (lo, hi) = stack.Pop();
        }
    }

    private static void ShortSort<T>(IList<T> a, int lo, int hi, Func<T, T, int> compare)
    {
        while (hi > lo)
        {
            var max = lo;
            for (var p = lo + 1; p <= hi; p++)
            {
                if (compare(a[p], a[max]) > 0)
                    max = p;
            }
            Swap(a, max, hi);
            hi--;
        }
    }

    private static void Swap<T>(IList<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);
}
