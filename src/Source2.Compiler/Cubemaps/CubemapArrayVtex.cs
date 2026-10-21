using System.Buffers.Binary;
using Source2.Compiler.Compression;
using Source2.Compiler.Lighting;
using Source2.Compiler.RcTexture;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using ImageFormat = Source2.Compiler.Lighting.LightmapTextures.ImageFormat;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// <c>env_cubemap_array.vtex_c</c> as resourcecompiler compiles the game's
/// <c>_buildcubemaps/env_cubemap_array.vtex</c> (docs/CUBEMAPS.md). Matched field for field against
/// de_dust2's (10-15 game files): RED2 lists per cube its EXR (CRC32) and the absent optional .txt beside
/// it, then the .vtex; the arguments are ___OverrideInputData___, an ImageArg and a BinaryBlobArg per EXR,
/// then the compile's texture arguments, all in name order ignoring case; the three texture compiler
/// special dependencies with mip algorithm GGXCubeMapBlur (2) and encode quality 3. DATA: flags 0x50
/// (cube, array), depth the cube count, BC6H, the mips down to 4 x 4, then COMPRESSED_MIP_SIZE (method,
/// the stored size of each mip from mip 0) and CUBEMAP_RADIANCE_SH (offset 8, 27 floats a cube). The
/// payload is the mips smallest first, each cube by cube and face by face.
/// Not yet compared against a compile of known inputs: the mips' lower bound, the reflectivity source
/// and the face split of the EXR (a 6 x 1 strip, as client.dll composites it).
/// </summary>
public static class CubemapArrayVtex
{
    public const ushort Flags = 0x50;
    private const uint ExtraCompressedMipSize = 4, ExtraRadianceSh = 5;
    private const int EncodeQuality = 3;

    /// <summary>One cube: the EXR's path as the .vtex names it, its bytes (for the CRC) and its six faces, RGBA.</summary>
    public sealed record Cube(string RelativePath, byte[] ExrBytes, float[][] Faces);

    /// <summary>The texture compiler's arguments RED2 records besides the images, by name: a FloatArg of a value's bits.
    /// de_dust2's compile recorded these five at 0 and maxmipsize at 1048576.</summary>
    public static readonly IReadOnlyList<(string Name, string Type, uint Fingerprint, uint Default)> DefaultArguments =
    [
        ("fast", "FloatArg", 0, 0),
        ("final", "FloatArg", 0, 0),
        ("hueShiftFixup", "FloatArg", 0, 0),
        ("lightmapMaxResolution", "FloatArg", 0, 0),
        ("maxmipsize", "FloatArg", 0x49800000, 0x49800000),
        ("minmipsize", "FloatArg", 0, 0),
    ];

    /// <summary>
    /// The array's source the game writes beside the EXRs (client.dll's template, file offset 0x1ad0c70 in
    /// the 10-15 build), filled as csgo's gameinfo sets it: CUBEARRAY, BC6H, GGXCubeMapBlur, linear. Written in
    /// text mode, so CRLF line ends (the game's own file, 2026-10-21 capture), no BOM; its CRC is the last input
    /// dependency's.
    /// </summary>
    public static string Source(IReadOnlyList<string> exrPaths)
    {
        var s = new System.Text.StringBuilder();
        s.Append("<!-- dmx encoding keyvalues2_noids 4 format vtex 1 -->\n\"CDmeVtex\"\n{\n\t\"m_inputTextureArray\" \"element_array\"\n\t[\n");
        for (var i = 0; i < exrPaths.Count; i++)
            s.Append($"\t\t\"CDmeInputTexture\"\n\t\t{{\n\t\t\t\"m_name\" \"string\" \"inputTexture{i}\"\n\t\t\t\"m_fileName\" \"string\" \"{exrPaths[i]}\"\n"
                     + "\t\t\t\"m_colorSpace\" \"string\" \"linear\"\n\t\t\t\"m_typeString\" \"string\" \"CUBE\"\n"
                     + $"\t\t\t\"m_imageProcessorArray\" \"element_array\"\n\t\t\t[\n\t\t\t]\n\t\t}}{(i + 1 < exrPaths.Count ? "," : "")}\n");
        s.Append("\t]\n\t\"m_outputTypeString\" \"string\" \"CUBEARRAY\"\n\t\"m_outputFormat\" \"string\" \"BC6H\"\n"
                 + "\t\"m_textureOutputChannelArray\" \"element_array\"\n\t[\n\t\t\"CDmeTextureOutputChannel\"\n\t\t{\n"
                 + "\t\t\t\"m_srcChannels\" \"string\" \"rgba\"\n\t\t\t\"m_dstChannels\" \"string\" \"rgba\"\n"
                 + "\t\t\t\"m_mipAlgorithm\" \"CDmeImageProcessor\"\n\t\t\t{\n\t\t\t\t\"m_algorithm\" \"string\" \"GGXCubeMapBlur\"\n\t\t\t}\n"
                 + "\t\t\t\"m_outputColorSpace\" \"string\" \"linear\"\n\t\t\t\"m_inputTextureArray\" \"string_array\"\n\t\t\t[\n");
        for (var i = 0; i < exrPaths.Count; i++)
            s.Append($"\t\t\t\t\"inputTexture{i}\"{(i + 1 < exrPaths.Count ? "," : "")}\n");
        s.Append("\t\t\t]\n\t\t}\n\t]\n\t\"m_bHiddenAssetFlag\" \"bool\" \"1\"\n}\n");
        return s.ToString().Replace("\n", "\r\n");
    }

