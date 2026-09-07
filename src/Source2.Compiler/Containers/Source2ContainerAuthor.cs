using System.IO.Hashing;
using System.Reflection;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Authors compiled Source 2 KV3-resource containers <b>from scratch</b> — no
/// donor/template <c>_c</c> file involved. Replaces the old approach of
/// splicing a user's KV3 tree into an embedded donor container (which shipped
/// the donor's RED2 verbatim: wrong input dependencies, wrong compiler
/// identity for unmapped extensions, wrong/missing subasset definitions).
///
/// Every structural fact here is ground truth dumped from CS2's own
/// <c>resourcecompiler.exe</c> / <c>resourceinfo.exe</c> (2026-07-26 probes;
/// re-derivable any time with <c>tools/rc-oracle.ps1</c>):
///
/// <code>
///   uint32 fileSize; uint16 headerVersion (=12); uint16 resourceVersion;
///   uint32 blockInfoOffset (=8); uint32 blockCount;
///   { fourCC; uint32 relOffset; uint32 size; } x blockCount;   // RERL? RED2 DATA
///   16-aligned block payloads (KV3v5 blobs)
/// </code>
///
/// VRF's <see cref="Resource.Serialize"/> emits exactly this framing, so we
/// author an in-memory <see cref="Resource"/> (blocks constructed
/// programmatically) and let VRF do the byte layout — the same serializer the
/// rest of the pipeline already trusts (KV3-LZ4 patch included).
///
/// Per-type facts (RC ground truth):
/// <list type="bullet">
/// <item><c>.vsndevts</c> → resver 1; RED2+DATA; special dep
///   ("Sound Event Script Version", CompileSoundEventScript, 10);
///   m_SubassetDefinitions.soundevent = the event names; per-sound optional
///   input-dependency probes (.mp3/.vsnd/.wav).</item>
/// <item><c>.vdata</c> → resver 0; RED2+DATA(+FLCI, editor-only — we skip it,
///   matching pre-FLCI stock resources); special deps
///   ("KV3 Compiler Version", CompileVData, 2) + ("VData Compiler Version",
///   CompileVData, 1); SearchableUserData carries the tree's
///   generic_data_type.</item>
/// <item><c>.vpcf</c> → resver 1; RERL(only when the tree holds resource:
///   refs)+RED2+DATA; special deps ("KV3 Compiler Version", CompileParticle, 2)
///   + ("Particle Compiler Version", CompileParticle, 2);
///   m_SubassetReferences.particle_operator = census of operator _class uses;
///   SearchableUserData carries groupid/maxcount/time_to_sleep
///   (defaults 0 / 1000 / 8.0 when the def doesn't override them).</item>
/// </list>
///
/// The compiler-version fingerprints above are what the CURRENT CS2 build's
/// resourcecompiler stamps. They are tools-side dependency metadata (the
/// engine's loader keys on resource version + DATA, which is why years-old
/// community containers still load), but we mirror them exactly so our output
/// is indistinguishable from a stock compile. When a CS2 update bumps them,
/// re-run tools/rc-oracle.ps1 and update <see cref="SpecByExtension"/>.
/// </summary>
public static partial class Source2ContainerAuthor
{
    public sealed record SpecialDep(string Name, string CompilerIdentifier, int Fingerprint, int UserData = 0);

    public sealed record ContainerSpec(ushort ResourceVersion, SpecialDep[] SpecialDeps);

