# Lighting roadmap

Where our own lighting stands after 2026-10-18, what to do next, and every
finding of the 10-18 parity review. The goal for the next stretch is to take
the tracker's "Own lighting (vrad3 port)" module from 60% to about 80%. It is
also to land the fixes below, so that resourcecompiler's lightmap side
(textures, query data, atlases) is exact on more than probe01's defaults.

Rules that apply throughout:
- Every rule is settled from the DLLs (oracle, capture or decompile) and
  entered in docs/GROUND_TRUTH.md.
- Addresses go in tools/re/names/make_manual.py, then docs/ADDRESSES.md.
- After each big piece, re-read the port beside Valve's decompile.

## Where it stands

- **Lightmap bake** (`Lighting/Bake`):
  - Valve's own raygen and material hit groups run as C# on the CPU
    (`tools/re/glsl_to_cs.py`).
  - probe01 and cardtest match vrad3's raw image within 0.1% (block 0,
    ratio 0.999).
  - ze_hold_em_p is solved (see M1): block 5 goes from 1.41x to 0.987x of
    Valve's raw image.
- **Light probe volumes**: complete for the ambient cube type. probe01's
  solid masks equal Valve's, and the ambient cubes are within 0.2%.
  ze_hold_em_p's ambient cubes are 1.12 to 1.22x Valve's. That is most
  likely the same blend cause as M1, but it is not checked yet.
- **resourcecompiler side**: `LightmapTextures`, `LightmapQueryData`,
  `LightProbeAtlasImages` and `LightmapOutputs` write all seven
  `maps/<map>/lightmaps/` files byte for byte on probe01 at default
  settings. The review (F-numbers below) found that the non-default paths
  are not right yet:
  - `-lightmapMaxResolution 8192` and other mip counts above one;
  - `-lightmapVRadQuality 2`;
  - `-lightmapCompressionDisabled`.

  Valve's builds with each option are now captured. On the 8192 one, all
  seven of our files differ.
- **Not ported**:
  - far field and fog irradiance compute;
  - the LPV SH paths (`lpv_type sh2`);
  - light cookies (sampled as 1 everywhere);
  - subsurface;
  - a GPU path (for speed and AMD).

## Milestones to 80%

Percentages are judgement, weighted by remaining work.

### M1. Land the blend fix (+4%): done 2026-10-18

The -1 semantic index was not the rule. scenesystem's loader (1800912d0)
builds each mesh's input layout from the mesh's own material's input
signature (1804aaf20 / 1804aa730) and packs the vertices per format
(1804abf60 / 1804ac280); `SceneVertexLayout` ports it (ledger 75), and
`MapBySignature` is gone.
- ze_hold_em_p's 16 blocks are at 0.98 to 0.997 of `pre_post`.
- Its eight LPVs are at 0.94 to 0.997, near-black volumes aside (the M3 fixes
  then took them to 0.997 to 1.000).
- The only_c12 / only_c9 crash is fixed: the material block is padded to the
  hit group's struct size (csgo_environment reads its last pad).

**The cause.** `concrete_slabs_wet_1.vmat` (csgo_simple_2way_blend, ze's
floor) reads its blend weights from texcoord slot 4. In scene_meshes.kv3
that field is named `VertexPaintBlendParams`, with semantic index 0.
`RtxScene.Geometry` (`Lighting/Bake/RtxMaterials.cs`) maps only fields
named texcoord, color, normal and tangent, so the blend stayed pure layer A.

**What scenesystem does.** Its GeometryInfo builder (180093f10) handles a
layout field whose semantic index is -1 differently. It looks the field's
name up in the material's input signature (0xc4-byte entries: name,
Semantic at +0x40, D3DSemanticName at +0x80, D3DSemanticIndex at +0xc0) and
binds it as that D3D semantic. Our experiment, behind
`RtxScene.MapBySignature` / `CPUBAKE_SIGMAP=1`, does the same lookup for
every field not named by a D3D semantic. It reads the signature with
`MaterialAuthor.ExtractInputSignature`.

The experiment's results:

| case | before | with the experiment |
|---|---|---|
| `only_slabs` variant | 1.42x | 0.99x |
| ze block 5 | 1.41x | 0.987x |
| probe01 and cardtest | 0.999x | 0.999x (unchanged) |

To do:
1. **Settle where the -1 comes from.** vrad3's scene_meshes reader
   (1801fd4e0, vrad3 1001) keeps `semantic_index` as read, and 1801fd090
   copies the records. So the -1 is set later, where the render mesh's input
   layout is made from them: a vrad3 caller of 1801fd4e0 (18002bf6f), or
   meshsystem or rendersystem. 180158730 is the DMX path, not this one. Read
   it, port the exact condition (which fields, which material, the format
   mask 0x2410010004 scenesystem applies to texcoords), and record it in
   the ledger.
