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
    public const string UpstreamSha = "661a5f58961a3c88b1607de393a3198124ee622a";

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
