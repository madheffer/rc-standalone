# Map survey probes

Read-only measurements of published CS2 maps, taken from subscribed workshop VPKs
at `<Steam>/steamapps/workshop/content/730`. They need no CS2 install and no
Workshop Tools; they parse the VPKs and the resource containers directly. The
findings they produced are written up in [`../../docs/CONTAINERS.md`](../../docs/CONTAINERS.md).

| script | what it answers |
|---|---|
| `vpk.py` | the shared reader: VPK v2 tree, lazy entry reads, a nested map VPK opened in place, and the `*_c` block table |
| `s2id.py` | `Source2ResourceId` ported to python, plus a RERL block decoder, so a corpus check does not spawn a process per file |
| `census.py` | where a map's bytes go, by role and by type, and the KV3 version and compression of every block |
| `rerl_check.py` | does our resource id function reproduce the ids real maps ship? (90,371 refs, zero mismatches) |
| `vrman_check.py` | does every resource manifest parse under the three level self-relative layout, and how many entity models are collision only |
| `red2_survey.py` | the RED2 identity each map resource type declares, via `s2c inspect`; the only one that needs the CLI built |

Run any of them with `python <script>.py` from this directory.
