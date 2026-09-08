using Source2.Compiler;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The reference-integrity invariant, and the builders that have to hold it.
///
/// <para>A resource states each external reference TWICE: as a
/// <c>resource:</c>-flagged string in DATA, and as a RERL entry pairing that path with
/// its engine path-hash. Each half fails differently. An unflagged path reads as a bare
/// string and never resolves, which is the render-with-error-material fatal. A RERL
/// entry renamed without re-hashing still points at the OLD asset.</para>
///
/// <para>Not a house rule: sweeping pak01 and both live base packs found zero missing
/// entries, zero id mismatches and zero flagless refs.
/// <see cref="StockContent_HoldsTheReferenceInvariant"/> re-checks that against the
/// installed game rather than restating it from memory.</para>
/// </summary>
public class ResourceRefIntegrityTests
{
    private static KVObject? DataRoot(Resource r)
    {
        var d = r.GetBlockByType(BlockType.DATA);
        return d is KeyValuesOrNTRO kvn ? kvn.Data : d?.AsKeyValueCollection();
    }

    /// <summary>Every <c>resource:</c>/<c>resource_name:</c>-flagged non-empty
    /// string in the tree, deduped, in encounter order.</summary>
    private static List<string> FlaggedRefs(KVObject? node)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(node);
        return found;

