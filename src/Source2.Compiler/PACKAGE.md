# Source2.Compiler

Compiles and edits Counter-Strike 2 (Source 2) resources the way Valve's `resourcecompiler.exe`
does, byte for byte where it has been measured, with no Valve tools and no
ValveResourceFormat.

## Compile

One entry point for every source type:

```csharp
using Source2.Compiler.Compiling;

var compiler = ResourceCompiler.CreateDefault();
var result = compiler.Compile(new CompileRequest("materials/props/crate.vtex", File.ReadAllBytes(vtexPath))
{
    SearchPath = "csgo_addons/my_addon",              // recorded in RED2, as RC records it
    Content = new DirectoryContentSource(contentRoot), // where the .vtex's images are read from
});

if (!result.Succeeded)
    foreach (var d in result.Diagnostics) Console.Error.WriteLine(d);
File.WriteAllBytes(outputPath, result.Output!);
foreach (var child in result.Children) { /* write child.Path, child.Bytes */ }
```

Failures are diagnostics, not exceptions. A setting the compiler has not been measured on is
refused with an error naming it rather than guessed.

| source | output | settings type | byte-identical to RC |
|---|---|---|---|
| `.vdata` `.vsndevts` `.vpcf` `.vagrp` (KV3 text) | `_c` | | yes |
| `.wav` | `.vsnd_c` | `SoundSettings` | yes |
| `.svg` | `.vsvg_c` | `VectorGraphicSettings` | yes |
| `.vtex` | `.vtex_c` | `RcTexture.VtexCompileSettings` | yes, RGBA8888, DXT1/5, ATI1N/2N, BC7 |
| `.vmat` | `.vmat_c` + child `.vtex_c` | `Materials.MaterialCompileSettings` | the material on every probe; children for the Box mip family (normal maps not yet) |
| images | `.vtex_c` | `ImageTextureSettings` | no (the in-house path) |

A `.vmat` compile reads the game's compiled shaders (`MaterialCompileSettings.ShaderPackage`,
the game's `shaders_pc_dir.vpk`) and resolves shader default images through content mounts
(`csgo`, then `core`), as RC does.

## Edit

- `CompiledResource`: any compiled resource. KV3 blocks decode to trees you edit in place;
  `Write()` keeps every untouched block's bytes and re-encodes edited ones as RC would.
- `Meshes.CompiledModel`: a `.vmdl_c`'s trees and embedded meshes; vertex and index buffers
  decode and store again with meshoptimizer's codecs exactly as RC encodes them.

## Native code

BC7 runs Valve's encoder (bc7e) as a native library shipped in this package for win-x64 and
linux-x64 (`runtimes/<rid>/native`); it needs an x64 CPU with AVX2 and FMA. BC7 output follows
one CPU vendor's reciprocal estimate on every machine (`Bc7Vendor`, AMD by default). Images are
decoded with SkiaSharp.

## License

MIT. bc7e (bc7enc_rdo, Apache 2.0) and meshoptimizer's formats (MIT) are credited in the
repository.
