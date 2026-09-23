"""Call a ReVa MCP tool from the shell, for when the MCP client is not attached.

    python reva_call.py list                       # tool names
    python reva_call.py schema <tool>              # one tool's input schema
    python reva_call.py <tool> '<json args>'       # call it, print text content
"""
import json
import sys
import urllib.request

URL = "http://localhost:8080/mcp/message"
HEAD = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}


def post(body, session=None):
    h = dict(HEAD)
    if session:
        h["Mcp-Session-Id"] = session
    req = urllib.request.Request(URL, json.dumps(body).encode(), h)
    with urllib.request.urlopen(req, timeout=600) as r:
        sid = r.headers.get("Mcp-Session-Id") or session
        raw = r.read().decode("utf-8", "replace")
    if raw.lstrip().startswith("{"):
        return json.loads(raw) if raw.strip() else None, sid
    for line in raw.splitlines():   # event-stream: take the last data line
        if line.startswith("data:"):
            last = line[5:].strip()
    return (json.loads(last) if raw.strip() else None), sid


def session():
    _, sid = post({"jsonrpc": "2.0", "id": 0, "method": "initialize", "params": {
        "protocolVersion": "2025-03-26", "capabilities": {},
        "clientInfo": {"name": "reva_call", "version": "0"}}})
    post({"jsonrpc": "2.0", "method": "notifications/initialized"}, sid)
    return sid


def main():
    sid = session()
    if sys.argv[1] in ("list", "schema"):
        res, _ = post({"jsonrpc": "2.0", "id": 1, "method": "tools/list"}, sid)
        tools = res["result"]["tools"]
        if sys.argv[1] == "list":
            for t in tools:
                print(t["name"], "-", (t.get("description") or "").split("\n")[0][:110])
        else:
            t = next(t for t in tools if t["name"] == sys.argv[2])
            print(t.get("description"))
            print(json.dumps(t["inputSchema"], indent=1))
        return
    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    res, _ = post({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                   "params": {"name": sys.argv[1], "arguments": args}}, sid)
    if "error" in res:
        print("ERROR", json.dumps(res["error"]))
        sys.exit(1)
    for c in res["result"].get("content", []):
        print(c.get("text", c))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