2. **Make the mapping the default.** Remove the switch, and keep the
   per-mesh material choice Valve uses (the first instance's material is the
   experiment's guess).
3. **Bake ze_hold_em_p whole** against
   `tools/re/out/captures/ze_hold_em_p/pre_post` (all 16 blocks, the dev box
   for the full image). Then bake its eight LPVs (`CPUBAKE_LPV=all`) against
   the job's own `lpv_*` EXRs, to see whether the 1.12 to 1.22 excess also
   goes.
4. **Fix the crash** the material swaps showed: IndexOutOfRange in
   `CsgoEnvironmentS0.ClosestHit`, line 140, via
   `LightmapBaker.Validate:404`. It happens with scenes `scene_info_only_c12`
   and `scene_info_only_c9`, where csgo_environment is the only material
   with a hit group. The real maps do not trip it, which suggests a material
   block or bindless index assumption.

### M2. resourcecompiler lightmap outputs at every option (+4%): done 2026-10-18, two encoders open

Ledger 79. `CpuBakeProbe.LightmapOutputsFiles` (with `LMTEX_ARGS` for the
compile's arguments) against Valve's builds:
- **Byte-exact in all seven files:** probe01 default,
  `-lightmapMaxResolution 8192` and `-lightmapCompressionDisabled`.
- **ze_hold_em_p** (eight volumes): exact apart from 2 BC7 blocks of the
  dlshd atlas.
- **`-lightmapVRadQuality 2`:** exact apart from two encoders.
  - The BC6H hue-shift fixup encoder (180c46dd0, 5 of 1,048,576 irradiance
    blocks) is not ported; the note says so.
  - bc7e gives 5 different blocks of the dlshd atlas. Its input fill
    (180c366a0, cvtss2si) matches ours, so the difference is in main's
    native bc7e.

Verified and fixed:
- F01/F17 (final per quality, hueShiftFixup on BC6H);
- F02, F03, F05, F06, F07;
- F04: lightmapMaxResolution and fast come from the command line;
- F12/F16 (the query uv side);
- F15 (the atlas size is the allocator's last fit rounded to 4);
- F18 (uncompressed formats);
- F25 (the BC6H row fill truncates; exact on EXR halves, wrong on mip 1);
- F13, F20, F21 (the probe volume atlas side);
- the settings plumbing (`LightmapOutputs.Options` from MapCompile).

New rules, not in the review:
- "Texture Compiler Version Mip Box" and the encode quality as special
  dependency user data;
- the smallest mip's reflectivity;
- per-mip LZ4 sizes;
- final's quality 4 (LZ4 HC 12, BC6H veryslow);
- vrad3's chart_repacking.dat applied to the query data's uvs at quality
  1/2 (18020e230 / 18020e4e0).

Open: F19 (the world node with `-vrad3output`), F08/F09/F22/F26 (latent),
and F27 to F29 and F33 in main's texture files.

Fix and verify against the three captured Valve builds
(`tools/re/out/captures/probe01_options/<name>.vpk`, their `.vpk.vrad3` job
folders beside them). Use `CpuBakeProbe.LightmapOutputsFiles`. Its
`LMTEX=<job folder>|<vpk>|<map>` takes the addon from the vpk's path, so put
the vpk at `.../s2c_rc_probe/maps/probe01.vpk`. In Git Bash use Windows paths
with `MSYS_NO_PATHCONV=1`.

- **maxres8192**: F02, F03 (min dimension and the Box<n> mip algorithm after
  the halving), F05, F06, F12/F16 (the query data's uv scale from the halved
  side), F15 (atlas size from the allocator, rounded to 4) and F04 (RED2's
  `lightmapMaxResolution` argument; all seven files differ, the atlases
  included).
- **quality2**: F01/F17 (`final` per quality, `hueShiftFixup` on BC6H, and
  whether the texture compiler's hue-shift fixup changes the BC6H blocks).
- **nocompress**: F18 (uncompressed formats, the `UncompressedFormat` key).
- **Settings plumbing**: `LightmapOutputs.FromVrad3` takes no map settings.
  It needs the mip count, MinSpecLightmapSize, quality, compression and the
  LPV atlas switch (F22), passed in from MapCompile.
- **F19**: with `-vrad3output` the world node is still the unbaked one.
  Decide what a baked world node needs: lightmap UVs, the bake keys.
- **F25, BC6H half conversion**: the review says Valve truncates to half
  and our port rounds to nearest even. Treat this with suspicion, because
  probe01's irradiance is byte-exact. Check it against the disassembly
  before changing anything.

Main's texture files (`Lz4`, `Texture/Rc/*`, `Native/bc7e`) are copies of
main's. Any fix there has to land on main too, or stay identical to it.

### M3. LPV bake fidelity (+3%): done 2026-10-18

F35, F36, F41, F42, F43, F44 and F45 are verified against the
disassembly and fixed (ledger 76). F40 stays latent (needs err > 0). With
M1's layout, ze_hold_em_p's eight volumes are at 0.997 to 1.000 of Valve
after the post (the 1.12 to 1.22 excess is gone). The remaining per-texel
difference looks like GPU float evaluation (not isolated yet); see ledger
76 on what byte parity of baked lighting can mean.


- **F35**: the SFMT draw scale. vrad3 multiplies by 0x2f7ffffd, not 2^-32.
  Confirm it in the disassembly; probe01's masks may be insensitive to it.
- **F36**: the convergence test runs once per 16-pass command list, not
  before every pass.
- **F41**: the origin cell when floor() overflows. cvttss2si gives
  0x80000000, which clamps to cell 0.
- **F40**: lanes past X*Y*Z in the convergence test read stale luxels
  (a 96^3 buffer, a 32^3 converged texture).
- **F43**: a NaN luminance is processed, not skipped.
- **F42**: the AO pow should use the V_powf port (`Maps.LightPow.Pow`).
- **F44**: f16 multiplies in the transpiled LPV raygen must round to half.
  Also check the lightmap raygen and the hit groups for the same pattern.
- **F45**: the transpiler aliases GLSL arrays; they must copy.

### Lightmap bake, 2026-10-18 (after M3)

- Materials without an rtx program run Valve's rtx_default_hitgroup with
  180098cd0's block, and samples go through Valve's own validation shader
  (ledger 77): ze_hold_em_p block 5's luxel normals now equal Valve's raw
  image, AO correlation 0.967, probe01's probe volumes 32 to 50% of texels
  bit-identical.
- Open: the irradiance noise per luxel follows Valve's only without
  textured materials (ledger 78). Next step: GPU-side ground truth for one
  material's hit (capture or a probe shader on the RTX 4070).

### M4. Unported vrad3 features (+9%): not started, needs specimens

Take these in the order the ze maps need them. As of 2026-10-18 no map the
user has sets a light cookie (every lightcookie key is empty), and
neither ze_hold_em_p's nor probe01's script has fog or far field routines.
Each feature needs a specimen map and a CS2-closed capture first.
- **Cookies** (F37): `TextureLod2D` returns 1 in every host. Bind the
  cookie textures as vrad3 does. ze_hold_em_p has none, so find a specimen
  map first.
- **LPV SH paths** (F38): `lpv_type sh2`, the SH images in the readback.
- **Fog** (F39) and **far field compute**: `lpv_compute_block_fog_gpu`,
  the far field raygen and metrics, and resourcecompiler's
  env_farfield_<n> textures (F23 notes they share the lightmaps folder).
- **Subsurface.**

### Latent and doc items (with each area)

Fix these alongside the area they belong to:
- **Latent** (paths no current map takes): F07 to F09, F13, F20, F21, F24,
  F26, F30 to F32.
- **Wrong comments or citations**: F10, F11, F14, F27 to F29, F33, F34.
  `docs/TEXTURES.md`, which `AtiCompress`, `RcMipChain`, `Bc7e` and
  `LightmapTextures` cite, moved to source2-assets in the split (7cb1be2).

## Captures (CS2-closed window, 2026-10-18)

All under `tools/re/out/captures/` (git-ignored). Valve's bake is
deterministic: the same scene gives the same image, ratio 1.0000.

- **`ze_hold_em_p/`**:
  - `pre_post`: vrad3's image saved right after
    `compute_all_lightmap_blocks`, all 13 channels.
  - `_vrad3`: the whole job; copy it back into the addon's `_vrad3` to rerun
    vrad3.
  - Scene variants, each with its `scene_info_<name>.kv3`,
    `script-<name>.vrad3` and image (channels 00, 01 and 08 kept):
    `novol` (illumination volumes removed: no effect), `refl30all`,
    `refl30concrete`, `light0`, `light0refl30`, and `only_<c12 | tintdark |
    slabs | c9 | lights>`. An `only_` variant keeps that material real and
    sets every other material to reflectivity_30.
- **`probe01/pre_post`** and **`cardtest/pre_post`**: their raw images.
  The jobs are in `%TEMP%/vrad3_refs/<map>.vpk.vrad3`.
- **`probe01_options/`**: Valve's full probe01 builds with
  `-lightmapMaxResolution 8192`, `-lightmapVRadQuality 2` and
  `-lightmapCompressionDisabled` (package, log, vrad3 job).

Tools added:
- `tools/vrad3/vrad3_variant.py` edits a scene (strip keys, swap materials,
  keep lights) and saves Valve's pre-post image. It takes an addon or a job
  folder; vrad3 only runs inside the game tree.
- `tools/vrad3/exr_ratio.py` gives per-channel ratios between two images.
- In `CpuBakeTests.Bake`:
  - `CPUBAKE_PREPOST=<image>` compares a block raw against such an image
    (channels 0, 1, 8);
  - `CPUBAKE_SCENE=<file>` bakes a variant scene;
  - `CPUBAKE_INSTANCES=1` now lists each mesh's fields;
  - the bake runs on a 256 MB stack, because RayScene.Build overflows
    Windows' 1 MB.

A ze block takes about 40 s locally on 14 threads.

## Review findings (2026-10-18)

Nine read-only reviewers compared this session's ports with Valve's saved
decompiles (`tools/re/out/lmtex`, `tools/re/out/vrad3re/dec`,
`tools/re/out/review`, `tools/re/out/ssre`), with the upstream sources
(meshoptimizer v1.2, Compressonator V2.3.2953) and with capstone
disassembly.

**Every finding below is the reviewer's claim. None is verified yet**, apart
from the M2 options, whose effect the 8192 build confirms in aggregate.
Verify each against the decompile or disassembly before changing code.

Severity:
- **parity-break**: changes output on some input.
- **latent**: only on inputs no current map has.
- **doc**: a wrong comment or citation.

### Lightmap textures (LightmapTextures.cs)

**F01** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:495`  
'final' and 'hueShiftFixup' are always written with fingerprint 0. SerializeLightMap sets final to a per-quality byte, and sets hueShiftFixup=1 for BC6H when final is on, so -lightmapVRadQuality 2 builds differ.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40_tail.c:43-55; disasm 1801f4dc9-1801f4de8 and 1801f4e38-1801f4e4e; table 0x182a88554  
*Evidence:* Valve: vf 0x20 (plVar15, "final", (&DAT_182a88554)[param_3], -1) with param_3 = lightmapVRadQuality; the bytes at 182a88554 are 00 00 01, so final = 1 at quality 2. Then: if FUN_180c490a0(format) == 0x1a && GetInt("ResourceCompiler/BakedLighting/BC6HHueShiftFixup", 1) != 0 && final, vf 0x20("hueShiftFixup", 1, -1). Port: args.Add(Argument("final", argument.FinalType, 0)); args.Add(Argument("hueShiftFixup", "FloatArg", 0)); Vtex takes no quality input. At quality 2 every lightmap's RED2 differs, and irradiance (BC6H) gets the texture compiler's hue-shift fixup, which the port's BC6H encode does not do.

**F02** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:136`  
m_nOutputMinDimension should be min(atlas w, h) >> (mipCount-1), taken before any halving. The port uses the shorter side after halving, so the override CRC is wrong whenever the mip count is above 1.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:128-132; disasm 1801f40d1-1801f40ee (cmovae/shr edx, cl with cl = param_5-1); param_5 = mip count iVar33, f_atlas_caller.c:182-239  
*Evidence:* Valve: uVar27 = min(param_1[0x12], param_1[0x13]); local_1ff0 = uVar27 >> (param_5 - 1), set before the MaxResolution loop and never updated. Port: Descriptor(..., Math.Min(image.Width, image.Height), ...) after the QuarterSize loop, with no mip count. CS2 cases: an 8192 atlas (MinSpecLightmapSize 4096 gives mips=2) puts irradiance/shadows at Valve 4096 vs port 8192; lightmapMipCount 2 at 4096 gives 2048 vs 4096 for every channel.

**F03** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:136`  
The output channel's mip algorithm should be "Box<n>" when the mips left after halving are above 1, for every channel except debug_chart_color. The port always writes "None".  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:438-440; disasm 1801f497f-1801f4995 (r8d=[rsp+0x3c], rcx=r13+0x10, "Box%d"), reached via 1801f495c/1801f496d for irradiance, directional_irradiance, sh2_* and shadows; debug_chart_color jumps past it (1801f48c0 -> 1801f499b)  
*Evidence:* Valve: if (1 < local_204c) CUtlString::Format(outputChannel+0x10 /*m_mipAlgorithm.m_algorithm*/, "Box%d", local_204c), with local_204c = param_5 minus the QuarterSize passes. r13 is the output channel (set at 1801f44dd) on the irradiance, directional and sh2 paths too. Ghidra shows the check inside the shadows block only because the other paths reach it by goto LAB_1801f496d. Port: Descriptor("2DARRAY", ..., "None"). With 8k CS2 lightmaps (mips=2), irradiance and direct_light_shadows get "Box2"; the texture probably also gains mips, and Write always emits one.

**F04** · parity-break · low confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:497`  
Doubt: the 'lightmapMaxResolution' and 'fast' fingerprints are fixed at 0, but they look like arguments inherited from the map compile's command line, which would record the passed value.  
*Valve side:* SerializeLightMap 1801f3f40 sets only final/hueShiftFixup (f_1801f3f40_tail.c:43-55); the other RED2 arguments come from the RC context (not in the decompiles)  
*Evidence:* Port: Argument("fast", "FloatArg", 0), Argument("lightmapMaxResolution", "FloatArg", 0). SerializeLightMap never sets them, yet they appear in the child's RED2 as FloatArg, the type the command-line parser gives -lightmapMaxResolution (MapCompileArgsTests: Arguments["lightmapMaxResolution"] == 1024f). The volume 'final' is also a FloatArg, i.e. not set by 1801eee20. If Hammer's usual line (-lightmapMaxResolution 1024 ...) is passed, the fingerprint is probably 1024f's bits, not 0. probe01 may have been compiled without the argument.

**F05** · latent · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:126`  
The halving threshold should be max(minDimension, MaxResolution); the port uses MaxResolution alone.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:152-165; disasm 1801f41a1-1801f41d4 (cmovg r15d, esi)  
*Evidence:* Valve: iVar3 = max((int)local_1ff0, MaxResolution); while (min(w,h) > iVar3) QuarterSize. Port: while (Math.Min(image.Width, image.Height) > settings.MaxResolution). Under CS2's gameinfo (MinSpecLightmapSize 4096 = MaxResolution 4096) the two always agree. Without MinSpecLightmapSize (another gameinfo), an 8192 atlas with mips=1 is not halved by Valve but is halved to 4096 by the port.

**F06** · latent · medium confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:128`  
For direct_light_shadows, Valve scans for values under 1 in the bitmap before halving; the port scans the halved one.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:296 (piVar23 = local_2040*0x9d8 + *(param_1+2))  
*Evidence:* Valve: the four channel scans read the channel's original bitmap, not local_1f90, the halved result. Port: Format(channel, image, compressed) is called after the QuarterSize loop. Matters only if gameinfo sets a MaxResolution for direct_light_shadows (CS2 does not); averaging can push a sub-1 texel to >= 1 and change ATI1N/ATI2N/BC7.

**F07** · latent · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:131`  
gameinfo's CompressedFormat/UncompressedFormat string should be copied verbatim into m_outputFormat; the port writes the canonical upper-case name. The port also throws on names Valve accepts or ignores.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:483-538 (FUN_180c490a0 = V_stricmp_fast loop; CUtlString::SetDirect(&local_2008, local_2058, len))  
*Evidence:* Valve: if (FUN_180c490a0(str) != -1) m_outputFormat = str as written, and the lookup is case-insensitive. An invalid name leaves the picked format unchanged. Port: an enum match ignoring case, then FormatName(format), so "dxt1" gives "DXT1" in the descriptor and a different ___OverrideInputData___ CRC. An unknown name or any format outside the seven throws. CS2 writes "DXT1", so not hit today.

**F08** · latent · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:103`  
A direct_light_shadows bitmap with no value under 1 should be written as ATI1N with min and max dimension 4. The port throws instead, so a map with no shadowed texel fails to compile.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:430-435; disasm 1801f4807-1801f4842  
*Evidence:* Valve: format 0x18; if (!R) { [rbp-0x68]=4; [rbp-0x64]=4 } (m_nOutputMinDimension = m_nOutputMaxDimension = 4). Port: throw new NotSupportedException(...). This is an unported branch, not wrong output. Real input reaches it, e.g. a map with no shadow-casting direct light.

**F09** · latent · medium confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:111`  
For an unknown channel, Valve silently stops the whole channel loop: that channel and every later one are not written. The port throws.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:462-471  
*Evidence:* Valve: when no name hash matches, it destroys the locals and breaks out of the do/while over *param_1 channels. Port: throw new ArgumentException(...). Error path only.

**F10** · doc · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:65`  
The Fingerprints doc comment says the two hashes are not derived yet; the class summary and DescriptorFingerprint/ImageFingerprint derive both.  
*Valve side:* n/a (port-internal)  
*Evidence:* Line 65: "RED2's two hashes that are not derived yet (see the class summary)". The class summary at lines 40-46 says "RED2's two fingerprints are derived".

**F11** · doc · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:16`  
The class summary states the halving rule as 'while the shorter side is over MaxResolution'; Valve compares against max(MaxResolution, min side >> (mips-1)), and the min dimension and Box%d also depend on the mip count.  
*Valve side:* SerializeLightMap 1801f3f40, f_1801f3f40.c:132-181  
*Evidence:* Doc: "halved ... while its shorter side is over the channel's MaxResolution". The decompile and disasm show the threshold max(local_1ff0, MaxResolution) and the use of the mip count (see the findings above).

*Compared and matching:* LightmapTextures.Format, LightmapTextures.AtlasVtex, LightmapTextures.Descriptor, LightmapTextures.DescriptorFingerprint, LightmapTextures.ImageFingerprint, LightmapTextures.FormatName, LightmapTextures.Bitmap, LightmapTextures.Encode (DXT1/ATI1N/ATI2N), LightmapTextures.Cs2Channels, RcMipChain.QuarterSize (dispatch only, as used here), SerializeLightMap halving loop ping-pong.

*Not matched or not seen:*

- LightmapTextures.Vtex (mismatch; SerializeLightMap 1801f3f40 (f_1801f3f40.c:127-181, 483-538; tail 619-645)): Not handled: the mip count (param_5 = iVar33 from f_atlas_caller.c:186-227), the min-dimension rule, Box%d, the halving threshold, and final/hueShiftFixup.
- LightmapTextures.EditInfo (mismatch; f_1801f3f40_tail.c:38-55 (vf 0x0 blob -1, vf 0x48 image -1, vf 0x20 final/hueShiftFixup -1)): The 'final' and 'hueShiftFixup' fingerprints are fixed at 0 (see findings). The search path, the dependency list and the remaining argument defaults are set by the RC context, which is not in the decompiles.
- LightmapTextures.Reflectivity (unverified; texture compiler (no decompile in lmtex/review)): Settled only by byte comparison on probe01. Untested: the lane assignment when the texel count is not a multiple of 4 (an SSE loop usually sums the tail separately), which can happen in LPV atlases with odd volume sizes.
- LightmapTextures.Write / Data (LZ4, COMPRESSED_MIP_SIZE, header) (unverified; texture compiler (not in the decompiles); gameinfo TextureCompiler CompressMinRatio 95): The 5% / 32 KB rule matches gameinfo. The 'max side >= 33' gate, and whether the gate uses slice size or volume size for 3D, cannot be checked against Valve code here.
- LightmapTextures.Bc7Blocks / BC6H half conversion (unverified; texture compiler (not in the decompiles)): Settled by byte match on dlshd and irradiance only.
- LightmapTextures.VtexFormat (unverified; no Valve decompile; VTexFormat numbering): Agrees with the VRF enum: DXT1 1, RGBA8888 4, RGBA16161616F 10, BC6H 19, BC7 20, ATI2N 21, ATI1N 27.


### lightmap_query_data.kv3

**F12** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightmapOutputs.cs:54`  
The query-data uv scale is computed from the full lightmap side (the irradiance EXR's width and height). Valve computes it from the side left after the lightmapMipCount and MinSpecLightmapSize halvings.  
*Valve side:* BakeLighting 180217740: halvings at 180217982-180217997 and 180217a9f-180217ab2; uv scale from r14d/r15d at 18021808b-180218130, stored to param_9+8/+0xc at 1802181ed/1802181fd (f_atlas_caller.c:183-229, 418-444)  
*Evidence:* Valve: r14d = r15d = esi (180217930) are halved by 'sar eax,1' in the mip loop (180217982..997) and the MinSpec loop (180217a9f..ab2). Then 'movd xmm1, r15d; cvtdq2ps; divss 32767/side; floorf; cvttss2si; imul ebx, r15d; add ebx,ebx' (1802180a6-1802180d8). PackCharts gets the unhalved esi (18021807a). Port: '(width, height) = (exr.Width, exr.Height)' then 'LightmapQueryData.Build(..., width, height, ...)'. UvScale(4096) = 57344 is used whatever the mips are. LightmapSettings.From computes the halved w/h but returns 'requested', and LightmapSeams.Snap already uses the halved side, so the port's seams and query data disagree with each other. Example: lightmapMaxResolution 8192 with CS2's MinSpecLightmapSize 4096: Valve a = 7*4096*2 = 57344, port a = 3*8192*2 = 49152. Another: lightmapMipCount 2 at 4096: Valve a = 15*2048*2 = 61440, port 57344. Every u16 uv written changes. probe01 (4096, one mip, MinSpec 4096) never halves.

**F13** · latent · medium confidence · `src/Source2.Compiler/Lighting/LightmapQueryData.cs:67`  
The u16 uv conversion goes float -> uint -> ushort. Valve does cvttss2si to int32 and keeps the low 16 bits, so the two differ for negative quantised uvs.  
*Valve side:* 1802163b0 disasm 180216aa4 / 180216b82 (f_query.c:312, 355)  
*Evidence:* Valve: 'cvttss2si ebx, dword ptr [r14 + rdi*4 + 0xc]', then stores bx: -1.0 gives 0xFFFF. Port: '(ushort)(uint)welded[i * stride + 3]'. .NET's float->uint conversion saturates negatives to 0, so -1.0 gives 0x0000. This only happens if an atlas uv is below -0.5/a. Packed uvs are non-negative today, so it is latent. The same applies to line 68 (v).

**F14** · doc · high confidence · `src/Source2.Compiler/Lighting/LightmapQueryData.cs:15`  
The doc comment says the uv scale is set up by 'the caller's 1802171b0 setup'. The scale is computed in BakeLighting 180217740. 1802171b0 copies the packed LightmapUV back into the meshes. The comment also says 'size' where Valve uses the side after the mip and MinSpec halvings.  
*Valve side:* 180217740 (18021808b-1802181fd) vs 1802171b0 (tools/re/out/review/f_1802171b0.c)  
*Evidence:* f_1802171b0.c only does CMesh_Alloc and copies 'LightmapUV' (lines 188-200, 205-231); it has no 32767/floor/65535. f_atlas_caller.c:418-444 (BakeLighting) computes floorf(32767.0/(float)(int)uVar15) and param_9[2] = 65535.0/fVar34. Port: 'with a = (float)(int)(floor(32767 / size) size 2) (57344 for 4096; the caller's 1802171b0 setup)'. LightmapOutputs.cs:15 ('the uv scale from the lightmap's size') has the same problem.

*Compared and matching:* LightmapQueryData.Build: vertex copy and uv quantisation, LightmapQueryData.UvScale (the formula), LightmapQueryData.Cull, Cull box construction (Valve's resolution-volume boxes), CMesh_Weld call, meshopt_simplify call, KV3 output: keys, order, types, format, vertex and index blob layout.

*Not matched or not seen:*

- BakeLighting uv-scale input side (what the caller feeds UvScale) (mismatch; 180217740: r14d/r15d halved at 180217982-180217997 (lightmapMipCount loop) and 180217a9f-180217ab2 (MinSpecLightmapSize loop), read at 180218096/1802180aa; stored at 1802181ed/1802181fd): Valve uses the side left after the mip and MinSpec halvings. The port uses the irradiance EXR's full size (LightmapOutputs.cs:54).
- MeshWeld internals (clustering, KD tree, renumber) (unverified; 1812d80c0 / 1812e5880 / 1812e2530 / 1812dd2f0): Shared port with the world/physics weld. No decompile saved for this section; only the call site and the stride>=6 branch were checked.
- VisibilityMeshMerger.TriBoxOverlap (used with eps 240) (unverified; 181265950): Shared port reviewed with vis; no decompile of 181265950 in this section's files. Only the call arguments were checked.


### Probe atlases, LightmapOutputs, -vrad3output

**F15** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightProbeAtlasImages.cs:26`  
The atlas size is the packed boxes' extent. Valve's is the allocator dims of the last successful fit, rounded up to a multiple of 4, which can be larger when there are two or more volumes.  
*Valve side:* LightProbe_PackAtlas 1801f5a80: 1801f5fc3-1801f5fd4 (out[0..2] = r13d/ebx/r12d, the allocator dims, after every fit), 1801f6080-1801f60c8 (each out dim rounded: 0 gives 4, else (d+3)&~3)  
*Evidence:* Port: Size = max(place-1 + ((size+5)&~3)) per axis. Valve: `mov rax,[rbp+0x1e0]; mov [rax],r13d; mov [rax+4],ebx; mov [rax+8],r12d` on each fit, then `test ebx,ebx; jne; mov ebx,esi(4) / add ebx,3; and ebx,0xfffffffc` per axis. I ran the port's own Pack loop in Python on 3000 random sets of 2-5 volumes: 49 differ. Example: sizes (17,17,23),(25,18,37),(30,1,10),(9,17,15) give extent X 40, Valve X 44. Another: (6,5,9),(14,10,15),(2,7,17),(10,31,7),(26,12,1): last-fit X 34, rounded 36, extent 32. The image width X*Z and the 3D descriptor change, so both atlases differ. A single volume (probe01) always agrees. The class summary (lines 18-19) and docs/VRAD3.md ('the packed boxes' extent') state the wrong rule. Fix: ProbeAtlas.Pack should return the last-fit dims and the caller should round them.

**F16** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightmapOutputs.cs:62`  
The query data's uv scale uses the irradiance EXR's full width and height. Valve uses the side after the lightmapMipCount and MinSpecLightmapSize halvings (MinSpec is 4096 in CS2).  
*Valve side:* CStaticLightingProcessor_BakeLighting f_atlas_caller.c 183-229 (uVar13/uVar15 halved per lightmapMipCount and while > MinSpecLightmapSize), 418-444 (fVar34/fVar39 from uVar13/uVar15; param_9[2..3] = 65535/a), 633 (passed to 1802163b0)  
*Evidence:* Port: `(width, height) = (exr.Width, exr.Height)` then `LightmapQueryData.Build(..., width, height, ...)`, with UvScale(size) = floor(32767/size)*size*2. Valve: `uVar13 = (int)uVar13 / 2; uVar15 = (int)uVar15 / 2` in both loops, then `fVar34 = floorf(32767.0 / (float)(int)uVar15)`. For an 8192 map, Valve's scale side is 4096 (a = 57344) and the port's is 8192 (a = 49152). The gameinfo comment 'at 8k this inflates' shows 8k maps are expected. lightmapMipCount >= 2 above 512 also halves. The repo's LightmapSeams.Snap documents the halved side, but LightmapSettings returns the unhalved Width. Every u16 uv in lightmap_query_data.kv3 changes.

**F17** · parity-break · medium confidence · `src/Source2.Compiler/Lighting/LightmapOutputs.cs:55`  
lightmapVRadQuality 2 ('final') is not passed on: Valve sets final=1 on every channel texture, plus hueShiftFixup=1 on BC6H ones. FromVrad3 takes no quality, and EditInfo writes final and hueShiftFixup with fingerprint 0.  
*Valve side:* SerializeLightMap f_1801f3f40_tail.c 632 (`final` = DAT_182a88554[quality]) and 638-643 (hueShiftFixup 1 for BC6H when final and gameinfo BC6HHueShiftFixup, default 1); table 182a88554 = 00 00 01 (pefile)  
*Evidence:* Valve: `(*(plVar15+0x20))(plVar15,"final",(&DAT_182a88554)[param_3])`, and `if (iVar9 == 0x1a) ... if (BC6HHueShiftFixup && DAT_182a88554[param_3]) set "hueShiftFixup" 1`. The bytes at 182a88554 are {0,0,1}, so quality 2 gives final=1. Port: `LightmapTextures.Vtex(channel, ..., settings, searchPath, nativeFolder: nativeFolder)` with no quality, and LightmapTextures.cs 495-496 `Argument("final", ..., 0)`, `Argument("hueShiftFixup", "FloatArg", 0)`. A map baked at Final quality would differ at least in RED2. Whether final also changes the texture compiler's encoding is not checked.

**F18** · parity-break · medium confidence · `src/Source2.Compiler/Lighting/LightmapOutputs.cs:55`  
The lightmapCompressionDisabled map setting is ignored. Valve then serializes every channel uncompressed (0xf / Rgba8888, the UncompressedFormat override), but FromVrad3 always calls Vtex with compressed=true.  
*Valve side:* f_atlas_caller.c 250 (lightmapCompressionDisabled), 615 (SerializeLightMap param_4 = cVar8 == 0); f_1801f3f40.c 287-288, 447-449, 476-479, 490-500 (uncompressed formats, UncompressedFormat key)  
*Evidence:* Valve: `cVar8 = GetBool("lightmapCompressionDisabled")` and `SerializeLightMap(lVar29, plVar20, local_22c, cVar8 == '\0', ...)`, with param_4 == 0 picking format 0 for shadows and 0xf otherwise. Port: the `compressed` parameter of Vtex is never passed (default true), and FromVrad3 takes no map settings.

**F19** · parity-break · medium confidence · `src/Source2.Compiler/Maps/MapCompile.cs:184`  
With -vrad3output the world node is still built by WorldNodeFiles.Build, which takes no bake flag and is documented as the unbaked render half. The package then has an unbaked world node next to vrad3's baked lightmaps.  
*Valve side:* BakeLighting / world node build in a baking compile (WorldNodeFiles summary: 'The world step's render half without baked lighting')  
*Evidence:* Line 184: `WorldNodeFiles.Build(document, addon, map, assets, shaders, schema, game, ..., vis: ..., fingerprint: ...)` with no bakes argument. WorldNodeFiles.cs 7: 'The world step's render half without baked lighting (a -world -vis compile's package)'. Line 222 then adds lightmaps/ from vrad3. A baked node's meshes need the chart lightmap uvs that match lightmap_packing_geometry.dat. The commit message says the switch's build was not re-checked.

**F20** · latent · medium confidence · `src/Source2.Compiler/Lighting/LightProbeAtlasImages.cs:95`  
Rgba fills a missing A channel with 0, while Valve's EXR loader fills 1.0. A missing R, G or B makes Valve fail the load (and the whole probe volume write), while the port silently fills zeros.  
*Valve side:* LoadFromInMemoryEXR 180c40c20 (f_180c40c20.c 99-128, 161-170), used by 1801f64f0/1801f69c0 via LoadFromEXRFile 180c40290  
*Evidence:* Valve: `uVar22 = 0x3f800000; ... if (iVar14 != -1) uVar22 = A[i]`, and `"R channel not found!"` / G / B lead to return 0. Port: `if (!exr.Channels.Contains(names[c])) continue;` leaves 0. LightmapTextures.Bitmap is documented 'alpha 1' for the channel textures, so the two loaders in the repo disagree. This matters for dlshd (BC7 encodes alpha) if vrad3 ever writes an RGB-only lpv EXR. probe01 matched, so its files carry A.

**F21** · latent · medium confidence · `src/Source2.Compiler/Lighting/LightmapOutputs.cs:50`  
Missing inputs are handled differently from Valve. A missing channel EXR is skipped silently after MapCompile has removed every maps/<map>/lightmaps/ entry (MapCompile.cs 231-232), so the package silently loses that texture. A missing lpv_<id>_*.exr throws FileNotFoundException, which MapCompile's catch (NotSupportedException or InvalidOperationException only) does not catch.  
*Valve side:* 1801f64f0 f_1801f64f0.c 71-74, 1801f69c0 f_1801f69c0.c 68-71, 1801eee20 f_1801eee20.c 293-295, 394-398, 537-539 (return 0, no LPV texture written)  
*Evidence:* Port: `if (!File.Exists(path) \|\| ...) continue;` and `File.ReadAllBytes(Path.Combine(folder, $"lpv_{v.Id}_ambientcube.exr"))`. The only pre-check is lightmap_packing_geometry.dat (MapCompile.cs 111). Valve: a failed LoadFromEXRFile returns 0 from the atlas builders, and SerializeLightProbeVolumes returns 0 without writing any LPV texture. The bake reports the failure rather than writing a partial set.

**F22** · latent · high confidence · `src/Source2.Compiler/Lighting/LightmapOutputs.cs:64`  
lpvAtlas defaults to true and MapCompile never sets it from gameinfo. The non-atlas (per-volume ambient cube, uncompressed when +0x878 is 0) and LPVSH2 paths of 1801eee20 are not written.  
*Valve side:* f_atlas_caller.c 241 (GetGameInfoBool LPVAtlas), 254 and 337-339 (noLightProbeVolumes / disableLightingCalculations), 317 (LPVSH2), 445-456; f_1801eee20.c 173-286 (SH2 path), 297-314 (per-volume, +0x878 uncompressed)  
*Evidence:* Port: `bool lpvAtlas = true`, and MapCompile calls FromVrad3 without it. Valve packs only when `bVar5 (LPVAtlas) && cVar4 == 0`, otherwise it writes one texture per volume at +0x68 / +0x268. CS2's gameinfo has LPVAtlas 1 and no LPVSH2, so this does not occur today.

**F23** · latent · medium confidence · `src/Source2.Compiler/Maps/MapCompile.cs:231`  
The switch deletes every entry under maps/<map>/lightmaps/, but FromVrad3 never produces the debugLightmapSets uncompressed set or the env_farfield_<n> textures Valve also writes there.  
*Valve side:* f_atlas_caller.c 617-631 (debugLightmapSets: second SerializeLightMap with suffix "uncompressed" into lightmaps/<DAT_182a8a3fc>), docs/VRAD3.md 636-639 (lightmaps/env_farfield_<n>_*.vtex)  
*Evidence:* Port: `foreach (var old in entries.Keys.Where(k => k.StartsWithAscii(prefix)).ToList()) entries.Remove(old);` then only FromVrad3's files go back. Valve: `if (cVar10 != 0) SerializeLightMap(..., 0, ..., "uncompressed")`, and the far-field writer emits env_farfield_<n>_indirection / _sh2_* under lightmaps/. CS2's FGDs have no env_farfield_probe_volume, so that part is latent. debugLightmapSets is a map setting.

**F24** · latent · low confidence · `src/Source2.Compiler/Maps/WorldNodeBuild.cs:105`  
Valve multiplies each importance-volume point by a float at +0x10 of the volume record before boxing. The port's volumes (used in MapCompile.cs 228 for the query cull) carry no such factor.  
*Valve side:* 180215bf0 (f_180215bf0.c 52, 60, 65-183; disasm 180215cd6 `movss xmm5, dword ptr [rcx + 0x10]`)  
*Evidence:* Valve: `fVar17 = (float)piVar5[4]; fVar18 = fVar17 * x; ...` for every point, and disasm confirms a float load (movss), not an int conversion. Port: ImportanceVolumes adds the raw vertex positions, and LightmapQueryData.Cull boxes them unscaled. This matches only if the factor is always 1.0, which the setter (Step257b50, not in these files) would have to confirm.

*Compared and matching:* LightProbeAtlasImages.AmbientCube, LightProbeAtlasImages.Shadows, ProbeAtlas.Pack (sort, shrink loop, place write-back), LightmapOutputs.FromVrad3 (probe volume atlases), MapCompileArgs -vrad3output parsing.

*Not matched or not seen:*

- LightProbeAtlasImages.Size (mismatch; 1801f5a80 tail: 1801f5fce stores the current allocator dims on each fit; 1801f6080-1801f60c8 rounds them up to 4): The port takes the packed boxes' extent. Valve takes the allocator dims of the last fit, rounded up to a multiple of 4. They differ for some sets of two or more volumes.
- LightProbeAtlasImages.Rgba (mismatch; LoadFromInMemoryEXR 180c40c20 (f_180c40c20.c 76-171)): When A is missing Valve fills 1.0 and the port fills 0. When R, G or B is missing Valve fails the load and the port fills zeros.
- ProbeAtlas.Allocator3D (unverified; tier0 CUtl3DAllocator 180196680/180196990/180196780): No tier0 decompile in this section's files. Compared only at the call site (the args and the flag 0).
- LightmapOutputs.FromVrad3 (channel textures) (mismatch; SerializeLightMap 1801f3f40 (f_1801f3f40.c 285-482, f_1801f3f40_tail.c 592-643) and its callers (f_atlas_caller.c 250, 612-631)): The final/hueShiftFixup flags from lightmapVRadQuality, the lightmapCompressionDisabled formats and the debugLightmapSets uncompressed set are not passed on. The EXR-to-channel mapping (lightmap_chart_color.exr to debug_chart_color) is in ProcessReceivedLightingPackets, which is not in these files, so it is unchecked.
- LightmapOutputs.FromVrad3 (lightmap_query_data uv scale) (mismatch; CStaticLightingProcessor_BakeLighting f_atlas_caller.c 183-229 (mip and MinSpec halving), 418-444 (param_9[2], [3]), 633 (1802163b0 call)): The scale side is the irradiance EXR's width. Valve uses the side after the mip and MinSpecLightmapSize halvings.
- LightmapOutputs.Volumes (compute_lpv order and ids) (unverified; 1801eee20 iterates the 0x8c0 volume list, lpv_%d with +0x50): The script writer is not in these files. The port assumes the compute_lpv routine order and labels match RC's volume list and +0x50 ids.
- WorldNodeBuild.ImportanceVolumes as fed to 1802163b0 (unverified; 180215bf0 (f_180215bf0.c 36-219; disasm 180215cd6 movss xmm5,[rcx+0x10])): Valve multiplies every point by a per-record float at +0x10. The port has no such factor. Whether it is always 1.0 is not visible here, and Step257b50 is not in these files.
- MapCompile -vrad3output block (mismatch; BakeLighting f_atlas_caller.c; SerializeLightProbeVolumes return paths): The world node is still the unbaked build. Lightmap entries are deleted before outputs are known to be complete. Missing EXRs throw past the catch.


### Mip chain, resampling, BC6H/BC7 glue

**F25** · parity-break · high confidence · `src/Source2.Compiler/Lighting/LightmapTextures.cs:247`  
The BC6H input halves use .NET's round-to-nearest-even (Half) cast. Valve's row fill clamps each RGB value to max(0,x), clears the sign bit and converts with its own float16 routine, which truncates the mantissa (round toward zero) and saturates to 65504. Valve also writes alpha as 1.0 (0x3c00).  
*Valve side:* 180c3ef20 (BC6H row fill, disasm 180c3f009-180c3f07f) calling 1802ea750 (float->half, disasm 1802ea750-1802ea8a0)  
*Evidence:* Valve: `xorps xmm0,xmm0; maxss xmm0,[src]; andps xmm0,[0x7fffffff]; call 1802ea750`. In 1802ea750 the normal path is `shr eax,0xd; and ax,0x3ff` (mantissa truncated, no rounding bit). It saturates through `comiss xmm1,[65504]` -> 0x477fe000, and Inf goes to 0x7bff. Alpha is `mov word ptr [r14+2],0x3c00`. Port: `half[i] = (Half)image.Rgba[i];` (round to nearest even; negatives, -0 and Inf pass through; image alpha passed). The values are exact when they come straight from the EXR's Half planes. They are not exact after the QuarterSize at Vtex line 125-127 (LightmapChannels/<c>/MaxResolution on irradiance or sh2_dc), where about half the texels round up one half-ulp against Valve's truncation. A negative or -0 value in the EXR becomes a sign-set half where Valve has +0.

**F26** · latent · medium confidence · `src/Source2.Compiler/Texture/IspcTexcomp.cs:64`  
Valve's EncodeBC6Blocks returns false unless both width and height are multiples of 4. CompressBc6h instead encodes any size by edge-clamping. So for a non-multiple-of-4 BC6H image (for example a light-probe ambient-cube slice of the volume's X x Y probes, or a 1x1/2x2 level) the port writes blocks where RC takes some other path.  
*Valve side:* 180c46b20 disasm 180c46b4e-180c46b80 (review/f_180c46c40.c:31-40)  
*Evidence:* Valve: `mov r14d,[r15+4]; and eax,0x80000003; test eax,eax; jne 180c46db7` (return 0), and the same for [r15] (width). The caller at 180c469df does `test al,al; setne al` and propagates the failure. Port: `var bw = (width + 3) / 4; ... var sx = Math.Min(x, width - 1); var sy = Math.Min(by * 4 + y, height - 1);`. There is no precondition, so it never takes RC's fallback.

**F27** · doc · high confidence · `src/Source2.Compiler/Texture/Rc/RcMipChain.cs:110`  
The comment gives the SSE box condition as an output width that is a multiple of 4. Valve and the port's own code (line 112) use a multiple of 8.  
*Valve side:* 18127a9c0 disasm 18127aa05-18127aa23 (cmp r10d,8; and ecx,0x80000007)  
*Evidence:* Comment: "the SSE loop (an output width that is a multiple of 4, at least 8)". Code: `bool sse = n % 8 == 0 && n >= 8;`. Valve: `cmp r10d, 8; jl scalar; and ecx, 0x80000007; jne scalar` on the dst width.

**F28** · doc · high confidence · `src/Source2.Compiler/Texture/IspcTexcomp.cs:9`  
The class comment cites 180c46c40 / 180c44880, which are mid-function addresses; the functions start at 180c46b20 and 180c44860. Both are the BC6H encoder (EncodeBC6Blocks and its thread job, calling 180c72b80 -> CompressBlocksBC6H), not BC1/BC3, so they do not support the RGBA8 BC1/BC3 claim.  
*Valve side:* 180c46b20 / 180c44860 (review/f_180c46c40.c, review/f_180c44880.c)  
*Evidence:* Port: "resourcecompiler compresses one row of 4 x 4 blocks per call (180c46c40 / 180c44880) ... RGBA half floats for BC6H, RGBA8 for BC1/BC3." Ghidra resolves both to FUN_180c46b20 / FUN_180c44860, and both call FUN_180c3ef20 (half fill), FUN_180c72c00 (bc6h profile) and FUN_180c72b80 (BC6H).

**F29** · doc · high confidence · `src/Source2.Compiler/Texture/IspcTexcomp.cs:39`  
The comment says Bc6hProfiles is in resourcecompiler's table order. RC's table has six entries [veryfast, veryfast, fast, basic, slow, veryslow], reached through a quality map with default basic. The port's five-entry list matches RC's quality value, not its table order. Indices 0-4 still give the same profile.  
*Valve side:* 180c72980 profile table 182fe58a0; quality map in 180c46b20 (review/f_180c46c40.c:41-58)  
*Evidence:* Table at 182fe58a0 (pefile): GetProfile_bc6h_veryfast, GetProfile_bc6h_veryfast, GetProfile_bc6h_fast, GetProfile_bc6h_basic, GetProfile_bc6h_slow, GetProfile_bc6h_veryslow. Map: q 0->0, 1->2, 2->3, 3->4, 4->5, else->3. Port: `["veryfast", "fast", "basic", "slow", "veryslow"]` /// "in resourcecompiler's table order".

*Compared and matching:* RcMipChain.GammaToLinearBits, RcMipChain.Load (linear), RcMipChain.Load (sRGB), RcMipChain.QuarterSize size rule and dispatch, RcMipChain.QuarterSize box SSE, RcMipChain.QuarterSize box scalar, RcMipChain.QuarterSize 3-tap, RcMipChain.QuarterSize clamped-box fallback, RcMipChain.Encode, RcMipChain.Sample (bilinear), RcMipChain.WriteRgba8, IspcTexcomp.Load / Bc6hProfile, bc7e_s2c.cpp s2c_bc7e_encode, prepare.py REWRITES, build.ps1 / build.sh.

*Not matched or not seen:*

- RcMipChain.Resize (sample centre stepping) (unverified; caller of 181279940 not in the provided decompiles): No Valve source was provided for fx=(W/nw)*0.5 with fx+=sx accumulation.
- RcMipChain.FallbackRgba8 (mip pick rule) (unverified; not in the provided decompiles): 
- IspcTexcomp.CompressBc6h (surface, stride, row clamp, edge replicate, output offset) (mismatch; 180c46b20 + 180c3ef20 disasm 180c3ef20-180c3f0ea): Surface {ptr, max(W,4), 4, max(W,4)*8}, the row clamp min(by*4+y, H-1), edge replication and dst + by*bw*16 all match. The texel fill and the W/H%4 precondition differ; see findings.
- IspcTexcomp.CompressBc1 (unverified; no BC1 call site in the provided decompiles (180c44860/180c46b20 are both BC6H)): There are no callers in src either.


### DXT/ATI block encoder (AtiCompress.cs)

**F30** · latent · low confidence · `src/Source2.Compiler/Texture/Rc/AtiCompress.cs:596`  
The port zeroes data that Valve's RefineSSE2 leaves uninitialised: RmpErr column 3 in 3-point mode, plus RmpErr rows and BlkSSE2 lanes for padding colours. Valve's output depends on stack residue there.  
*Valve side:* RefineSSE2 1800364c0: BlkSSE2 at rbp+0x550..0x84f and RmpErr at rbp+0x960.. are written only for i<_NmrClrs and r<dwNumPoints (loops at 180036620 and 1800369f0); CompressonatorXCodec.cpp:1060-1104  
*Evidence:* DLL: the RmpErr fill loops only r<dwNumPoints and i<n, and BlkSSE2 is never memset. The SIMD loop still loads RmpErr[k*4+c][0..3] and BlkSSE2[ch][k*4..k*4+3] for all of them. Port: Array.Clear(_rmpErr) in SetRmpErr, and blkIn padding is zero. The two agree only while the residue is finite (padding then multiplies by rpt=0) and column 3's residue never undercuts the 1e6-sentinel term. That holds for residue left by earlier blocks, but not by construction, for example on the first block a thread encodes. The old docs/TEXTURES.md (7cb1be2^, lines 141-144) records one such first-block divergence in the C++ harness.

**F31** · latent · high confidence · `src/Source2.Compiler/Texture/Rc/AtiCompress.cs:83`  
BaseWeights does not model the wrapper's lower clamp, which turns weights at or below 0.01 into 0.01f before squaring.  
*Valve side:* wrapper 1800013ec-180001472: each fWeighting double is compared with 0.01 (comisd [0x180057438]); weights <= 0.01 become 0.01f (0x180057440) before SetParameter squares them  
*Evidence:* DLL: 'comisd xmm5, [0.01]; jbe -> movaps xmm2, xmm6(0.01f)', else cvtsd2ss, then SetParameter("WeightR", f) stores f*f. Port: returns r*r (or 1) directly. RC only ever passes 1.0 or 0.3086/0.6094/0.0820 (180c48307/180c48310), so no current input reaches this branch.

**F32** · latent · medium confidence · `src/Source2.Compiler/Texture/Rc/AtiCompress.cs:298`  
The port starts pos0/pos1 at 0, where Valve's code has uninitialised values. The two would differ only if no candidate in the first search iteration scored under MAX_ERROR.  
*Valve side:* CompressRGBBlockXSSE2 180039052/180039090: Pos[0]/Pos[1] live in xmm15/xmm8, which start as bits of the _RsltRmpPnts pointer ([rbp-0x68]/[rbp-0x64]); CompressonatorXCodec.cpp:1802 'CODECFLOAT Pos[NUM_ENDPOINTS];' is uninitialised  
*Evidence:* Port: 'float pos0 = 0f, pos1 = 0f;'. Upstream: Pos is uninitialised, and the DLL seeds the registers from pointer bits. PosG takes Pos on the first iteration (ErrG = 1e7), so the difference would show if every RampSrch err were >= 128000 or NaN. The bounded error (<~350 for 16 points) makes this unreachable for real input.

**F33** · doc · high confidence · `src/Source2.Compiler/Texture/Rc/AtiCompress.cs:21`  
The class doc cites docs/TEXTURES.md for the three quirks, but that file no longer exists in this repo.  
*Valve side:* git 7cb1be2 (split: readers and exporters move to Source2.Assets) removed docs/TEXTURES.md from this repo  
*Evidence:* Port: 'Three details of Valve's DLL are kept on purpose, see docs/TEXTURES.md'. Glob docs/TEXTURES.md finds nothing. git log shows the file last touched in 7cb1be2 (the split), and 7cb1be2^:docs/TEXTURES.md holds the quirk write-up at lines 112-120. Bc7e.cs:6, RcMipChain.cs:7 and LightmapTextures.cs:269 cite the same missing file. The quirks themselves are real in the DLL: squared weights at 18002f0d1 (mulss xmm2,xmm2), byte-wise medians at 180031940, and the two-lane alpha error at 18003c848.

*Compared and matching:* BaseWeights, Weights (CalculateColourWeightings), RgbBlock (CCodec_DXTC::CompressRGBBlock), CompRgbBlock (CompRGBBlock DWORD), CompressRgbBlockXSse2, RampSrchWSse2 (RampSrchWSS2E, inlined), FindAxis, MkRmpOnGrid, MkWkRmpPts, BldClrRmp / BldRmp, ClstrErr, RefineSse2 + SetRmpErr, Clstr / ClstrIntnl / ConstructColor, AlphaBlock (CCodec_DXTC::CompressAlphaBlock + EncodeAlphaBlock), CompBlock1X (byte), CompBlock1, RmpSrch1Sse2 + Refine1 (inlined), Clstr1 / GetRmp1 / BldRmp1, Compress (partial-block padding), EncodeBlock ATI2N channel order, Cvt / Min / Max helpers.


### meshopt_simplify (MeshoptSimplify.cs)

**F34** · doc · high confidence · `src/Source2.Compiler/Meshopt/MeshoptSimplify.cs:16`  
The doc comment calls meshopt_simplify 'a jump to meshopt_simplifyEdge', but the compiled wrapper sets up a stack frame and calls it; it is not a tail jump.  
*Valve side:* rc 1812b8710 (f_1812b8710.c L2-6; disasm 1812b8710-1812b877a)  
*Evidence:* Port: '(0923 1812b8710, a jump to meshopt_simplifyEdge 1812b39f0 ...)'. Disasm: '1812b8713: sub rsp, 0x88 ... 1812b877a: call 0x1812b39f0', with NULL attributes/weights/lock and 0 counts zeroed into the argument slots. docs/ADDRESSES.md line 440 uses the same wording ('jumps to'). Behaviour is unaffected.

*Compared and matching:* Simplify (meshopt_simplifyEdge driver: copy, adjacency, remap, classify, rescale, quadric fill order, capacity, error_limit, pass loop, collapse_remap/locked reset, break conditions, final sqrt(result_error)*error_scale), meshopt_simplify wrapper, UpdateEdgeAdjacency, HashBuckets2, BuildPositionRemap / PositionHash / PositionEqual, HasEdge, ClassifyVertices, RescalePositions, Normalize, QuadricAdd, QuadricEval / QuadricError, QuadricFromPlane, QuadricFromPoint (via vertex regularisation), QuadricFromTriangle, QuadricFromTriangleEdge, FillFaceQuadrics, FillVertexQuadrics, FillEdgeQuadrics, HasTriangleFlip, HasTriangleFlips (collapse_remap overload), GetComplexTarget, BoundEdgeCollapses, PickEdgeCollapses + CanCollapse/HasOpposite tables, RankEdgeCollapses, SortEdgeCollapses, PerformEdgeCollapses, UpdateQuadrics, RemapEdgeLoops, RemapIndexBuffer.


### Light probe volume bake

**F35** · parity-break · high confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:62`  
The SFMT draw scale is 2^-32 exactly (0x2f800000), but vrad3 multiplies by the float 0x2f7ffffd (2.3283060e-10), so every cell offset of every voxelize and compute pass is off by a few ulps.  
*Valve side:* vrad3 180032850: mulss xmm6, xmm8 at 18003297c with xmm8 = [0x1802a7e88] (loaded 1800328aa); 180035e80: mulss xmm8, xmm10 at 180036888 with xmm10 = [0x1802a7e88] (loaded 1800361e2); dec/180032850.c:92-94 and dec/180035e80.c:578-580 print it as 2.328306e-10  
*Evidence:* Port: `private const float TwoPowMinus32 = 1f / 4294967296f;` used in Offset() for both passes. Valve: the bytes at 0x1802a7e88 read with pefile are fd ff 7f 2f = 2.328306020e-10, not 2.3283064e-10. With numpy float32: d=0x80000000 gives Valve 0.4999999 and port 0.5; d=0x12345678 gives 0.0711111 vs 0.07111111; d=0xffffffff gives 0.9999998 vs 1.0, so the port can produce an offset of exactly 1.0, which Valve never does. The shader's own 2^-32 (s0_f0.hlsl:142) is a different constant and is correct in the transpile.

**F36** · parity-break · high confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:146`  
The convergence test runs before every pass from minspp on, but vrad3 runs it only at the start of each 16-pass command list (pass 0, then each batch start >= minspp), so blocks converge at different passes whenever minspp < maxspp.  
*Valve side:* vrad3 180035e80: the test is in the outer per-command-list loop (dec/180035e80.c:307; disasm 180036258 test esi,esi / je, 18003625c cmp esi,r15d / jl 18003681f). The inner pass loop 180036830..1800369c3 (inc ebx; cmp ebx,0x10) never goes back to it; the outer loop restarts at 180036210.  
*Evidence:* Port: `if (pass == 0 \|\| minSpp <= pass) TestConverged(...)` inside the per-pass loop, which tests at 512, 513, 514 and so on. Valve: `if ((iVar11 == 0) \|\| (local_f38 <= iVar11)) { ...dispatch lightmap_test_converged... }` runs once, then `do { ... } while ((iVar11 < iVar7) && (++iVar13 < 0x10));` runs up to 16 passes with no test, so it tests at 512, 528, and so on (and first at 512 for a minspp of 500). resourcecompiler's current script passes 64 64 (docs/VRAD3.md:73), and then both test only at pass 0. The divergence needs minspp < maxspp, for example the command's defaults of 512/1024. The class summary (lines 29-30) and docs/VRAD3.md:546 state the same wrong rule.

**F37** · parity-break · high confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:400`  
Light cookies are sampled as constant 1: the LPV hosts' TextureLod2D returns vec4(1) (line 338 for file 0 and line 400 for file 1, and Vrad3Raygen.cs:118 for lightmaps), so any light with a cookie texture lights the probes differently from vrad3.  
*Valve side:* rtx_vrad3_lpv s0_f1.hlsl:1233,1242,1949,1958,3352,3361 (textureLod(sampler2D(g_bindless_Texture2D[m_nCookie], ...)) behind `if (m_nCookie >= 0)`); scenesystem 18008c110 (ssre/f_18008c110.c:81-88) replaces the cookie with a bindless texture handle  
*Evidence:* Port: `protected override vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod) => new(1f);`. The transpiled shader calls `TextureLod2D((int)g_Lights_1._member0[i].m_nCookie, 0\|1, uv, 0)` when `m_nCookie >= 0` and multiplies the light by its .xyz. Light.FromEntry keeps the raw cookie index from 0xc4, so the branch is taken for every cookie light and the texture is ignored. The bug only shows on maps that have cookie lights.

**F38** · latent · high confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:185`  
The readback drops vIrradianceSH: vrad3 always copies the luxel's three SH float4s into the three SH images, but Result has no SH output, so an lpv_type sh2 volume (or anything that writes the SH images) cannot be reproduced.  
*Valve side:* vrad3 180035e80 readback (dec/180035e80.c:692-708): puVar28[0x18..0x23] (LpvLuxel.vIrradianceSH[0..2]) copied into the three SH images at DAT_1803a8f88 + k*0x13b qwords, at row y, column z*X+x+startx  
*Evidence:* Valve: loop `*(lVar24 * plVar21[0x1f] + lVar27 * plVar21[-1] + plVar21[-0x71]) = puVar25[-2]; ... puVar25 = puVar28 + 0x1a; ... plVar21 += 0x13b; while (plVar21 < 0x1803aad10)` gives 3 images x 4 components. Port: the read-back loop (lines 170-191) only reads vAmbientCube and the shadow slots, and Result (lines 44-56) has no SH field. The class summary and LightProbeVolumePost both scope the port to the ambient cube type, so this is latent unless SH volumes are compiled.

**F39** · latent · high confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:152`  
m_nFog is always 0 and the fog readback (first three floats of the luxel into the fog image) is missing, so lpv_compute_block_fog_gpu (fog irradiance volumes) has no CPU equivalent.  
*Valve side:* vrad3 1800371a0 / 1800371b0 (lpv_compute_block_gpu passes r8=0, lpv_compute_block_fog_gpu passes r8b=1 to 180035e80); push m_nFog = param_3 at 1800361a4 (movzx eax, byte [rbp+0xe80]); fog readback dec/180035e80.c:737-744  
*Evidence:* Port: `new RtxVrad3LpvS0F1.LpvPushConstants_t_natural(pass, 4, 1, 0, packed, ...)`. Valve: `local_ea8[3] = (int)param_3;` and `if (param_3 == 0) {cube/SH/shadow readback} else { fog image <- puVar28[0..2] }`. The shader branches on m_nFog (s0_f1.hlsl:3121, 3201, 3869, 4092).

**F40** · latent · medium confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:283`  
In the convergence test the port reads lanes whose linear index runs past X*Y*Z as invalid zeros, and sizes the converged grid from the volume's cells. vrad3 reads those lanes from the reused OutputLuxels buffer (sized by `lpv_image_gpu 96 96 96`, so stale luxels from an earlier volume) and uses a 32^3 converged texture.  
*Valve side:* vrad3 18002a5d0 (dec/18002a5d0.c:62-72 OutputLuxels sized W*H*D from lpv_image_gpu's arguments and kept while big enough; 84-128 lpv_block_converged.vtex sized ceil(W/3) x ceil(H/3) x ceil(D/3)); lightmap_test_converged s1_f0.hlsl:53-54  
*Evidence:* Port: `valid[k] = index[k] < luxels.Length && ...` with luxels sized n0*n1*n2, and `converged` / ImageLoadConverged bounded by (n+2)/3. Valve: `g_LpvLuxels.Load4(_10826 * 192)` indexes a buffer of 96^3 entries that is only recreated when a larger one is needed (`if (DAT_1803ab468 < uVar18)`), so entries past the current volume hold the previous volume's data, and their w>0 can count as valid. A grid axis over 96 also makes Valve read 0 (not converged) past texel 31, where the port tests real groups. This only matters while the test runs with err > 0, which the current 64/64 script never does.

**F41** · latent · high confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumeBaker.cs:114`  
When floor((0-min)/cell) overflows int, Valve's cvttss2si yields 0x80000000, which clamps to cell 0, but the port's (int) cast on net10.0 saturates to int.MaxValue, which clamps to n-1.  
*Valve side:* vrad3 1800323f0: cvttss2si esi, xmm0 at 18003246e (32-bit truncation, overflow gives 0x80000000), then `if (-1 < iVar10 && ...)` clamp (dec/1800323f0.c:30-50)  
*Evidence:* Port: `Math.Clamp((int)MathF.Floor((0f - bmin[a]) / ((bmax[a] - bmin[a]) / bn[a])), 0, bn[a] - 1)` on TargetFramework net10.0 (saturating conversions). Valve: `iVar10 = (int)fVar15;` compiled to cvttss2si, so a large positive quotient becomes negative and then 0. NaN gives 0 in both. It needs an origin more than 2^31 cells from the box or a zero-size box, so it is effectively unreachable.

*Compared and matching:* LightProbeVolumeBaker.Bake: bordered grid (cells+2, box grown by (max-min)/cells), voxelize readback (occupied = mask & 1), LightProbeVolumeBaker.Bake: lpv_flood_fill seed cell, early-out, all-solid fix, upload, LightProbeVolumeBaker.NearestEmpty, LightProbeVolumeBaker.FloodFill, Sfmt19937 (Bake) constructor / CertifyPeriod, Sfmt19937 (Bake) GenerateAll / Recursion, Lighting/Sfmt19937 (rc chart colours) vs Bake/Sfmt19937, LightProbeVolumeBaker.TestConverged, LpvLuxel initial values (ids 255), LpvPushConstants layout / m_mLpvLocalToWorld column construction, RaygenHost.GpuLight (with Light.FromEntry) vs PathTraceBarnLight_FromShape, Vrad3Raygen (bba20de~1..11e5b2d refactor).

*Not matched or not seen:*

- LightProbeVolumeBaker.Bake: lpv_voxelize_gpu pass loop and push constants (mismatch; vrad3 180032850 (dec/180032850.c:42-121; disasm 180032850-180032a80)): 32 passes, push (pass,4,1,0 \| packed 0 \| offset (third,second,first,0) \| bordered min,0 \| bordered max,0 \| row_major 3x4 e60..e8f) and dispatch over bordered dims all match; the draw scale constant does not (see finding).
- LightProbeVolumeBaker.Offset (mismatch; vrad3 180032977/1800329b1/1800329fa (mulss by [0x1802a7e88]) and 180036888/1800368c8/180036919 (same constant)): Component order x=third, y=second, z=first, w=0 matches; uint->float is cvtsi2ss on the zero-extended value (same as C#); multiplier differs.
- LightProbeVolumeBaker.Bake: lpv_compute_block_gpu push constants and region box (mismatch; vrad3 180035e80 (dec/180035e80.c:261-297, 547-596; disasm 18003604d-1800361ae, 180036830-1800369c3)): defaults 512/1024/0.02, cell = (max-min)/(float)n of the original grid, region min/max = (float)start\|end * cell + min (mulss, addss), packed (n<<16)\|(start&0xffff), filter radius 1, dims 4, w lanes 0, fresh SFMT same seed, dispatch region dims: all match. m_nFog hardcoded (latent) and the convergence cadence differs (finding).
- LightProbeVolumeBaker.Bake: convergence test scheduling (mismatch; vrad3 180035e80 (dec/180035e80.c:300-307, 550-596; disasm 180036258-18003625f, 1800369be-1800369c3, 180036a28-180036a2b)): 
- LightProbeVolumeBaker.Bake: readback of ambient cube and shadow records (mismatch; vrad3 180035e80 (dec/180035e80.c:631-736)): Ambient cube RGB clamp (<=0 -> 0), row face*Y+y, column z*X+x+startx, shadow sum/weight when 0<weight with (char) id: match. vIrradianceSH readback to the three SH images is not ported (finding).
- RaygenHost.LightBins / Globals light-bin cell (unverified; rtx_vrad3_lpv s0_f1.hlsl:1130-1160 (subgroupOr over bins, clip-space cull per light)): vrad3's binning code is not among the saved decompiles; the shader already evaluates the wave-wide OR of bins, which supports one all-lights bin, but this is not proven bit-exact.
- RaygenHost.Trace (payload 0 stochastic opacity, payload 1 visibility product) (unverified; vrad3 any-hit / closest-hit shaders (not in the listed decompiles)): 
- RaygenHost.Random (Halton imageLoad wrap/clamp) (unverified; rtx_vrad3_lpv s0_f1.hlsl:314-326 (shader wraps x,y itself); per-pass Halton upload via (DAT_180345dc0+0x1a0) at 180036840): z clamp vs robust zero read only differs beyond 128 layers, which the LPV shader does not reach; the per-pass Halton generator itself is outside this section.


### Light probe volume post-process

**F42** · latent · low confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumePost.cs:133`  
The AO factor uses MathF.Pow(r, 2f) instead of the repo's V_powf port (Maps.LightPow.Pow). That makes it depend on the host runtime's powf (ucrtbase on Windows, glibc on the Linux dev box), not on tier0's FMA3 CRT powf.  
*Valve side:* 180083580 at 1800839bd: call [1802514c8] = tier0!V_powf (import), exponent from +0x320 = 2.0  
*Evidence:* Valve: movss xmm1,[r12+0x320]; divss xmm6,xmm7; call qword ptr [V_powf]. Port: `var f = MathF.Pow(r, 2f);`. Other ported V_powf sites (SceneLights.cs:21, PathTraceScene.cs:91, LightTrace.cs, RenderClusters.cs:197) use LightPow.Pow. For exponent 2 every correctly rounded powf equals r*r, and the table-based CRT powf very probably does too, so a real difference is unlikely.

**F43** · latent · medium confidence · `src/Source2.Compiler/Lighting/Bake/LightProbeVolumePost.cs:165`  
A NaN luminance makes the port skip the cell, but Valve processes it, because its branch only skips when 5e-5 > lum, which is false on an unordered compare.  
*Valve side:* 1800851b0 at 18008550c: comiss xmm12(5e-5), xmm2(lum); ja 180085a01 (dec/1800851b0.c:116)  
*Evidence:* Valve: `comiss xmm12, xmm2; ja skip`, which is not taken for NaN, so the cell goes on to neighbour averaging and the flag test. Port: `if (!(5e-05f <= lum)) continue;` skips NaN. This matters only if the bake writes a NaN texel.

*Compared and matching:* Directions (AO direction table), FaceWeights / AO constants, OcclusionFactor, AmbientOcclusion (per-cell driver), RemoveFireflies, Dilate, DilateShadows, DirectLightShadows, WriteOpenBand, MedianFilter, Grid.Solid border offset.


### GLSL to C# transpiler, LPV raygen

**F44** · parity-break · medium confidence · `src/Source2.Compiler/Lighting/Bake/Glsl/Raygen/RtxVrad3LpvS0F1.g.cs:1198`  
f16vec3 *= float16_t multiplies (emissive times fEmissiveBoost) run in float with no rounding to half. GLSL does this multiply in 16-bit and the result is a half.  
*Valve side:* rtx_vrad3_lpv_vulkan_60_rtx/s0_f1.hlsl:849 and :1019 (also rtx_vrad3_lightmap_vulkan_60_rtx/s0_f0.hlsl:1008, :1178); root cause tools/re/glsl_to_cs.py:28 TYPE_MAP  
*Evidence:* GLSL: `f16vec3 _359 = _431; ... _359 *= float16_t(_522);` where _431 = p.material.vEmissive and _522 = fEmissiveBoost. C#: `vec3 _359 = _431; ... _359 *= f16(_522);` The product of two halves (up to 22 significant bits) stays in float and is never rounded to 11 bits, and values above 65504 stay finite where the GPU gives inf. The same pattern is at F1 line 1701 (`_367 *= f16(_537)`) and in RtxVrad3LightmapS0F0.g.cs lines 1597 and 2100. TYPE_MAP turns float16_t and f16vecN into float types and the statement rewrite never re-rounds. The fix is to emit `_359 = f16vec3(_359 * f16(_522))` for compound ops on half-typed variables.

**F45** · latent · high confidence · `tools/re/glsl_to_cs.py:404`  
An array declaration or assignment from another array variable is emitted as a C# reference alias. GLSL arrays are values and the declaration copies.  
*Valve side:* rtx_vrad3_lpv_vulkan_60_rtx/s0_f1.hlsl:3928 `vec4 _471[3] = _486;`  
*Evidence:* Generated RtxVrad3LpvS0F1.g.cs:8928 `vec4[] _471 = _486;`. It is harmless in this shader: after the alias, _486 is only rebound (line 9211 `_486 = new vec4[] {...}`) and never mutated element by element. Any shader that writes to _486[i] after the copy and before reading _471 would silently read the new values. The same code path exists in transpile() at line 260. A scan of every Glsl/**/*.g.cs finds only this one occurrence. A copy such as `(T[])x.Clone()` would make it safe.

*Compared and matching:* glsl_to_cs.py array_ctors (T[](a,b,c) -> new T[] { a, b, c }), glsl_to_cs.py push-constant parse (layout(row_major) strip), glsl_to_cs.py subgroupAll(x) -> (x) / RtxVrad3LpvS0F0.Main, glsl_to_cs.py imageLoad(g_LpvBlockConverged) -> ImageLoadConverged(ivec3), glsl_to_cs.py shared struct files (Raygen/Structs/*.g.cs), RtxVrad3LpvS0F0.g.cs Main (whole body), subgroupOr(...) -> (...) in F1 light-bin loops, Hit-group array initialisers (CsgoComplexS2560/S5120, CsgoEnvironmentS0, CsgoLightmappedgenericS32, CsgoSimple2wayBlendS0, RtxDefaultHitgroupS0/S1), LightProbeVolumeBaker ComputeRaygen/SolidRaygen dispatch vs shader data hazards.

*Not matched or not seen:*

- glsl_to_cs.py DECL with array + initialiser (transpile 260, transpile_raygen 404) (mismatch; s0_f1.hlsl:281, 3928 (vec4 _471[3] = _486;), 4192): An initialiser that is an array constructor is now kept, which is correct; before this change it became new T[n] (zeros). An initialiser that is another array variable becomes a C# reference alias where GLSL copies by value. See the finding.
- RtxVrad3LpvS0F1.g.cs Main (whole body) (mismatch; rtx_vrad3_lpv_vulkan_60_rtx/s0_f1.hlsl main()): 4029/4029 statements; control flow, operators, ids and literals all match. The only semantic difference is half-precision compound multiplies, which run in float (see the finding). Also checked: integer division and modulo built as a-b*(a/b), shift counts, the uvec3(float) saturating conversions, mix(ivec4,ivec4,bvec4) select semantics, the _296 const table and the switch-case structure.
