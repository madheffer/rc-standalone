# Authoring a compiled resource with no donor file

What a template was actually contributing, which types no longer need one, and
where the boundary genuinely is. Everything below is measured against stock CS2
content or read out of ValveResourceFormat's own parsers; sample sizes are given
so the claims can be re-checked rather than believed.

Re-derive after a game update; none of this is guaranteed stable across builds.

## The finding, in one line

A template was never contributing anything meaningful. For a texture it supplied
**one 16-bit integer** - the resource version - plus whatever encoding
dependencies its RED2 happened to carry. The reason a donor file was needed at
all is that `Resource.Version` had a private setter upstream, so the only way to
get a container with the right version was to read one off disk.

## What the header actually contains

`Resource.Serialize` writes exactly this, and nothing in it is donor-specific
except `Version`:

```
u32 fileSize          // patched after the blocks are laid out
u16 headerVersion     // always 12
u16 Version           // the file-type version  <-- the only thing a template gave us
u32 blockInfoOffset   // always 8
u32 blockCount
{ fourCC, u32 offset, u32 size } x blockCount
16-aligned block payloads
```

So the patch is one line (`third_party/patches/vrf-resource-version-settable`),
and with it the binary types become authorable.

## Measured per type

Surveyed from `csgo/pak01_dir.vpk` with the probe in this document's history;
"invariant" means every sampled file agreed.

| Type | Version | Blocks | Always-present RED2 dependencies | Authorable? |
|---|---|---|---|---|
| `.vtex_c` | 1 (200/200) | `RED2 DATA`, no RERL (200/200) | `Texture Compiler Version` fp=11; `Texture Encode Quality` fp=1 user=3 | **yes, fully** |
| `.vsvg_c` | 2 (200/200) | `RED2 DATA` (200/200) | `Vector Graphic Version` fp=2 | yes (not yet wired) |
| `.vmat_c` | 1 (200/200) | `RERL RED2 DATA INSG` (200/200) | `Material Compiler Version` fp=25; the two texture ones above | **all but INSG** |
| `.vsnd_c` | 4 and 5 in the wild | `RED2 DATA` | `Sound Compiler Version` fp=1 | yes (not yet wired) |
| `.vmdl_c` | 1 | up to 40 blocks incl. `MVTX MIDX PHYS ASEQ` | — | no, and not meaningfully: a model is its mesh data |

Sampled 200 of 71,175 textures, 200 of 21,796 materials, 200 of 1,120 vector
graphics.

### Textures need nothing

`BuildTexture` already authored the entire DATA block - header, extra-data table,
mip chain, per-mip size array - and its own RED2. Only the container frame came
from the donor. `ResourceBuilder.BuildTexture(def)` now takes no template at all.

A template is still *accepted*, for one reason: its RED2 special dependencies
describe **how the pixels are encoded**, not who compiled them. A stock glove
normal map carries `Mip HemiOctAnisoRoughness`, and a consumer that loses it
decodes the normal as plain RGB with roughness read as Z. With no template there
is nowhere for that to ride in from, so state it via
`TextureDef.EncodingSemantics`.

The two are equivalent where they overlap:
`TemplateFreeAuthoringTests.Texture_AuthoredAndTemplated_AgreeByteForByte` compiles
one image twice, once authored and once framed by that same output, and requires
the bytes to match.

### Materials: everything except the input signature

`ApplyMaterial` already clears the donor's data tree and rebuilds it whole, and
it derives the RERL from the texture parameters. So the container is authorable -
except for one block.

**INSG** is the vertex input signature the material's shader consumes: a list of
`{m_pName, m_pSemantic, m_pD3DSemanticName, m_nD3DSemanticIndex}`. Three
measurements decide what to do about it:

- Every stock material has one (200/200), and every material in three shipping
  community workshop packs has one (320/320). None is empty.
- It is **not** a function of the shader name. 600 sampled materials gave 22
  distinct signatures across 14 shaders; `csgo_character.vfx` alone accounts for
  seven, because the signature follows the shader's enabled feature combo.
- Producing one from first principles means resolving the compiled shader, which
  is a different compiler than this one.

So `MaterialAuthor.NewContainer` takes the signature as a required argument, and
`MaterialAuthor.ExtractInputSignature` lifts one from a material you have picked
because it uses the shader and features you want.

That is not a step back from the template flow. Handing a donor `.vmat_c` to
`BuildMaterial` **already** adopts whatever signature that file carried - it is
the same dependency, silent. Making it an argument is the fix, not the
limitation.

## Sprite sheets: `.mks` and the SHEET block

