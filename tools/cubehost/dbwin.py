"""dbwin.py <out file> -- <command...>: runs a command and keeps every OutputDebugString of the session meanwhile.

The DBWIN protocol (the shared buffer DBWIN_BUFFER and its two events), the way DebugView listens: no debugger is
attached to anything. Lines are written as "<pid>: <text>".
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import threading

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.CreateEventW.restype = wt.HANDLE
k32.CreateFileMappingW.restype = wt.HANDLE
k32.MapViewOfFile.restype = ctypes.c_void_p
k32.MapViewOfFile.argtypes = [wt.HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]


def main():
    out, cmd = sys.argv[1], sys.argv[sys.argv.index("--") + 1:]
    ready = k32.CreateEventW(None, False, True, "DBWIN_BUFFER_READY")
    data = k32.CreateEventW(None, False, False, "DBWIN_DATA_READY")
    mapping = k32.CreateFileMappingW(wt.HANDLE(-1), None, 4, 0, 4096, "DBWIN_BUFFER")
    view = k32.MapViewOfFile(mapping, 4, 0, 0, 4096)
    if not (ready and data and mapping and view):
        raise SystemExit(f"DBWIN setup failed ({ctypes.get_last_error()}); is another debug output listener running?")
    lines = []
    stop = threading.Event()

    def listen():
        while not stop.is_set():
            k32.SetEvent(ready)
            if k32.WaitForSingleObject(data, 200) != 0:
                continue
            pid = ctypes.c_uint32.from_address(view).value
            text = ctypes.string_at(view + 4).decode("utf-8", "replace")
            lines.append(f"{pid}: {text.rstrip()}")

    t = threading.Thread(target=listen, daemon=True)
    t.start()
    code = subprocess.run(cmd).returncode
    stop.set()
    t.join(1)
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"exit {code}; {len(lines)} debug lines in {out}")
    sys.exit(code)


if __name__ == "__main__":
    main()