    /// <summary>
    /// The faces of a CUBE input from its EXR's RGBA bitmap: six size x size tiles left to right (a 6 x 1
    /// strip, as client.dll composites the readback; to be confirmed by tools/re/cubemap_oracle.py).
    /// </summary>
    public static float[][] FacesFromStrip(RcMipChain.Level image)
    {
        var size = image.Height;
        if (image.Width != size * 6)
            throw new NotSupportedException($"a {image.Width} x {image.Height} cube image is not a 6 x 1 strip");
        var faces = new float[6][];
        for (var f = 0; f < 6; f++)
        {
            faces[f] = new float[size * size * 4];
            for (var y = 0; y < size; y++)
                Array.Copy(image.Rgba, (y * image.Width + f * size) * 4, faces[f], y * size * 4, size * 4);
        }
        return faces;
    }

    /// <summary>
    /// The compiled array. <paramref name="filter"/> is the GGX blur of one cube's faces
    /// (GpuCubemapBlur.Filter: [mip][face] RGBA for the full chain); the BC6H encoder comes from
    /// <paramref name="nativeFolder"/>.
    /// </summary>
    public static byte[] Build(IReadOnlyList<Cube> cubes, int size, Func<float[][], float[][][]> filter,
        string vtexRelativePath, byte[] vtexBytes, string searchPath, string nativeFolder,
        IReadOnlyList<(string Name, string Type, uint Fingerprint, uint Default)>? arguments = null)
    {
        // Mips down to 4 x 4: 7 for 256 (de_dust2), 6 for 128 (older workshop maps).
        var mipCount = 0;
        for (var s = size; s >= 4; s >>= 1)
            mipCount++;
        var levels = new float[cubes.Count][][][];
        var sh = new float[cubes.Count * CubemapRadianceSh.FloatsPerCube];
        var lastMips = new float[cubes.Count][][];
        for (var c = 0; c < cubes.Count; c++)
        {
            var faces = cubes[c].Faces;
            var filtered = filter(faces);
            lastMips[c] = filtered[^1];
            levels[c] = new float[mipCount][][];
            levels[c][0] = faces;
            for (var m = 1; m < mipCount; m++)
                levels[c][m] = filtered[m];
            var planes = faces.Select(f => (Plane(f, 0), Plane(f, 1), Plane(f, 2))).ToList();
            CubemapRadianceSh.Project(planes, size).CopyTo(sh, c * CubemapRadianceSh.FloatsPerCube);
        }

        var stored = new byte[mipCount][];
        var lz4 = false;
        for (var m = 0; m < mipCount; m++)
        {
            var side = size >> m;
            var mip = new List<byte>();
            for (var c = 0; c < cubes.Count; c++)
                for (var f = 0; f < 6; f++)
                    mip.AddRange(LightmapTextures.Encode(new RcMipChain.Level(side, side, levels[c][m][f]), ImageFormat.Bc6h, nativeFolder, EncodeQuality));
            var raw = mip.ToArray();
            stored[m] = raw;
            var packed = new byte[Lz4HcStream.CompressBound(raw.Length)];
            var n = Lz4HcStream.Compress(raw, packed, LightmapTextures.Lz4LevelFor(EncodeQuality));
            if (!(n == 0 || (long)raw.Length * LightmapTextures.CompressMinRatio <= (long)n * 100 || raw.Length - 0x8000 <= n))
            {
                stored[m] = packed[..n];
                lz4 = true;
            }
        }

        var reflectivity = Reflectivity(lastMips);
        var data = Data(size, cubes.Count, mipCount, reflectivity, [.. stored.Select(x => x.Length)], lz4, sh);

        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, 1);
        var exrs = cubes.Select(c => (c.RelativePath, System.IO.Hashing.Crc32.HashToUInt32(c.ExrBytes))).ToList();
        resource.Blocks.Add(AuthoredKv3.Block(EditInfo(exrs, vtexRelativePath, System.IO.Hashing.Crc32.HashToUInt32(vtexBytes), searchPath, arguments ?? DefaultArguments),
            KV3IDLookup.Get("generic"), BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, data, resource));
        var container = ValveLayout.Serialize(resource);
        var file = new List<byte>(container.Length + stored.Sum(x => x.Length));
        file.AddRange(container);
        for (var m = mipCount - 1; m >= 0; m--)
            file.AddRange(stored[m]);
        return [.. file];
    }

    /// <summary>
    /// The header's reflectivity (CompileResourceInternal 180133da0: per frame, 180c3bb80 on each face's last level): per cube, its faces'
    /// averages at the full chain's last mip (1 x 1, below the mips the texture keeps) added in face order
    /// and times 1 / 6; then the cubes' values added in order over the cube count. Every channel of
    /// tools/re/cubemap_oracle.py's four compiles (ledger 117).
    /// </summary>
    public static float[] Reflectivity(IReadOnlyList<float[][]> lastMips)
    {
        var result = new float[4];
        for (var c = 0; c < 4; c++)
        {
            var total = 0f;
            foreach (var faces in lastMips)
            {
                var sum = 0f;
                foreach (var face in faces)
                {
                    if (face.Length != 4)
                        throw new NotSupportedException("the reflectivity's last mip is 1 x 1 here; a larger one averages through 180c3bb80, not ported");
                    sum += face[c];
                }
                total += sum * (1f / faces.Length);
            }
            result[c] = total / lastMips.Count;
        }
        return result;
    }

    private static float[] Plane(float[] rgba, int channel)
    {
        var plane = new float[rgba.Length / 4];
        for (var i = 0; i < plane.Length; i++)
            plane[i] = rgba[i * 4 + channel];
        return plane;
    }

    // DATA: the 40-byte header, two extra entries, COMPRESSED_MIP_SIZE {method, offset 8, count} and the
    // sizes, then CUBEMAP_RADIANCE_SH {offset 8, count} and the floats (de_dust2: 40, 52, 64, 76, 104, 112).
    internal static byte[] Data(int size, int depth, int mips, float[] reflectivity, int[] storedSizes, bool lz4, float[] sh)
    {
        const int header = 40, entries = 2 * 12;
        var mipSizesAt = header + entries + 12;
        var shAt = mipSizesAt + 4 * mips;
        var d = new byte[shAt + 8 + 4 * sh.Length];
        var s = d.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], Flags);
        for (var c = 0; c < 4; c++)
            BinaryPrimitives.WriteSingleLittleEndian(s[(4 + 4 * c)..], reflectivity[c]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], (ushort)depth);
        d[26] = LightmapTextures.VtexFormat(ImageFormat.Bc6h);
        d[27] = (byte)mips;
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 2);
        // Entries {type, offset from its own offset field, size}.
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], ExtraCompressedMipSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], (uint)(header + entries - 44));
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], ExtraRadianceSh);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], (uint)(shAt - 56));
        BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 8);
        // COMPRESSED_MIP_SIZE: {method, offset to the sizes from its field, count}, then the sizes.
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], lz4 ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(s[68..], (uint)(mipSizesAt - 68));
        BinaryPrimitives.WriteUInt32LittleEndian(s[72..], (uint)mips);
        for (var m = 0; m < mips; m++)
            BinaryPrimitives.WriteUInt32LittleEndian(s[(mipSizesAt + 4 * m)..], (uint)storedSizes[m]);
        // CUBEMAP_RADIANCE_SH: {offset to the floats from its field, count}, then the floats.
        BinaryPrimitives.WriteUInt32LittleEndian(s[shAt..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(shAt + 4)..], (uint)sh.Length);
        for (var i = 0; i < sh.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(s[(shAt + 8 + 4 * i)..], sh[i]);
        return d;
    }

    private static KVObject Dependency(string path, string searchPath, uint crc, bool optional, bool exists)
    {
        var d = KVObject.Collection();
        d.Add("m_RelativeFilename", new KVObject(path));
        d.Add("m_SearchPath", new KVObject(searchPath));
        d.Add("m_nFileCRC", new KVObject(crc));
        d.Add("m_bOptional", new KVObject(optional));
        d.Add("m_bFileExists", new KVObject(exists));
        d.Add("m_bIsGameFile", new KVObject(false));
        return d;
    }

    private static KVObject Argument(string name, string type, uint fingerprint, uint fingerprintDefault)
    {
        var a = KVObject.Collection();
        a.Add("m_ParameterName", new KVObject(name));
        a.Add("m_ParameterType", new KVObject(type));
        a.Add("m_nFingerprint", new KVObject(fingerprint));
        a.Add("m_nFingerprintDefault", new KVObject(fingerprintDefault));
        return a;
    }

    /// <summary>RED2: the EXRs (path, CRC32 of the file), the .vtex and its CRC, the search path and the arguments.</summary>
    public static KVObject EditInfo(IReadOnlyList<(string Path, uint Crc)> exrs, string vtexRelativePath, uint vtexCrc, string searchPath,
        IReadOnlyList<(string Name, string Type, uint Fingerprint, uint Default)> arguments)
    {
        var inputs = KVObject.Array();
        foreach (var (path, crc) in exrs)
        {
            inputs.Add(Dependency(path, searchPath, crc, false, true));
            inputs.Add(Dependency(Path.ChangeExtension(path, ".txt"), searchPath, 0, true, false));
        }
        inputs.Add(Dependency(vtexRelativePath, searchPath, vtexCrc, false, true));

        // By name ignoring case as V_stricmp does (lowercased, so '_' sorts before letters); an image's
        // ImageArg before its BinaryBlobArg.
        var rest = new List<(string Name, KVObject Arg)>();
        foreach (var (path, _) in exrs)
        {
            rest.Add((path, Argument(path, "ImageArg", 0, 0)));
            rest.Add((path, Argument(path, "BinaryBlobArg", 0, 0)));
        }
        foreach (var (name, type, fingerprint, fingerprintDefault) in arguments)
            rest.Add((name, Argument(name, type, fingerprint, fingerprintDefault)));
        var args = KVObject.Array();
        args.Add(Argument("___OverrideInputData___", "BinaryBlobArg", 0, 0));
        foreach (var (_, arg) in rest.OrderBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal))
            args.Add(arg);

        var special = KVObject.Array();
        foreach (var (name, fingerprint, userData) in new[]
                 {
                     ("Texture Compiler Version", 11u, 0u),
                     ("Texture Compiler Version Mip GGXCubeMapBlur", 2u, 0u),
                     ("Texture Encode Quality", 1u, (uint)EncodeQuality),
                 })
        {
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(name));
            d.Add("m_CompilerIdentifier", new KVObject("CompileTexture"));
            d.Add("m_nFingerprint", new KVObject(fingerprint));
            d.Add("m_nUserData", new KVObject(userData));
            special.Add(d);
        }

        var user = KVObject.Collection();
        user.Add("HiddenAssetFlag", new KVObject(1L));
        user.Add("IsChildResource", new KVObject(0L));
        var root = KVObject.Collection();
        root.Add("m_InputDependencies", inputs);
        root.Add("m_AdditionalInputDependencies", KVObject.Array());
        root.Add("m_ArgumentDependencies", args);
        root.Add("m_SpecialDependencies", special);
        foreach (var key in new[] { "m_SpecialInputDependencies", "m_AdditionalRelatedFiles", "m_ChildResourceList", "m_WeakReferenceList" })
            root.Add(key, KVObject.Array());
        root.Add("m_SearchableUserData", user);
        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return root;
    }
}
