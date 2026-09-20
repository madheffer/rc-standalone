using System.IO.Hashing;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

public static partial class Source2ContainerAuthor
{
    /// <summary>
    /// The identity a map root declares for each kind of child it compiled.
    /// Measured across 116 shipped maps: the rows never vary, only which of them
    /// are present, and that follows what the map actually contains.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SpecialDep[]> MapChildIdentities =
        new Dictionary<string, SpecialDep[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".vents"] = [new("Entity Lump Compiler Version", "CompileEntityLump", 3)],
            [".vmdl"] = [new("ModelDoc Compiler Version", "CompileModel", 3)],
            [".vwrld"] = [new("World Compiler Version", "CompileWorld", 1)],
            [".vwnod"] = [new("World Node Compiler Version", "CompileWorldNode", 1)],
            // A texture contributes three rows. The encode-quality row's user data
            // is a SETTING rather than a version - 51 of the 116 maps carry 4 and
            // 49 carry 3 - and 3 is what this compiler's own textures record.
            [".vtex"] =
            [
                new("Texture Compiler Version", "CompileTexture", 11),
                new("Texture Compiler Version Mip None", "CompileTexture", 1),
                new("Texture Encode Quality", "CompileTexture", 1, UserData: 3),
            ],
        };

    /// <summary>
    /// Author a <c>.vmap_c</c>, the root of a compiled map.
    ///
    /// <para>It holds no map data at all: the DATA block is empty in all 116 maps
    /// surveyed. What it is, is the manifest the engine loads a map through - a RERL
    /// of every child resource, a RED2 repeating them as its child resource list,
    /// and an identity assembled from the identities of everything the compile
    /// produced.</para>
    ///
    /// <para>RC's per-option argument tail (<c>bakelighting</c> and three dozen more,
    /// varying by toolchain) is deliberately not reproduced, for the reason the
    /// texture author gives: it encodes options a compile ran under, and inventing
    /// them is fabricating metadata. Only <c>___OverrideInputData___</c> is written,
    /// which all 116 maps carry.</para>
    /// </summary>
    /// <param name="sourceFileName">Content-relative path of the <c>.vmap</c> source.</param>
    /// <param name="sourceBytes">The source's bytes; their CRC32 is recorded.</param>
    /// <param name="children">Every resource the compile produced, as uncompiled paths.</param>
    /// <param name="externalReferences">Assets the map points at without having built
    /// them, such as stock materials. They join the RERL but not the child list.</param>
    public static byte[] AuthorMapRoot(
        string sourceFileName, byte[] sourceBytes, IReadOnlyList<string> children,
        IReadOnlyList<string>? externalReferences = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        return AuthorMapRoot(sourceFileName, Crc32.HashToUInt32(sourceBytes), children, externalReferences);
    }

    /// <summary>
    /// As above, for a caller that has the source's CRC but not its bytes, which is
    /// what comparing against a shipped map's own record needs.
    /// </summary>
    public static byte[] AuthorMapRoot(
        string sourceFileName, uint sourceCrc, IReadOnlyList<string> children,
        IReadOnlyList<string>? externalReferences = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        ArgumentNullException.ThrowIfNull(children);

        var childPaths = children
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // The RERL is not the child list. It is everything the map needs to load:
        // its children PLUS the game assets it points at, which the child list
        // does not mention at all (measured: 212 references against 171 children,
        // the other 41 being stock materials).
        var references = childPaths
            .Concat(externalReferences ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        using var resource = new Resource();
        ResourceVersionProp.SetValue(resource, (ushort)1);

        var rerl = new ResourceExtRefList { Resource = resource };
        foreach (var path in references)
            rerl.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo
            {
                Id = Source2ResourceId.ForPath(path),
                Name = path,
            });
        if (rerl.ResourceRefInfoList.Count > 0)
            resource.Blocks.Add(rerl);

        var red2 = BuildMapRootEditInfo(sourceFileName, sourceCrc, childPaths);
        resource.Blocks.Add(AuthoredKv3.Block(NarrowIntegers(red2), KV3IDLookup.Get("generic"), BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, [], resource));

        return ResourceBuilder.Serialize(resource);
    }

    private static KVObject BuildMapRootEditInfo(string sourceFileName, uint sourceCrc, IReadOnlyList<string> children)
    {
        var root = KVObject.Collection();

        var inputDeps = KVObject.Array();
        inputDeps.Add(InputDependency(sourceFileName, sourceCrc, optional: false, exists: true));
        root.Add("m_InputDependencies", inputDeps);
        root.Add("m_AdditionalInputDependencies", KVObject.Array());

        var argDeps = KVObject.Array();
        var arg = KVObject.Collection();
        arg.Add("m_ParameterName", new KVObject("___OverrideInputData___"));
        arg.Add("m_ParameterType", new KVObject("BinaryBlobArg"));
        arg.Add("m_nFingerprint", new KVObject(0));
        arg.Add("m_nFingerprintDefault", new KVObject(0));
        argDeps.Add(arg);
        root.Add("m_ArgumentDependencies", argDeps);

        root.Add("m_SpecialDependencies", MapRootIdentities(children));
        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());

        var childList = KVObject.Array();
        foreach (var child in children)
            childList.Add(new KVObject(child));
        root.Add("m_ChildResourceList", childList);
        root.Add("m_WeakReferenceList", KVObject.Array());

        // WorldModelDocAll rides on every map root, and IsChildResource is 0
        // because this is the one map resource that is not a child of anything.
        var userData = KVObject.Collection();
        userData.Add("WorldModelDocAll", new KVObject(1));
        userData.Add("IsChildResource", new KVObject(0));
        root.Add("m_SearchableUserData", userData);

        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return root;
    }

    /// <summary>The union of the child identities, ordered the way RC writes them
    /// (by the dependency's name, ordinal).</summary>
    private static KVObject MapRootIdentities(IEnumerable<string> children)
    {
        var deps = new List<SpecialDep>
        {
            new("Map Compiler Version", "CompileMap", 2),
            new("Manifest Compiler Version", "CompileResourceManifest", 2),
        };
        var kinds = children
            .Select(c => Path.GetExtension(c))
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds)
            if (MapChildIdentities.TryGetValue(kind, out var rows))
                deps.AddRange(rows);

        var array = KVObject.Array();
        foreach (var dep in deps.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(dep.Name));
            d.Add("m_CompilerIdentifier", new KVObject(dep.CompilerIdentifier));
            d.Add("m_nFingerprint", new KVObject(dep.Fingerprint));
            d.Add("m_nUserData", new KVObject(dep.UserData));
            array.Add(d);
        }
        return array;
    }
}
