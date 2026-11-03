using System.IO.Hashing;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The pieces of a RED2 block (CResourceEditInfo) as resourcecompiler writes them, shared by every
/// compile that authors one: input dependencies sorted by path, argument dependencies sorted by name,
/// special dependencies sorted by name ignoring case, and the fixed tail of the tree.
/// </summary>
internal static class EditInfo
{
    /// <summary>One file a compile read or looked for.</summary>
    public readonly record struct Input(string Path, string SearchPath, uint Crc, bool Optional, bool Exists)
    {
        /// <summary>A file that was read, with the CRC32 of <paramref name="bytes"/>.</summary>
        public static Input Read(string path, string searchPath, ReadOnlySpan<byte> bytes) =>
            new(path, searchPath, Crc32.HashToUInt32(bytes), false, true);

        /// <summary>An optional sibling RC looked for and did not find.</summary>
        public static Input Absent(string path, string searchPath) => new(path, searchPath, 0, true, false);
    }

    /// <summary>One argument a compile records (<c>m_ArgumentDependencies</c>).</summary>
    public readonly record struct Argument(string Name, string Type, uint Fingerprint = 0, uint FingerprintDefault = 0);

    /// <summary><paramref name="inputs"/> sorted by path, the order RC lists a compile's files in.</summary>
    public static IEnumerable<Input> ByPath(IEnumerable<Input> inputs) => Red2Sorted(inputs, d => d.Path);

