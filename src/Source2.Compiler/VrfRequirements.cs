namespace Source2.Compiler;

/// <summary>
/// What this library needs from ValveResourceFormat, stated so a host that
/// supplies its own VRF can verify it rather than hope.
///
/// <para>Standalone, this is satisfied by <c>tools/vendor.cs</c>, which fetches
/// <see cref="UpstreamSha"/> and applies the patches in
/// <c>third_party/patches</c>. Embedded in a larger project, that project points
/// <c>Source2CompilerVrfProject</c> at its own patched VRF, and then nothing
/// checks the two agree unless something does so deliberately - which is what
/// this exists for.</para>
///
/// <para><b>Why a host's extra patches are not this library's problem.</b> A host
/// will usually carry more patches than <see cref="RequiredPatchIds"/>, for
/// features this library has nothing to do with. That is fine exactly as long as
/// those patches land outside the code this library can reach, and
/// <see cref="ReachableNamespaces"/> is the statement of what that reach is.
/// A host can check its extra patches against it mechanically; the reference
/// implementation is <c>CompilerVrfCompatTests</c> in the vpkeditor pipeline.</para>
/// </summary>
public static class VrfRequirements
{
    /// <summary>
    /// The upstream ValveResourceFormat commit this library is built and tested
    /// against. A host on a different commit is not necessarily broken, but it is
    /// untested; the patches below are expressed as literal snippets of this one.
    /// </summary>
    public const string UpstreamSha = "319b6b811f2b2aca7704f5b77e63c0868388fed4";

    /// <summary>
    /// The patches this library's behaviour depends on. A host's VRF must carry
    /// all of these - a superset is expected and fine.
    ///
    /// <para>Kept in step with <c>third_party/patches/index.json</c> by
    /// <c>VrfRequirementsTests</c>, so this cannot quietly fall behind the patches
    /// that actually exist.</para>
    /// </summary>
    public static readonly string[] RequiredPatchIds =
    [
        // KV3 bodies must be LZ4-compressed or CS2's material loader rejects them.
        "vrf-kv3-lz4-usings",
        "vrf-kv3-lz4-header",
        "vrf-kv3-lz4-body",
        // Authoring a .vsvg_c means replacing the Panorama payload and its CRC.
        "vrf-panorama-writable-data",
        // Authoring a container at all: Version is the one header field only Read() could set.
        "vrf-resource-version-settable",
        // Re-serializing a model must not drop its vertex buffers.
        "vrf-vbib-serialize-passthrough",
        // Reading community content: an all-bits UInt64 mesh-group mask, and
        // trailing padding after a texture's last mip.
        "vrf-kv3-integer-array-unchecked-uint64",
        "vrf-resource-texture-trailing-padding",
        // CS2's CTRL-block models ship empty LoD masks.
        "vrf-model-lod-mask-fallback-lod",
        // Guards, because this compiler is driven by files strangers upload.
        "vrf-kv3-depth-guard-field",
        "vrf-kv3-depth-guard-readbinaryvalue",
        "vrf-model-anim-include-cycle-guard",
        "vrf-vtex-dimension-guard",
    ];

    /// <summary>
    /// The VRF namespaces this library's code can reach. Anything outside them is
    /// code this library never executes, so a host's patches there cannot change
    /// what it produces.
    ///
    /// <para>Pinned by <c>VrfRequirementsTests</c> against the actual
    /// <c>using</c> directives in this project, so it describes the code rather
    /// than an intention about it.</para>
    /// </summary>
    public static readonly string[] ReachableNamespaces =
    [
        "ValveResourceFormat",
        "ValveResourceFormat.Blocks",
        "ValveResourceFormat.ResourceTypes",
        "ValveResourceFormat.Serialization.KeyValues",
        "ValveResourceFormat.Utils",
        // Only for DirFileLoader's IFileLoader implementation: the interface it
        // satisfies, and the ShaderCollection it declines to return. No type
        // declared under either is otherwise used.
        "ValveResourceFormat.IO",
        "ValveResourceFormat.CompiledShader",
    ];
}
