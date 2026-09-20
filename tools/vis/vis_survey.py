"""Derive the VXVS layout from every installed map, instead of trusting one file.

Two claims get tested here, both of which a writer has to get right:

  strides   every block's byte offset minus the previous block's, divided by the
            previous block's element count, must be the SAME integer in all 116
            maps - otherwise the records are not fixed width and the index alone
            does not describe the payload.

  PVS size  m_nVisBlocks should be exactly rows * m_nPVSBytesPerCluster, where
            rows is the base cluster count plus the sky and sun clusters when
            those are numbered past the base set.
"""
import collections
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import vis_index                                          # noqa: E402


def rows_expected(index):
    """The PVS matrix has one row per cluster, and sky and sun get their own row
    only when they are numbered beyond the base clusters."""
    base = index["m_nBaseClusterCount"]
    extra = {index["m_nSkyVisibilityCluster"], index["m_nSunVisibilityCluster"]}
    return base + len([c for c in extra if c >= base])


def main():
    scratch = os.path.join(os.environ.get("TEMP", "."), "vis_index_probe.vres")
    strides = collections.defaultdict(collections.Counter)
    pvs_ok = pvs_bad = 0
    span_ok = span_bad = 0
    widths = collections.Counter()
    grids = collections.Counter()
    failures = []
    total = 0

    for name, payload, vxvs_size in vis_index.maps():
        with open(scratch, "wb") as f:
            f.write(vis_index.rewrap(payload))
        try:
            index = vis_index.parse(vis_index.decompile(scratch))
        except Exception as exc:
            failures.append((name, str(exc)[:80]))
            continue
        total += 1
        blocks = index["blocks"]

        bounds = [blocks[k] for k in vis_index.ORDER]
        for i, key in enumerate(vis_index.ORDER):
            offset, count = bounds[i]
            end = bounds[i + 1][0] if i + 1 < len(bounds) else vxvs_size
            if count:
                stride = (end - offset) / count
                strides[key][stride if stride % 1 else int(stride)] += 1

        last_off, last_count = bounds[-1]
        span_total = last_off + last_count * vis_index.STRIDES[vis_index.ORDER[-1]]
        if span_total == vxvs_size:
            span_ok += 1
        else:
            span_bad += 1
            failures.append((name, f"VXVS {vxvs_size} but blocks end at {span_total}"))

        want = rows_expected(index) * index["m_nPVSBytesPerCluster"]
        if want == blocks["m_nVisBlocks"][1]:
            pvs_ok += 1
        else:
            pvs_bad += 1
            failures.append((name, f"PVS {blocks['m_nVisBlocks'][1]} but rows*bpc = {want}"))

        need = -(-index["m_nBaseClusterCount"] // 8)
        widths[(index["m_nPVSBytesPerCluster"] - need, index["m_nPVSBytesPerCluster"] % 4)] += 1
        grids[index["m_flGridSize"]] += 1

    print(f"{total} maps read\n")
    print("stride per block (value: maps)")
    for key in vis_index.ORDER:
        seen = dict(strides[key])
        flag = "OK " if len(seen) == 1 else "VARIES"
        print(f"  {flag} {key:28s} {seen}")
    print(f"\nblocks tile VXVS exactly:      {span_ok} ok, {span_bad} bad")
    print(f"PVS = rows * bytesPerCluster:  {pvs_ok} ok, {pvs_bad} bad")
    print(f"\n(bytesPerCluster - ceil(clusters/8), bytesPerCluster mod 4): {dict(widths)}")
    print(f"grid sizes: {dict(grids)}")
    if failures:
        print(f"\n{len(failures)} problem(s):")
        for name, why in failures[:12]:
            print(f"  {name}: {why}")


if __name__ == "__main__":
    main()
