"""Serve ReVa's MCP endpoint headless, on the persistent cs2 project.

The MCP client config expects http://localhost:8080/mcp/message. ReVa is a
Ghidra plugin, so without a running Ghidra that port is closed. This starts
Ghidra headless through pyghidra, opens D:/tools/ghidra_projects/cs2 (which
locks it: stop this before running analyzeHeadless on the same project), and
blocks until killed.

    D:/tools/reva-venv/Scripts/python D:/tools/reva_serve.py
"""
import os
import time

os.environ.setdefault("GHIDRA_INSTALL_DIR", r"D:\tools\ghidra_12.1.3_PUBLIC")
os.environ.setdefault("JAVA_HOME", r"C:\Program Files\Java\jdk-22")

import pyghidra

from pyghidra.launcher import HeadlessPyGhidraLauncher

launcher_ = HeadlessPyGhidraLauncher(install_dir=os.environ["GHIDRA_INSTALL_DIR"])
launcher_.add_vmargs("-Xmx12G")
launcher_.start()

from java.io import File
from reva.headless import RevaHeadlessLauncher

launcher = RevaHeadlessLauncher(None, False, False,
                                File(r"D:\tools\ghidra_projects"), "cs2")
launcher.start()
print(f"ReVa ready on port {launcher.getPort()}", flush=True)

while True:
    time.sleep(3600)
