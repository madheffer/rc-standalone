// Holds the compile until resourcecompiler exits, for capture_settle.py --agent with
// --keep-package: the package the compile writes then comes from the same run as the capture
// (the settle's dynamic order varies run to run, GROUND_TRUTH 52).
hold();
