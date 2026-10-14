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
    public static IEnumerable<Input> ByPath(IEnumerable<Input> inputs) => inputs.OrderBy(d => d.Path, StringComparer.Ordinal);

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

    /// <summary><paramref name="arguments"/> as <c>m_ArgumentDependencies</c>, sorted by name. A
    /// non-zero fingerprint is RC's uint32.</summary>
    public static KVObject Arguments(IEnumerable<Argument> arguments)
    {
        var array = KVObject.Array();
        foreach (var a in arguments.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var o = KVObject.Collection();
            o.Add("m_ParameterName", new KVObject(a.Name));
            o.Add("m_ParameterType", new KVObject(a.Type));
            o.Add("m_nFingerprint", a.Fingerprint == 0 ? new KVObject(0) : new KVObject(a.Fingerprint));
            o.Add("m_nFingerprintDefault", a.FingerprintDefault == 0 ? new KVObject(0) : new KVObject(a.FingerprintDefault));
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
    /// data as RC's struct writes them: 0 and 1 take the no-payload codes, anything else is UINT32
    /// ("Texture Encode Quality" user data 3, "Texture Compiler Version" 11, "Vector Graphic Version"
    /// 2; world.vwrld_c, n0.vwnod_c, rc_probe.vsndevts_c).
    /// </summary>
    public static KVObject SpecialDependencies(IEnumerable<Source2ContainerAuthor.SpecialDep> deps)
    {
        var array = KVObject.Array();
        foreach (var dep in deps.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
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

    private static KVObject Unsigned(int value) => value is 0 or 1 ? new KVObject((long)value) : new KVObject((uint)value);

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