    /// <summary>Per-source-extension container facts, dumped from resourcecompiler output.</summary>
    public static readonly IReadOnlyDictionary<string, ContainerSpec> SpecByExtension =
        new Dictionary<string, ContainerSpec>(StringComparer.OrdinalIgnoreCase)
        {
            [".vsndevts"] = new(1, [new("Sound Event Script Version", "CompileSoundEventScript", 10)]),
            [".vdata"]    = new(0, [new("KV3 Compiler Version", "CompileVData", 2),
                                    new("VData Compiler Version", "CompileVData", 1)]),
            [".vpcf"]     = new(1, [new("KV3 Compiler Version", "CompileParticle", 2),
                                    new("Particle Compiler Version", "CompileParticle", 2)]),
            // .vagrp is extinct in CS2 content (0 in pak01 + every base pack,
            // 2026-07-20 audit) so there is no RC ground truth to mirror; it
            // keeps the sound-event identity the legacy donor skeleton gave it.
            [".vagrp"]    = new(1, [new("Sound Event Script Version", "CompileSoundEventScript", 10)]),
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
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "compiled";
        // Keep the recorded path clean: strip anything outside [A-Za-z0-9_-].
        var safe = new string(baseName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return folder + "/" + safe.ToLowerInvariant() + sourceExtension.ToLowerInvariant();
    }

    // Resource.Version has a private setter (VRF is a reader first); this is
    // the single reflection point of the author. Cached; loudly fails if the
    // upstream property ever moves.
    private static readonly PropertyInfo ResourceVersionProp =
        typeof(Resource).GetProperty(nameof(Resource.Version), BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Resource.Version property missing — VRF API changed?");

    /// <summary>
    /// Author a compiled container for the parsed KV3 source <paramref name="userDoc"/>.
    /// </summary>
    /// <param name="userDoc">The user's parsed KV3 document (its header Format GUID is stamped into DATA).</param>
    /// <param name="sourceExtension">Source extension incl. dot (".vsndevts", ".vdata", ".vpcf", ".vagrp").</param>
    /// <param name="sourceTextBytes">The raw source text — its CRC32 is recorded in RED2's input dependency, exactly as resourcecompiler records the source file CRC.</param>
    /// <param name="sourceFileName">Relative source path for RED2's input dependency (e.g. "soundevents/my_sounds.vsndevts"); a neutral name is synthesized when the caller doesn't know it.</param>
    public static byte[] AuthorKv3Resource(
        KVDocument userDoc,
        string sourceExtension,
        byte[] sourceTextBytes,
        string? sourceFileName = null)
    {
        if (!SpecByExtension.TryGetValue(sourceExtension, out var spec))
            throw new InvalidOperationException($"No container spec for '{sourceExtension}'.");

        sourceFileName ??= "vpkeditor/compiled" + sourceExtension.ToLowerInvariant();

        using var resource = new Resource();
        ResourceVersionProp.SetValue(resource, spec.ResourceVersion);

        // RERL — resourcecompiler emits the block ONLY when the compiled data
        // actually references other resources (verified: a ref-free vpcf ships
        // just RED2+DATA; one with material refs ships RERL first). Every
        // distinct resource:-flagged string becomes an entry with the engine's
        // own path-hash id (MurmurHash64B — reproduces stock RERL ids exactly).
        var refs = new List<string>();
        CollectResourceRefs(userDoc.Root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), refs);
        if (refs.Count > 0)
        {
            var rerl = new ResourceExtRefList { Resource = resource };
            foreach (var path in refs)
                rerl.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo
                {
                    Id   = Source2ResourceId.ForPath(path),
                    Name = path,
                });
            resource.Blocks.Add(rerl);
        }

        // RED2 — authored for THIS compile (real source name + CRC, correct
        // compiler identity, real subasset data), as a binary-KV3 blob with the
        // "generic" format GUID (the GUID resourcecompiler stamps; the KV3
        // binary VERSION is whatever VRF's serializer emits — the engine
        // accepts every KV3 binary generation, current RC emits v5).
        var red2Doc = BuildRed2Document(spec, sourceExtension, sourceFileName, sourceTextBytes, userDoc);
        resource.Blocks.Add(new BinaryKV3(NarrowIntegers(red2Doc), KV3IDLookup.Get("generic"), BlockType.RED2) { Resource = resource });

        // DATA — the user's tree, format GUID from their own kv3 header.
        resource.Blocks.Add(new BinaryKV3(NarrowIntegers(userDoc.Root), userDoc.Header.Format, BlockType.DATA) { Resource = resource });

        // FLCI (source-file line map) is deliberately not authored: it is
        // editor-only metadata and pre-FLCI stock resources load fine without it.

        return ResourceBuilder.Serialize(resource);
    }

