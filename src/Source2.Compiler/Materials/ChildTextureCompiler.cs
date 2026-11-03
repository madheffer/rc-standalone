using System.IO.Hashing;
using Source2.Compiler.RcTexture;
using ValveKeyValue;

namespace Source2.Compiler.Materials;

/// <summary>
/// A material's generated texture to its <c>.vtex_c</c>, as resourcecompiler writes the child
/// (docs/MATERIALS.md): the inputs packed channel by channel into one image, the shared texture
/// build (<see cref="VtexCompiler"/>), and the RED2 of a child. Mip algorithms and image
/// processors not measured yet are refused.
/// </summary>
internal static class ChildTextureCompiler
{
    /// <summary>Compile one child.</summary>
    /// <param name="texture">The generated texture.</param>
    /// <param name="find">An image's search path and bytes, from the first content mount that has it.</param>
    /// <param name="firstSearchPath">The first mount's search path, where RC records an absent file.</param>
    /// <param name="vendor">Whose BC7 to reproduce.</param>
    public static byte[] Compile(GeneratedTexture texture, Func<string, (string SearchPath, byte[] Bytes)?> find,
        string firstSearchPath, Bc7Vendor vendor)
    {
        RcTextureChannels.Check(texture, texture.ChildPath);

        // Every distinct image, with its .txt sibling (RC's image settings).
        var files = texture.Inputs.Select(i => i.FileName).Distinct().ToList();
        var images = new Dictionary<string, (string SearchPath, byte[] Bytes)>(StringComparer.Ordinal);
        var decoded = new Dictionary<string, (byte[] Rgba, int Width, int Height)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            images[file] = find(file) ?? throw new InvalidOperationException($"{file} (for {texture.ChildPath}) was not found.");
            var txt = EditInfo.SettingsFile(file);
            if (find(txt) is { } settings && !IsEmptySettings(settings.Bytes))
                throw new InvalidOperationException($"{txt} sets image settings, which this compiler has not measured yet.");
            decoded[file] = VtexCompiler.DecodeRgba(images[file].Bytes, file);
        }

        var (levels, srgb, maxMips) = RcTextureChannels.Build(texture, decoded, vendor, texture.ChildPath);

        var format = texture.OutputFormat;
        var first = decoded[texture.Inputs[0].FileName];
        var (data, stored, _) = VtexCompiler.BuildTexture(levels, format, srgb,
            noLod: false, maxMips, vendor, (first.Width, first.Height));
        return VtexCompiler.WriteTexture(BuildRed2(texture, files, images, find, firstSearchPath), data, stored);
    }

    // core's default_normal.txt: "settings" { }.
    private static bool IsEmptySettings(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"", "").Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return text is ["settings", "{", "}"];
    }

    // A child's RED2: no input dependencies of its own; the images and their .txt as additional
    // ones; the texture compiler's arguments with the logical CRC as the override fingerprint.
    private static KVObject BuildRed2(GeneratedTexture texture, IReadOnlyList<string> files,
        IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images,
        Func<string, (string SearchPath, byte[] Bytes)?> find, string firstSearchPath)
    {
        var root = KVObject.Collection();
        root.Add("m_InputDependencies", KVObject.Array());
        var deps = new List<EditInfo.Input>();
        foreach (var file in files)
        {
            deps.Add(EditInfo.Input.Read(file, images[file].SearchPath, images[file].Bytes));
            var txt = EditInfo.SettingsFile(file);
            deps.Add(find(txt) is { } t
                ? new EditInfo.Input(txt, t.SearchPath, System.IO.Hashing.Crc32.HashToUInt32(t.Bytes), true, true)
                : EditInfo.Input.Absent(txt, firstSearchPath));
        }
        root.Add("m_AdditionalInputDependencies", EditInfo.Inputs(EditInfo.ByPath(deps)));
        root.Add("m_ArgumentDependencies", EditInfo.Arguments(EditInfo.TextureArguments(files, texture.LogicalCrc)));

        var special = new Dictionary<string, Source2ContainerAuthor.SpecialDep>(StringComparer.OrdinalIgnoreCase);
        foreach (var dep in Source2ContainerAuthor.TextureBaseDeps.Concat(RcTextureChannels.Deps(texture)))
            special[dep.Name] = dep;
        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(special.Values));
        EditInfo.AddTail(root, EditInfo.Searchable(child: true));
        return Source2ContainerAuthor.NarrowIntegers(root);
    }

}
