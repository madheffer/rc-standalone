"""Serve ReVa's MCP endpoint headless, on the persistent cs2 project.

The MCP client config expects http://localhost:8080/mcp/message. ReVa is a
Ghidra plugin, so without a running Ghidra that port is closed. This starts
Ghidra headless through pyghidra, opens the cs2 project under paths.GHIDRA_ROOT
(which locks it: stop this before running analyzeHeadless on the same
project), and blocks until killed. From the repository root, in the
background, logging into tools/re/out:

    D:/tools/reva-venv/Scripts/python -u tools/re/reva_serve.py > tools/re/out/reva_serve.log 2>&1
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402

os.environ.setdefault("GHIDRA_INSTALL_DIR", paths.GHIDRA_INSTALL)
os.environ.setdefault("JAVA_HOME", r"C:\Program Files\Java\jdk-22")

import pyghidra

from pyghidra.launcher import HeadlessPyGhidraLauncher

launcher_ = HeadlessPyGhidraLauncher(install_dir=os.environ["GHIDRA_INSTALL_DIR"])
launcher_.add_vmargs("-Xmx12G")
launcher_.start()

from java.io import File
from reva.headless import RevaHeadlessLauncher

# reva-headless.properties beside this script, when there: the decompiler timeout (default 10 s).
config = os.path.join(os.path.dirname(os.path.abspath(__file__)), "reva-headless.properties")
launcher = RevaHeadlessLauncher(File(config) if os.path.exists(config) else None, False, False,
                                File(paths.GHIDRA_PROJECTS), "cs2")
launcher.start()
print(f"ReVa ready on port {launcher.getPort()}", flush=True)

while True:
    time.sleep(3600)
