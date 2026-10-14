using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// Authors compiled Source 2 KV3-resource containers from scratch, with no donor
/// <c>_c</c> file involved.
///
/// <para>Every structural fact here was dumped from CS2's own
/// <c>resourcecompiler.exe</c> and <c>resourceinfo.exe</c>, and is re-derivable
/// with <c>tools/rc-oracle.ps1</c>:</para>
///
/// <code>
///   uint32 fileSize; uint16 headerVersion (=12); uint16 resourceVersion;
///   uint32 blockInfoOffset (=8); uint32 blockCount;
///   { fourCC; uint32 relOffset; uint32 size; } x blockCount;   // RERL? RED2 DATA
///   16-aligned block payloads (KV3v5 blobs)
/// </code>
///
/// <para>Blocks are built in memory and <see cref="ResourceFile.Write"/> lays them out.
/// Per-type versions and identities are in <see cref="SpecByExtension"/>; their
/// fingerprints are tools-side metadata the loader does not key on, mirrored so output
/// is indistinguishable from a stock compile. Re-run the oracle after a CS2 update.</para>
/// </summary>
public static partial class Source2ContainerAuthor
{
    /// <summary>A RED2 special dependency: a compiler identity and version the output depends on.</summary>
    /// <param name="Name">What it names, e.g. <c>"Texture Compiler Version Mip Box"</c>.</param>
    /// <param name="CompilerIdentifier">The compiler that declares it, e.g. <c>"CompileTexture"</c>.</param>
    /// <param name="Fingerprint">Its version.</param>
    /// <param name="UserData">Extra data (an encode quality, say), 0 for none.</param>
    public sealed record SpecialDep(string Name, string CompilerIdentifier, int Fingerprint, int UserData = 0);

    /// <summary>What a compiled KV3 resource of one source type carries.</summary>
    /// <param name="ResourceVersion">The container header's per-type version.</param>
    /// <param name="SpecialDeps">The compiler identities the type declares in RED2.</param>
    /// <param name="ChildResource">
    /// The resource is compiled as part of a larger one rather than from a source
    /// file of its own, which is how every piece of a map is built. Such a resource
    /// records no input dependency and no special input dependency list at all, and
    /// says so in its searchable user data. Measured across 116 shipped maps.
    /// </param>
    /// <param name="LineColumnInfo">
    /// A compile of this type from KV3 text also writes FLCI, the map from each value to its
    /// line and column in the source (<see cref="Kv3LineColumns"/>). Today's resourcecompiler
    /// does it for <c>.vdata</c>: both RcReference probes carry it. Older stock <c>.vdata_c</c>
    /// files (propdata, nav_hulls...) predate it. The <c>.vsndevts</c> and <c>.vpcf</c> probes
    /// have none.
    /// </param>
    public sealed record ContainerSpec(ushort ResourceVersion, SpecialDep[] SpecialDeps, bool ChildResource = false, bool LineColumnInfo = false);

    /// <summary>Per-source-extension container facts, dumped from resourcecompiler output.</summary>
    public static readonly IReadOnlyDictionary<string, ContainerSpec> SpecByExtension =
        new Dictionary<string, ContainerSpec>(StringComparer.OrdinalIgnoreCase)
        {
            [".vsndevts"] = new(1, [new("Sound Event Script Version", "CompileSoundEventScript", 10)]),
            [".vdata"] = new(0, [new("KV3 Compiler Version", "CompileVData", 2),
                                    new("VData Compiler Version", "CompileVData", 1)], LineColumnInfo: true),
            [".vpcf"] = new(1, [new("KV3 Compiler Version", "CompileParticle", 2),
                                    new("Particle Compiler Version", "CompileParticle", 2)]),
            // .vagrp is extinct in CS2 content (0 in pak01 + every base pack,
            // 2026-07-20 audit) so there is no RC ground truth to mirror; it
            // keeps the sound-event identity the legacy donor skeleton gave it.
            [".vagrp"] = new(1, [new("Sound Event Script Version", "CompileSoundEventScript", 10)]),
            // The index layer of a map. Every one of these is a child of a .vmap
            // compile, never compiled from its own source, and all three carry the
            // same identity in all 116 maps surveyed in docs/CONTAINERS.md.
            [".vwrld"] = new(1, [new("World Compiler Version", "CompileWorld", 1)], ChildResource: true),
            [".vwnod"] = new(1, [new("World Node Compiler Version", "CompileWorldNode", 1)], ChildResource: true),
            [".vents"] = new(1, [new("Entity Lump Compiler Version", "CompileEntityLump", 3)], ChildResource: true),
            // Not KV3: its DATA is a string table, so it is authored through
            // ResourceManifestAuthor rather than from a source document.
            [".vrman"] = new(1, [new("Manifest Compiler Version", "CompileResourceManifest", 2)], ChildResource: true),
        };

