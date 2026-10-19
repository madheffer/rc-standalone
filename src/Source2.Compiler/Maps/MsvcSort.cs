namespace Source2.Compiler.Maps;

/// <summary>
/// MSVC's <c>std::sort</c> with a delegate comparison, for the map compile's callers.
/// The one implementation is <see cref="Simulation.MsvcSort"/> (insertion sort at or
/// below 32, the median-of-three fat partition with Tukey's ninther above 40, heap sort
/// once the depth budget is spent); MsvcSortTests checks it against MSVC's own order.
/// It is not stable, so where the compile sorts with <c>std::sort</c> and equal keys
/// carry different payloads, only the same algorithm puts them in the same order.
/// <c>SortPairs</c> in visbuilder, vrad3's std_Sort_32ByteElements (BlockGeometry's tiles)
/// and BakeLighting's chart mesh sort are instances of it.
/// </summary>
internal static class MsvcSort
{
    private readonly struct DelegateLess<T>(Func<T, T, bool> less) : Simulation.ILess<T>
    {
        public bool Less(in T a, in T b) => less(a, b);
    }

    public static void Sort<T>(T[] items, Func<T, T, bool> less)
        => Simulation.MsvcSort.Sort(items.AsSpan(), new DelegateLess<T>(less));

    public static void Sort<T>(Span<T> items, Func<T, T, bool> less)
        => Simulation.MsvcSort.Sort(items, new DelegateLess<T>(less));
}
