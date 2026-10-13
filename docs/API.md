# The compiler's API

Everything a host compiles goes through one entry point, `ResourceCompiler`
(`src/Source2.Compiler/Compiling`). The goal is that a host (vpkstuff, the CLI, anyone using the
package) never needs a second path: no VRF, no in-house builder, no per-type special cases. Each
source type is a registered `IResourceCompiler`, and each one's target is resourcecompiler's
bytes, 1:1. A new type is a registration; the map compiler joins the same way.

```csharp
var compiler = ResourceCompiler.CreateDefault();
var result = compiler.Compile(new CompileRequest("scripts/weapons.vdata", sourceBytes)
{
    SearchPath = "csgo_addons/my_addon",            // recorded in RED2, as RC records it
    Content = new DirectoryContentSource(contentRoot), // where referenced files are read
});
if (result.Succeeded)
    Save(result.OutputPath, result.Output);           // "scripts/weapons.vdata_c"
foreach (var d in result.Diagnostics)
    Log(d);                                           // "scripts/weapons.vdata(12,3): error: ..."
```

## Contract

- **Request.** A content-relative source path (forward slashes, the path RC would see), the
  source bytes, the search path, an optional `IContentSource` for files the source refers to,
  an optional output path, and optional per-type `Settings`.
- **Result.** The output path and bytes, any child resources, the input dependencies read back
  from the output's own RED2 (so they always agree with the file), and diagnostics.
- **Failures are diagnostics.** A source that cannot compile (bad KV3, an unknown type, an
  unreadable image) is an `Error` diagnostic with the source path; `Compile` does not throw for
  input. Exceptions mean a bug.
- **Stateless and thread-safe** once the registry is built. Compilers hold no state.
- **Deterministic.** The same request gives the same bytes on every machine (the BC7 encoder
  emulates one CPU vendor's reciprocal; see TEXTURES.md).

## Types

| Source | Compiler | Output | Matches resourcecompiler |
|---|---|---|---|
| `.vsndevts` `.vdata` `.vpcf` `.vagrp` | `Kv3DocumentCompiler` | `<source>_c` | byte for byte (`.vpcf`: current-format sources; legacy migration is PARTICLE_MIGRATION.md; `.vagrp` has no RC ground truth) |
| `.wav` | `SoundCompiler` | `.vsnd_c` | today's RC bytes |
| `.svg` | `VectorGraphicCompiler` | `.vsvg_c` | today's RC bytes |
| `.png` `.tga` `.jpg` `.bmp` `.webp` | `ImageTextureCompiler` | `.vtex_c` | no: the in-house path, until `.vtex` sources compile through the exact pipeline (TEXTURES.md) |
| `.vtex` | `RcTexture.VtexCompiler` | `.vtex_c` | yes, on the measured settings (RGBA8888, DXT1/5, ATI1N/2N, BC7; one 2D input, Box mips); anything else is refused (TEXTURES.md) |
| `.vmat` | `Materials.MaterialCompiler` | `.vmat_c` + child `.vtex_c` | yes on all 12 probes, children included: Box family, normal maps, alpha-weighted colour (MATERIALS.md) |
| `.vmdl` (editing) | `Meshes.CompiledModel` | `.vmdl_c` | yes: unedited models and re-stored buffers byte-identical on pak01 (meshopt v1 codecs); source compile is roadmap (MESHES.md) |
| map sources | later, from the map branch | children of `.vpk` | |

Editing an already compiled resource (keep unchanged blocks byte for byte, re-encode the edited
ones) is a separate API, `CompiledResource`; compiling and editing do not share an entry point.

## Order of work (2026-10-13)

1. This facade. Done.
2. `.vmat`, `.vmdl` and `.vtex` byte for byte. `.vtex`: done on the measured settings
   (TEXTURES.md). `.vmat`: the material and its child textures on every probe, normal maps and
   alpha-weighted colour included (MATERIALS.md). `.vmdl`: editing through `Meshes.CompiledModel` (MESHES.md); compiling from
   ModelDoc sources is roadmap.
3. A NuGet package. Packs, restores and publishes into vpkstuff's folder feed with
   `tools/publish-to-vpkstuff.ps1` (CONSUMING.md); the vendored library retires when vpkstuff
   switches to it (VPKSTUFF_PACKAGE_HANDOFF.md, phase 1). Public API tracking (PublicApiAnalyzers) is left out while the
   surface is 0.x and several agents edit it.
4. vpkstuff's call sites move onto the package, and VRF leaves vpkstuff
   (VPKSTUFF_PACKAGE_HANDOFF.md, phases 2 and 3; done by vpkstuff's side).
5. The map compiler, ported off VRF, registers its types.