    /// <summary>Build the RC-style relative source path recorded in RED2's input
    /// dependency from an uploaded file name: the type's canonical content folder
    /// + the sanitized basename (e.g. "soundevents/my_sounds.vsndevts").</summary>
    public static string SuggestSourcePath(string sourceExtension, string? uploadedFileName)
    {
        var folder = sourceExtension.ToLowerInvariant() switch
        {
            ".vsndevts" or ".vagrp" => "soundevents",
            ".vdata" => "scripts",
            ".vpcf" => "particles",
            _ => "vpkeditor",
        };
        var baseName = Path.GetFileNameWithoutExtension(uploadedFileName ?? "");
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "compiled";
        // Keep the recorded path clean: strip anything outside [A-Za-z0-9_-].
        var safe = new string(baseName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return folder + "/" + safe.ToLowerInvariant() + sourceExtension.ToLowerInvariant();
    }

    /// <summary>
    /// Author a compiled container for the parsed KV3 source <paramref name="userDoc"/>.
    /// </summary>
    /// <param name="userDoc">The user's parsed KV3 document (its header Format GUID is stamped into DATA).</param>
    /// <param name="sourceExtension">Source extension incl. dot (".vsndevts", ".vdata", ".vpcf", ".vagrp").</param>
    /// <param name="sourceTextBytes">The raw source text - its CRC32 is recorded in RED2's input dependency, exactly as resourcecompiler records the source file CRC.</param>
    /// <param name="sourceFileName">Relative source path for RED2's input dependency (e.g. "soundevents/my_sounds.vsndevts"); a neutral name is synthesized when the caller doesn't know it.</param>
    /// <param name="searchPath">The content tree RC compiled from, recorded with each input dependency
    /// and in FLCI: <c>csgo_addons/&lt;addon&gt;</c> for an addon, <c>csgo</c> for the game's own.</param>
    public static byte[] AuthorKv3Resource(
        KVDocument userDoc,
        string sourceExtension,
        byte[] sourceTextBytes,
        string? sourceFileName = null,
        string searchPath = "csgo")
    {
        ArgumentNullException.ThrowIfNull(userDoc);
        // The DATA block is stamped with the document's own format GUID, so a
        // document parsed without one cannot be authored. Kv3SourceCompiler
        // already refuses that input, but this is public and reachable directly.
        if (userDoc.Header?.Format is null)
            throw new InvalidOperationException(
                "The parsed KV3 document has no Format header, so there is no format GUID to stamp " +
                "into DATA. The source's first line must be a " +
                "<!-- kv3 encoding:text:version{...} format:<schema>:version{...} --> comment.");

        if (!SpecByExtension.TryGetValue(sourceExtension, out var spec))
            throw new InvalidOperationException($"No container spec for '{sourceExtension}'.");

        sourceFileName ??= "vpkeditor/compiled" + sourceExtension.ToLowerInvariant();

        return Assemble(userDoc.Root, userDoc.Header.Format, spec, sourceExtension,
                        sourceFileName, sourceTextBytes, userDoc, narrowIntegers: true, searchPath: searchPath);
    }

    /// <summary>
    /// Author a KV3 resource from a tree that is ALREADY typed, taking its integer
    /// widths as given.
    ///
    /// <para>That is the difference between a map resource and every other type here.
    /// Compiling KV3 text, RC types each integer by its value, which
    /// <see cref="NarrowIntegers"/> reproduces. A map resource is not compiled from
    /// text at all: RC serializes a C++ structure, so a field's width is the
    /// STRUCT's, and a world's <c>m_nCompileTimestamp</c> ships UInt32 for a value
    /// that the by-value rule would make Int32. Text cannot express that, so a
    /// faithful map resource has to be authored from a tree.</para>
    /// </summary>
    /// <param name="dataRoot">The DATA tree, typed as it should be written.</param>
    /// <param name="format">The KV3 format GUID to stamp on DATA.</param>
    /// <param name="sourceExtension">Source extension incl. dot, e.g. ".vwrld".</param>
    /// <param name="editRed2">Changes the RED2 tree before it is written, or null.</param>
    public static byte[] AuthorKv3Tree(KVObject dataRoot, KV3ID format, string sourceExtension, Func<KVObject, KVObject>? editRed2 = null)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        if (!SpecByExtension.TryGetValue(sourceExtension, out var spec))
            throw new InvalidOperationException($"No container spec for '{sourceExtension}'.");
        if (!spec.ChildResource)
            throw new InvalidOperationException(
                $"'{sourceExtension}' is compiled from its own source text, whose integer widths "
              + "follow the value rule. Use AuthorKv3Resource.");

        return Assemble(dataRoot, format, spec, sourceExtension,
                        sourceFileName: "", sourceTextBytes: [], userDoc: null, narrowIntegers: false, editRed2);
    }

