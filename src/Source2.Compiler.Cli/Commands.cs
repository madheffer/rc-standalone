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

    /// <summary>
    /// Re-author one of Valve's compiled map resources through OUR writer and say
    /// how the result compares. This is the unit the staged pipeline swaps: a file
    /// that comes back byte-identical is proven, and one that differs is the
    /// candidate worth putting in front of the game.
    /// </summary>
    public static int Reauthor(string[] a)
    {
        var input = Positional(a, "compiled map resource");
        var outPath = Opt(a, "-o");

        var original = File.ReadAllBytes(input);
        var name = Path.GetFileName(input);
        using var valve = new Resource { FileName = name };
        valve.Read(new MemoryStream(original));

        // "world.vwrld_c" is compiled from a ".vwrld", which is what the author
        // needs to stamp the right identity on it.
        var compiled = Path.GetExtension(name);
        var sourceExtension = compiled.EndsWith("_c", StringComparison.Ordinal)
            ? compiled[..^2]
            : compiled;

        byte[] ours;
        if (sourceExtension is ".vrman")
        {
            var data = valve.Blocks.First(b => b.Type == BlockType.DATA);
            var payload = original.AsSpan((int)data.Offset, (int)data.Size).ToArray();
            ours = ResourceManifestAuthor.Author(ResourceManifestAuthor.ReadData(payload));
        }
        else if (valve.DataBlock is KeyValuesOrNTRO { Format: { } format } tree)
        {
            ours = Source2ContainerAuthor.AuthorKv3Tree(tree.Data, format, sourceExtension);
        }
        else
        {
            Console.Error.WriteLine($"cannot re-author {sourceExtension}: its DATA block is not a KV3 tree. "
                                  + "A map root needs its child list, so use the pipeline for .vmap_c.");
            return 2;
        }

        // Bytes are the wrong verdict on their own. KV3 binary has several valid
        // encodings of one tree - string table order, compression choice - so our
        // writer reproducing the TREE while differing in bytes is a pass, and it
        // is what the authoring tests assert. Say which of the two happened.
        var identical = ours.AsSpan().SequenceEqual(original);
        using var mine = new Resource { FileName = name };
        mine.Read(new MemoryStream(ours));

        // A manifest's DATA is a bare string list rather than KV3, so fall back to
        // comparing that block's bytes instead of calling it "not compared".
        var theirTree = ResourceDecompiler.DataBlockToKv3(valve, out _);
        var ourTree = ResourceDecompiler.DataBlockToKv3(mine, out _);
        var sameTree = theirTree is not null
            ? theirTree == ourTree
            : DataBytes(original, valve).Span.SequenceEqual(DataBytes(ours, mine).Span);
        var theirRefs = References(valve);
        var ourRefs = References(mine);

        Console.WriteLine($"{name}");
        Console.WriteLine($"  valve {original.Length,10:n0} bytes   ours {ours.Length,10:n0} bytes");
        Console.WriteLine($"  bytes      {(identical ? "IDENTICAL" : "differ: " + BlockDelta(name + "_c", original, ours))}");
        Console.WriteLine($"  DATA       {(sameTree ? "IDENTICAL" : "DIFFERS")}"
                        + (theirTree is null ? " (raw bytes; this type's DATA is not KV3)" : " (decoded tree)"));
        Console.WriteLine($"  references {(theirRefs.SetEquals(ourRefs) ? $"IDENTICAL ({theirRefs.Count})" : $"DIFFER ({theirRefs.Count} vs {ourRefs.Count})")}");
        Console.WriteLine($"  resver     {(valve.Version == mine.Version ? "same" : $"{valve.Version} vs {mine.Version}")}");

        var usable = sameTree && theirRefs.SetEquals(ourRefs) && valve.Version == mine.Version;
        Console.WriteLine($"  VERDICT    {(identical ? "byte-exact" : usable ? "equivalent, worth an in-game test" : "NOT EQUIVALENT")}");

        if (outPath is not null)
        {
            File.WriteAllBytes(outPath, ours);
            Console.WriteLine($"  wrote {outPath}");
        }
        return usable ? 0 : 1;
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

    public static int MapDiff(string[] a)
    {
        var referencePath = Positional(a, "reference .vpk");
        var candidatePath = Require(Opt(a, "-c") ?? Opt(a, "--candidate"), "-c <candidate.vpk>");
        var limit = int.Parse(Opt(a, "--limit") ?? "15");

        using var reference = new ValvePak.Package();
        reference.Read(referencePath);
        using var candidate = new ValvePak.Package();
        candidate.Read(candidatePath);

        var left = Entries(reference);
        var right = Entries(candidate);

        var onlyReference = left.Keys.Except(right.Keys).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var onlyCandidate = right.Keys.Except(left.Keys).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var common = left.Keys.Intersect(right.Keys).OrderBy(p => p, StringComparer.Ordinal).ToList();

        var identical = new List<string>();
        var differing = new List<(string Path, int Reference, int Candidate, string Blocks)>();
        foreach (var path in common)
        {
            var one = Io.VpkEntries.Read(reference, left[path]);
            var two = Io.VpkEntries.Read(candidate, right[path]);
            if (one.AsSpan().SequenceEqual(two))
                identical.Add(path);
            else
                differing.Add((path, one.Length, two.Length, BlockDelta(path, one, two)));
        }

        Console.WriteLine($"reference {Path.GetFileName(referencePath)}  {left.Count:n0} files");
        Console.WriteLine($"candidate {Path.GetFileName(candidatePath)}  {right.Count:n0} files");
        Console.WriteLine();
        Console.WriteLine($"  identical   {identical.Count:n0}");
        Console.WriteLine($"  differing   {differing.Count:n0}");
        Console.WriteLine($"  only in reference  {onlyReference.Count:n0}");
        Console.WriteLine($"  only in candidate  {onlyCandidate.Count:n0}");

        ByType("differing by type", differing.Select(d => d.Path));
        ByType("missing from candidate, by type", onlyReference);
        ByType("added by candidate, by type", onlyCandidate);

        if (differing.Count > 0)
        {
            Console.WriteLine($"\ndiffering files (first {Math.Min(limit, differing.Count)}):");
            foreach (var (path, one, two, blocks) in differing
                         .OrderByDescending(d => Math.Abs(d.Candidate - d.Reference)).Take(limit))
            {
                Console.WriteLine($"  {path}");
                Console.WriteLine($"      {one,12:n0} -> {two,12:n0} bytes   {blocks}");
            }
        }
        foreach (var (title, list) in new[] { ("only in reference", onlyReference), ("only in candidate", onlyCandidate) })
        {
            if (list.Count == 0)
                continue;
            Console.WriteLine($"\n{title} (first {Math.Min(limit, list.Count)}):");
            foreach (var path in list.Take(limit))
                Console.WriteLine("  " + path);
        }
        return differing.Count == 0 && onlyReference.Count == 0 && onlyCandidate.Count == 0 ? 0 : 1;

        static void ByType(string title, IEnumerable<string> paths)
        {
            var groups = paths.GroupBy(p => Path.GetExtension(p) is { Length: > 0 } e ? e : "(none)")
                              .OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0)
                return;
            Console.WriteLine($"\n{title}: "
                + string.Join("  ", groups.Select(g => $"{g.Key} {g.Count():n0}")));
        }
    }

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
