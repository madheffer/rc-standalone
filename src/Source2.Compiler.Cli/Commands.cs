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
        var outPath = Opt(a, "-o") ?? input + "_c";

        // RED2 records where the source lived, content-relative. Use --ship-as
        // when you know the real path; otherwise put the file under its type's
        // canonical folder, which is what resourcecompiler would have seen.
        var shipAs = Opt(a, "--ship-as") ?? Source2ContainerAuthor.SuggestSourcePath(ext, Path.GetFileName(input));

        var bytes = Kv3SourceCompiler.Compile(File.ReadAllBytes(input), ext, shipAs);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes)");
        return 0;
    }

    public static int Decompile(string[] a)
    {
        var input = Positional(a, "input file");
        using var res = new Resource { FileName = Path.GetFileName(input) };
        res.Read(input);

        var block = Opt(a, "--block") is { } b
            ? Enum.Parse<BlockType>(b, ignoreCase: true)
            : BlockType.DATA;
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

        var bytes = ResourceBuilder.BuildPanoramaSvg(template, File.ReadAllBytes(input), Opt(a, "--ship-as"));
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

        var bytes = ResourceBuilder.BuildSound(template, File.ReadAllBytes(input), Opt(a, "--ship-as"));
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

    /// <summary>
    /// Print an entity lump one key per line, with the KV3 value type, so a diff
    /// against another compile is a text diff. The type is half the comparison: a
    /// key RC wrote as a bool and we wrote as the string "1" reads the same in a
    /// pretty-printed tree and is a different value to the engine.
    /// </summary>
    public static int VisDiff(string[] a)
    {
        var reference = Positional(a, "reference .vvis_c");
        var candidate = Require(Opt(a, "-c") ?? Opt(a, "--candidate"), "-c <candidate.vvis_c>");
        var points = int.Parse(Opt(a, "--points") ?? "1500");
        var seed = int.Parse(Opt(a, "--seed") ?? "20260920");

        var left = Maps.VoxelVisibilityReader.ReadFile(reference);
        var right = Maps.VoxelVisibilityReader.ReadFile(candidate);
        var report = Maps.VisComparison.Compare(left, right, points, seed);

        Console.WriteLine($"reference {Path.GetFileName(reference)}  "
                        + $"{left.BaseClusterCount:n0} clusters, "
                        + $"{new Maps.VoxelVisibilityQuery(left).MeanVisibleFraction() * 100:F1}% mean visible");
        Console.WriteLine($"candidate {Path.GetFileName(candidate)}  "
                        + $"{right.BaseClusterCount:n0} clusters, "
                        + $"{new Maps.VoxelVisibilityQuery(right).MeanVisibleFraction() * 100:F1}% mean visible");
        Console.WriteLine();
        Console.WriteLine($"  points      {report.PlacedPoints:n0} placed by both of {report.Attempts:n0} drawn "
                        + $"({report.ReferenceOnlyPoints:n0} reference only, {report.CandidateOnlyPoints:n0} candidate only)");
        Console.WriteLine($"  placement   {report.PlacementDisagreement * 100:F2}% disagreement on where space is");
        Console.WriteLine($"  pairs       {report.Pairs:n0}");
        Console.WriteLine($"  agreement   {report.Agreement * 100:F4}%");
        Console.WriteLine($"  HOLES       {report.Holes:n0}  ({report.HoleRate * 100:F4}% of visible) "
                        + "- geometry the player should see, culled");
        Console.WriteLine($"  overdraw    {report.Overdraw:n0}  ({report.OverdrawRate * 100:F4}% of hidden) "
                        + "- drawn needlessly, frame time only");
        return report.Holes == 0 ? 0 : 1;
    }

    public static int Entities(string[] a)
    {
        var input = Positional(a, "compiled entity lump");
        using var res = new Resource { FileName = Path.GetFileName(input) };
        res.Read(input);

        var lump = res.DataBlock as EntityLump
                   ?? throw new InvalidOperationException($"{input} is not an entity lump.");
        var root = lump.Data;
        Console.WriteLine($"lump\t{root.GetStringProperty("m_name")}");
        foreach (var child in root.GetArray<string>("m_childLumps") ?? [])
            Console.WriteLine($"childLump\t{child}");

        var index = 0;
        foreach (var entity in root.GetArray("m_entityKeyValues"))
        {
            var values = entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values");
            foreach (var kv in values?.Children ?? [])
                Console.WriteLine($"{index}\t{kv.Key}\t{kv.Value.ValueType}\t{Render(kv.Value)}");
            foreach (var c in entity.GetArray("m_connections"))
                Console.WriteLine($"{index}\t@connection\t-\t{c.GetStringProperty("m_outputName")}"
                                + $" -> {c.GetStringProperty("m_targetName")}.{c.GetStringProperty("m_inputName")}"
                                + $" ({c.GetStringProperty("m_overrideParam")})");
            index++;
        }
        Console.WriteLine($"entities\t{index}");
        return 0;

        static string Render(ValveKeyValue.KVObject v)
            => v.IsArray ? "[" + string.Join(", ", v.Values.Select(x => x.ToString())) + "]" : v.ToString() ?? "";
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
