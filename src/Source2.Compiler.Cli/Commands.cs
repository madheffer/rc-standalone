using Source2.Compiler.Compiling;
using System.Text;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Cli;

/// <summary>
/// One method per subcommand. Each is a thin argument-parsing shell over a
/// single call into <c>Source2.Compiler</c>, so what the CLI can do is exactly
/// what the library can do.
/// </summary>
internal static class Commands
{
    public static int Compile(string[] a)
    {
        var input = Positional(a, "input file");
        var ext = Path.GetExtension(input);

        // RED2 records where the source lived, content-relative. Use --ship-as
        // when you know the real path; otherwise put the file under its type's
        // canonical folder, which is what resourcecompiler would have seen.
        var (treeFromPath, relativeFromPath) = ContentLocation(input);
        var shipAs = Opt(a, "--ship-as") ?? relativeFromPath ?? Source2ContainerAuthor.SuggestSourcePath(ext, Path.GetFileName(input));
        var searchPath = Opt(a, "--search-path") ?? treeFromPath ?? "csgo";

        // Files the source refers to come from its own content tree, as RC reads them.
        var full = Path.GetFullPath(input).Replace('\\', '/');
        IContentSource? content = relativeFromPath is not null && full.EndsWith(relativeFromPath, StringComparison.OrdinalIgnoreCase)
            ? new DirectoryContentSource(full[..^relativeFromPath.Length])
            : null;

        var result = ResourceCompiler.CreateDefault().Compile(
            new CompileRequest(shipAs, File.ReadAllBytes(input)) { SearchPath = searchPath, Content = content });
        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine(d);
        if (!result.Succeeded || result.Output is null)
            return 1;

        var outPath = Opt(a, "-o") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileName(result.OutputPath));
        File.WriteAllBytes(outPath, result.Output);
        Console.WriteLine($"{outPath}  ({result.Output.Length:n0} bytes)");
        foreach (var child in result.Children)
        {
            var childPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, Path.GetFileName(child.Path));
            File.WriteAllBytes(childPath, child.Bytes);
            Console.WriteLine($"{childPath}  ({child.Bytes.Length:n0} bytes)");
        }
        return 0;
    }

    internal static (string? Tree, string? Relative) ContentLocation(string path)
    {
        var parts = Path.GetFullPath(path).Replace('\\', '/').Split('/');
        for (var i = parts.Length - 3; i >= 0; i--)
        {
            if (!parts[i].Equals("content", StringComparison.OrdinalIgnoreCase))
                continue;
            var treeLength = parts[i + 1].Equals("csgo_addons", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            if (i + 1 + treeLength >= parts.Length)
                return (null, null);
            return (string.Join('/', parts[(i + 1)..(i + 1 + treeLength)]),
                    string.Join('/', parts[(i + 1 + treeLength)..]).ToLowerInvariant());
        }
        return (null, null);
    }

    public static int Decompile(string[] a)
    {
        var input = Positional(a, "input file");
        var res = CompiledResource.Read(File.ReadAllBytes(input));
        var block = (Opt(a, "--block") ?? "DATA").ToUpperInvariant();
        var text = ResourceDecompiler.BlockToKv3(res, block, out var why);
        if (text is null)
        {
            Console.Error.WriteLine("cannot decompile: " + why);
            return 1;
        }

        var outPath = Opt(a, "-o");
        if (outPath is null)
            Console.Out.Write(text);
        else { File.WriteAllText(outPath, text); Console.WriteLine($"{outPath}  ({text.Length:n0} chars)"); }
        return 0;
    }

    public static int Texture(string[] a)
    {
        var input = Positional(a, "image file");
        var outPath = Require(Opt(a, "-o"), "-o <out.vtex_c>");
        // --template is optional: without one the container is authored outright.
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;

        var def = new ResourceBuilder.TextureDef
        {
            ImageBytes = File.ReadAllBytes(input),
            GenerateMipmaps = !a.Contains("--no-mips"),
            Compression = ParseFormat(Opt(a, "--format")),
            SourceName = Opt(a, "--ship-as"),
        };
        if (Opt(a, "--max-dim") is { } md)
            def.MaxDimension = int.Parse(md);

        var bytes = ResourceBuilder.BuildTexture(template, def);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes, {def.Compression}, "
                        + $"encoder {ResourceBuilder.Bc7CacheTag(def.Bc7Mode)})");
        return 0;
    }

    /// <summary>
    /// Compile an <c>.mks</c> sprite-sheet script into an animated
    /// <c>.vtex_c</c>: pack its frame images into an atlas, write the SHEET
    /// block describing the sequences, and encode the atlas.
    /// </summary>
    public static int Sheet(string[] a)
    {
        var input = Positional(a, "mks script");
        var outPath = Opt(a, "-o") ?? Path.ChangeExtension(input, ".vtex_c");
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;
        var root = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";

        var script = MksSource.Parse(File.ReadAllBytes(input));

        // Frame images resolve relative to the script, the way mksheet reads them.
        using var packed = SheetAtlas.Pack(script, name =>
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Frame image not found next to the script: {name}");
            return File.ReadAllBytes(path);
        });

        var atlas = packed.Atlas;
        var rgba = new byte[atlas.Width * atlas.Height * 4];
        atlas.GetPixelSpan()[..rgba.Length].CopyTo(rgba);

        var def = new ResourceBuilder.TextureDef
        {
            RawRgba = rgba,
            RawWidth = atlas.Width,
            RawHeight = atlas.Height,
            // A mip chain would blend neighbouring frames of the atlas into each
            // other, which is why stock sheets ship NO_LOD with a single level.
            GenerateMipmaps = false,
            Flags = ResourceBuilder.VTexFlags.NO_LOD,
            Compression = ParseFormat(Opt(a, "--format")),
            SourceName = Opt(a, "--ship-as") ?? "materials/vpkeditor/" + Path.GetFileName(input),
            SheetData = SpriteSheet.Write(packed.Sequences),
        };

        var bytes = ResourceBuilder.BuildTexture(template, def);
        File.WriteAllBytes(outPath, bytes);

        var frames = script.Sequences.Sum(s => s.Frames.Count);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes, {atlas.Width}x{atlas.Height} atlas, "
                        + $"{script.Sequences.Count} sequence(s), {frames} frame(s), {def.Compression})");
        return 0;
    }

    public static int Svg(string[] a)
    {
        var input = Positional(a, "svg file");
        var outPath = Require(Opt(a, "-o"), "-o <out.vsvg_c>");
        // --template is optional: without one the container is authored outright.
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;

        var (svgTree, svgRelative) = ContentLocation(input);
        var bytes = ResourceBuilder.BuildPanoramaSvg(template, File.ReadAllBytes(input),
            Opt(a, "--ship-as") ?? svgRelative, Opt(a, "--search-path") ?? svgTree ?? "csgo");
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes)");
        return 0;
    }

    public static int Sound(string[] a)
    {
        var input = Positional(a, "wav file");
        var outPath = Require(Opt(a, "-o"), "-o <out.vsnd_c>");
        // --template is optional: without one the container is authored outright.
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;

        var (soundTree, soundRelative) = ContentLocation(input);
        var bytes = ResourceBuilder.BuildSound(template, File.ReadAllBytes(input),
            Opt(a, "--ship-as") ?? soundRelative, Opt(a, "--search-path") ?? soundTree ?? "csgo");
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes)");
        return 0;
    }

    public static int ResourceId(string[] a)
    {
        var path = Positional(a, "resource path");
        Console.WriteLine($"{Source2ResourceId.ForPath(path):x16}  {path}");
        return 0;
    }

    /// <summary>A container's DATA block payload.</summary>
    private static ReadOnlyMemory<byte> DataBytes(byte[] bytes, Resource resource)
    {
        var block = resource.Blocks.FirstOrDefault(b => b.Type == BlockType.DATA);
        return block is null ? default : bytes.AsMemory((int)block.Offset, (int)block.Size);
    }

    /// <summary>Every resource id a container's RERL names.</summary>
    private static HashSet<ulong> References(Resource resource)
        => [.. (resource.ExternalReferences?.ResourceRefInfoList ?? []).Select(r => r.Id)];

    private static Dictionary<string, ValvePak.PackageEntry> Entries(ValvePak.Package package)
        => Io.VpkEntries.ByExtension(package)
                        .SelectMany(kv => kv.Value)
                        .ToDictionary(e => e.GetFullPath(), e => e, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which blocks of a compiled resource actually differ. A resource whose only
    /// difference is RED2 is the same asset compiled somewhere else: that block
    /// carries compile identity, and its m_nFingerprint changes with the install
    /// root alone.
    /// </summary>
    private static string BlockDelta(string path, byte[] one, byte[] two)
    {
        if (!path.EndsWith("_c", StringComparison.Ordinal))
            return "not a compiled resource";
        try
        {
            using var a = new Resource();
            a.Read(new MemoryStream(one));
            using var b = new Resource();
            b.Read(new MemoryStream(two));

            var names = a.Blocks.Select(x => x.Type).Union(b.Blocks.Select(x => x.Type)).ToList();
            var parts = new List<string>();
            foreach (var name in names)
            {
                var first = Slice(one, a, name);
                var second = Slice(two, b, name);
                if (first is null || second is null)
                    parts.Add($"{name}:only-in-{(first is null ? "candidate" : "reference")}");
                else if (!first.Value.Span.SequenceEqual(second.Value.Span))
                    parts.Add($"{name}:differs({first.Value.Length:n0}->{second.Value.Length:n0})");
            }
            return parts.Count == 0 ? "blocks identical, container framing differs" : string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return "unreadable: " + ex.Message[..Math.Min(60, ex.Message.Length)];
        }

        static ReadOnlyMemory<byte>? Slice(byte[] bytes, Resource resource, BlockType type)
        {
            var block = resource.Blocks.FirstOrDefault(x => x.Type == type);
            return block is null ? null : bytes.AsMemory((int)block.Offset, (int)block.Size);
        }
    }

    public static int Inspect(string[] a)
    {
        var input = Positional(a, "compiled resource");
        using var res = new Resource { FileName = Path.GetFileName(input) };
        res.Read(input);

        Console.WriteLine($"type           {res.ResourceType}");
        Console.WriteLine($"resver         {res.Version}");
        Console.WriteLine($"blocks         {string.Join(' ', res.Blocks.Select(b => b.Type))}");

        if (res.EditInfo is { } edit)
        {
            Console.WriteLine("RED2 special dependencies");
            foreach (var d in edit.SpecialDependencies)
                Console.WriteLine($"  {d.String,-38} {d.CompilerIdentifier,-28} fp={d.Fingerprint} user={d.UserData}");
            Console.WriteLine("RED2 input dependencies");
            foreach (var d in edit.InputDependencies)
                Console.WriteLine($"  {d.ContentRelativeFilename,-58} crc={d.FileCRC}");
        }

        var rerl = res.ExternalReferences?.ResourceRefInfoList;
        Console.WriteLine($"RERL           {rerl?.Count ?? 0} entries");
        foreach (var r in rerl ?? [])
        {
            var expected = r.Name is null ? 0 : Source2ResourceId.ForPath(r.Name);
            var agrees = r.Name is not null && expected == r.Id ? "ok" : "MISMATCH";
            Console.WriteLine($"  {r.Id:x16} {agrees,-8} {r.Name}");
        }
        return 0;
    }

    // argument plumbing

    private static ResourceBuilder.TextureCompression ParseFormat(string? f) => (f ?? "bc7").ToLowerInvariant() switch
    {
        "bc7" => ResourceBuilder.TextureCompression.BC7,
        "bc5" => ResourceBuilder.TextureCompression.BC5,
        "bc4" => ResourceBuilder.TextureCompression.BC4,
        "bc3" or "dxt5" => ResourceBuilder.TextureCompression.BC3,
        "bc1" or "dxt1" => ResourceBuilder.TextureCompression.BC1,
        "rgba" or "none" => ResourceBuilder.TextureCompression.None,
        _ => throw new ArgumentException($"unknown --format '{f}' (bc7|bc5|bc4|bc3|bc1|rgba)"),
    };

    private static string Positional(string[] a, string what)
        => a.FirstOrDefault(x => !x.StartsWith('-'))
           ?? throw new ArgumentException($"missing {what}");

    private static string? Opt(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static string Require(string? v, string what)
        => v ?? throw new ArgumentException($"missing {what}");
}
