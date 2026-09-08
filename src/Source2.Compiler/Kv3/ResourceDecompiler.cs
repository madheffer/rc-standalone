using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// Decompile a compiled Source 2 resource's DATA block back to KV3 text - the
/// inverse of <see cref="Kv3SourceCompiler"/>.
///
/// <para><c>Block.AsKeyValueCollection()</c> handles only the two generic KV
/// blocks, <see cref="BinaryKV3"/> and <see cref="NTRO"/>, which back
/// <c>.vdata_c</c>, <c>.vsndevts_c</c> and <c>.vagrp_c</c>. A <c>.vpcf_c</c>,
/// <c>.vmdl_c</c> or <c>.vmat_c</c> parses into a typed
/// <see cref="KeyValuesOrNTRO"/> subclass instead, which holds the same tree in
/// its <c>.Data</c> property but throws from AsKeyValueCollection(). Reading
/// <c>.Data</c> is what lets those decompile.</para>
///
/// <para>Some DATA blocks have no KV3 form at all - Sound is audio metadata plus
/// a stream, Texture is pixels. Those return null with a reason.</para>
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
