using System.Text;
using ValveKeyValue;
using ValveResourceFormat;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The write paths a host edits resources with give Valve's bytes back for anything they did not
/// change, and the decompile, edit, recompile path loses nothing. The stock sweeps behind these
/// are <c>s2c reserialize-parity</c> and <c>s2c recompile-parity</c>; these pin the three bugs they
/// found on committed files, so they run without a game install.
/// </summary>
public class RoundTripParityTests
{
    private static string RcReference(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "RcReference", name);
    }

    public static TheoryData<string> CompiledReferences => new()
    {
        "rc_probe.vsndevts_c", "rc_probe.vdata_c", "rc_probe_ints.vdata_c", "rc_probe_refs.vpcf_c",
    };

    /// <summary>
    /// <see cref="ResourceBuilder.Serialize"/> of a resource read and not changed is the file it read.
    /// It used to be VRF's container (an "S2V" padding marker) around VRF's KV3 writer, which turns
    /// typed arrays generic in RED2 and in typed DATA (materials, particles): 0 of 22,009 stock
    /// materials came back as they were.
    /// </summary>
    [Theory]
    [MemberData(nameof(CompiledReferences))]
    public void Serialize_gives_an_unchanged_resource_back_byte_for_byte(string name)
    {
        var original = File.ReadAllBytes(RcReference(name));
        using var resource = new Resource { FileName = name };
        resource.Read(new MemoryStream(original));
        Assert.True(ResourceBuilder.Serialize(resource).AsSpan().SequenceEqual(original), name);
    }

    /// <summary>An edited block is re-encoded; the blocks the edit did not touch stay Valve's bytes.</summary>
    [Fact]
    public void Serialize_re_encodes_only_the_block_an_edit_touched()
    {
        var name = "rc_probe_refs.vpcf_c";
        var original = File.ReadAllBytes(RcReference(name));
        using var resource = new Resource { FileName = name };
        resource.Read(new MemoryStream(original));
        var red2 = resource.Blocks.Single(b => b.Type == BlockType.RED2);
        var red2Bytes = original.AsSpan((int)red2.Offset, (int)red2.Size).ToArray();
        var data = (ValveResourceFormat.ResourceTypes.KeyValuesOrNTRO)resource.DataBlock!;
        data.Data.Add("s2c_edit_marker", new KVObject(7));

        var written = ResourceBuilder.Serialize(resource);
        using var back = new Resource { FileName = name };
        back.Read(new MemoryStream(written));
        var red2Back = back.Blocks.Single(b => b.Type == BlockType.RED2);
        Assert.True(written.AsSpan((int)red2Back.Offset, (int)red2Back.Size).SequenceEqual(red2Bytes), "RED2 should be untouched");
        Assert.Equal(7, (int)((ValveResourceFormat.ResourceTypes.KeyValuesOrNTRO)back.DataBlock!).Data["s2c_edit_marker"]!);
    }

    /// <summary>
    /// A quoted value is a string. ValveKeyValue's KV3 text reader types "false", "12" and "null"
    /// by their spelling; resourcecompiler keeps them strings (stock soundevents carry
    /// <c>_auto_calc_vnsd_duration_ = "false"</c> as a STRING).
    /// </summary>
    [Fact]
    public void Quoted_literals_compile_as_strings()
    {
        const string text = """
            <!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
            {
            	quoted_bool = "false"
            	quoted_int = "12"
            	quoted_null = "null"
            	bare_bool = false
            	bare_int = 12
            	in_array = [ "true", 3, "4.5" ]
            	ref = resource:"materials/x.vmat"
            }
            """;
        var compiled = Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(text), ".vdata", "scripts/q.vdata");
        using var resource = new Resource { FileName = "q.vdata_c" };
        resource.Read(new MemoryStream(compiled));
        var root = ((ValveResourceFormat.ResourceTypes.BinaryKV3)resource.DataBlock!).Data.Root;
        Assert.Equal(KVValueType.String, root["quoted_bool"]!.ValueType);
        Assert.Equal(KVValueType.String, root["quoted_int"]!.ValueType);
        Assert.Equal(KVValueType.String, root["quoted_null"]!.ValueType);
        Assert.Equal(KVValueType.Boolean, root["bare_bool"]!.ValueType);
        Assert.NotEqual(KVValueType.String, root["bare_int"]!.ValueType);
        Assert.Equal(KVValueType.String, root["in_array"]![0].ValueType);
        Assert.NotEqual(KVValueType.String, root["in_array"]![1].ValueType);
        Assert.Equal("4.5", (string)root["in_array"]![2]);
        Assert.Equal(KVFlag.Resource, root["ref"]!.Flag);
    }

    /// <summary>
    /// Hostile nesting is refused before anything recurses over the tree. ValveKeyValue parses any
    /// depth, and 200,000 nested brackets used to end the process with a stack overflow in
    /// CollectResourceRefs, which no catch stops: one upload could take a compile server down.
    /// </summary>
    [Fact]
    public void Deeply_nested_text_is_refused_not_a_crash()
    {
        const int n = 200_000;
        var text = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n{ a = "
            + new string('[', n) + new string(']', n) + " }\n";
        var ex = Assert.Throws<InvalidOperationException>(() => Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(text), ".vdata"));
        Assert.Contains("nests", ex.Message);

        // Brackets inside strings and comments do not count, and ordinary depth compiles.
        Assert.Equal(2, Kv3LineColumns.NestingDepth("{ a = \"[[[[\" // [[[[\n b = [ 1 ] /* {{{{ */ }"));
        var ok = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n{ a = "
            + new string('[', 200) + new string(']', 200) + " }\n";
        Assert.NotEmpty(Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(ok), ".vdata"));
    }

    /// <summary>
    /// ValveKeyValue's KV3 text reader allocates without end on a stray ';' or a C0 control
    /// character outside a string (found by <c>s2c fuzz</c>; 12.7 GB before the machine ran
    /// short). Such text is refused before it reaches the reader; inside strings and comments
    /// the same characters are fine.
    /// </summary>
    [Theory]
    [InlineData("{ a = 1; }")]
    [InlineData("{ a;b = 1 }")]
    [InlineData("{\u0002 a = 1 }")]
    [InlineData("{ a = 1 \u001f }")]
    public void Characters_that_hang_the_reader_are_refused(string body)
    {
        var text = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n" + body + "\n";
        var ex = Assert.Throws<InvalidOperationException>(() => Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(text), ".vdata"));
        Assert.Contains("stray character", ex.Message);

        var fine = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n"
            + "{ a = \"x;y\u0002z\" // ; \u0002\n b = 1 }\n";
        Assert.NotEmpty(Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(fine), ".vdata"));
    }

    /// <summary>
    /// A decompiled double reads back as the same double. ValveKeyValue prints six decimals, so
    /// 0.19607843137254902 came out 0.196078 and 1e-30 came out 0.0.
    /// </summary>
    [Theory]
    [InlineData(0.19607843137254902, "0.19607843137254902")]
    [InlineData(1e-30, "0.000000000000000000000000000001")]
    [InlineData(1.0, "1.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1.2345678901234568E+29, "123456789012345680000000000000.0")]
    public void Doubles_decompile_to_text_that_reads_back_exactly(double value, string expected)
    {
        Assert.Equal(expected, ResourceDecompiler.RoundTrip(value));
        var root = KVObject.Collection();
        root.Add("v", new KVObject(value));
        var arr = KVObject.Array();
        arr.Add(new KVObject(value));
        arr.Add(new KVObject(long.MinValue + 1_000_003));
        root.Add("a", arr);
        var text = ResourceDecompiler.ToKv3Text(root);
        var doc = KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits((double)doc.Root["v"]!));
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits((double)doc.Root["a"]![0]));
        Assert.Equal(long.MinValue + 1_000_003, (long)doc.Root["a"]![1]);
    }
}
