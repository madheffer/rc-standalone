"""prefab_probe_map.py <base.vmap> <target map path> <out.vmap>: a probe map for CMapPrefab.
A copy of <base.vmap> with one CMapPrefab in its world, moved (1024 512 64) and turned (0 90 0),
referencing <target map path> (content-relative, e.g. maps/s2c_propover.vmap). The element's fields
are those Hammer writes (c2m2_fairgrounds_csgo_multi's prefab node); loadIfNested on, loadAtRuntime off."""
import os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def element(type_, fields):
    e = pm.Element(type_)
    e.fields = [list(f) for f in fields]
    return e


def main(base, target, out):
    work = tempfile.mkdtemp(prefix="prefab_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    plugs = element("DmePlugList", [("id", "elementid", "5c2cf001-0000-4000-8000-000000000001"),
                                     ("names", "string_array", []), ("dataTypes", "int_array", []),
                                     ("plugTypes", "int_array", []), ("descriptions", "string_array", [])])
    prefab = element("CMapPrefab", [
        ("id", "elementid", "5c2cf000-0000-4000-8000-000000000000"), ("name", "string", "probe prefab"),
        ("nodeID", "int", str(node)), ("referenceID", "uint64", "0x5c2cf00000000001"),
        ("children", "element_array", []), ("variableTargetKeys", "string_array", []),
        ("variableNames", "string_array", []), ("relayPlugData", "DmePlugList", plugs),
        ("connectionsData", "element_array", []), ("target", "element", ""),
        ("variableOverrideNames", "string_array", []), ("variableOverrideValues", "string_array", []),
        ("origin", "vector3", "1024 512 64"), ("angles", "qangle", "0 90 0"), ("scales", "vector3", "1 1 1"),
        ("transformLocked", "bool", "0"), ("force_hidden", "bool", "0"), ("editorOnly", "bool", "0"),
        ("customVisGroup", "string", ""), ("randomSeed", "int", "1799009846"),
        ("tintColor", "color", "255 255 255 255"), ("visexclude", "bool", "0"),
        ("targetMapPath", "string", target), ("targetName", "string", ""),
        ("fixupEntityNames", "bool", "0"), ("useTargetNameAsPrefix", "bool", "0"),
        ("loadIfNested", "bool", "1"), ("prefabRuntimeEntity", "bool", "0"), ("loadAtRuntime", "bool", "0")])
    world.get("children").append(prefab)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, "prefab node", node)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
