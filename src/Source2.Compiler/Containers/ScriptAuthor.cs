using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler;

/// <summary>
/// A cs_script source (.js) compiled to <c>.vjs_c</c> as resourcecompiler does it on a user's install, measured on
/// cs_script_demo's seven scripts with the 1.41.9.4 tools (2026-11-02, ledger 200): container version 4, blocks RED2
/// then DATA; DATA is the source byte for byte; RED2 records the source (CRC32) and an optional, absent
/// same-named <c>.vjs</c> under the addon's search path, and the CompileJavaScript identities, compiler version 4
/// and the Panorama preprocessor at fingerprint 0.
///
/// <para>Fingerprint 0 is the preprocessor's absence: RC looks for <c>panorama/preprocessor_config.txt</c> in
/// <c>core</c>, which no user install has. Valve's own build machine has it, so the scripts Valve ships record it as
/// an input (fingerprint 6) and their DATA has comments and <c>Console.Log</c> calls blanked to spaces. That pass is
/// not ported. A .ts source is refused, as a user's RC refuses it ("Failed to find compiler": its TypeScript
/// compiler loads a typescript.js no install has; ledger 200).</para>
/// </summary>
public static class ScriptAuthor
{
    /// <summary>The container header's version for <c>.vjs_c</c>.</summary>
    public const ushort ResourceVersion = 4;

    /// <summary>RED2's special dependencies, in RC's order.</summary>
    public static readonly Source2ContainerAuthor.SpecialDep[] SpecialDeps =
    [
        new("JavaScript Compiler Version", "CompileJavaScript", 4),
        new("Panorama Preprocessor Presence/Version", "CompileJavaScript", 0),
    ];

    /// <summary>Compile one script.</summary>
    /// <param name="relativePath">The source's path inside its mod tree, e.g. <c>maps/scripts/hello.js</c>.</param>
    /// <param name="searchPath">The mod tree, e.g. <c>csgo_addons/cs_script_demo</c>.</param>
    /// <param name="source">The source file's bytes.</param>
    public static byte[] Compile(string relativePath, string searchPath, byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!relativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{relativePath}: only .js sources compile (resourcecompiler on a user's install compiles no .ts; transpile it to .js)");
        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, ResourceVersion);
        var red2 = Source2ContainerAuthor.BuildBinaryEditInfo(relativePath, source, SpecialDeps, null,
            [relativePath[..^".js".Length] + ".vjs"], searchPath);
        resource.Blocks.Add(AuthoredKv3.Block(red2, KV3IDLookup.Get("generic"), BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, source, resource));
        // Zero padding between blocks, as RC writes it (VRF's writer pads with markers).
        return ValveLayout.Serialize(resource);
    }
}
