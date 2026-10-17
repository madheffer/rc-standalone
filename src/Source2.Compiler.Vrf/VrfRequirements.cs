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
/// <para>A host usually carries MORE patches than <see cref="RequiredPatchIds"/>, for
/// features this library never touches. That is fine as long as they land outside the
/// code it can execute, which is checkable without anything declared here: for each
/// extra patch, ask whether these sources name any type declared in the file it
/// targets. See <see cref="ImportedNamespaces"/> for why by type and not by
/// namespace.</para>
/// </summary>
public static class VrfRequirements
{
    /// <summary>
    /// The upstream ValveResourceFormat commit this library is built and tested
    /// against. A host on a different commit is not necessarily broken, but it is
    /// untested; the patches below are expressed as literal snippets of this one.
    /// </summary>
    public const string UpstreamSha = "d9926fcb7df821b8476f4750250d5dbf61a6bab7";

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
        // Authoring a .vsvg_c means replacing the Panorama payload and its CRC.
        "vrf-panorama-writable-data",
        // Authoring a container at all: Version is the one header field only Read() could set.
        "vrf-resource-version-settable",
        // Re-serializing a model must not drop its vertex buffers.
        "vrf-vbib-serialize-passthrough",
        // Reading community content: an all-bits UInt64 mesh-group mask.
        "vrf-kv3-integer-array-unchecked-uint64",
        // Guards, because this compiler is driven by files strangers upload.
        "vrf-kv3-depth-guard-field",
        "vrf-kv3-depth-guard-readbinaryvalue",
        "vrf-vtex-dimension-guard",
        // Sizes and counts from the file are checked before anything is allocated (s2c fuzz).
        "vrf-resource-block-count-guard",
        "vrf-rerl-count-guard",
        "vrf-kv3-size-guard",
        // The ceiling the size guard enforces, which VrfKv3ReadLimit scopes.
        "vrf-kv3-read-limit",
        "vrf-kv3-legacy-size-guard",
        "vrf-kv3-legacy-value-guard",
        "vrf-kv3-legacy-capacity-guard",
        // Any file through Resource.Read: block sizes within the file, and SNAP blocks bounded.
        "vrf-resource-block-bounds-guard",
        "vrf-particle-snapshot-size-guard",
        "vrf-particle-snapshot-count-guard",
        "vrf-kv3-string-count-guard",
        "vrf-kv3-capacity-array",
        "vrf-kv3-capacity-typed-array",
        "vrf-kv3-capacity-object",
    ];

    /// <summary>
    /// The VRF namespaces this library imports. Informational only.
    ///
    /// <para>Deliberately NOT the thing to check a host's extra patches against:
    /// namespaces are too coarse to express the reach. This imports
    /// <c>ValveResourceFormat.IO</c> solely so <c>DirFileLoader</c> can implement
    /// <c>IFileLoader</c>, and never touches <c>GltfModelExporter</c>, which lives
    /// in the same namespace. Judged by namespace, every glTF patch a host holds
    /// would look like a hazard, and a check that cries wolf gets switched off.</para>
    ///
    /// <para><b>Check by type instead.</b> A host has this library's sources; for
    /// each patch it holds beyond <see cref="RequiredPatchIds"/>, take the types
    /// declared in the file that patch targets and ask whether any of these
    /// sources name one. That answers "can this patch change what the compiler
    /// emits" exactly, and needs nothing declared here that could drift. The
    /// reference implementation is <c>CompilerVrfCompatTests</c> in the vpkeditor
    /// pipeline.</para>
    /// </summary>
    public static readonly string[] ImportedNamespaces =
    [
        "ValveResourceFormat",
        "ValveResourceFormat.Blocks",
        "ValveResourceFormat.ResourceTypes",
        "ValveResourceFormat.Serialization.KeyValues",
        "ValveResourceFormat.Utils",
        "ValveResourceFormat.IO",
        "ValveResourceFormat.CompiledShader",
    ];
}
