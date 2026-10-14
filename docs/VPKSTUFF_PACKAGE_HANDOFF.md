# Hand-off: vpkstuff takes Source2.Compiler as a package

For the vpkstuff agent doing the vpkstuff side. Written 2026-10-14 by the source2-compiler
session. Everything here is the vpkstuff repository; the compiler side (package, publish
command, adapter switch) is done and on `rc-standalone.git` `main`.

**Ground rules.** Never edit compiler code inside vpkstuff: change it in source2-compiler and
publish again. Stage and ship through vpkstuff's normal flow (staging, in-game check); this
work does not need a live deploy of its own. Commit with the noreply identity the box requires
(`git -c user.email=madheffer@users.noreply.github.com -c user.name=embattled commit ...`).

## What you are given

- **The package.** `Source2.Compiler` (`lib/net10.0` with XML docs, `runtimes/{win-x64,linux-x64}/native`
  holding the BC7 encoder `bc7e_s2c_{amd,intel}`; the PDB and every source embedded in the DLL, so
  stack traces carry file and line). Version
  `0.1.<commit count of source2-compiler main>`, one version per publish, never reused.
  Dependencies: SkiaSharp 4.153.1 (+ Linux NoDependencies), ValveKeyValue >= 0.70.0.499,
  ValvePak 6.0.0.182, ZstdSharp.Port 0.8.8, System.IO.Hashing 10.0.12, BCnEncoder.Net 2.3.0,
  Microsoft.Extensions.Logging.Abstractions 10.0.12. All already match vpkstuff's pins.
- **A branch with the first package in the feed.** `chore/compiler-<version>` (worktree
  `D:\_cursor projects\vpkstuff-wt-compiler-<version>`), made by
  `source2-compiler/tools/publish-to-vpkstuff.ps1`: `dotnet/packages/Source2.Compiler.<version>.nupkg`
  plus this document as `docs/SOURCE2_COMPILER_PACKAGE.md` (packages up to 0.1.100 also came with a
  `.snupkg`; later ones embed their symbols and have none). Build the switch
  (phase 1) on that branch.
- **The publish command** for every later update, run from source2-compiler `main`:
  `powershell -ExecutionPolicy Bypass -File tools\publish-to-vpkstuff.ps1`. Once phase 1 is
  merged it also bumps the pins, refreshes the vendored adapter and runs the compiler gate, then
  commits on a new `chore/compiler-<version>` branch. It never pushes unless `-Push`.
- **API stability.** Between the sha vpkstuff vendored (`f93414f`) and the first package no
  public member was removed or changed; everything is additive. The global aliases in
  `dotnet/shared/CompilerAliases.cs` keep working.
- **The GPU BC7 hook.** vpkstuff now vendors `94055dc` from `feat/gpu-bc7-hook` (`f93414f` plus
  one commit: `GpuBc7Encoder.Use(encoder, cacheTag)`, which vpkstuff calls at startup to install
  its Vulkan encoder). The same change is `80e733e` on `main`, so every package from `0.1.100` on
  carries it; do not switch to an older package, or the startup call will not compile.

## Phase 1: consume the package instead of the vendored library

The library moves to the package; the VRF adapter (`Source2.Compiler.Vrf`) stays a vendored
source copy, because it must compile against vpkstuff's patched VRF. It now builds against the
package when `Source2CompilerPackageVersion` is set (its csproj switches from a project
reference to a package reference).

1. **Feed.** Add `dotnet/nuget.config`:

   ```xml
   <configuration>
     <packageSources>
       <clear />
       <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
       <add key="source2-compiler" value="packages" />
     </packageSources>
     <packageSourceMapping>
       <packageSource key="source2-compiler"><package pattern="Source2.Compiler" /></packageSource>
       <packageSource key="nuget.org"><package pattern="*" /></packageSource>
     </packageSourceMapping>
   </configuration>
   ```

   The relative `packages` resolves against the config file, so it works in every worktree and
   in the container. Source mapping keeps `Source2.Compiler` from ever resolving off nuget.org.
2. **Pin.** In `dotnet/Directory.Packages.props` add, in its own labelled group with a short note
   pointing here: `<PackageVersion Include="Source2.Compiler" Version="<version>" />`. The
   publish command rewrites this line by its exact shape; keep it on one line.
