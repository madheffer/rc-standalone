"""check_partorder.py <capture.json> -- does append-then-qsort explain the world part's shape order?

Reads a capture_physshapes.py capture. The world part is the part whose
mesh gathering saw the most shapes. Its inserts, in call order, go through
the part insert as resourcecompiler does it (append, then the CRT qsort by
type over the whole list); the result is compared shape for shape with the
list the gatherer saw: type, and for meshes the vertex count and first
vertex. Also prints how the part's shapes break down by type and where the
meshes sit among the other shapes in insert order.
"""
import collections
import json
import sys


def crt_qsort(a, cmp):
    """The Microsoft CRT qsort (tier0 V_qsort), element for element."""
    n = len(a)
    if n < 2:
        return

    def sw(i, j):
        a[i], a[j] = a[j], a[i]

    def shortsort(lo, hi):
        while hi > lo:
            mx = lo
            for p in range(lo + 1, hi + 1):
                if cmp(a[p], a[mx]) > 0:
                    mx = p
            sw(mx, hi)
            hi -= 1

    stack = []
    lo, hi = 0, n - 1
    while True:
        size = hi - lo + 1
        if size <= 8:
            shortsort(lo, hi)
        else:
            mid = lo + size // 2
            if cmp(a[lo], a[mid]) > 0:
                sw(lo, mid)
            if cmp(a[lo], a[hi]) > 0:
                sw(lo, hi)
            if cmp(a[mid], a[hi]) > 0:
                sw(mid, hi)
            lg, hg = lo, hi
            while True:
                if mid > lg:
                    lg += 1
                    while lg < mid and cmp(a[lg], a[mid]) <= 0:
                        lg += 1
                if mid <= lg:
                    lg += 1
                    while lg <= hi and cmp(a[lg], a[mid]) <= 0:
                        lg += 1
                hg -= 1
                while hg > mid and cmp(a[hg], a[mid]) > 0:
                    hg -= 1
                if hg < lg:
                    break
                sw(lg, hg)
                if mid == hg:
                    mid = lg
            hg += 1
            if mid < hg:
                hg -= 1
                while hg > mid and cmp(a[hg], a[mid]) == 0:
                    hg -= 1
            if mid >= hg:
                hg -= 1
                while hg > lo and cmp(a[hg], a[mid]) == 0:
                    hg -= 1
            if hg - lo >= hi - lg:
                if lo < hg:
                    stack.append((lo, hg))
                if lg < hi:
                    lo = lg
                    continue
            else:
                if lg < hi:
                    stack.append((lg, hi))
                if lo < hg:
                    hi = hg
                    continue
        if not stack:
            return
        lo, hi = stack.pop()


def key(s):
    return (s["type"], s.get("vertices"), tuple(round(v, 3) for v in s.get("v0") or []))


def main():
    calls = json.load(open(sys.argv[1], encoding="utf-8"))
    gathers = [c for c in calls if "call" in c]
    inserts = [c for c in calls if "insert" in c]
    world = max(gathers, key=lambda c: c["count"])
    # The world part is the one whose inserts number as many as its shapes, last before the gather.
    by_part = collections.defaultdict(list)
    for i in inserts:
        by_part[i["part"]].append(i)
    part = next(p for p, xs in reversed(list(by_part.items())) if len(xs) == world["count"])
    order = by_part[part]
    print(f"world part {part}: {len(order)} shapes; types in insert order:",
          dict(collections.Counter(s["type"] for s in order)))
    runs = []
    for s in order:
        if runs and runs[-1][0] == s["type"]:
            runs[-1][1] += 1
        else:
            runs.append([s["type"], 1])
    print("insert runs (type x count):", " ".join(f"{t}x{n}" for t, n in runs[:40]), "..." if len(runs) > 40 else "")

    sim = []
    for s in order:
        sim.append(s)
        crt_qsort(sim, lambda x, y: x["type"] - y["type"])
    got = world["shapes"]
    same = sum(1 for x, y in zip(sim, got) if key(x) == key(y))
    print(f"simulated vs gathered: {same} of {len(got)} agree")
    for i, (x, y) in enumerate(zip(sim, got)):
        if key(x) != key(y):
            print(f"  first difference at {i}: simulated {key(x)} gathered {key(y)}")
            break


if __name__ == "__main__":
    main()
