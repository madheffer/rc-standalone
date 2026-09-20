"""Measure how lighting scales with sample count, on a job that already exists.

The generated script traces each block with `lightmap_compute_block_gpu X Y T`.
Measured on ze_ffvii_mako_reactor_v6_p: moving X and Y together is LINEAR at
0.344 s per sample over 8 to 512, so the pair is not an X by Y grid of X*Y samples.
X is clamped by Y (512:16 costs what 16:16 costs), and below Y it contributes
non-linearly, which is consistent with T being a convergence threshold. See
docs/VRAD3.md; the law for X below Y is not pinned down.

    python sweep_samples.py <addon> [--samples 8,16,32,64] [--full]
    python sweep_samples.py <addon> --pairs 16:16,16:128,128:128

--pairs sets X and Y separately, which is how you tell an adaptive min/max from a
plain count: if T lets the tracer stop early, 16:128 costs far less than 128:128.

By default the variant stops after the block loop: no filtering, no seam weld, no
EXR writes. That isolates the trace from ~20 seconds of fixed post-processing and
600 MB of output. --full runs the real script instead.
"""
import argparse
import os
import re
import subprocess
import sys
import time

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")

BLOCK = re.compile(r"Compute block (\d+)/(\d+) on GPU\.\.\.\s*Done \(([\d.]+) seconds\)")
TOTAL = re.compile(r"Lightmapping took ([\d.]+) seconds")


def job_dir(addon):
    return os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")


def read_script(addon):
    with open(os.path.join(job_dir(addon), "script-gpu.vrad3"), encoding="utf-8", errors="replace") as f:
        return f.read()


def variant(script, x, y, full):
    """The original script with the sample counts changed, optionally trimmed to
    the block loop so only the trace is measured."""
    out = re.sub(r"(lightmap_compute_block_gpu\s+)\d+\s+\d+", rf"\g<1>{x} {y}", script)
    if full:
        return out
    # Keep everything up to the block-loop invocation, then the loop itself, and
    # stop: the post passes and the EXR writes are fixed cost and measured already.
    head, sep, tail = out.partition("run compute_all_lightmap_blocks")
    if not sep:
        return out
    label = tail.partition("compute_all_lightmap_blocks:")[2]
    return head + sep + "\nexit\n\ncompute_all_lightmap_blocks:" + label


def run(addon, script_text, name):
    path = os.path.join(job_dir(addon), name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(script_text)

    map_arg = next((m.group(1) for m in [re.search(r"-map (\S+)", script_text)] if m), "maps/unknown.vmap")
    argv = [os.path.join(CS2, "game", "bin", "win64", "vrad3.exe"),
            "-map", map_arg, "-script", name,
            "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    started = time.time()
    done = subprocess.run(argv, cwd=job_dir(addon), capture_output=True, text=True, errors="replace")
    wall = time.time() - started

    blocks = [float(m.group(3)) for m in BLOCK.finditer(done.stdout)]
    total = next((float(m.group(1)) for m in TOTAL.finditer(done.stdout)), None)
    return blocks, total, wall, done.stdout


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("addon")
    ap.add_argument("--samples", default="8,16,32,64", help="values used for BOTH X and Y")
    ap.add_argument("--pairs", help="explicit X:Y pairs, e.g. 16:16,16:128")
    ap.add_argument("--full", action="store_true", help="run the whole script, not just the trace")
    args = ap.parse_args()

    if not os.path.exists(os.path.join(job_dir(args.addon), "script-gpu.vrad3")):
        sys.exit(f"no lighting job at {job_dir(args.addon)}")
    script = read_script(args.addon)

    baseline = re.search(r"lightmap_compute_block_gpu\s+(\d+)\s+(\d+)\s+([\d.]+)", script)
    print(f"job: {args.addon}   script says {baseline.group(0) if baseline else 'unknown'}")
    if args.pairs:
        pairs = [tuple(int(v) for v in text.split(":")) for text in args.pairs.split(",")]
    else:
        pairs = [(int(text), int(text)) for text in args.samples.split(",")]

    print(f"{'x':>6s} {'y':>6s} {'blocks':>7s} {'trace s':>9s} {'vs 16:16':>9s} {'s per x':>8s} {'wall s':>8s}")

    reference = None
    for x, y in pairs:
        blocks, total, wall, log = run(args.addon, variant(script, x, y, args.full), f"script-sweep-{x}-{y}.vrad3")
        if not blocks:
            last = log.strip().splitlines()[-1][:60] if log.strip() else "nothing"
            print(f"{x:>6d} {y:>6d}   (no block timings - vrad3 said: {last})")
            continue
        trace = sum(blocks)
        if (x, y) == (16, 16):
            reference = trace
        ratio = f"{trace / reference:.2f}x" if reference else "-"
        print(f"{x:>6d} {y:>6d} {len(blocks):>7d} {trace:9.2f} {ratio:>9s} "
              f"{trace / x:8.3f} {wall:8.1f}")


if __name__ == "__main__":
    main()
