// tools/re/msvc_sort_reference.cpp: MSVC std::sort on tie-heavy 32-byte records (cl /O2 /EHsc /std:c++20); MsvcSortTests holds an FNV-1a 64 hash of each line
// of its output (n, distinct keys, then the ids in sorted order). Regenerate the hashes if the STL changes.
// std::sort on 32-byte records by an int key with many ties; prints the ids in sorted order.
#include <algorithm>
#include <cstdio>
#include <cstdint>
struct Rec { int count; int pad; void* p; int a; int b; int id; int key; };
int main() {
    uint32_t s = 12345;
    auto next = [&]() { s = s * 1664525u + 1013904223u; return s >> 8; };
    int sizes[] = { 5, 31, 32, 33, 41, 64, 100, 257, 1000, 5000, 20000 };
    for (int n : sizes)
        for (int keys : { 1, 3, 17, 100000 }) {
            Rec* r = new Rec[n];
            for (int i = 0; i < n; i++) { r[i] = Rec{}; r[i].id = i; r[i].key = (int)(next() % keys); if (next() % 13 == 0) r[i].key = 0x7fffffff; }
            std::sort(r, r + n, [](const Rec& x, const Rec& y) { return x.key < y.key; });
            printf("%d %d", n, keys);
            for (int i = 0; i < n; i++) printf(" %d", r[i].id);
            printf("\n");
            delete[] r;
        }
}
