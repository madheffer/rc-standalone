"""corpus.py [addon/map ...] [--all] [--recompile]: world physics parity over every map source.

For each map (content/csgo_addons/<addon>/maps/<map>.vmap): a fresh compile with
the installed resourcecompiler (-world -phys -fshallow, through
compile_fresh.py, kept at %TEMP%/physcorpus/<addon>__<map>.vpk and reused
while newer than the toolchain DLLs), then WorldPhysicsAuthorTests (WPBUILD,
with the GPU material sampler) and EntityPhysicsModelTests (ENTBUILD) from a
copy of the test binaries. One JSON line per map goes to
%TEMP%/physcorpus/results.jsonl: the compile's exit, each block's difference
count, the entity model summary, and the first differences.

CS2 must be closed (compile_fresh.py refuses otherwise).
"""
import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEMP = os.environ.get("TEMP", ".")
OUT = os.path.join(TEMP, "physcorpus")
RUN = os.path.join(TEMP, "physcorpus_bin")
DLLS = ["resourcecompiler.dll", "vphysics2.dll", "physicsbuilder.dll", "meshsystem.dll"]


def maps():
    for vmap in sorted(glob.glob(os.path.join(CS2, "content", "csgo_addons", "*", "maps", "*.vmap"))):
        yield os.path.basename(os.path.dirname(os.path.dirname(vmap))), os.path.splitext(os.path.basename(vmap))[0]


def toolchain_time():
    return max(os.path.getmtime(os.path.join(CS2, "game", "bin", "win64", d)) for d in DLLS)


def test(env, filt):
    run_env = dict(os.environ, **env)
    p = subprocess.run(["dotnet", "test", os.path.join(RUN, "Source2.Compiler.Tests.dll"), "--filter", filt,
                        "--logger", "console;verbosity=detailed"], cwd=RUN, env=run_env, capture_output=True, text=True,
                       timeout=7200)
    return p.stdout


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("only", nargs="*")
    ap.add_argument("--recompile", action="store_true")
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    # The tests run from a copy so a build in the repo does not lock them.
    shutil.rmtree(RUN, ignore_errors=True)
    shutil.copytree(os.path.join(REPO, "tests", "Source2.Compiler.Tests", "bin", "Release", "net10.0"), RUN)
    wanted = {tuple(x.split("/")) for x in a.only}
    newest = toolchain_time()
    results = open(os.path.join(OUT, "results.jsonl"), "a", encoding="utf-8")
    for addon, name in maps():
        if wanted and (addon, name) not in wanted:
            continue
        vpk = os.path.join(OUT, f"{addon}__{name}.vpk")
        record = {"addon": addon, "map": name}
        if a.recompile or not os.path.exists(vpk) or os.path.getmtime(vpk) < newest:
            p = subprocess.run([sys.executable, os.path.join(REPO, "tools", "pipeline", "compile_fresh.py"), addon, name, "--out", vpk, "--", "-phys"],
                               capture_output=True, text=True, timeout=14400)
            record["compile"] = p.stdout.strip().splitlines()[-3:]
            if "RESTORE CMP FAILED" in p.stdout:
                print("restore failed, stopping:", addon, name)
                break
        if not os.path.exists(vpk):
            record["error"] = "no package"
        else:
            spec = f"{addon}|{name}|{vpk}"
            out = test({"WPBUILD": spec, "WPBUILD_GPU": "1", "WPBUILD_SHOW": "6"}, "FullyQualifiedName~WorldPhysicsAuthorTests.BuildsFromTheMap")
            record["world"] = {m.group(1): int(m.group(2)) for m in re.finditer(r"^ (PHYS|CTRL|RED2|DATA|manifest): (\d+) differences", out, re.M)}
            record["world_failed"] = "Failed Source2" in out
            record["world_first"] = [l.strip() for l in out.splitlines() if l.startswith("   ")][:12]
            err = re.search(r"Error Message:\s*\n\s*(.*)", out)
            if err:
                record["world_error"] = err.group(1).strip()
            out = test({"ENTBUILD": spec, "ENTBUILD_SHOW": "6"}, "FullyQualifiedName~EntityPhysicsModelTests")
            summary = re.search(r"^ (\d+ brush entity models: .*)$", out, re.M)
            record["entities"] = summary.group(1) if summary else None
            record["entities_failed"] = "Failed Source2" in out
            record["entities_first"] = [l.strip() for l in out.splitlines() if re.match(r"^ maps/.*differences", l)][:8]
            err = re.search(r"Error Message:\s*\n\s*(.*)", out)
            if err:
                record["entities_error"] = err.group(1).strip()
        results.write(json.dumps(record) + "\n")
        results.flush()
        print(addon, name, record.get("world"), "failed" if record.get("world_failed") else "", record.get("entities"))


if __name__ == "__main__":
    main()