    private static byte[] Assemble(
        KVObject dataRoot, KV3ID format, ContainerSpec spec, string sourceExtension,
        string sourceFileName, byte[] sourceTextBytes, KVDocument? userDoc, bool narrowIntegers, Func<KVObject, KVObject>? editRed2 = null,
        string searchPath = "csgo")
    {
        var file = new ResourceFile { Version = spec.ResourceVersion };

        // RERL - resourcecompiler emits the block ONLY when the compiled data
        // actually references other resources (verified: a ref-free vpcf ships
        // just RED2+DATA; one with material refs ships RERL first). Every
        // distinct resource:-flagged string becomes an entry with the engine's
        // own path-hash id (MurmurHash64B - reproduces stock RERL ids exactly).
        var refs = new List<string>();
        CollectResourceRefs(dataRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase), refs);
        // A map's child resource lists them by path (probe01's n0.vwnod: the
        // aggregate models before the cluster model).
        if (spec.ChildResource)
            refs = [.. refs.OrderBy(r => r, StringComparer.Ordinal)];
        if (refs.Count > 0)
            file.Blocks.Add(new ResourceBlock("RERL", ResourceRefs.Write([.. refs.Select(r => (Source2ResourceId.ForPath(r), r))], 0)));

        // RED2 - authored for THIS compile (real source name + CRC, correct
        // compiler identity, real subasset data), as binary KV3 v5 under the
        // "generic" format GUID, as resourcecompiler writes it.
        var red2Doc = BuildRed2Document(spec, sourceExtension, sourceFileName, sourceTextBytes, userDoc, searchPath: searchPath);
        if (editRed2 != null)
            red2Doc = editRed2(red2Doc);
        file.Blocks.Add(new ResourceBlock("RED2", Kv3Binary.Write(NarrowIntegers(red2Doc), Kv3Formats.Get("generic"))));

        // DATA - the caller's tree, under its own format GUID.
        file.Blocks.Add(new ResourceBlock("DATA", Kv3Binary.Write(narrowIntegers ? NarrowIntegers(dataRoot) : dataRoot, format)));

        // FLCI, the source line map, after DATA (Kv3LineColumns). Skipped when
        // the scanner cannot account for every value, rather than point at
        // the wrong lines.
        if (userDoc is not null && spec.LineColumnInfo
            && Kv3LineColumns.Author(System.Text.Encoding.UTF8.GetString(sourceTextBytes),
                                     $"content/{searchPath}/{sourceFileName}", Kv3LineColumns.CountValues(dataRoot)) is { } flci)
            file.Blocks.Add(new ResourceBlock("FLCI", Kv3Binary.Write(flci, Kv3Formats.Get("generic"))));

