"""Drive a running CS2 over its VConsole port, and read the console back.

Launch the game with `-netconport 29000` (and `-insecure` for local testing), then
this can load maps and report exactly what the engine says about them. That turns
an in-game test from something a person has to sit and do into something a
harness can run.

    python cs2_console.py --command "map ze_hold_em_p" --wait 40
    python cs2_console.py --listen 20

The protocol, worked out against the running game because it is not documented:

    magic     4 bytes, ASCII, e.g. CMND PRNT AINF CHAN ADON CVRB
    version   u16 big endian, and it MUST be 212; anything else is refused with
              "Message Version Mismatch: 'CMND', Expected 212, Got 0"
    length    u32 big endian, counting this 10 byte header
    body      length - 10 bytes

A CMND body is two bytes of padding and then the command as a nul terminated
string. Without the padding the game parses from the third byte and reports
"Unknown command: ho" for "echo hi", which is how the offset was found.
"""
import argparse
import socket
import struct
import sys
import time

VERSION = 212
HEADER = 10


def frame(magic, body):
    return magic + struct.pack(">H", VERSION) + struct.pack(">I", HEADER + len(body)) + body


def command(text):
    """A CMND packet. The two leading bytes are what the parser skips."""
    return frame(b"CMND", b"\x00\x00" + text.encode("utf-8") + b"\x00")


def packets(buf):
    """Walk a byte stream as (magic, body), stopping at the first partial one."""
    at = 0
    while at + HEADER <= len(buf):
        magic = buf[at:at + 4]
        total = struct.unpack_from(">I", buf, at + 6)[0]
        if total < HEADER or at + total > len(buf):
            break
        yield magic, buf[at + HEADER:at + total]
        at += total


def readable(body):
    """Console text out of a PRNT body, which carries binary fields around it."""
    return "".join(chr(c) if 32 <= c < 127 else " " for c in body).strip()


class Console:
    def __init__(self, host="127.0.0.1", port=29000, timeout=5):
        self.socket = socket.create_connection((host, port), timeout=timeout)
        self.socket.settimeout(0.3)
        self.buffer = b""

    def drain(self, seconds):
        """Collect for a while and return the console lines seen."""
        lines = []
        started = time.time()
        while time.time() - started < seconds:
            try:
                chunk = self.socket.recv(262144)
                if not chunk:
                    break
                self.buffer += chunk
            except socket.timeout:
                continue
        keep = b""
        for magic, body in packets(self.buffer):
            if magic == b"PRNT":
                text = readable(body)
                if text:
                    lines.append(text)
        self.buffer = keep
        return lines

    def send(self, text):
        self.socket.sendall(command(text))

    def close(self):
        self.socket.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=29000)
    ap.add_argument("--command", action="append", default=[], help="run this, repeatable")
    ap.add_argument("--settle", type=float, default=2.0, help="seconds to drain before sending")
    ap.add_argument("--wait", type=float, default=10.0, help="seconds to collect after sending")
    ap.add_argument("--listen", type=float, default=0.0, help="just watch for this long")
    ap.add_argument("--grep", help="only print lines containing this")
    args = ap.parse_args()

    try:
        console = Console(args.host, args.port)
    except OSError as problem:
        sys.exit(f"cannot reach CS2 on {args.host}:{args.port} ({problem}). "
                 "Launch it with -netconport 29000.")

    console.drain(args.settle)
    if args.listen:
        lines = console.drain(args.listen)
    else:
        for text in args.command:
            print(f">>> {text}")
            console.send(text)
            time.sleep(0.4)
        lines = console.drain(args.wait)
    console.close()

    for line in lines:
        if args.grep and args.grep.lower() not in line.lower():
            continue
        print("   ", line[:200])
    print(f"({len(lines)} console lines)")


if __name__ == "__main__":
    main()