An animated particle texture carries a fourth extra-data entry, `SHEET`, holding
its sequences and the UV rect of every frame.

**The animation lives in the texture, not the particle.** A `.vpcf` picks a
sequence index (`C_INIT_RandomSequence` over `m_nSequenceMin..Max`) and a rate;
the frames, their display times and their UVs all come out of SHEET. So
re-skinning an existing animated effect needs no particle edit: ship a `.vtex_c`
at the stock path whose SHEET has the same sequence and frame shape.

**The atlas layout is arbitrary.** SHEET stores an explicit rect per frame, so
there is no grid convention to match and no need to imitate Valve's packer -
which is not a grid either. In `explosion_blast_01_flame` the 255x511 frames sit
at a ~240px stride, so neighbours overlap inside their black padding.

Layout, version 8, read out of VRF's parser and confirmed by arithmetic against
that file (4 sequences x (20 B name + 16 x 12 B frames + 16 x 32 B images) + 8 B
header + 4 x 32 B sequence headers = 3032 B, its exact SHEET size). **Every
offset is relative to its own position**, not to the start of the block:

```
u32 version = 8
u32 numSequences
sequence header x numSequences, 32 B each:
  u32 id; u8 clamp; u8 alphaCrop; u8 noColor; u8 noAlpha
  u32 framesOffset; u32 numFrames; f32 totalTime
  u32 nameOffset; u32 floatParamsOffset; u32 floatParamsCount
then per sequence: name (null-terminated, padded to 4 B), frames, images
  frame, 12 B:  f32 displayTime; u32 imageOffset; u32 imageCount
  image, 32 B:  f32 croppedMin.xy, croppedMax.xy, uncroppedMin.xy, uncroppedMax.xy
```

Two conventions that are easy to get wrong and are both pinned by tests:

- `totalTime` is the **sum of the frame display times**, not a frame rate. VRF
  calls the field `FramesPerSecond`, which it is not. Valve's flame sequences
  read 32.0 for 16 frames because the last frame holds for 17.
- UVs address **texel centres**: a frame at column `x` spanning `w` pixels runs
  from `(x + 0.5) / atlasWidth` to `(x + w - 0.5) / atlasWidth`. Valve's first
  frame starts at `0.00012207031` on a 4096-wide atlas, exactly half a texel.
  Without the inset, bilinear sampling bleeds in the neighbouring frame.

A sheet texture also declares itself: `Texture Compiler Version GenerateSheetData`
fp=5, present on every stock sheet and on no plain texture, and ships
`NO_LOD` (flags 0x8) with a single mip, because a mip chain would blend
neighbouring frames together.

`SpriteSheetTests.StockSheet_RoundTripsByteForByte` decodes a real animated
texture out of the game and requires the writer to reproduce its SHEET payload
byte for byte. That single comparison pins every offset, the alignment after each
name, the per-sequence block order and the `totalTime` convention at once.

### The `.mks` grammar

Taken from VRF's own emitter (`TextureExtract.TryGetMksData`), which reconstructs
a script from a compiled sheet and is the closest thing to a specification:

```
// comments run to end of line
packmode rgb+a       // optional; the default is flat
sequence 0           // uses colour and alpha
LOOP                 // optional; without it the sequence clamps
frame flame_0.png 1
frame flame_1.png 17
sequence-rgb 1       // colour only  -> NoAlpha
sequence-a 2         // alpha only   -> NoColor
```

`MksSource.Parse` rejects anything it does not understand, naming the line,
rather than skipping it. A silently ignored directive would produce a sheet that
animates differently from what the author wrote.

## Still open

- **Confirmed in game so far:** a template-free `.vtex_c` (rendered at 2048x1024
  with its full mip chain), an `.mks`-built sheet (CS2 parsed the SHEET block and
  selected a single frame rather than drawing the whole atlas), and a
  template-free `.vsvg_c` (drawn in the killfeed and the weapon slot). Loaded
  from a content VPK on a live CS2 server.
- **`.vsnd_c` is authored but not yet confirmed in game.** It matches the stock
  v4 container shape and reads back through VRF, and `ModernizeVsnd` still lifts
  it to v5, but nothing has played one from an authored container yet.
- A material's shader dependency (`"csgo_core/csgo_character.vfx" /
  CompileMaterial / fp=8`) carries a per-shader fingerprint only a real compile
  knows, so it is passed in rather than guessed.
- Our textures write a `METADATA` extra-data entry that the stock textures
  sampled here do not carry. Harmless as far as anything shows, but it is a
  deviation, and it predates this work.