    /// <summary>Build the RED2 CResourceEditInfo document resourcecompiler would
    /// have written for this compile (field set + shapes verified per type).</summary>
    private static KVObject BuildRed2Document(
        ContainerSpec spec, string sourceExtension, string sourceFileName, byte[] sourceTextBytes, KVDocument userDoc)
    {
        var root = KVObject.Collection();

        // m_InputDependencies[0] = the source file itself, CRC32 of its bytes —
        // byte-verified against resourcecompiler output (plain zlib CRC32).
        var inputDeps = KVObject.Array();
        inputDeps.Add(InputDependency(sourceFileName, crc: Crc32.HashToUInt32(sourceTextBytes), optional: false, exists: true));

        // vsndevts: RC also records optional source-audio probes (.mp3/.vsnd/.wav
        // per referenced sound) so its incremental rebuild triggers when a WAV
        // appears. Mirrored for fidelity; the engine never reads these.
        if (sourceExtension.Equals(".vsndevts", StringComparison.OrdinalIgnoreCase))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sounds = new List<string>();
            CollectVsndPaths(userDoc.Root, seen, sounds);
            foreach (var vsnd in sounds)
            {
                var baseName = vsnd[..^".vsnd".Length];
                foreach (var probeExt in new[] { ".mp3", ".vsnd", ".wav" })
                    inputDeps.Add(InputDependency(baseName + probeExt, crc: 0, optional: true, exists: false));
            }
        }

        root.Add("m_InputDependencies", inputDeps);
        root.Add("m_AdditionalInputDependencies", KVObject.Array());

        // The one argument dependency every RC-compiled KV3 resource carries.
        var argDeps = KVObject.Array();
        var arg = KVObject.Collection();
        arg.Add("m_ParameterName", new KVObject("___OverrideInputData___"));
        arg.Add("m_ParameterType", new KVObject("BinaryBlobArg"));
        arg.Add("m_nFingerprint", new KVObject(0));
        arg.Add("m_nFingerprintDefault", new KVObject(0));
        argDeps.Add(arg);
        root.Add("m_ArgumentDependencies", argDeps);

