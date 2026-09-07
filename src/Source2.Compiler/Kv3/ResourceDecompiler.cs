using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Decompile a compiled Source 2 resource's DATA block back to KV3 text — the
/// inverse of <see cref="Kv3SourceCompiler"/>. Single home for the typed-block
/// handling every decompile surface shares (a drop-a-file endpoint, a batch
/// worker, a VPK browser); reading a typed block's tree is the whole trick, and
/// it is easy to get wrong once per caller.
///
/// Why this is needed: <c>Block.AsKeyValueCollection()</c> only understands the
/// two generic KV blocks — <see cref="BinaryKV3"/> and <see cref="NTRO"/> — which
/// back the plain KV3 resources (.vdata_c, .vsndevts_c, .vagrp_c). A .vpcf_c /
/// .vmdl_c / .vmat_c instead parses into a strongly-typed
/// <see cref="KeyValuesOrNTRO"/> subclass (ParticleSystem / Model / Material /
/// World / PhysAggregateData). That block holds the SAME KV3 tree in its public
/// <c>.Data</c> property, but AsKeyValueCollection() throws on it
/// ("Cannot use ParticleSystem as key-value collection"). Reading <c>.Data</c>
/// directly is what lets those typed resources decompile.
///
/// A few DATA blocks genuinely have no KV3 form (Sound is binary audio metadata
/// plus a stream; Texture is pixel data). For those we return null and the caller
/// surfaces the reason it reports.
/// </summary>
public static class ResourceDecompiler
{
    /// <summary>
    /// Render the resource's DATA block as KV3 text. Returns the text on success,
    /// or null with a user-facing <paramref name="reason"/> when the block has no
    /// KV3 representation (or there is no DATA block at all).
    /// </summary>
    public static string? DataBlockToKv3(Resource resource, out string reason)
    {
        reason = string.Empty;

        var data = resource.DataBlock;
        if (data is null)
        {
            reason = "No DATA block in resource.";
            return null;
        }

        // KeyValuesOrNTRO subclasses expose the KV3 tree via .Data (their typed
        // identity is exactly why AsKeyValueCollection() throws on them). Plain
        // BinaryKV3 / NTRO keep the original, proven path so the already-working
        // types (.vdata_c / .vsndevts_c / .vagrp_c) produce byte-identical text.
        KVObject? kv = data is KeyValuesOrNTRO typed
            ? typed.Data
            : data is BinaryKV3 or NTRO ? data.AsKeyValueCollection() : null;

        if (kv is null)
        {
            reason = $"This resource type uses a typed data block ({data.GetType().Name}) " +
                     "that has no KV3 text representation.";
            return null;
        }

        return kv.ToKV3String();
    }
}
