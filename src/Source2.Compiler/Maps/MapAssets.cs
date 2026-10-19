using Source2.Compiler.Io;

namespace Source2.Compiler.Maps;

/// <summary>
/// The game assets a map refers to directly, which the .vmap_c's RERL lists beside its children
/// (180f71a50's map_asset_references, the collector's add at 180fc0a00): every material a mesh or
/// overlay names, each resource-typed entity key's value, each script key's file and each
/// bakeresource key's path, missing assets included. Sorted, lower case.
/// </summary>
public static class MapAssets
{
    public static SortedSet<string> References(DmxBinary.Document document, IReadOnlyList<MapEntities.Entity> walk, FgdSchema schema, string mapPath)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string value, string extension)
        {
            var v = value.Trim().Replace('\\', '/');
            if (v.Length == 0)
                return;
            if (extension.Length > 0 && !v.EndsWithAscii("." + extension))
                v += "." + extension;
            found.Add(Tier0Strings.LowerAscii(v));
        }
        // The materials the visible meshes' and overlays' faces use (prefab and instance contents included).
        // A mesh under a class with auto_apply_material takes that material instead (added with the entity).
        foreach (var mesh in MapMeshes.Read(document))
            if (!mesh.Hidden && !(mesh.ParentClass is { } cls && schema.MetadataOf(cls, "auto_apply_material") is { Length: > 0 }))
                foreach (var face in mesh.Faces)
                    Add(face.Material, "vmat");
        foreach (var overlay in MapMeshes.ReadOverlays(document))
            if (!overlay.Hidden && overlay.Element?.Get<DmxBinary.Element>("meshData") is { } data)
                foreach (var m in data.Get<object?[]>("materials") ?? [])
                    if (m is string name)
                        Add(name, "vmat");
        // Node settings that name assets: a path's materialName (cables) and a smart prop's definition.
        foreach (var e in WithPrefabs(document))
        {
            if (e.Type is "CMapPath" or "CMapCable" && e.Get<string>("materialName") is { Length: > 0 } pathMaterial)
                Add(pathMaterial, "vmat");
            else if (e.Type == "CMapSmartProp" && e.Get<string>("smartPropFilename") is { Length: > 0 } definition)
                Add(definition, "");
        }
        foreach (var entity in walk)
        {
            if (entity.Hidden)
                continue;
            foreach (var (key, value) in EntityLumpAuthor.KeyTable(entity, schema))
            {
                if (schema.KeyOf(entity.ClassName, key) is not { } k || value.Length == 0)
                    continue;
                if (k.TypeId == 0x20 && value.Contains('/'))
                    Add(value, k.Extension);
                else if (k.TypeId == 0x1b && value.Contains('/'))
                    Add(value, "");
                else if (k.TypeId is 0x19 or 0x1a)
                    foreach (var script in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        Add("scripts/vscripts/" + script + (Path.HasExtension(script) ? "" : ".lua"), "");
            }
            if (schema.MetadataOf(entity.ClassName, "auto_apply_material") is { Length: > 0 } applied)
                Add(applied, "vmat");
            foreach (var r in schema.BakeResourcesOf(entity.ClassName))
            {
                // The probe volume's own textures are the map's atlas (a child), not a bake resource.
                if (r.Key.StartsWithAscii("lightprobetexture"))
                    continue;
                var given = entity.Keys.FirstOrDefault(k => k.Key.EqualsAscii(r.Key)).Value;
                Add(string.IsNullOrEmpty(given) ? $"_bakeresourcecache/{mapPath}_baked/{r.Prefix}_{entity.IdPath.Replace(':', '_')}" : given, r.Extension);
            }
        }
        return found;
    }

    // The document's elements, then each loaded prefab's (MapPrefabs.Attach), depth first.
    private static IEnumerable<DmxBinary.Element> WithPrefabs(DmxBinary.Document document)
    {
        foreach (var e in document.Elements)
        {
            yield return e;
            if (e.Type == "CMapPrefab" && e.Attributes.TryGetValue(MapPrefabs.DocumentKey, out var loaded) && loaded is DmxBinary.Document inner)
                foreach (var x in WithPrefabs(inner))
                    yield return x;
        }
    }
}
