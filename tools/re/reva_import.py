"""reva_import.py <dll path> -- import a binary into the ReVa Ghidra project and run auto-analysis to the end.

Blocks until the analysis job finishes, so it can run under heavy.py (a big DLL such as server.dll
keeps Ghidra busy for a long time). Prints the analysis log as it comes.
"""
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402


def call(tool, args):
    r = subprocess.run([sys.executable, paths.REVA_CALL, tool, json.dumps(args)], capture_output=True, text=True, encoding="utf-8")
    try:
        return json.loads(r.stdout)
    except ValueError:
        return {"raw": r.stdout[:2000]}


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    path = os.path.abspath(sys.argv[1])
    program = "/" + os.path.basename(path)
    print("import", call("import-file", {"path": path, "analyzeAfterImport": False}), flush=True)
    job = call("analyze-program", {"programPath": program, "timeoutSeconds": -1, "waitSeconds": 5})
    job_id = job.get("jobId")
    print("analyze", {k: v for k, v in job.items() if k != "log"}, flush=True)
    cursor = 0
    while job_id and job.get("status") not in ("completed", "failed", "cancelled", "timed_out"):
        job = call("analysis-status", {"jobId": job_id, "sinceLogSeq": cursor, "waitSeconds": 60})
        for line in job.get("log", []) or []:
            print(line.get("message", line) if isinstance(line, dict) else line, flush=True)
        cursor = job.get("logCursor", cursor)
        if "raw" in job:
            time.sleep(30)
    print("done", {k: v for k, v in job.items() if k != "log"}, flush=True)


if __name__ == "__main__":
    main()
