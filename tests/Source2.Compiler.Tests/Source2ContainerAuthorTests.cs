using System.IO.Hashing;
using System.Text;
using Source2.Compiler;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the donor-free container author (<see cref="Source2ContainerAuthor"/>)
/// to CS2's own resourcecompiler ground truth.
///
/// The <c>tests/Source2.Compiler.Tests/RcReference/</c> fixtures are REAL
/// resourcecompiler.exe inputs + outputs (compiled 2026-07-26 with the
/// shipping CS2 toolchain; the probe procedure is documented in
/// <c>tools/rc-oracle.ps1</c>). The structural
/// comparison below - resource version, block sequence, compiler identities +
/// fingerprints, input-dependency identity (name + source CRC), subasset
/// definitions, RERL entry set with engine path-hash ids, AND the decoded DATA
/// tree value-for-value including KV3 value types and flags - is what "our
/// compiler produces what Valve's compiler produces" means.
///
/// Byte equality is NOT the contract and is not attainable: RC writes KV3 binary
/// v5 (a two-buffer split layout) while VRF's writer emits v4, and even matching
/// that, the LZ4 payload would have to come out of a bit-identical encoder. The
/// decoded tree is the thing the engine actually consumes, so that is what is
/// pinned. See <c>docs/RC_PARITY.md</c> for the measured gap list.
/// </summary>
public class Source2ContainerAuthorTests
{
    private static string? FindRcReference(string fileName)
    {
        // Walk up from the test bin dir to the project's RcReference/ dir.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "RcReference", fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static Resource ReadResource(byte[] bytes, string name)
    {
        var res = new Resource { FileName = name };
        res.Read(new MemoryStream(bytes));
        return res;
    }

    [Fact]
    public void AuthoredVdata_Red2CarriesRealSourceIdentity_NoDonorMetadata()
    {
        var text = Encoding.UTF8.GetBytes(
            "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n" +
            "{\n\tgeneric_data_type = \"prop_data\"\n\tsome_entry = { base = \"\" }\n}\n");

        var compiled = Kv3SourceCompiler.Compile(text, ".vdata", "scripts/my_custom.vdata");
        using var res = ReadResource(compiled, "my_custom.vdata_c");

        Assert.Equal(0, res.Version);
        var edit = Assert.IsType<ResourceEditInfo2>(res.EditInfo);

        // The input dependency is THIS compile's source - name and CRC32 -
        // not the donor's ("scripts/weapons.vdata" was the old donor leak).
        var input = Assert.Single(edit.InputDependencies);
        Assert.Equal("scripts/my_custom.vdata", input.ContentRelativeFilename);
        Assert.Equal(Crc32.HashToUInt32(text), input.FileCRC);

        Assert.Contains(edit.SpecialDependencies, d => d.CompilerIdentifier == "CompileVData" && d.String == "VData Compiler Version");
        Assert.Contains(edit.SpecialDependencies, d => d.CompilerIdentifier == "CompileVData" && d.String == "KV3 Compiler Version");
        Assert.DoesNotContain(edit.SpecialDependencies, d => d.CompilerIdentifier == "CompileSoundEventScript");
    }

    [Fact]
    public void AuthoredVsndevts_DefinesItsSoundEventsAsSubassets()
    {
        var text = Encoding.UTF8.GetBytes(
            "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n" +
            "{\n\tmy.event.one = { type = \"csgo_mega\" volume = 1.0 }\n\tmy.event.two = { type = \"csgo_mega\" volume = 0.5 }\n}\n");

        var compiled = Kv3SourceCompiler.Compile(text, ".vsndevts", "soundevents/my_events.vsndevts");
        using var res = ReadResource(compiled, "my_events.vsndevts_c");

        Assert.Equal(1, res.Version);
        var edit = Assert.IsType<ResourceEditInfo2>(res.EditInfo);

        // resourcecompiler records every defined sound event as a subasset
        // definition - the old donor path shipped the DONOR's event list.
        Assert.NotNull(edit.SubassetDefinitions);
        var events = Assert.Contains("soundevent", edit.SubassetDefinitions!);
        Assert.Equal(["my.event.one", "my.event.two"], events);
    }

    [Fact]
    public void VsndText_IsRejectedWithAudioRouteHint()
    {
        var text = Encoding.UTF8.GetBytes(
            "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n{\n}\n");

        var ex = Assert.Throws<InvalidOperationException>(() => Kv3SourceCompiler.Compile(text, ".vsnd"));
        Assert.Contains("audio container", ex.Message);
    }

    public static TheoryData<string, string, string> RcReferenceCases => new()
    {
        { "rc_probe.vsndevts", "rc_probe.vsndevts_c", "soundevents/rc_probe.vsndevts" },
        { "rc_probe.vdata", "rc_probe.vdata_c", "scripts/rc_probe.vdata" },
        { "rc_probe_ints.vdata", "rc_probe_ints.vdata_c", "scripts/rc_probe_ints.vdata" },
        { "rc_probe_refs.vpcf", "rc_probe_refs.vpcf_c", "particles/rc_probe_refs.vpcf" },
    };

    /// <summary>Canonical one-line-per-leaf rendering of a KV3 tree: full path,
    /// KV3 value type, KV3 flag, value. Comparing these strings catches a wrong
    /// value, a wrong integer width, a dropped resource flag, and a reordered
    /// array - all of which a plain "does it parse" check sails past.</summary>
    private static string RenderTree(Resource res)
    {
        var data = res.GetBlockByType(BlockType.DATA);
        var root = data is KeyValuesOrNTRO kvn ? kvn.Data : data!.AsKeyValueCollection();
        var sb = new StringBuilder();
        Render(root, "", sb);
        return sb.ToString();

        static void Render(KVObject? n, string path, StringBuilder sb)
        {
            if (n is null) { sb.AppendLine($"{path} = <null>"); return; }
            if (n.IsArray)
            {
                var i = 0;
                foreach (var c in n.Values)
                    Render(c, $"{path}[{i++}]", sb);
                if (i == 0)
                    sb.AppendLine($"{path} = []");
                return;
            }
            if (n.IsCollection)
            {
                var any = false;
                foreach (var c in n.Children) { any = true; Render(c.Value, $"{path}.{c.Key}", sb); }
                if (!any)
                    sb.AppendLine($"{path} = {{}}");
                return;
            }
            var val = n.ValueType == KVValueType.String ? $"\"{(string)n}\"" : n.ToString();
            sb.AppendLine($"{path} = [{n.ValueType}{(n.Flag == KVFlag.None ? "" : "/" + n.Flag)}] {val}");
        }
    }

    [Theory]
    [MemberData(nameof(RcReferenceCases))]
    public void AuthoredContainer_MatchesResourceCompilerReference(string sourceName, string compiledName, string relativePath)
    {
        var srcPath = FindRcReference(sourceName);
        var refPath = FindRcReference(compiledName);
        if (srcPath is null || refPath is null)
        {
            Console.WriteLine($"[SKIP] test/rc-reference/{sourceName} not present.");
            return;
        }

        var sourceBytes = File.ReadAllBytes(srcPath);
        var ext = Path.GetExtension(sourceName);
        var ours = Kv3SourceCompiler.Compile(sourceBytes, ext, relativePath);

        using var mine = ReadResource(ours, compiledName);
        using var valve = ReadResource(File.ReadAllBytes(refPath), compiledName);

        // Container identity.
        Assert.Equal(valve.Version, mine.Version);

        // Block sequence - minus FLCI, the editor-only source-line map we
        // deliberately don't author (pre-FLCI stock resources load without it).
        var valveBlocks = valve.Blocks.Select(b => b.Type).Where(t => t != BlockType.FLCI).ToArray();
        var myBlocks = mine.Blocks.Select(b => b.Type).ToArray();
        Assert.Equal(valveBlocks, myBlocks);

        // RED2: compiler identities + fingerprints must match Valve's exactly.
        var mySpecial = mine.EditInfo!.SpecialDependencies.Select(d => (d.String, d.CompilerIdentifier, d.Fingerprint)).ToArray();
        var valveSpecial = valve.EditInfo!.SpecialDependencies.Select(d => (d.String, d.CompilerIdentifier, d.Fingerprint)).ToArray();
        Assert.Equal(valveSpecial, mySpecial);

        // RED2: same input-dependency file list (we brand m_SearchPath as the
        // stock "csgo" content root rather than the addon RC compiled from, so
        // paths - not search roots - are compared), and the SAME source CRC on
        // the primary entry as resourcecompiler recorded.
        var myInputs = mine.EditInfo!.InputDependencies.Select(d => d.ContentRelativeFilename).ToArray();
        var valveInputs = valve.EditInfo!.InputDependencies.Select(d => d.ContentRelativeFilename).ToArray();
        Assert.Equal(valveInputs, myInputs);
        Assert.Equal(valve.EditInfo!.InputDependencies[0].FileCRC, mine.EditInfo!.InputDependencies[0].FileCRC);

        // Subasset definitions (vsndevts: the defined event names).
        var myDefs = (mine.EditInfo as ResourceEditInfo2)?.SubassetDefinitions;
        var valveDefs = (valve.EditInfo as ResourceEditInfo2)?.SubassetDefinitions;
        Assert.Equal(valveDefs is null, myDefs is null);
        if (valveDefs is not null)
        {
            Assert.Equal(valveDefs.Keys.OrderBy(k => k), myDefs!.Keys.OrderBy(k => k));
            foreach (var kind in valveDefs.Keys)
                Assert.Equal(valveDefs[kind].OrderBy(v => v), myDefs[kind].OrderBy(v => v));
        }

        // RERL: every entry resourcecompiler recorded must be present with the
        // SAME engine path-hash id. Ours may be a superset: RC consults the
        // content tree at compile time and silently DROPS a renderer material
        // it cannot resolve (observed: this probe's bendibeam.vmat, absent
        // from the probe addon, is missing from RC's RERL while the equally
        // unresolvable child-vpcf refs are kept). Our compile has no content
        // tree, so it lists every resource:-flagged ref - which matches RC
        // exactly when the refs resolve (pinned by
        // Kv3SourceCompile_Vpcf_RerlSynthesisReproducesStockRerl on stock
        // blood_impact_basic) and over-declares only what RC would have
        // dropped as missing anyway.
        var myRerl = (mine.ExternalReferences?.ResourceRefInfoList ?? []).ToDictionary(r => r.Id, r => r.Name);
        var valveRerl = valve.ExternalReferences?.ResourceRefInfoList ?? [];
        foreach (var entry in valveRerl)
        {
            var name = Assert.Contains(entry.Id, (IDictionary<ulong, string>)myRerl);
            Assert.Equal(entry.Name, name);
        }

        // DATA tree, value-for-value with KV3 value types and flags. This is the
        // part the engine actually consumes, and until 2026-08-10 nothing
        // compared it: the compiler was typing every integer UInt64 where RC
        // types Int32 / Int64-zero-one, in every KV3 compile we shipped, and the
        // structural checks above all passed anyway.
        //
        // .vpcf is exempt and stays exempt: RC does not merely recompile a
        // vpcf26 source, it MIGRATES it to the current particle schema -
        // rewriting operator classes (C_INIT_RandomLifeTime -> C_INIT_InitFloat,
        // C_OP_DistanceToCP -> C_OP_DistanceToTransform), restructuring fields
        // (m_bAdditive -> m_nOutputBlendMode, m_hMaterial -> m_vecTexturesInput)
        // and stamping the migrated format GUID. We do not implement that
        // migration, so a RAW legacy vpcf source compiles to the legacy schema
        // under its own declared GUID. The product path (decompile a compiled
        // vpcf, edit, recompile) starts from already-migrated text and is
        // unaffected. Tracked in docs/RC_PARITY.md.
        if (!sourceName.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(RenderTree(valve), RenderTree(mine));
        }
    }
}