        var specialDeps = KVObject.Array();
        foreach (var dep in spec.SpecialDeps)
        {
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(dep.Name));
            d.Add("m_CompilerIdentifier", new KVObject(dep.CompilerIdentifier));
            d.Add("m_nFingerprint", new KVObject(dep.Fingerprint));
            d.Add("m_nUserData", new KVObject(dep.UserData));
            specialDeps.Add(d);
        }
        root.Add("m_SpecialDependencies", specialDeps);

        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());
        root.Add("m_ChildResourceList", KVObject.Array());
        root.Add("m_WeakReferenceList", KVObject.Array());
        root.Add("m_SearchableUserData", BuildSearchableUserData(sourceExtension, userDoc));

        var (subRefs, subDefs) = BuildSubassets(sourceExtension, userDoc);
        root.Add("m_SubassetReferences", subRefs);
        root.Add("m_SubassetDefinitions", subDefs);

        return root;
    }

    /// <summary>
    /// Author the RED2 for a BINARY resource we compiled ourselves (<c>.vtex_c</c>,
    /// <c>.vsnd_c</c>, <c>.vsvg_c</c>), so the output stops shipping the donor
    /// template's edit info.
    ///
    /// <para>Before this, every such compile kept its embedded donor's RED2
    /// verbatim. A texture declared (dumped with Valve's own resourceinfo.exe,
    /// 2026-08-10):</para>
    /// <code>
    ///   m_RelativeFilename = "materials/mac/hud_hit_marker_hs.vtex"
    ///   m_SearchPath       = "csgo_addons/c"
    ///   m_nFileCRC         = 3986264652
    /// </code>
    /// <para>i.e. a third party's source paths and their file CRCs, on our output.
    /// Exactly the defect class fixed for the KV3 types on 2026-07-26, just milder
    /// because the donor is at least type-correct. Sounds and killfeed icons had
    /// the same leak from their own donors.</para>
    ///
    /// <para><b>Only provenance is replaced.</b> The template's
    /// <c>m_SpecialDependencies</c> are carried over verbatim when it has them,
    /// because those describe the DATA's ENCODING SEMANTICS, not who authored the
    /// source. A stock glove normal template carries
    /// <c>Mip HemiOctAnisoRoughness</c>, and dropping it makes every consumer
    /// decode the normal as a plain RGB image with roughness read as Z — the
    /// "black patches" class. An earlier revision authored the generic set
    /// unconditionally and <c>GloveSurfaceMapShipTests</c> caught it. The
    /// per-type tables are only the fallback for a template carrying none.</para>
    ///
    /// <para>Deliberately NOT reproduced: RC's per-option
    /// <c>m_ArgumentDependencies</c> tail (for a texture: <c>fast</c>,
    /// <c>final</c>, <c>hueShiftFixup</c>, <c>lightmapMaxResolution</c>,
    /// <c>maxmipsize</c>, <c>minmipsize</c>, plus ImageArg/BinaryBlobArg entries
    /// naming the source). Those encode options from a source file our in-memory
    /// composites never had — inventing values would be fabricating metadata
    /// rather than fixing it. Only <c>___OverrideInputData___</c>, which every RC
    /// compile of every type carries, is emitted.</para>
    /// </summary>
    /// <param name="sourceName">Content-relative source path to record, or null for a neutral one.</param>
    /// <param name="sourceBytes">The bytes we actually encoded — their CRC32 is recorded, as RC records the real source CRC.</param>
    /// <param name="fallbackSpecialDeps">Per-type compiler identity, used only when the template carries none.</param>
    /// <param name="templateSpecialDeps">The template's special dependencies, preserved verbatim.</param>
    /// <param name="optionalProbes">Sibling paths RC records as optional/non-existent probes (CRC 0) so its incremental rebuild retriggers if one appears. Honest to emit: each is a statement that the file does not exist.</param>
    public static KVObject BuildBinaryEditInfo(
        string? sourceName,
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<SpecialDep> fallbackSpecialDeps,
        IReadOnlyList<SpecialDep>? templateSpecialDeps = null,
        IReadOnlyList<string>? optionalProbes = null)
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? "vpkeditor/compiled" : sourceName!;

        var root = KVObject.Collection();

        // RC lists dependencies sorted by path; the real source sits among the
        // optional probes rather than first (verified on the vsnd probe, where
        // the real .wav lands last alphabetically).
        var deps = new List<(string Path, uint Crc, bool Optional, bool Exists)>
        {
            (name, Crc32.HashToUInt32(sourceBytes), false, true),
        };
        foreach (var probe in optionalProbes ?? [])
            if (!string.Equals(probe, name, StringComparison.OrdinalIgnoreCase))
                deps.Add((probe, 0u, true, false));

        var inputDeps = KVObject.Array();
        foreach (var (path, crc, optional, exists) in deps.OrderBy(d => d.Path, StringComparer.Ordinal))
            inputDeps.Add(InputDependency(path, crc, optional, exists));
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

        var specialDeps = KVObject.Array();
        foreach (var dep in templateSpecialDeps is { Count: > 0 } ? templateSpecialDeps : fallbackSpecialDeps)
        {
            var d = KVObject.Collection();
            d.Add("m_String", new KVObject(dep.Name));
            d.Add("m_CompilerIdentifier", new KVObject(dep.CompilerIdentifier));
            d.Add("m_nFingerprint", new KVObject(dep.Fingerprint));
            d.Add("m_nUserData", new KVObject(dep.UserData));
            specialDeps.Add(d);
        }
        root.Add("m_SpecialDependencies", specialDeps);

        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());
        root.Add("m_ChildResourceList", KVObject.Array());
        root.Add("m_WeakReferenceList", KVObject.Array());

        var searchable = KVObject.Collection();
        searchable.Add("IsChildResource", new KVObject(0));
        root.Add("m_SearchableUserData", searchable);

        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());

        return NarrowIntegers(root);
    }

    /// <summary>Back-compat wrapper for the texture case.</summary>
    public static KVObject BuildTextureEditInfo(
        string? sourceName, ReadOnlySpan<byte> sourceBytes, IReadOnlyList<SpecialDep>? templateSpecialDeps = null)
        => BuildBinaryEditInfo(
            string.IsNullOrWhiteSpace(sourceName) ? "vpkeditor/texture.png" : sourceName,
            sourceBytes, TextureSpecialDeps, templateSpecialDeps);

    /// <summary>Author the RED2 for a compiled sound. RC probes for a fixed set of
    /// sibling files next to the source; mirrored because each entry is an honest
    /// "this file does not exist" (CRC 0, optional, not-exists).</summary>
    public static KVObject BuildSoundEditInfo(
        string? sourceName, ReadOnlySpan<byte> sourceBytes, IReadOnlyList<SpecialDep>? templateSpecialDeps = null)
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? "vpkeditor/audio.wav" : sourceName!;
        return BuildBinaryEditInfo(name, sourceBytes, SoundSpecialDeps, templateSpecialDeps,
            SoundProbeSiblings(name));
    }

    /// <summary>Author the RED2 for a compiled Panorama vector graphic. RC records
    /// the <c>.svg</c> source plus an optional same-named <c>.vsvg</c>.</summary>
    public static KVObject BuildVectorGraphicEditInfo(
        string? sourceName, ReadOnlySpan<byte> sourceBytes, IReadOnlyList<SpecialDep>? templateSpecialDeps = null)
    {
        var name = string.IsNullOrWhiteSpace(sourceName) ? "panorama/images/vpkeditor/icon.svg" : sourceName!;
        return BuildBinaryEditInfo(name, sourceBytes, VectorGraphicSpecialDeps, templateSpecialDeps,
            [StripExt(name) + ".vsvg"]);
    }

    /// <summary>The sibling probes resourcecompiler records for a sound compile
    /// (dumped 2026-08-10 from a real <c>.wav</c> compile): a per-tree and
    /// per-folder <c>encoding.txt</c>, plus same-named <c>.fbx / .mp3 / .rts /
    /// .txt / .vsnd</c>.</summary>
    private static string[] SoundProbeSiblings(string sourceName)
    {
        var noExt = StripExt(sourceName);
        var dir = noExt.LastIndexOf('/') is var i && i > 0 ? noExt[..i] : "sounds";
        var top = dir.IndexOf('/') is var j && j > 0 ? dir[..j] : dir;
        return
        [
            top + "/encoding.txt",
            dir + "/encoding.txt",
            noExt + ".fbx",
            noExt + ".mp3",
            noExt + ".rts",
            noExt + ".txt",
            noExt + ".vsnd",
        ];
    }

    private static string StripExt(string path)
        => path.LastIndexOf('.') is var i && i > path.LastIndexOf('/') && i > 0 ? path[..i] : path;

    /// <summary>The three special dependencies resourcecompiler stamps on a
    /// <c>.vtex_c</c>, byte-verified on two independent real compiles.</summary>
    public static readonly SpecialDep[] TextureSpecialDeps =
    [
        new("Texture Compiler Version",           "CompileTexture", 11),
        new("Texture Compiler Version Mip None",  "CompileTexture", 1),
        new("Texture Encode Quality",             "CompileTexture", 1, UserData: 3),
    ];

    /// <summary>What resourcecompiler stamps on a <c>.vsnd_c</c> (resource version
    /// 5), dumped 2026-08-10 from a real PCM WAV compile.</summary>
    public static readonly SpecialDep[] SoundSpecialDeps =
    [
        new("Sound Compiler Version", "CompileSound", 1),
    ];

    /// <summary>What resourcecompiler stamps on a <c>.vsvg_c</c> (resource version
    /// 2), dumped 2026-08-10 from a real SVG compile.</summary>
    public static readonly SpecialDep[] VectorGraphicSpecialDeps =
    [
        new("Vector Graphic Version", "CompileVectorGraphic", 2),
    ];

    private static KVObject InputDependency(string relativeFilename, uint crc, bool optional, bool exists)
    {
        var d = KVObject.Collection();
        d.Add("m_RelativeFilename", new KVObject(relativeFilename));
        // Stock CS2 resources record the game's own content root; we author
        // stock-style containers, so mirror it (RC records whatever mod tree
        // it compiled from — "csgo" for every shipped Valve resource).
        d.Add("m_SearchPath", new KVObject("csgo"));
        d.Add("m_nFileCRC", new KVObject(crc));
        d.Add("m_bOptional", new KVObject(optional));
        d.Add("m_bFileExists", new KVObject(exists));
        d.Add("m_bIsGameFile", new KVObject(false));
        return d;
    }

    /// <summary>m_SearchableUserData — the asset-browser metadata resourcecompiler
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
    /// type has none — RC writes literal nulls, not empty objects).</summary>
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
    /// Our KV3 text reader types every unsigned literal <c>UInt64</c> and every
    /// negative one <c>Int64</c>, so a compile stamped 8-byte integers where RC
    /// stamps 4-byte ones. Measured against the real compiler (2026-08-10, CS2
    /// resourcecompiler.exe on a boundary probe — see
    /// <c>Source2ContainerAuthorTests.AuthoredContainer_IntegerTypesMatchResourceCompiler</c>):
    ///
    /// <b>Ordinary value position</b> (an object's field, or an element of a
    /// mixed-type array):
    /// <list type="bullet">
    /// <item>0 and 1 → <c>Int64</c>, which VRF's writer emits as the dedicated
    ///   INT64_ZERO / INT64_ONE type codes (no payload bytes) — exactly what RC
    ///   does.</item>
    /// <item>anything else representable as a signed 32-bit value → <c>Int32</c>
    ///   (verified at both boundaries: 2147483647 and -2147483648 are Int32,
    ///   2147483648 and -2147483649 are Int64).</item>
    /// <item>everything larger → <c>Int64</c>; above <c>long.MaxValue</c> there
    ///   is no signed form, so the UInt64 stands (RC was not probed there — no
    ///   CS2 schema field is a raw uint64 literal).</item>
    /// </list>
    ///
    /// <b>All-integer array</b> — RC writes these as a TYPED array, one element
    /// type for the whole run, so the rule changes twice over:
    /// <list type="bullet">
    /// <item>the singleton codes are unavailable: <c>[0, 0]</c> and <c>[1, 1]</c>
    ///   come out <c>Int32</c>, not INT64_ZERO/ONE.</item>
    /// <item>the type is the WIDEST any element needs, applied to all of them:
    ///   <c>[7, 5000000000]</c> is Int64+Int64, not Int32+Int64.</item>
    /// </list>
    /// A mixed array is not typed and so keeps ordinary per-element rules — a
    /// probed <c>[0, "s"]</c> keeps the singleton. Empty arrays are unaffected.
    ///
    /// RC never emits UInt64. Rebuilds the tree rather than mutating: KVObject
    /// leaves are typed at construction. Key order, array order and KV3 flags are
    /// preserved, so the RERL/subasset passes above see the same tree either way.
    /// </summary>
    internal static KVObject NarrowIntegers(KVObject node)
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
            foreach (var v in node.Values) arr.Add(NarrowIntegers(v));
            return arr;
        }
        if (node.IsCollection)
        {
            var obj = KVObject.Collection();
            foreach (var kv in node.Children) obj.Add(kv.Key, NarrowIntegers(kv.Value));
            return obj;
        }

        return node.ValueType switch
        {
            KVValueType.UInt64 => RetypeUnsigned((ulong)node, node.Flag),
            KVValueType.Int64  => RetypeSigned((long)node, node.Flag),
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
                    if ((long)v is < int.MinValue or > int.MaxValue) needs64 = true;
                    break;
                case KVValueType.UInt64:
                    if ((ulong)v > long.MaxValue) return false;
                    if ((ulong)v > int.MaxValue) needs64 = true;
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
        v is 0 or 1                              ? new KVObject(v) { Flag = flag }
        : v >= int.MinValue && v <= int.MaxValue ? new KVObject((int)v) { Flag = flag }
        :                                          new KVObject(v) { Flag = flag };

    private static KVObject RetypeUnsigned(ulong v, KVFlag flag) =>
        v is 0 or 1            ? new KVObject((long)v) { Flag = flag }
        : v <= int.MaxValue    ? new KVObject((int)v) { Flag = flag }
        : v <= long.MaxValue   ? new KVObject((long)v) { Flag = flag }
        :                        new KVObject(v) { Flag = flag };

    /// <summary>Depth-first collect of distinct <c>resource:</c>-flagged string
    /// values (first-encounter order) — the reference set RERL must list.</summary>
    internal static void CollectResourceRefs(KVObject? node, HashSet<string> seen, List<string> refs)
    {
        if (node is null) return;
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

    /// <summary>Collect distinct ".vsnd"-suffixed string values (any flag) — the
    /// sound files a vsndevts references, for RED2's source-audio probes.</summary>
    private static void CollectVsndPaths(KVObject? node, HashSet<string> seen, List<string> refs)
    {
        if (node is null) return;
        if (node.IsCollection || node.IsArray)
        {
            foreach (var child in node.Children)
                CollectVsndPaths(child.Value, seen, refs);
            return;
        }
        if (node.ValueType == KVValueType.String && (string)node is { Length: > 5 } s
            && s.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase) && seen.Add(s))
        {
            refs.Add(s);
        }
    }

    /// <summary>Census of `_class` string values below the root (each nested
    /// operator/initializer/emitter/renderer definition carries one).</summary>
    private static void CollectOperatorClasses(KVObject? node, bool isRoot, Dictionary<string, int> census)
    {
        if (node is null) return;
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
