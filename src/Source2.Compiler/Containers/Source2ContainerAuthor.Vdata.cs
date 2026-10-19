using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

public static partial class Source2ContainerAuthor
{
    /// <summary>
    /// A .vdata a map compile writes into the map's content folder and compiles as a child
    /// (the bomb damage bake's baked_bomb_damage.vdata): RED2, DATA, FLCI.
    /// <list type="bullet">
    /// <item>RED2 records the text as an additional input (its CRC, the addon's search path),
    /// the override data argument, the two CompileVData identities, and as searchable data the
    /// generic_data_type with IsChildResource 1.</item>
    /// <item>DATA is the tree, integers narrowed as text compiles them.</item>
    /// <item>FLCI is the compiler's line map: the source's content path, and per node in
    /// pre-order its file id (0), line and column, both delta-coded from the previous node.</item>
    /// </list>
    /// </summary>
    public static byte[] AuthorVdataChild(KVObject dataRoot, string text, IReadOnlyList<(int Line, int Column)> positions,
                                          SavedSource saved, string contentPath)
    {
        var spec = SpecByExtension[".vdata"];
        using var resource = new Resource();
        ResourceVersionProp.SetValue(resource, spec.ResourceVersion);

        var red2 = KVObject.Collection();
        red2.Add("m_InputDependencies", KVObject.Array());
        var additional = KVObject.Array();
        additional.Add(InputDependency(saved.RelativeFilename, saved.Crc, optional: false, exists: true, saved.SearchPath));
        red2.Add("m_AdditionalInputDependencies", additional);
        var args = KVObject.Array();
        var arg = KVObject.Collection();
        arg.Add("m_ParameterName", new KVObject("___OverrideInputData___"));
        arg.Add("m_ParameterType", new KVObject("BinaryBlobArg"));
        arg.Add("m_nFingerprint", new KVObject(0u));
        arg.Add("m_nFingerprintDefault", new KVObject(0u));
        args.Add(arg);
        red2.Add("m_ArgumentDependencies", args);
        var special = KVObject.Array();
        foreach (var dep in spec.SpecialDeps)
        {
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(dep.Name));
            d.Add("m_CompilerIdentifier", new KVObject(dep.CompilerIdentifier));
            d.Add("m_nFingerprint", new KVObject((uint)dep.Fingerprint));
            d.Add("m_nUserData", new KVObject((uint)dep.UserData));
            special.Add(d);
        }
        red2.Add("m_SpecialDependencies", special);
        red2.Add("m_SpecialInputDependencies", KVObject.Array());
        red2.Add("m_AdditionalRelatedFiles", KVObject.Array());
        red2.Add("m_ChildResourceList", KVObject.Array());
        red2.Add("m_WeakReferenceList", KVObject.Array());
        var user = KVObject.Collection();
        if (dataRoot.TryGetValue("generic_data_type", out var gdt) && gdt.ValueType == KVValueType.String)
            user.Add("generic_data_type", new KVObject((string)gdt));
        user.Add("IsChildResource", new KVObject(1L));
        red2.Add("m_SearchableUserData", user);
        red2.Add("m_SubassetReferences", KVObject.Null());
        red2.Add("m_SubassetDefinitions", KVObject.Null());
        resource.Blocks.Add(AuthoredKv3.Block(NarrowIntegers(red2), KV3IDLookup.Get("generic"), BlockType.RED2, resource));

        resource.Blocks.Add(AuthoredKv3.Block(NarrowIntegers(dataRoot), KV3IDLookup.Get("generic"), BlockType.DATA, resource));

        var flci = KVObject.Collection();
        var files = KVObject.Array();
        files.Add(new KVObject(contentPath));
        flci.Add("flc_file_list", files);
        KVObject Ints(IEnumerable<int> values)
        {
            var a = KVObject.Array();
            foreach (var v in values)
                a.Add(new KVObject(v));
            return a;
        }
        flci.Add("flc_file_ids", Ints(positions.Select(_ => 0)));
        flci.Add("flc_lines", Ints(positions.Select((p, i) => i == 0 ? p.Line : p.Line - positions[i - 1].Line)));
        flci.Add("flc_columns", Ints(positions.Select((p, i) => i == 0 ? p.Column : p.Column - positions[i - 1].Column)));
        flci.Add("flc_count", new KVObject(positions.Count));
        resource.Blocks.Add(AuthoredKv3.Block(flci, KV3IDLookup.Get("generic"), BlockType.FLCI, resource));

        return ValveLayout.Serialize(resource);
    }
}