        // resourcecompiler's layout: blocks padded with zeros, RERL's entry
        // array on an 8-byte boundary of the file.
        return file.Write();
    }

    /// <summary>
    /// Author a container whose DATA block is not KV3, for a resource compiled as
    /// part of a larger one. Today that is the resource manifest, whose payload is
    /// a string table rather than a tree.
    /// </summary>
    /// <param name="sourceExtension">Source extension incl. dot, e.g. ".vrman".</param>
    /// <param name="dataBytes">The DATA payload, already laid out.</param>
    /// <param name="references">Paths the resource points at, in the order it states them.</param>
    /// <param name="savedSource">The source file RC wrote to disk and compiled this from, when it did
    /// (a physics or world node manifest); RC records it as an additional input.</param>
    /// <param name="overrideFingerprint">RED2's ___OverrideInputData___ fingerprint, the CRC32 of the input RC recorded for it; 0 when it recorded none.</param>
    public static byte[] AuthorChildResource(string sourceExtension, byte[] dataBytes, IEnumerable<string> references,
        SavedSource? savedSource = null, uint overrideFingerprint = 0)
    {
        ArgumentNullException.ThrowIfNull(dataBytes);
        ArgumentNullException.ThrowIfNull(references);

        if (!SpecByExtension.TryGetValue(sourceExtension, out var spec))
            throw new InvalidOperationException($"No container spec for '{sourceExtension}'.");
        if (!spec.ChildResource)
            throw new InvalidOperationException(
                $"'{sourceExtension}' is compiled from its own source, so it records that source. "
              + "Use AuthorKv3Resource.");

        var file = new ResourceFile { Version = spec.ResourceVersion };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = references.Where(path => !string.IsNullOrWhiteSpace(path) && seen.Add(path))
            .Select(path => (Source2ResourceId.ForPath(path), path)).ToList();
        if (refs.Count > 0)
            file.Blocks.Add(new ResourceBlock("RERL", ResourceRefs.Write(refs, 0)));

        var red2Doc = BuildRed2Document(spec, sourceExtension, sourceFileName: "", sourceTextBytes: [], userDoc: null, savedSource, overrideFingerprint);
        file.Blocks.Add(new ResourceBlock("RED2", Kv3Binary.Write(NarrowIntegers(red2Doc), Kv3Formats.Get("generic"))));
        file.Blocks.Add(new ResourceBlock("DATA", dataBytes));

        return file.Write();
    }

    /// <summary>Build the RED2 CResourceEditInfo document resourcecompiler would
    /// have written for this compile (field set + shapes verified per type).</summary>
    private static KVObject BuildRed2Document(
        ContainerSpec spec, string sourceExtension, string sourceFileName, byte[] sourceTextBytes, KVDocument? userDoc,
        SavedSource? savedSource = null, uint overrideFingerprint = 0, string searchPath = "csgo")
    {
        var root = KVObject.Collection();

        // m_InputDependencies[0] = the source file itself, CRC32 of its bytes -
        // byte-verified against resourcecompiler output (plain zlib CRC32). A
        // child resource has no source file of its own and records none.
        var inputs = new List<EditInfo.Input>();
        if (!spec.ChildResource)
            inputs.Add(EditInfo.Input.Read(sourceFileName, searchPath, sourceTextBytes));

        // vsndevts: RC also records optional source-audio probes (.mp3/.vsnd/.wav
        // per referenced sound) so its incremental rebuild triggers when a WAV
        // appears. Mirrored for fidelity; the engine never reads these.
        if (userDoc is not null && sourceExtension.Equals(".vsndevts", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var vsnd in Strings(userDoc.Root, s => s.Length > 5 && s.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase)))
            {
                var baseName = vsnd[..^".vsnd".Length];
                foreach (var probeExt in new[] { ".mp3", ".vsnd", ".wav" })
                    inputs.Add(EditInfo.Input.Absent(baseName + probeExt, searchPath));
            }
        }

        root.Add("m_InputDependencies", EditInfo.Inputs(inputs));
        root.Add("m_AdditionalInputDependencies", EditInfo.Inputs(savedSource is { } saved
            ? [new EditInfo.Input(saved.RelativeFilename, saved.SearchPath, saved.Crc, false, true)]
            : []));

        // The one argument dependency every RC-compiled KV3 resource carries. A child
        // resource's RED2 is RC's struct written out, so its fields keep their widths:
        // 64-bit fingerprints and user data, a 32-bit unsigned compiler fingerprint
        // (world_physics.vrman_c, decoded).
        if (spec.ChildResource)
        {
            var arg = KVObject.Collection();
            arg.Add("m_ParameterName", new KVObject("___OverrideInputData___"));
            arg.Add("m_ParameterType", new KVObject("BinaryBlobArg"));
            arg.Add("m_nFingerprint", overrideFingerprint == 0 ? new KVObject(0L) : new KVObject(overrideFingerprint));
            arg.Add("m_nFingerprintDefault", new KVObject(0L));
            var args = KVObject.Array();
            args.Add(arg);
            root.Add("m_ArgumentDependencies", args);
        }
        else
            root.Add("m_ArgumentDependencies", EditInfo.Arguments([EditInfo.OverrideInputData()]));

        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(spec.SpecialDeps));

        // The 0923 compiler writes m_SpecialInputDependencies empty on child resources too
        // (atixref, ze_hold_em_p); workshop maps from older compilers omit it.
        if (spec.ChildResource)
        {
            var userData = KVObject.Collection();
            userData.Add("IsChildResource", new KVObject(1L));
            EditInfo.AddTail(root, userData);
            return root;
        }

        ArgumentNullException.ThrowIfNull(userDoc);
        var (subRefs, subDefs) = BuildSubassets(sourceExtension, userDoc);
        EditInfo.AddTail(root, BuildSearchableUserData(sourceExtension, userDoc), subassetReferences: subRefs, subassetDefinitions: subDefs);
        return root;
    }

    /// <summary>
    /// Author the RED2 for a binary resource compiled here (<c>.vtex_c</c>,
    /// <c>.vsnd_c</c>, <c>.vsvg_c</c>), describing this compile rather than
    /// whatever a donor happened to say.
    ///
    /// <para><b>Only provenance is replaced.</b> A template's
    /// <c>m_SpecialDependencies</c> ride along verbatim, because they describe how
    /// the DATA is ENCODED, not who wrote the source. A stock glove normal carries
    /// <c>Mip HemiOctAnisoRoughness</c>, and dropping it makes every consumer
    /// decode that normal as plain RGB with roughness read as Z. The per-type
    /// tables are only the fallback for a template carrying none.</para>
    ///
    /// <para>RC's per-option argument-dependency tail is deliberately NOT reproduced:
    /// it encodes options from a source file an in-memory composite never had, so
    /// inventing values would be fabricating metadata.</para>
    /// </summary>
    /// <param name="sourceName">Content-relative source path, or null for a neutral one.</param>
    /// <param name="sourceBytes">The bytes actually encoded; their CRC32 is recorded.</param>
    /// <param name="fallbackSpecialDeps">Used only when the template carries none.</param>
    /// <param name="templateSpecialDeps">Preserved verbatim.</param>
    /// <param name="optionalProbes">Sibling paths RC records as absent, so its incremental
    /// rebuild retriggers if one appears. Each is a statement that the file does not exist.</param>
    /// <param name="searchPath">The content tree RC compiled from (<c>csgo</c>, or <c>csgo_addons/&lt;addon&gt;</c>), recorded with each input dependency.</param>
    public static KVObject BuildBinaryEditInfo(
        string? sourceName,
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<SpecialDep> fallbackSpecialDeps,
        IReadOnlyList<SpecialDep>? templateSpecialDeps = null,
        IReadOnlyList<string>? optionalProbes = null,
        string searchPath = "csgo")
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? "vpkeditor/compiled" : sourceName!;

        var root = KVObject.Collection();

        // RC lists dependencies sorted by path; the real source sits among the
        // optional probes rather than first (verified on the vsnd probe, where
        // the real .wav lands last alphabetically).
        var inputs = new List<EditInfo.Input> { EditInfo.Input.Read(name, searchPath, sourceBytes) };
        foreach (var probe in optionalProbes ?? [])
            if (!string.Equals(probe, name, StringComparison.OrdinalIgnoreCase))
                inputs.Add(EditInfo.Input.Absent(probe, searchPath));
        root.Add("m_InputDependencies", EditInfo.Inputs(EditInfo.ByPath(inputs)));
        root.Add("m_AdditionalInputDependencies", KVObject.Array());
        root.Add("m_ArgumentDependencies", EditInfo.Arguments([EditInfo.OverrideInputData()]));
        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(
            templateSpecialDeps is { Count: > 0 } ? templateSpecialDeps : fallbackSpecialDeps));
        EditInfo.AddTail(root, EditInfo.Searchable(child: false));
        return NarrowIntegers(root);
    }

    /// <summary>The RED2 for a texture built from an image (<see cref="BuildBinaryEditInfo"/> with
    /// the texture defaults: a neutral source name and <see cref="TextureSpecialDeps"/>).</summary>
    /// <param name="sourceName">Content-relative source path, or null for a neutral one.</param>
    /// <param name="sourceBytes">The bytes actually encoded; their CRC32 is recorded.</param>
    /// <param name="templateSpecialDeps">Preserved verbatim.</param>
    /// <param name="searchPath">The content tree RC compiled from (<c>csgo</c>, or <c>csgo_addons/&lt;addon&gt;</c>), recorded with each input dependency.</param>
    public static KVObject BuildTextureEditInfo(
        string? sourceName, ReadOnlySpan<byte> sourceBytes, IReadOnlyList<SpecialDep>? templateSpecialDeps = null,
        string searchPath = "csgo")
        => BuildBinaryEditInfo(
            string.IsNullOrWhiteSpace(sourceName) ? "vpkeditor/texture.png" : sourceName,
            sourceBytes, TextureSpecialDeps, templateSpecialDeps, searchPath: searchPath);

    /// <summary>Author the RED2 for a compiled sound. RC probes for a fixed set of
    /// sibling files next to the source; mirrored because each entry is an honest
    /// "this file does not exist" (CRC 0, optional, not-exists).</summary>
    /// <param name="sourceName">Content-relative source path, or null for a neutral one.</param>
    /// <param name="sourceBytes">The bytes actually encoded; their CRC32 is recorded.</param>
    /// <param name="templateSpecialDeps">Preserved verbatim; <see cref="SoundDeps"/> when there are none.</param>
    /// <param name="searchPath">The content tree RC compiled from, recorded with each input dependency.</param>
    public static KVObject BuildSoundEditInfo(
        string? sourceName, ReadOnlySpan<byte> sourceBytes, IReadOnlyList<SpecialDep>? templateSpecialDeps = null,
        string searchPath = "csgo")
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? "vpkeditor/audio.wav" : sourceName!;
        return BuildBinaryEditInfo(name, sourceBytes, SoundDeps, templateSpecialDeps,
            SoundProbeSiblings(name), searchPath);
    }

    /// <summary>Author the RED2 for a compiled Panorama vector graphic. RC records
    /// the <c>.svg</c> source plus an optional same-named <c>.vsvg</c>.</summary>
    /// <param name="sourceName">Content-relative source path, or null for a neutral one.</param>
    /// <param name="sourceBytes">The bytes actually encoded; their CRC32 is recorded.</param>
    /// <param name="templateSpecialDeps">Preserved verbatim; <see cref="VectorGraphicDeps"/> when there are none.</param>
    /// <param name="searchPath">The content tree RC compiled from, recorded with each input dependency.</param>
    public static KVObject BuildVectorGraphicEditInfo(
        string? sourceName, ReadOnlySpan<byte> sourceBytes, IReadOnlyList<SpecialDep>? templateSpecialDeps = null,
        string searchPath = "csgo")
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? "panorama/images/vpkeditor/icon.svg" : sourceName!;
        return BuildBinaryEditInfo(name, sourceBytes, VectorGraphicDeps, templateSpecialDeps,
            [StripExt(name) + ".vsvg"], searchPath);
    }

    /// <summary>The sibling probes resourcecompiler records for a sound compile
    /// (dumped 2026-08-10 from a real <c>.wav</c> compile, and again 2026-10-12 from
    /// the 1.41.9.0 build, which added <c>.dmx</c>): a per-tree and per-folder
    /// <c>encoding.txt</c>, plus same-named <c>.dmx / .fbx / .mp3 / .rts / .txt /
    /// .vsnd</c>.</summary>
    private static string[] SoundProbeSiblings(string sourceName)
    {
        var noExt = StripExt(sourceName);
        var dir = noExt.LastIndexOf('/') is var i && i > 0 ? noExt[..i] : "sounds";
        var top = dir.IndexOf('/') is var j && j > 0 ? dir[..j] : dir;
        return
        [
            top + "/encoding.txt",
            dir + "/encoding.txt",
            noExt + ".dmx",
            noExt + ".fbx",
            noExt + ".mp3",
            noExt + ".rts",
            noExt + ".txt",
            noExt + ".vsnd",
        ];
    }

    private static string StripExt(string path)
        => path.LastIndexOf('.') is var i && i > path.LastIndexOf('/') && i > 0 ? path[..i] : path;

    /// <summary>
    /// The special dependencies a RED2 tree lists (<c>m_SpecialDependencies</c>), in order; none for null.
    /// </summary>
    /// <exception cref="InvalidDataException">An entry lacks its name, its compiler or a numeric fingerprint.</exception>
    public static IEnumerable<SpecialDep> ReadSpecialDeps(KVObject? red2)
    {
        if (red2 is null || !red2.TryGetValue("m_SpecialDependencies", out var list) || !list.IsArray)
            yield break;
        foreach (var d in list.Values)
            yield return new SpecialDep(
                d.GetStringProperty("m_String") ?? throw new InvalidDataException("A RED2 special dependency has no m_String."),
                d.GetStringProperty("m_CompilerIdentifier") ?? throw new InvalidDataException("A RED2 special dependency has no m_CompilerIdentifier."),
                (int)Number(d, "m_nFingerprint"), (int)Number(d, "m_nUserData"));

        static long Number(KVObject o, string key) =>
            o.TryGetValue(key, out var v) && v.ValueType is KVValueType.Boolean or KVValueType.Int16 or KVValueType.UInt16
                or KVValueType.Int32 or KVValueType.UInt32 or KVValueType.Int64 or KVValueType.UInt64
                ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
                : throw new InvalidDataException($"A RED2 special dependency has no numeric {key}.");
    }

    /// <summary>
    /// The RED2 of a <c>.vtex</c> compile, as resourcecompiler writes it (all ten RcReference
    /// textures): input dependencies for the <c>.vtex</c>, each image and each image's absent
    /// <c>.txt</c> sibling, sorted by path; the texture compiler's argument dependencies (the
    /// four float switches, each image as an image and a blob, <c>maxmipsize</c> at its default
    /// 1048576 and <c>minmipsize</c>), sorted by name.
    /// </summary>
    /// <param name="vtexPath">The <c>.vtex</c>, content-relative.</param>
    /// <param name="vtexBytes">Its bytes; their CRC32 is recorded.</param>
    /// <param name="images">Each input image, content-relative, with its bytes.</param>
    /// <param name="specialDeps">The special dependencies, sorted here by name.</param>
    /// <param name="searchPath">The content tree RC compiled from, recorded with each input dependency.</param>
    public static KVObject BuildTextureSourceEditInfo(string vtexPath, ReadOnlySpan<byte> vtexBytes,
        IReadOnlyList<(string Path, byte[] Bytes)> images, IReadOnlyList<SpecialDep> specialDeps, string searchPath = "csgo")
    {
        var root = KVObject.Collection();
        var inputs = new List<EditInfo.Input> { EditInfo.Input.Read(vtexPath, searchPath, vtexBytes) };
        foreach (var (path, bytes) in images)
        {
            inputs.Add(EditInfo.Input.Read(path, searchPath, bytes));
            inputs.Add(EditInfo.Input.Absent(EditInfo.SettingsFile(path), searchPath));
        }
        root.Add("m_InputDependencies", EditInfo.Inputs(EditInfo.ByPath(inputs)));
        root.Add("m_AdditionalInputDependencies", KVObject.Array());
        root.Add("m_ArgumentDependencies", EditInfo.Arguments(EditInfo.TextureArguments(images.Select(i => i.Path))));
        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(specialDeps));
        EditInfo.AddTail(root, EditInfo.Searchable(child: false));
        return NarrowIntegers(root);
    }

    /// <summary>m_SearchableUserData - the asset-browser metadata resourcecompiler
    /// derives from the source tree, per type (dump-verified shapes).</summary>
    private static KVObject BuildSearchableUserData(string sourceExtension, KVDocument userDoc)
    {
        var data = KVObject.Collection();
        if (sourceExtension.Equals(".vdata", StringComparison.OrdinalIgnoreCase))
        {
            // RC lifts the vdata's generic_data_type ahead of IsChildResource.
            if (userDoc.Root.IsCollection
                && userDoc.Root.TryGetValue("generic_data_type", out var gdt)
                && gdt.ValueType == KVValueType.String)
                data.Add("generic_data_type", new KVObject((string)gdt));
            data.Add("IsChildResource", new KVObject(0));
            return data;
        }

        data.Add("IsChildResource", new KVObject(0));

        if (sourceExtension.Equals(".vpcf", StringComparison.OrdinalIgnoreCase))
        {
            // Dump-verified defaults (probe source carried none of these keys
            // and RC stamped exactly 0 / 1000 / 8.0); real values win when the
            // particle definition sets them.
            data.Add("particle_groupid", new KVObject(GetIntOr(userDoc.Root, "m_nGroupID", 0)));
            data.Add("particle_maxcount", new KVObject(GetIntOr(userDoc.Root, "m_nMaxParticles", 1000)));
            data.Add("particle_time_to_sleep", new KVObject(GetDoubleOr(userDoc.Root, "m_flNoDrawTimeToGoToSleep", 8.0)));
        }

        return data;
    }

    /// <summary>Per-type m_SubassetReferences / m_SubassetDefinitions (null when the
    /// type has none - RC writes literal nulls, not empty objects).</summary>
    private static (KVObject SubRefs, KVObject SubDefs) BuildSubassets(string sourceExtension, KVDocument userDoc)
    {
        if (sourceExtension.Equals(".vsndevts", StringComparison.OrdinalIgnoreCase) && userDoc.Root.IsCollection)
        {
            // Every top-level key IS a sound-event definition.
            var events = KVObject.Array();
            foreach (var child in userDoc.Root.Children)
                events.Add(new KVObject(child.Key));
            var defs = KVObject.Collection();
            defs.Add("soundevent", events);
            return (KVObject.Null(), defs);
        }

        if (sourceExtension.Equals(".vpcf", StringComparison.OrdinalIgnoreCase))
        {
            // RC records a census of every particle operator class the system
            // uses ({ _class name -> use count }), excluding the root
            // CParticleSystemDefinition itself.
            var census = new Dictionary<string, int>(StringComparer.Ordinal);
            CollectOperatorClasses(userDoc.Root, isRoot: true, census);
            if (census.Count > 0)
            {
                var ops = KVObject.Collection();
                foreach (var (cls, count) in census)
                    ops.Add(cls, new KVObject(count));
                var subRefs = KVObject.Collection();
                subRefs.Add("particle_operator", ops);
                return (subRefs, KVObject.Null());
            }
        }

        return (KVObject.Null(), KVObject.Null());
    }

    private static long GetIntOr(KVObject root, string key, long fallback)
        => root.IsCollection && root.TryGetValue(key, out var v)
           && v.ValueType is KVValueType.Int16 or KVValueType.UInt16
               or KVValueType.Int32 or KVValueType.UInt32 or KVValueType.Int64 or KVValueType.UInt64
           ? (long)v : fallback;

    private static double GetDoubleOr(KVObject root, string key, double fallback)
        => root.IsCollection && root.TryGetValue(key, out var v)
           && v.ValueType is KVValueType.FloatingPoint or KVValueType.FloatingPoint64
           ? (double)v : fallback;

    /// <summary>
    /// Retype every integer leaf the way resourcecompiler does before the tree is
    /// written to binary KV3.
    ///
    /// The text reader types every unsigned literal UInt64, so a compile stamps
    /// 8-byte integers where RC stamps 4-byte ones. The rules below were measured
    /// against the real compiler at both boundaries; RC never emits UInt64.
    ///
    /// <para>In ordinary value position, 0 and 1 become <c>Int64</c>, which the writer
    /// emits as the dedicated no-payload singleton codes; anything else that fits
    /// signed 32-bit becomes <c>Int32</c>; larger becomes <c>Int64</c>.</para>
    ///
    /// <para>An ALL-INTEGER array is written typed, one element type for the run, so
    /// the rule changes twice: the singleton codes are unavailable, and the type is the
    /// widest any element needs applied to all of them. A mixed array is not typed and
    /// keeps the ordinary per-element rules.</para>
    ///
    /// <para>Rebuilds rather than mutates, because KVObject leaves are typed at
    /// construction, preserving key order, array order and flags.</para>
    /// </summary>
    public static KVObject NarrowIntegers(KVObject node)
    {
        if (node.IsArray)
        {
            var arr = KVObject.Array();
            if (TryTypedIntegerArray(node, out var needs64))
            {
                foreach (var v in node.Values)
                {
                    var n = AsInt64(v);
                    arr.Add(needs64 ? new KVObject(n) { Flag = v.Flag }
                                    : new KVObject((int)n) { Flag = v.Flag });
                }
                return arr;
            }
            foreach (var v in node.Values)
                arr.Add(NarrowIntegers(v));
            return arr;
        }
        if (node.IsCollection)
        {
            var obj = KVObject.Collection();
            foreach (var kv in node.Children)
                obj.Add(kv.Key, NarrowIntegers(kv.Value));
            return obj;
        }

        return node.ValueType switch
        {
            KVValueType.UInt64 => RetypeUnsigned((ulong)node, node.Flag),
            KVValueType.Int64 => RetypeSigned((long)node, node.Flag),
            _ => node,
        };
    }

    /// <summary>True when every element is an integer that fits <c>long</c> (so RC
    /// would write a typed array); <paramref name="needs64"/> reports whether any
    /// of them falls outside <c>int</c>. False for empty, mixed, or
    /// above-long.MaxValue arrays, which keep per-element typing.</summary>
    private static bool TryTypedIntegerArray(KVObject array, out bool needs64)
    {
        needs64 = false;
        var any = false;
        foreach (var v in array.Values)
        {
            any = true;
            switch (v.ValueType)
            {
                case KVValueType.Int64:
                    if ((long)v is < int.MinValue or > int.MaxValue)
                        needs64 = true;
                    break;
                case KVValueType.UInt64:
                    if ((ulong)v > long.MaxValue)
                        return false;
                    if ((ulong)v > int.MaxValue)
                        needs64 = true;
                    break;
                default:
                    return false;
            }
        }
        return any;
    }

    private static long AsInt64(KVObject v) =>
        v.ValueType == KVValueType.UInt64 ? (long)(ulong)v : (long)v;

    private static KVObject RetypeSigned(long v, KVFlag flag) =>
        v is 0 or 1 ? new KVObject(v) { Flag = flag }
        : v >= int.MinValue && v <= int.MaxValue ? new KVObject((int)v) { Flag = flag }
        : new KVObject(v) { Flag = flag };

    private static KVObject RetypeUnsigned(ulong v, KVFlag flag) =>
        v is 0 or 1 ? new KVObject((long)v) { Flag = flag }
        : v <= int.MaxValue ? new KVObject((int)v) { Flag = flag }
        : v <= long.MaxValue ? new KVObject((long)v) { Flag = flag }
        : new KVObject(v) { Flag = flag };

    /// <summary>Depth-first collect of distinct <c>resource:</c>-flagged string
    /// values (first-encounter order) - the reference set RERL must list.</summary>
    public static void CollectResourceRefs(KVObject? node, HashSet<string> seen, List<string> refs)
    {
        if (node is null)
            return;
        if (node.IsCollection || node.IsArray)
        {
            foreach (var child in node.Children)
                CollectResourceRefs(child.Value, seen, refs);
            return;
        }
        if (node.Flag == KVFlag.Resource && node.ValueType == KVValueType.String
            && (string)node is { Length: > 0 } path && seen.Add(path))
        {
            refs.Add(path);
        }
    }

    /// <summary>Distinct string values (any flag) matching <paramref name="match"/>, depth first in
    /// first-encounter order, compared ignoring case.</summary>
    private static List<string> Strings(KVObject? node, Func<string, bool> match)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        void Walk(KVObject? n)
        {
            if (n is null)
                return;
            if (n.IsCollection || n.IsArray)
            {
                foreach (var child in n.Children)
                    Walk(child.Value);
                return;
            }
            if (n.ValueType == KVValueType.String && (string)n is { } s && match(s) && seen.Add(s))
                found.Add(s);
        }
        Walk(node);
        return found;
    }

    /// <summary>Census of `_class` string values below the root (each nested
    /// operator/initializer/emitter/renderer definition carries one).</summary>
    private static void CollectOperatorClasses(KVObject? node, bool isRoot, Dictionary<string, int> census)
    {
        if (node is null)
            return;
        if (node.IsCollection)
        {
            if (!isRoot && node.TryGetValue("_class", out var cls) && cls.ValueType == KVValueType.String)
            {
                var name = (string)cls;
                census[name] = census.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            foreach (var child in node.Children)
                CollectOperatorClasses(child.Value, isRoot: false, census);
            return;
        }
        if (node.IsArray)
        {
            foreach (var child in node.Children)
                CollectOperatorClasses(child.Value, isRoot: false, census);
        }
    }
}

/// <summary>A source file RC saved to disk and compiled a child resource from:
/// its content-relative name, the search path it was found under
/// (<c>csgo_addons/&lt;addon&gt;</c>) and the CRC32 of its bytes.</summary>
public readonly record struct SavedSource(string RelativeFilename, string SearchPath, uint Crc);