        void Walk(KVObject? n)
        {
            if (n is null)
                return;
            if (n.IsCollection || n.IsArray)
            {
                foreach (var c in n.Children)
                    Walk(c.Value);
                return;
            }
            if (n.Flag is KVFlag.Resource or KVFlag.ResourceName
                && n.ValueType == KVValueType.String
                && (string)n is { Length: > 0 } s
                && seen.Add(s))
            {
                found.Add(s);
            }
        }
    }

    /// <summary>Assert the two halves agree: every flagged DATA path has a RERL
    /// entry, and that entry's id is the engine hash of the path it names.
    /// Returns how many refs were checked so a caller can reject a vacuous pass.
    /// </summary>
    private static int AssertRefsResolve(byte[] compiled, string label)
    {
        using var res = new Resource { FileName = label };
        res.Read(new MemoryStream(compiled));

        var rerl = res.ExternalReferences?.ResourceRefInfoList ?? [];
        var byName = rerl.Where(r => r.Name is not null)
                         .GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var refs = FlaggedRefs(DataRoot(res));
        foreach (var path in refs)
        {
            Assert.True(byName.TryGetValue(path, out var entry),
                $"{label}: DATA references '{path}' but no RERL entry names it - the engine resolves "
              + "through the RERL, so the reference is dangling.");
            Assert.True(entry!.Id == Source2ResourceId.ForPath(path),
                $"{label}: RERL entry '{path}' carries id {entry.Id:x16} but the path hashes to "
              + $"{Source2ResourceId.ForPath(path):x16} - a renamed entry that was never re-hashed "
              + "still points at the old asset.");
        }

        // Every RERL entry should itself be self-consistent, renamed or not.
        foreach (var entry in rerl)
        {
            if (entry.Name is not { Length: > 0 } name)
                continue;
            Assert.True(entry.Id == Source2ResourceId.ForPath(name),
                $"{label}: stale RERL id for '{name}' ({entry.Id:x16} != "
              + $"{Source2ResourceId.ForPath(name):x16}).");
        }

        return refs.Count;
    }

    /// <summary>
    /// REGRESSION PIN: <c>RewriteParticlePaths</c> re-hashes a renamed RERL entry.
    /// Fails against the pre-2026-08-10 code, which set Name and left Id - the
    /// defect the audit found baked into every hitmarker particle we have shipped.
    /// Uses the embedded hitmarker template, so it is fixture-free.
    /// </summary>
    [Fact]
    public void RewriteParticlePaths_ReHashesRenamedRerlEntries()
    {
        var vpcf = CS2Fixtures.Template(".vpcf_c");
        if (vpcf is null) { CS2Fixtures.Skip("a compiled .vpcf_c"); return; }
        using var before = new Resource();
        before.Read(new MemoryStream(vpcf));

        // Rename every material ref the template carries into our own namespace,
        // which is exactly what the hitmarker build does.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in before.ExternalReferences?.ResourceRefInfoList ?? [])
            if (entry.Name is { Length: > 0 } n)
                map[n] = "materials/vpkedit_hm/" + Path.GetFileName(n);
        Assert.NotEmpty(map);

        var rewritten = ResourceBuilder.RewriteParticlePaths(vpcf, map);

        using var after = new Resource();
        after.Read(new MemoryStream(rewritten));
        var names = (after.ExternalReferences?.ResourceRefInfoList ?? [])
            .Select(r => r.Name).ToList();
        Assert.All(names, n => Assert.StartsWith("materials/vpkedit_hm/", n));

        Assert.True(AssertRefsResolve(rewritten, "rewritten hitmarker.vpcf_c") > 0,
            "the template carried no flagged refs - wrong fixture, the pin would be vacuous");
    }

    /// <summary>The authoring builders hold the invariant too: a from-scratch
    /// material lists its textures in the RERL with correct ids, and a
    /// from-scratch model lists its material group.</summary>
    [Fact]
    public void AuthoringBuilders_ProduceResolvableReferences()
    {
        var stockVmat = CS2Fixtures.Template(".vmat_c");
        if (stockVmat is null) { CS2Fixtures.Skip("a compiled .vmat_c"); return; }

        var def = new ResourceBuilder.MaterialDef
        {
            Name = "materials/vpkedit/refcheck.vmat",
            Shader = "csgo_complex.vfx",
        };
        def.TextureParams["g_tColor"] = "materials/vpkedit/refcheck_color.vtex";
        def.TextureParams["g_tNormal"] = "materials/vpkedit/refcheck_normal.vtex";

        var mat = ResourceBuilder.BuildMaterial(stockVmat, def);
        Assert.Equal(2, AssertRefsResolve(mat, "BuildMaterial output"));
    }

    /// <summary>
    /// A compiled texture must describe ITS OWN compile, not the donor template's.
    ///
    /// Until 2026-08-10 <c>BuildTexture</c> kept the embedded
    /// <c>hitmarker_vtex_template.vtex_c</c>'s RED2 verbatim, so every skin
    /// composite, glove texture, hitmarker and /compile upload shipped declaring
    /// <c>materials/mac/hud_hit_marker_hs.vtex</c> under search path
    /// <c>csgo_addons/c</c> with that author's file CRCs - the 2026-07-26 KV3
    /// donor-metadata defect, still live on the binary types. This pins the fix
    /// from both ends: our identity present, the donor's absent.
    /// </summary>
    [Fact]
    public void BuildTexture_AuthorsItsOwnEditInfo_NotTheDonorTemplates()
    {
        var template = CS2Fixtures.Template(".vtex_c");
        if (template is null) { CS2Fixtures.Skip("a compiled .vtex_c"); return; }

        const int w = 64, h = 64;
        var raw = new byte[w * h * 4];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = (byte)(i * 7);

        var def = new ResourceBuilder.TextureDef
        {
            RawRgba = raw,
            RawWidth = w,
            RawHeight = h,
            Compression = ResourceBuilder.TextureCompression.BC7,
            Bc7Mode = ResourceBuilder.Bc7EncoderMode.Cpu,
            GenerateMipmaps = true,
            SourceName = "materials/vpkedit/probe_color.png",
        };

        var vtex = ResourceBuilder.BuildTexture(template!, def);

        using var res = new Resource { FileName = "probe.vtex_c" };
        res.Read(new MemoryStream(vtex));

        var edit = Assert.IsType<ResourceEditInfo2>(res.EditInfo);

        // Ours, with the CRC of the bytes we actually encoded.
        var input = Assert.Single(edit.InputDependencies);
        Assert.Equal("materials/vpkedit/probe_color.png", input.ContentRelativeFilename);
        Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(raw), input.FileCRC);

        // The donor's identity must be gone from the whole edit-info block.
        foreach (var dep in edit.InputDependencies)
        {
            Assert.DoesNotContain("materials/mac/", dep.ContentRelativeFilename, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hud_hit_marker", dep.ContentRelativeFilename, StringComparison.OrdinalIgnoreCase);
        }

        // Compiler identity is resourcecompiler's, dumped from two independent
        // real compiles (our rc-oracle probe and the donor itself).
        foreach (var expected in Source2ContainerAuthor.TextureSpecialDeps)
        {
            Assert.Contains(edit.SpecialDependencies, d =>
                d.String == expected.Name
                && d.CompilerIdentifier == expected.CompilerIdentifier
                && d.Fingerprint == expected.Fingerprint
                && d.UserData == expected.UserData);
        }
        Assert.DoesNotContain(edit.SpecialDependencies, d => d.CompilerIdentifier == "CompileSoundEventScript");
    }

    /// <summary>
    /// Same fix for the killfeed icon path: a compiled <c>.vsvg_c</c> must name
    /// the icon WE compiled, not the embedded <c>template.vsvg_c</c>'s own source.
    /// Shape dumped 2026-08-10 from a real resourcecompiler SVG compile: the
    /// <c>.svg</c> source with its CRC, plus a same-named optional <c>.vsvg</c>
    /// probe, and one <c>CompileVectorGraphic</c> special dependency.
    /// </summary>
    [Fact]
    public void BuildPanoramaSvg_AuthorsItsOwnEditInfo_NotTheDonorTemplates()
    {
        var template = CS2Fixtures.Template(".vsvg_c");
        if (template is null) { CS2Fixtures.Skip("a compiled .vsvg_c"); return; }

        var donorInputs = ReadInputPaths(template);

        var svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\" width=\"64\" height=\"64\">"
          + "<path d=\"M 8 8 L 56 8 L 56 56 L 8 56 Z\" fill=\"#ffffff\"/></svg>");

        const string shipAs = "panorama/images/icons/equipment/vpkedit_probe.svg";
        var vsvg = ResourceBuilder.BuildPanoramaSvg(template, svg, shipAs);

        using var res = new Resource { FileName = "probe.vsvg_c" };
        res.Read(new MemoryStream(vsvg));
        var edit = Assert.IsType<ResourceEditInfo2>(res.EditInfo);

        var paths = edit.InputDependencies.Select(d => d.ContentRelativeFilename).ToList();
        Assert.Contains(shipAs, paths);
        Assert.Contains("panorama/images/icons/equipment/vpkedit_probe.vsvg", paths);

        // Nothing the donor named may survive.
        foreach (var donor in donorInputs)
            Assert.DoesNotContain(donor, paths);

        Assert.Contains(edit.SpecialDependencies, d => d.CompilerIdentifier == "CompileVectorGraphic");
    }

    /// <summary>
    /// And the sound path. A compiled <c>.vsnd_c</c> must name the clip WE
    /// compiled. RC records a fixed set of sibling probes next to the source
    /// (CRC 0 / optional / not-exists) plus the real file - mirrored, because
    /// each probe is an honest "this file does not exist". Also asserts the pin
    /// survives <see cref="ResourceBuilder.ModernizeVsnd"/>, which is what the
    /// override path actually ships.
    /// </summary>
    [Fact]
    public void BuildSound_AuthorsItsOwnEditInfo_AndSurvivesModernize()
    {
        var template = CS2Fixtures.Template(".vsnd_c");
        if (template is null) { CS2Fixtures.Skip("a compiled .vsnd_c"); return; }
        var donorInputs = ReadInputPaths(template);

        const int rate = 44100, frames = 4410;
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / rate));
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        using var wav = new MemoryStream();
        using (var w = new BinaryWriter(wav, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((ushort)2);
            w.Write((ushort)16);
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        var wavBytes = wav.ToArray();

        const string shipAs = "sounds/weapons/ak47/vpkedit_probe.wav";
        var built = ResourceBuilder.BuildSound(template!, wavBytes, shipAs);

        foreach (var bytes in new[] { built, ResourceBuilder.ModernizeVsnd(built) })
        {
            using var res = new Resource { FileName = "probe.vsnd_c" };
            res.Read(new MemoryStream(bytes));
            var edit = Assert.IsType<ResourceEditInfo2>(res.EditInfo);
            var paths = edit.InputDependencies.Select(d => d.ContentRelativeFilename).ToList();

            var real = Assert.Single(edit.InputDependencies, d => !d.Optional);
            Assert.Equal(shipAs, real.ContentRelativeFilename);
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(wavBytes), real.FileCRC);

            // RC's sibling probe set, derived from the source base name.
            Assert.Contains("sounds/weapons/ak47/vpkedit_probe.mp3", paths);
            Assert.Contains("sounds/weapons/ak47/vpkedit_probe.vsnd", paths);
            Assert.Contains("sounds/weapons/ak47/encoding.txt", paths);

            foreach (var donor in donorInputs)
                Assert.DoesNotContain(donor, paths);

            Assert.Contains(edit.SpecialDependencies, d => d.CompilerIdentifier == "CompileSound");
        }
    }

    /// <summary>Input-dependency paths a compiled resource declares.</summary>
    private static List<string> ReadInputPaths(byte[] compiled)
    {
        using var res = new Resource();
        res.Read(new MemoryStream(compiled));
        return (res.EditInfo?.InputDependencies ?? [])
            .Select(d => d.ContentRelativeFilename)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }

    /// <summary>
    /// GROUND TRUTH: the invariant is Valve's, not ours. Sweeps the installed
    /// game's pak01 and asserts zero violations, so if a CS2 update ever changes
    /// how references are stated, this fails before our builders start
    /// "fixing" output to match a rule that no longer holds.
    /// </summary>
    [Fact]
    public void StockContent_HoldsTheReferenceInvariant()
    {
        var pak = CS2Fixtures.StockPak();
        if (pak is null) { CS2Fixtures.Skip("an installed CS2 pak01_dir.vpk"); return; }

        using var package = new Package();
        package.Read(pak);
        Assert.NotNull(package.Entries);

        var checkedFiles = 0;
        var violations = new List<string>();
        foreach (var ext in new[] { "vmat_c", "vmdl_c", "vpcf_c" })
        {
            if (!package.Entries.TryGetValue(ext, out var entries))
                continue;
            foreach (var e in entries)
            {
                if (checkedFiles >= 600)
                    break;      // a representative sweep, not the whole pak
                byte[] bytes;
                try { package.ReadEntry(e, out bytes); }
                catch { continue; }
                Resource res;
                try { res = new Resource(); res.Read(new MemoryStream(bytes)); }
                catch { continue; }
                using (res)
                {
                    checkedFiles++;
                    var byName = (res.ExternalReferences?.ResourceRefInfoList ?? [])
                        .Where(r => r.Name is not null)
                        .GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (var path in FlaggedRefs(DataRoot(res)))
                    {
                        if (!byName.TryGetValue(path, out var entry))
                            violations.Add($"{e.GetFullPath()}: '{path}' has no RERL entry");
                        else if (entry.Id != Source2ResourceId.ForPath(path))
                            violations.Add($"{e.GetFullPath()}: '{path}' id {entry.Id:x16} != {Source2ResourceId.ForPath(path):x16}");
                    }
                }
            }
        }

        Assert.True(checkedFiles > 100, $"only read {checkedFiles} stock resources - sweep did not run");
        Assert.Empty(violations);
    }

}
