using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the KVObject API surface that the patched VRF + ValveKeyValue duo
/// expose. The vendored VRF carries the "KVObject API unification" patch
/// (see PATCHES.md) - KVObject is now a single value type that holds
/// scalars, collections, and arrays, instead of three separate KV*-shaped
/// classes. So any upstream change to that surface (a renamed indexer, a
/// re-introduced KVValue, a different ContainsKey signature) is the
/// highest-impact upgrade risk we can guard against here.
///
/// Each test exercises the public verbs the backend itself uses
/// (Kv3SourceCompiler + ResourceBuilder + the BinaryKV3 read path) so an
/// upstream re-vendor that still satisfies our patches passes vacuously.
/// The specific contracts pinned:
///   1. <see cref="BinaryKV3.Data"/>.Root is a usable KVObject after
///      Resource.Read().
///   2. KVObject ContainsKey / TryGetValue / indexer round-trip on a
///      collection-shaped KVObject we construct ourselves.
///   3. Array-shaped KVObjects expose IsArray + Count + iteration the way
///      the backend's serializer expects.
/// </summary>
public class KvObjectRoundTripTests
{
    /// <summary>A real BinaryKV3-backed resource out of the installed game, so
    /// the API surface is pinned against bytes Valve actually shipped rather
    /// than against something this project wrote itself.</summary>
    private static byte[]? FindKv3Fixture() => CS2Fixtures.Template(".vsndevts_c");

    [Fact]
    public void KvObject_ReadFromBinaryKv3_SurfacesNamedKeys()
    {
        var bytes = FindKv3Fixture();
        if (bytes is null)
        { CS2Fixtures.Skip("a compiled .vsndevts_c"); return; }
        Assert.NotEmpty(bytes);

        using var resource = new Resource();
        using var ms = new MemoryStream(bytes, writable: false);
        resource.Read(ms);

        var data = Assert.IsType<BinaryKV3>(resource.DataBlock);
        Assert.NotNull(data.Data);
        var root = data.Data.Root;
        Assert.NotNull(root);
        Assert.True(root.IsCollection,
            "Top-level KV3 DATA block should expose a collection-shaped Root - patched VRF returns a unified KVObject.");

        // Pin the public KVObject surface the backend reads through:
        //   • Keys
        //   • ContainsKey(string)
        //   • indexer access by string key returning a KVObject
        // These three are the API verbs every Kv3-touching code path uses.
        // If VRF or ValveKeyValue flips any of them (e.g. ContainsKey →
        // HasKey, Keys → KeyNames) this assertion stops compiling.
        var anyKey = root.Keys.FirstOrDefault();
        Assert.NotNull(anyKey);
        Assert.True(root.ContainsKey(anyKey!),
            "KVObject.ContainsKey returned false for a key it just listed - iteration/lookup contract diverged.");
        Assert.NotNull(root[anyKey!]);
    }

    [Fact]
    public void KvObject_CollectionConstructAndAdd_RoundTripsViaIndexer()
    {
        // Mirrors the construction shape Kv3SourceCompiler uses when it
        // builds the BinaryKV3 DATA block from a parsed text source. The
        // ctor + Add + implicit-conversion API needs to keep the same
        // names - KVObject is now a single value type, so an upstream
        // change that re-introduces KVValue would break this exact line.
        var obj = new KVObject();
        Assert.True(obj.IsCollection);

        obj.Add("name", "smoke");      // implicit conversion from string
        obj.Add("count", 42);            // implicit conversion from int
        obj.Add("ratio", 3.14);          // implicit conversion from double
        obj.Add("active", true);          // implicit conversion from bool

        Assert.True(obj.ContainsKey("name"));
        Assert.Equal("smoke", (string)obj["name"]);

        Assert.True(obj.ContainsKey("count"));
        Assert.Equal(42, (int)obj["count"]);

        Assert.True(obj.ContainsKey("ratio"));
        Assert.Equal(3.14, (double)obj["ratio"]);

        Assert.True(obj.ContainsKey("active"));
        Assert.True((bool)obj["active"]);

        // Negative path - ContainsKey reports absence correctly.
        Assert.False(obj.ContainsKey("absent"));

        // TryGetValue out-shape pinned: the backend's lookup helpers route
        // through this overload, so an upstream change to the signature
        // (e.g. dropping out-param for a Try-returns-T pattern) breaks
        // backend code at compile time.
        Assert.True(obj.TryGetValue("name", out var nameVal));
        Assert.Equal("smoke", (string)nameVal!);
    }

    [Fact]
    public void KvObject_ArrayShape_ExposesIsArrayAndCountAndIteration()
    {
        // Arrays are the second-most-common shape after collections - the
        // patch history shows several KVObject-array iterator changes
        // upstream, so we pin the contract: build an array via the static
        // factory, push items, read back via Count + Children, expect
        // same count + values.
        var arr = KVObject.Array();
        Assert.True(arr.IsArray);

        arr.Add(0);
        arr.Add(1);
        arr.Add(2);
        arr.Add(3);

        Assert.Equal(4, arr.Count);

        // Children is the canonical iteration surface for both shapes;
        // for arrays each KeyValuePair has the index as its key.
        var collected = arr.Children.Select(kv => (int)kv.Value).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3 }, collected);
    }
}