3. **Reference.** In `VpkStuff.Pipeline.csproj` replace
   `<ProjectReference Include="..\vendor\Source2.Compiler\Source2.Compiler.csproj" />` with
   `<PackageReference Include="Source2.Compiler" />`. Keep the adapter's and VRF's
   ProjectReferences. Check Backend/Tests for any direct reference to the vendored library.
4. **Adapter props.** In `dotnet/vendor/Directory.Build.props`:
   - add `<Source2CompilerPackageVersion><version></Source2CompilerPackageVersion>` (the adapter
     is outside central package management, so it needs the version explicitly; the publish
     command keeps it equal to the pin);
   - remove `Source2CompilerValveKeyValueVersion` (it only fed the library's own source build);
   - keep `Source2CompilerVrfProject` and the CS8669 suppression only if the adapter still needs
     them (the CS8669 noise came from the library).
   `dotnet/vendor/Directory.Packages.props` (the CPM opt-out) stays, for the adapter.
5. **Remove the vendored library.** Delete `dotnet/vendor/Source2.Compiler/`.
6. **Sync script.** `deploy/sync-source2-compiler.py` vendors only the adapter now:
   - copy `src/Source2.Compiler.Vrf` as today; stop copying `src/Source2.Compiler`;
   - in `SOURCE2_COMPILER.json`, replace the library's `files` with
     `"package": {"id": "Source2.Compiler", "version": ..., "nupkgSha256": ...}` plus the source
     `sha`; keep `adapter`;
   - `--check` verifies the adapter tree, that `dotnet/packages` holds exactly the pinned
     version's `.nupkg`, that its SHA-256 matches the manifest, and that the pin,
     `Source2CompilerPackageVersion` and the manifest agree;
   - `--from <checkout>` should work as the publish command calls it (it runs it from the
     vpkstuff worktree root after placing the package; fill `nupkgSha256` from the file).
7. **Ship gate.** `deploy/ship-to-helm.sh` keeps calling `sync-source2-compiler.py --check`;
   update its die message to say "publish again from source2-compiler" instead of "re-sync".
8. **Dockerfiles** (`deploy/Dockerfile`, `dotnet/VpkStuff.Backend/Dockerfile`): in the build
   stage `COPY dotnet/nuget.config dotnet/packages/ ./dotnet/...` before `dotnet restore`, and in
   the runtime stage copy them next to `vendor/` too, since the Dependencies tab's trials restore
   and build inside the container. Restore with `-r linux-x64` already selects the package's
   `runtimes/linux-x64/native` libraries; confirm `libbc7e_s2c_amd.so` lands in `/app/publish`.
9. **Native BC7.** vpkstuff gccs its own `Native/bc7enc` for the library's older
   `Bc7Native`/`BuildTexture` path; keep it while that path is used (phase 3 retires it). The
   exact `.vtex` path uses the package's `bc7e` libraries and needs an x64 CPU with AVX2 and FMA
   (the worker's i5-8600 has both). vpkstuff's GPU encoder stays in vpkstuff and is installed
   through `GpuBc7Encoder.Use` as today; the package has no graphics dependency.
10. **Dependencies tab.** `VendoredCompiler` reads `SOURCE2_COMPILER.json`; show the package
    version and source sha from the new manifest shape, and keep its check-first trial working
    (it builds against `dotnet/packages`).
11. **Tests.** Update `CompilerVrfCompatTests` for the new layout (ValveKeyValue pin equality
    now concerns the package's lower bound 0.70.0.499; manifest shape). Then run the compiler
    gate (`CompilerVrfCompatTests`, the skin-build regression set, `AnimSet|AnimClip|ViewmodelAnimSet`)
    and the full suite. No compiled output should move in phase 1: the library code paths
    vpkstuff calls are the same code.

Done when: a fresh clone and the container restore `Source2.Compiler` from `dotnet/packages`,
`sync-source2-compiler.py --check` passes, the gate and full suite pass, and a staging smoke run
builds a skin, a glove and a model.

## Phase 2: move call sites off VRF (later, in slices)

vpkstuff uses VRF in about 129 files: mostly reading a compiled file into `Resource`, editing
KV3 trees, and writing it back through the adapter. The package now covers that without VRF.
Move one area at a time, each with its goldens; VRF and the adapter leave when nothing uses them.

| VRF / adapter today | package |
|---|---|
| `new Resource(); resource.Read(stream)` | `CompiledResource.Read(bytes)`; for models `Meshes.CompiledModel.Read(bytes)` |
| `resource.DataBlock` / `GetBlockByType(BlockType.X)` as `BinaryKV3.Data` | `res.Kv3("DATA")`, `res.Kv3("CTRL")`; repeated types (a model's MDAT per mesh) `res.Kv3At(index)` or `CompiledModel.Meshes[i].Data` |
| `VrfResourceBuilder.Serialize(resource)` | `res.Write()`: untouched blocks keep their bytes, edited KV3 blocks re-encode as RC does |
| `SerializeModelDataOnly` / `SerializeModelKeepBlocksLive` / `SerializeModelSingleBlock` | `CompiledModel.Write()` (the same rule, automatically: only what you edited re-encodes) |
| `VrfResourceBuilder.GetDataRoot(resource)` | `res.Kv3("DATA")` |
| `RebuildModelRaw(template, blocks)` | `res.SetRaw(type, bytes)` / `res.SetRawAt(index, bytes)`, then `Write()` |
| `resource.ExternalReferences` (RERL) | `res.References`, `res.SetReferences(paths)` (ids computed) |
| `resource.EditInfo` (RED2) | `res.Kv3("RED2")` |
| VBIB vertex/index buffers (decoded by VRF, written back uncompressed by `VmdlEditor`) | `CompiledModel.Meshes[i].VertexBuffers[j].Read()` / `.Write(bytes, count)`: re-encoded with meshopt exactly as RC does when RC had compressed it (smaller files; every pak01 buffer round-trips byte-identically) |
| `DirFileLoader` (VRF `IFileLoader`) | `IContentSource` (`DirectoryContentSource`, `MemoryContentSource`) |
| `VrfResourceDecompiler.DataBlockToKv3` | `ResourceDecompiler` (already in the library) |
| `StringToken.Get(name)` | `Shaders.ShaderExpression.ParamHash(name)` (MurmurHash2 of the lowercased name, seed 0x31415926) |
| `CompiledShader` (VCS reading) | `Shaders.ShaderProgram.Read(vcs)` (features/VS/PS: variables, combos, attributes, signatures) |

Not covered yet, so keep VRF there until asked upstream: decoding textures to pixels (previews,
glTF export), animation decoding, glTF model export, typed physics (`PhysAggregateData`),
particles, full VfxEval functions, a typed skeleton view (read `m_modelSkeleton` from DATA
meanwhile). Ask in source2-compiler for whichever blocks a slice; it is ported there, then
published.

Expected output changes in phase 2: models whose buffers vpkstuff edits get meshopt-compressed
buffers instead of uncompressed ones (smaller, as RC writes). Re-bless those goldens with that
cause and check the models in game.

## Phase 3: exact compiles for what vpkstuff builds (after phase 2)

Today vpkstuff builds textures with `ResourceBuilder.BuildTexture` (in-house, not RC-identical)
and materials with `ResourceBuilder.BuildMaterial` (from a template `.vmat_c`). The package
compiles both from sources byte for byte, through one entry point:

```csharp
var result = ResourceCompiler.CreateDefault().Compile(new CompileRequest(path, sourceBytes)
{
    SearchPath = "csgo_addons/<addon>",
    Content = new MemoryContentSource(),   // the images the .vtex/.vmat names
    Settings = new MaterialCompileSettings { ShaderPackage = ..., Mounts = [...] },  // .vmat only
});
// result.Output, result.Children (a .vmat's child textures), result.Diagnostics
```

- **Textures:** write a `.vtex` (KeyValues2 `CDmeVtex`, see the RcReference fixtures) and
  compile it. RGBA8888, DXT1/5, ATI1N/2N, BC7 are exact; unmeasured settings are refused.
- **Materials:** write a `.vmat` (`Layer0 { shader ...; TextureColor "..."; F_...; g_... }`) and
  compile it with `MaterialCompileSettings`. The material and its generated child textures are
  RC-identical (colour, metalness, translucency, normal maps with roughness). The worker needs
  CS2's `game/csgo/shaders_pc_dir.vpk` (+ its `_000..` parts) and, for shader defaults the
  material leaves unset, Valve's default images (`materials/default/default_*.tga` from the
  Workshop Tools content of `csgo` and `core`); the server install has neither, so they have to
  be provided on the worker (the user decides how; the compiled defaults also ship in pak01
  under the same hashed names).
- `BuildTexture`/`Bc7Native`/vpkstuff's `Native/bc7enc` retire once nothing calls them; every
  texture golden moves, so re-bless win-x64 and linux-x64 with the cause and check in game.
