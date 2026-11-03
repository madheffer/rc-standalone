#!/usr/bin/env python3
"""feed_index.py <feed> [--add <incoming>] -- maintain the Source2.Compiler release feed on the box.

The feed is a directory of immutable releases plus an index:

    <feed>/releases/<version>/release.json      what tools/release.ps1 wrote (schema 1)
    <feed>/releases/<version>/<nupkg>           the package
    <feed>/releases/<version>/adapter.tar       src/Source2.Compiler.Vrf at the release commit
    <feed>/index.json                           every release, newest first

--add moves an uploaded <incoming> directory into releases/<version> after checking every file's
SHA-256 against its release.json, and refuses a version that is already published: a release
never changes once it is in the feed. The index is then rebuilt from the release.json files and
replaced atomically, so a reader never sees a half-written one. Standard library only, because
it runs with the box's python3.
"""
import hashlib, json, os, re, sys


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def version_key(v):
    core, _, pre = v.partition("-")
    return tuple(int(p) if p.isdigit() else 0 for p in core.split(".")), pre == "", pre


def add(feed, incoming):
    with open(os.path.join(incoming, "release.json"), encoding="utf-8") as fh:
        rel = json.load(fh)
    version = rel["version"]
    if not re.fullmatch(r"\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?", version):
        sys.exit(f"refusing: '{version}' is not a release version")
    for f in rel["files"]:
        path = os.path.join(incoming, f["name"])
        if os.path.basename(f["name"]) != f["name"] or not os.path.isfile(path):
            sys.exit(f"refusing: {f['name']} is missing from the upload")
        got = sha256(path)
        if got != f["sha256"]:
            sys.exit(f"refusing: {f['name']} hashes to {got}, release.json says {f['sha256']}")
    dest = os.path.join(feed, "releases", version)
    if os.path.exists(dest):
        sys.exit(f"refusing: {version} is already published; a release never changes")
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    os.rename(incoming, dest)
    print(f"published {version} -> {dest}")


def rebuild(feed):
    root = os.path.join(feed, "releases")
    entries = []
    for name in os.listdir(root) if os.path.isdir(root) else []:
        path = os.path.join(root, name, "release.json")
        if not os.path.isfile(path):
            continue
        with open(path, encoding="utf-8") as fh:
            rel = json.load(fh)
        entries.append({
            "version": rel["version"],
            "sha": rel["sha"],
            "date": rel["date"],
            "breaking": bool(rel.get("api", {}).get("breaking")),
            "path": f"releases/{rel['version']}",
        })
    entries.sort(key=lambda e: version_key(e["version"]), reverse=True)
    tmp = os.path.join(feed, ".index.json.tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        json.dump({"schema": 1, "package": "Source2.Compiler", "releases": entries}, fh, indent=2)
        fh.write("\n")
    os.replace(tmp, os.path.join(feed, "index.json"))
    print(f"index: {len(entries)} release(s), newest {entries[0]['version'] if entries else '(none)'}")


def main():
    if len(sys.argv) not in (2, 4) or (len(sys.argv) == 4 and sys.argv[2] != "--add"):
        sys.exit(__doc__.split("\n\n")[0])
    feed = sys.argv[1]
    if len(sys.argv) == 4:
        add(feed, sys.argv[3])
    rebuild(feed)


if __name__ == "__main__":
    main()