    // The RED2 finalize (rc 18016dd80..18016df5d, 20260923) sorts each list with MSVC std::sort
    // (18017a780, 18017a9c0, 18017abe0, 18017ae90, 18017b0a0, 18017a5a0) and each searchable table
    // by member name (181ebae60 -> 181ea5d30 -> 181ea6ae0); every comparison is tier0's
    // V_stricmp_fast < 0 (the comparators' import calls, 18017caac ... 18017dcc5, 181ea6c0f): A-Z
    // folded to a-z, so '_' sorts before letters and "Abc" beside "abc".
    internal static List<T> Red2Sorted<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var array = items.ToArray();
        Simulation.MsvcSort.Sort(array.AsSpan(), new StricmpLess<T>(key));
        return [.. array];
    }

    private readonly struct StricmpLess<T>(Func<T, string> key) : Simulation.ILess<T>
    {
        public bool Less(in T a, in T b) => Io.Tier0Strings.StricmpFast(key(a), key(b)) < 0;
    }

    /// <summary><paramref name="inputs"/>, in the order given, as an <c>m_InputDependencies</c>-shaped array.</summary>
    public static KVObject Inputs(IEnumerable<Input> inputs)
    {
        var array = KVObject.Array();
        foreach (var d in inputs)
        {
            var o = KVObject.Collection();
            o.Add("m_RelativeFilename", new KVObject(d.Path));
            o.Add("m_SearchPath", new KVObject(d.SearchPath));
            o.Add("m_nFileCRC", new KVObject(d.Crc));
            o.Add("m_bOptional", new KVObject(d.Optional));
            o.Add("m_bFileExists", new KVObject(d.Exists));
            o.Add("m_bIsGameFile", new KVObject(false));
            array.Add(o);
        }
        return array;
    }

    /// <summary><paramref name="arguments"/> as <c>m_ArgumentDependencies</c>, sorted by name. The
    /// fingerprints are RC's uint32s, 0 included (UINT32_ZERO since tier0 20261015).</summary>
    public static KVObject Arguments(IEnumerable<Argument> arguments)
    {
        var array = KVObject.Array();
        foreach (var a in Red2Sorted(arguments, a => a.Name))
        {
            var o = KVObject.Collection();
            o.Add("m_ParameterName", new KVObject(a.Name));
            o.Add("m_ParameterType", new KVObject(a.Type));
            o.Add("m_nFingerprint", new KVObject(a.Fingerprint));
            o.Add("m_nFingerprintDefault", new KVObject(a.FingerprintDefault));
            array.Add(o);
        }
        return array;
    }

    /// <summary>The one argument every resourcecompiler compile records.</summary>
    public static Argument OverrideInputData(uint fingerprint = 0) => new("___OverrideInputData___", "BinaryBlobArg", fingerprint);

    /// <summary>
    /// The texture compiler's arguments: the override (with <paramref name="overrideFingerprint"/>),
    /// its four float switches, <c>maxmipsize</c> at its default 1048576, <c>minmipsize</c>, and each
    /// image as an image and as a blob.
    /// </summary>
    public static IEnumerable<Argument> TextureArguments(IEnumerable<string> images, uint overrideFingerprint = 0)
    {
        var maxMip = BitConverter.SingleToUInt32Bits(1048576f);
        yield return OverrideInputData(overrideFingerprint);
        yield return new("fast", "FloatArg");
        yield return new("final", "FloatArg");
        yield return new("hueShiftFixup", "FloatArg");
        yield return new("lightmapMaxResolution", "FloatArg");
        yield return new("maxmipsize", "FloatArg", maxMip, maxMip);
        yield return new("minmipsize", "FloatArg");
        foreach (var image in images)
        {
            yield return new(image, "ImageArg");
            yield return new(image, "BinaryBlobArg");
        }
    }

    /// <summary>
    /// <paramref name="deps"/> as <c>m_SpecialDependencies</c>, sorted by name ignoring case (stock
    /// vmat_c: "csgo/csgo_character.vfx" before "Material Compiler Version"). Fingerprint and user
    /// data are RC's uint32s ("Texture Encode Quality" user data 3, "Texture Compiler Version" 11,
    /// "Vector Graphic Version" 2); a 0 or 1 takes the no-payload UINT32_ZERO / UINT32_ONE codes
    /// since tier0 20261015 (the 11-03 .vtex probes), which the KV3 writer picks for a uint.
    /// </summary>
    public static KVObject SpecialDependencies(IEnumerable<Source2ContainerAuthor.SpecialDep> deps)
    {
        var array = KVObject.Array();
        foreach (var dep in Red2Sorted(deps, d => d.Name))
        {
            var o = KVObject.Collection();
            o.Add("m_String", new KVObject(dep.Name));
            o.Add("m_CompilerIdentifier", new KVObject(dep.CompilerIdentifier));
            o.Add("m_nFingerprint", Unsigned(dep.Fingerprint));
            o.Add("m_nUserData", Unsigned(dep.UserData));
            array.Add(o);
        }
        return array;
    }

    private static KVObject Unsigned(int value) => new((uint)value);

    /// <summary>
    /// The fields after <c>m_SpecialDependencies</c>, in RC's order: no special input dependencies or
    /// related files, <paramref name="children"/> (none when null), no weak references,
    /// <paramref name="searchable"/>, and the subasset lists (null when not given).
    /// </summary>
    public static void AddTail(KVObject root, KVObject searchable, KVObject? children = null,
        KVObject? subassetReferences = null, KVObject? subassetDefinitions = null)
    {
        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());
        root.Add("m_ChildResourceList", children ?? KVObject.Array());
        root.Add("m_WeakReferenceList", KVObject.Array());
        root.Add("m_SearchableUserData", searchable);
        root.Add("m_SubassetReferences", subassetReferences ?? KVObject.Null());
        root.Add("m_SubassetDefinitions", subassetDefinitions ?? KVObject.Null());
    }

    /// <summary>The searchable user data of a resource with nothing else to say: IsChildResource.</summary>
    public static KVObject Searchable(bool child)
    {
        var o = KVObject.Collection();
        o.Add("IsChildResource", new KVObject(child ? 1 : 0));
        return o;
    }

    /// <summary>The <c>.txt</c> sibling RC reads as an image's settings.</summary>
    public static string SettingsFile(string image) => Path.ChangeExtension(image, ".txt").Replace('\\', '/');
}
