# How CS2 draws particles

Reversed from CS2's own compiled shaders and `particles.dll`, so that a
renderer outside the game (VpkStuff's web preview) can match it. Every rule
cites the file, combo and line, or the DLL address it was read from. Anything
not settled by a dump or a decompile is listed as UNSETTLED at the end of its
section. Nothing here is fitted to screenshots.

Build: CS2 PatchVersion 1.41.9.1, ClientVersion 2000933 (`game/csgo/steam.inf`).
Shaders: VCS v72 (header 12), `vulkan_50`, from `game/csgo/shaders_vulkan_dir.vpk`
dated 2026-10-15. `gameinfo.gi` lists `Game csgo` before `Game core`, so the
csgo copies shadow the older ones in `game/core`.

Line citations use these dumps (static/dynamic combo in the name):

| tag | file |
|---|---|
| PS0 | `spritecard_vulkan_50_ps_s0_f0.cb.hlsl` |
| VS0 | `spritecard_vulkan_50_vs_s0_f0.cb.hlsl` |
| TVS | `spritecard_vulkan_50_vs_s16_f0.cb.hlsl` (S_DRAW_AS_TRAIL=1) |

The `.cb.hlsl` files are the HLSL backend with cbuffer members renamed by
packoffset (`PC_c19z` = SpriteCardPixelControls_t c19.z). SPIR-V keeps no member
names and member indices move between dynamic combos, so offsets are the only
stable names.

VPK CRCs of the analysed files: `spritecard_vulkan_50_features.vcs` 0x204379dd,
`_vs` 0x201c5139, `_ps` 0x57bdbe59; `cables_vulkan_50_vs` 0x0be2f528,
`_ps` 0x3d82aa0f.

## 1. Which shader draws what

- **Sprites, trails and ropes all use `spritecard.vfx`** ("Shader used by the
  particle system"). Its 13 features: F_DRAW_AS_TRAIL, F_MOTION_VECTORS,
  F_TEXTURE_LAYERS (0..4), F_REFRACT, F_HSV_SHIFT, F_PARTICLE_SHADOWS,
  F_REVERSE_DEPTH, F_DISABLE_DEPTHTEST, F_LIGHTEN_MODE, F_MOD2X, F_FOG_PARTICLES,
  F_OPAQUE, F_SELF_ILLUM_PER_PARTICLE.
- VS static combos: S_MODE_*, S_DRAW_AS_TRAIL, S_MOTION_VECTORS,
  S_PARTICLE_SHADOWS, S_TEXTURE_LAYERS. VS dynamic: D_QUAD_OVERDRAW,
  D_SUPPORT_FRAME_CROP, D_BAKED_LIGHTING_FROM_PROBE, D_PANORAMA_PARTICLE.
- PS static adds S_REVERSE_DEPTH, S_REFRACT, S_OPAQUE, S_HSV_SHIFT. PS dynamic:
  D_DEPTH_FEATHERING, D_SCENE_DEPTH_MSAA, D_SHADOW_MODE, D_QUAD_OVERDRAW,
  D_MBOIT_PASS1, D_MBOIT_PASS2, D_PANORAMA_PARTICLE.
- F_ADDSELF, F_SATURATE_COLOR_PRE_ALPHA_BLEND, F_PARTICLE_ORIENTATION,
  F_ADDRESS_V_CLAMP and similar exist as strings in `particles.dll`
  (0x51f828..0x51fbc0) but are not spritecard features. The material is
  procedural (`procedural_spritecard.vmat`), and the C++ drives everything else
  through two cbuffers: SpritecardVertexControls_t (VC, b3) and
  SpriteCardPixelControls_t (PC, b4).
- **Cables use `cables.vfx`** ("Cables shader used for particle system cables"),
  a lit tube shader with its own material (section 7).
- **No spritecard combo draws particle lights.** Lights reach particles only
  through the scene light binner (section 8).

## 2. Cbuffers and vertex inputs

VC (b3): float4 c0..c5; c11.xy sheet width W and 1/height; c11.zw sheet row base
and sequence count; c16.xyz uint packed selectors; c16.w uint flags.

PC (b4): float4 c0, c1, c3, c4, c5, c6, c8, c9; c2 only in the depth-feathering
combo; c19.xy uint packed texture-layer modes; c19.z uint flags.

Per-view (b1), inferred from use: c0 world-to-projection, c17 clip plane, c20
viewport min/size, c24 position offset before projection, c26 camera position,
c27 camera up, c28 camera forward, c16.zw and c23.xy depth reconstruction. CSGO
(b6) holds fog parameters and flags. LIT (set 3 b0) holds light-binner
parameters.

Vertex inputs, from the shader's per-particle scale-source switch, whose case
values match SpriteCardPerParticleScale_t (read from `particles.dll` schema data
at 0x52f0f0: 0 NONE, 1 AGE, 2 ANIMATION_FRAME, 3 EXTRA_DATA1, 4 EXTRA_DATA2,
5 ALPHA, 6 RADIUS, 7 ROLL, 8 YAW, 9 PITCH, 10 RANDOM, 11 NEG_RANDOM,
12 RANDOM_TIME, 13 NEG_RANDOM_TIME; VS0:200-300):

| slot | meaning |
|---|---|
| POSITION0 | particle centre |
| TEXCOORD0 | corner in [-1, 1] |
| TEXCOORD1.x | random seed |
| TEXCOORD1.y / .w | extra data 1 / 2 |
| TEXCOORD1.z | age |
| TEXCOORD2.x | sequence index |
| TEXCOORD3.xyz | roll, yaw, pitch |
| TEXCOORD3.w | radius |
| TEXCOORD4 | normal |

## 3. Sprite vertex shader (static 0)

**Orientation.** `o = min(4, (VC_c16w >> 1) & 7)` (VS0:153). Schema values:
0 screen-aligned, 1 screen-Z, 2 world-Z, 3 align-to-normal, 4 screen-align-to-
normal, 5 full 3-axis. The clamp makes the `o == 5` branch (VS0:3177)
unreachable. Basis per value (VS0:3194-3260):

| o | up | right |
|---|---|---|
| 0 | PV_c27 | cross(PV_c28, PV_c27) |
| 1 | (0,0,1) | cross((0,0,1), PV_c28) |
| 2 | (1,0,0) | (0,-1,0) |
| 3 | from normal n = TEXCOORD4 | from n |
| 4 | normalize(cross(n, PV_c28)) | n |

For o = 3 and 4, alpha is also multiplied by
`1 - smoothstep(VC_c2.x, VC_c2.y, |dot(n, normalize(cam - pos))|)` (VS0:3232, 3246).
Roll (T3.x) rotates `up' = up*cos - right*sin`; yaw (T3.y) is a Rodrigues
rotation of right about up' (VS0:3271-3294).

**Quad size.** `offset = (R_yaw*(right*cos + up*sin))*(T0.x + VC_c1.y) + up'*(T0.y + VC_c1.z)`
(VS0:3294) and `worldPos = centre + offset*r` (VS0:3329): the half-extent is the
radius. r becomes 0 when vertex alpha is below 1/255. UVs are
`(T0.x*0.5 + 0.5, -T0.y*0.5 + 0.5)` (VS0:186), so corners are at plus or minus 1.

**Screen-size limits** (VS0:3300-3321), when VC_c2.z > 0, with d = |cam - centre|:
if r > VC_c0.z*d the colour is 0; else if r > VC_c0.y*d the colour is multiplied
by `1 - (r - c0.y*d)/(d*(c0.z - c0.y))`; then `r = clamp(r, VC_c0.x*d, VC_c0.w*d)`.

**Aspect block** (VS0:160-183), when `o == 0 && VC_c2.z == 0`: with
`a = PV_c21.y/PV_c21.x`, if a > 16/9 then `pos.y *= (16/9)/a`, else
`pos.x *= a*0.5625`. This scales the centre, not the quad. The source2-compiler
in-game finding that screen-space sprite quads are not aspect-corrected (packs
cancel it with `m_flFinalTextureScaleU = 0.5`) is consistent with this applying
only to a screen-space path; see UNSETTLED.

**Depth bias.** Projected z becomes `max(w*(z'/w'), min(0.001, z))`, where z'
comes from `pos + PV_c28*VC_c1.x` (VS0:3335-3337). Only depth moves.

**Vertex colour gamma.** Exact sRGB-to-linear (0.04045 / 12.92 / 2.4) when
`(int(VC_c4.z) >> 8) & 15` is set (VS0:3129-3158).

**Sprite sheet** (VS0:1577-1740), when `VC_c16w & 32768` and VC_c11z.y > 0;
`g_tParticleSheetTexture`, point filtered, wrap:
- `row = VC_c11z.x + 3*seq*VC_c11.y` (seq set to 0 if it is at least the
  sequence count; three rows per sequence).
- `hdr = texel(0, row)`; `k = (1/4096)/hdr.y` if `VC_c16w & 4096`, else 1.
- `t = k*W*0.5*age*(sel*VC_c4.w, or 1 if that is 0)*VC_c1.w`, sel being the
  scale-source value from section 2.
- `idx = 2*floor(t)`; hold the last frame (`min(W - 2, idx)`) if hdr.z > 0,
  otherwise wrap (`fmod(idx, W)`); `u = idx/W`.
- Blend weight `w = lerp(texel(u,row).x, texel(u,row).w, fract(t))`.
- Frame rects at (u, row+1) and (u, row+2), crop rect R at (u + 1/W, row+1); UV
  remap `F.xy + (local - R.xy)*(F.zw - F.xy)/(R.zw - R.xy)`, local being the zoom
  lerp (`saturate(1/VC_c3.y)`, weighted w/2 + 1/2 and w/2) then
  `/VC_c5.zw + VC_c5.xy` (VS0:1731-1738).
- Output `(uv0, uv1)` and blend weight; with no sheet the flat output is
  (-1, -1, 1, 0).

## 4. Sprite pixel shader (static 0, dynamic 0)

1. Base sample (PS0:85-88): `g_tBaseTexture` is SrgbRead, so hardware decodes
   to linear. Clamp if `int(PC_c3.z) & 1`, else wrap; trilinear, aniso 1.
2. MOD2X bias: if `PC_c19z & 1`, subtract (1.5, 2.5, 1.5)/256 from rgb (PS0:94).
3. Frame blend (PS0:106-170): only if `PC_c19z & 32768`, flat > -1 and
   `PC_c19z & 1024`: `lerp(f0, f1, w)`. The luminance mode needs
   `(PC_c19z & 65536) == 1`, which is never true: dead code in this build
   (PS0:141).
4. Texture type, `(PC_c19.y >> 16) & 15` (PS0:194): SpriteCardTextureType_t
   0 DIFFUSE, 1 ZOOM, 2 1D_COLOR_LOOKUP, 3 UVDISTORTION, 4 UVDISTORTION_ZOOM.
5. Channel mix, `PC_c19.x & 15` (PS0:422): SpriteCardTextureChannel_t 0..14
   (0 RGB sets a = 1; 1 RGBA passes; 2 A sets rgb = 1; 4 RGB_ALPHAMASK gives
   (lerp(1, rgb, a), 1)).
6. Texture blend, `(PC_c19.x >> 4) & 15` (PS0:550): SpriteCardTextureBlendMode_t
   MULTIPLY c, MOD2X 2c, REPLACE c, ADD 1 + c, SUBTRACT 1 - c, AVERAGE (1 + c)/2,
   LUMINANCE rgb = lum709.
7. `c = max(lerp(1, c, PC_c8.y [times in0.w if (int(PC_c3.z) >> 12) & 1]), 0)`, then
   `c.rgb *= vtx.rgb` (PS0:598-599).
8. Unless `PC_c19z & 262144`: if PC_c0.w < PC_c0.z, `a = smoothstep(c0.w, c0.z, a)`
   (PS0:614). Outline: for a in [c6.x, c6.w],
   `t = a <= c6.z ? smoothstep(c6.x, c6.z, a) : smoothstep(c6.w, c6.y, a)`,
   `c = lerp(c, PC_c5, t)` (PS0:621-642).
9. `rgb = lerp(rgb, lum709(rgb), PC_c9.x)` (PS0:654); `c *= PC_c4` (PS0:659);
   `a += PC_c8.x` (PS0:660); `a = saturate(a)` if `PC_c19z & 1024` (PS0:667).
10. `a *= vtx.a` (PS0:686). Alpha-reference path when `PC_c19z & 262144`
    (PS0:676-680): `a = min(sat((in4.z + in4.w - a)/c0.z), 1 - sat((in4.z - a + c0.w)/c0.w))*max(c6.x, a)*max(c6.y, vtx.a)`.
11. Lighting (PS0:689): `rgb *= in5.rgb*PC_c0.y + PC_c0.x + in5.w`, in5 being the
    vertex light sum (in5.w = 0 in this combo).
12. Hard alpha (PS0:697): `a' = PC_c19z & 524288 ? saturate(a*128) : a`.
13. MOD2X output (PS0:707): `rgb = lerp(0.5, lerp(0.5, rgbPreLight, vtx.rgb), a')`,
    saturated (PS0:807). No fog, no overbright on this path.
14. Otherwise: `rgb *= PC_c1.z` if `PC_c19z & 16` (PS0:720); fog if `PC_c19z & 128`
    (section 5); `rgb *= lerp(a', 1, saturate(-PC_c1.w))` (PS0:795);
    `out = (rgb, a'*|PC_c1.w|)` (PS0:803).

PC_c1.w is a signed blend scalar: positive premultiplies rgb by alpha, 0 is
purely additive under ONE / INV_SRC_ALPHA (and zeroes the fog colour), negative
leaves rgb unmultiplied.

There is no alpha test (no `discard` in any forward combo, S_OPAQUE included),
no rgb saturate outside MOD2X, and no explicit add-self term.

**Render state**, decoded from the PS dynamic expressions in
`csgo_spritecard_vulkan_50_ps.json`, enum values from vfx_dx11.dll's name-to-value
tables at file offset 0x1226a0:

| state | value |
|---|---|
| BlendEnable | true |
| BlendOp | F_LIGHTEN_MODE ? MAX : ADD |
| SrcBlend | F_MOD2X ? DEST_COLOR : ONE |
| DstBlend | F_LIGHTEN_MODE ? ONE : (F_MOD2X ? SRC_COLOR : INV_SRC_ALPHA) |
| SrcBlendAlpha | F_MOD2X ? ZERO : INV_DEST_ALPHA |
| DstBlendAlpha | ONE |
| DepthWriteEnable | false |
| DepthFunc | F_DISABLE_DEPTHTEST ? ALWAYS : LESS_EQUAL |

VRF prints the DstBlend expression as `F_LIGHTEN_MODE || ...`; the table is the
bytecode decoded by hand.

## 5. Depth feathering, fog, MBOIT

- **Depth feathering** (D_DEPTH_FEATHERING=1, `_ps_s0_f1.cb.hlsl`:700-711): the
  scene point is rebuilt from `g_tDepthTexture.Load(pixel)` as
  `cam + v/((sat((d - PV_c23.x)/(PV_c23.y - PV_c23.x))*PV_c16.z + PV_c16.w)*dot(PV_c28, v))`,
  v = worldPos - cam. `off = (1 - texAlpha) + (no sheet ? |uv*2 - 1| : 0)`;
  `a *= saturate((dist - off*PC_c1.y*PC_c2.y/(no sheet ? 2 : 1) - PC_c1.x)/(PC_c1.y - PC_c1.x))`.
- **Gradient fog** (CSGO_c0.y; PS0:733-790), v = worldPos - PV_c26:
  `f = sat(CSGO_c23.xy + CSGO_c23.zw*(|v|, z))`,
  `amt = f.x^c24.x * f.y^c24.y * CSGO_c25.w`,
  `rgb = lerp(CSGO_c25.rgb*PC_c1.w, rgb, 1 - amt)`. Alpha unchanged.
- **Cubemap fog** (CSGO_c0.z):
  `g = sat(max(0, |v|*c27.y + c27.x)^c27.w)*sat(max(0, z*c28.y + c28.x)^c28.z)`,
  `amt = sat(g)*c33.w`, colour = `cube(normalize(CSGO_c29*v), lod c28.w*sat(1 - g*c27.z))*c34.x*PC_c1.w`,
  `a *= 1 - amt`. csgo_complex uses the same formula.
- **MBOIT** (f4 pass 1, f8 pass 2): pass 1 writes `-log(1 - clamp(a, 1e-5, 0.9999))`
  and six power moments of warped depth
  `2*(log(z) - CSGO_c37.w)/(CSGO_c38 - CSGO_c37.w) - 1`; pass 2 outputs
  `premulColour*T(z)` from the moments with bias CSGO_c37.y. Additive particles
  (|c1.w| == 0) are given a = 0.0005 so they register.

## 6. Trails and ropes (S_DRAW_AS_TRAIL=1)

- Four control points in T3..T6 (xyz position, w radius); `t = T0.y*0.5 + 0.5`.
- Position: uniform Catmull-Rom (tension 0.5) between P1 = T4 and P2 = T5
  (TVS:2753). Width: the same spline over the radii (TVS:2784).
- `side = normalize(cross(tangent, N))`, N being the interpolated particle normal
  (T9/T10) for o = 3, (0,0,1) for o = 2, cam - pos otherwise (TVS:2765-2783).
- `pos = spline + side*width*T0.x` (TVS:3041): full width is 2 x radius. Sprite
  screen-size clamp and depth bias apply as well.
- Colour `lerp(COLOR0, COLOR1, t)`, each optionally sRGB-decoded (TVS:2742);
  alpha also fades by `1 - smoothstep(VC_c2.x, VC_c2.y, |dot(segmentDir, viewDir)|)`.
- V (TVS:173): if VC_c3.w > 0, `V = (lerp(T2.z, T7.x, t) + VC_c3.z)*VC_c3.w`
  (distance plus offset, times 1/world size); otherwise the corner UV, scaled by
  (1 - T8.xy) and flipped by T8.zw.
- Tessellation counts and the static vertex buffer (`spritecardtessvb`,
  `tes_index_buffer`) are C++ side.

## 7. Cables (cables.vfx)

`cables_vulkan_50_vs_s0_f0.glsl`: control points from `g_transformBuffer` (xyz,
w radius), Catmull-Rom over t = TEXCOORD0.z, tube cross-section from
TEXCOORD0.xy. `u = g_vColorMapParams.x + .z*T0.w`, `v = .y + .w*(segment + t)`.
The instance colour is sRGB-decoded twice: `srgb2lin(srgb2lin(c))`. DstBlend is
`F_ADDITIVE_BLEND ? ONE : INV_SRC_ALPHA`.

## 8. Lights on particles

Particles are lit per vertex in the VS from `g_BarnLights` (std430, 480-byte
stride) using cluster cull bits (VS0:3380-3646). csgo_complex runs the same
function (static 5120, S_SHADER_QUALITY=1: `csgo_complex_..._s5120_f0.glsl`:626-669).

`L = LIT_c18.rgb + LIT_c1946.rgb + sum(lights) + (LIT_c7[0..2].w)`. A light is
skipped outside its projection box (m0 at byte 0) and its OBB (m15 at byte 272).
Per light, m3 at byte 144:
- near fade `smoothstep(0, 1, z_light*m3.z)`; far fade `smoothstep(0, 1, (1 - z)*m3.w)`;
- point or spot (m2.w != 0; m2 at byte 128 = position, r squared):
  `* m2.w/max(d^2, m2.w)`, then `* smoothstep(0, 1, m3.x + m3.y*d)`, then
  `* saturate(m6.x + m6.y*dot(fwd, normalize(Lvec)))` (VS0:3488). **The spot cone
  is a saturated linear ramp in cosine**, no smoothstep. fwd is the quaternion's
  X axis (m5 at byte 176); Lvec goes to the closest point on a capsule segment
  along the quaternion's Y axis times m6.z (m6 at byte 192; 0 for non-capsule
  lights);
- directional (m2.w == 0): m2 is the direction, no distance falloff.

Colour is `m4.rgb*att` (m4 at byte 160). m4.w == 0 applies a barn-door mask
`smoothstep(1, m9.x, |x|)*smoothstep(1, m9.y, |y|)` (superellipse when
m9.z != 0; VS0:3507-3540); m4.w < 0 samples a spherical cookie slice -m4.w of
`g_tLightCookieTexture`, m4.w > 0 a projected cookie slice. Shadows apply when
m8.z > 0 and CSGO_c2.x == 0 (9-tap PCF, weights LIT_c0.w*4, LIT_c1.x, LIT_c1.y),
mixed by `lerp(1, shadow, m12)` (byte 260). There is no light-style term, so any
flicker is already in m4. S_SHADER_QUALITY=0 replaces the smoothsteps with
`min(1, x)` and `saturate`.

This is the same shape as the baked barn light vrad3 evaluates
(`Lighting/Bake/Light.cs`, from scenesystem 18008c110 PathTraceBarnLight_FromShape,
on research/map-authoring): the runtime and bake records share their form.

## 9. Tone mapping (for brightness matching only)

`post_process_vulkan_50_ps_s0_f{0,18,48,96}.glsl`:
`x = min(colour*PV.scalar*PP._m0*2.8, PP._m7)`; Hable/Uncharted 2
`((x(Ax + CB) + DE)/(x(Ax + B) + DF) - E/F)*PP._m8` with A..F = PP._m1.._m6; then
exact linear-to-sRGB, screen fade and `pow(c, FullScreenGamma/2.2)`. Bloom (f18)
is added before the curve; a screen-blend bloom term passes through
`x/(x + 0.187)*1.035`; the 3D LUT is sampled at `saturate(c)*0.96875 + 1/64`.
D_COLORBUFFER_TONEMAPSPACE 1 decodes `c/(1 - max(rgb))` first; 2 also skips the
exposure multiply. Forward particle output is linear HDR before exposure.

## UNSETTLED (shader side)

1. Which vpcf field fills which VC/PC slot. Leading candidates: PC_c0.x/y =
   1 - diffuse / diffuse; PC_c0.zw = alpha map-to-zero/one or edge softness;
   PC_c1.xy = feathering min/max distance; PC_c2.y = feathering filter;
   PC_c1.z = overbright; PC_c1.w = output-blend scalar; PC_c4 = colour/alpha
   scale; PC_c5/c6 = outline colour/params; PC_c8.x = alpha bias; PC_c9.x =
   desaturation; VC_c0 = screen sizes; VC_c1.x = depth bias; VC_c1.yz = centre
   offsets; VC_c1.w = animation rate; VC_c2.xy = fade dot; VC_c3.y = zoom;
   VC_c3.zw = V offset / 1 over V world size.
2. The flag bits: PC_c19z 1/16/128/1024/32768/65536/262144/524288; VC_c16w
   2048/4096/32768/262144; VC_c4.z sub-fields.
3. The orientation enum to shader value mapping, and when FULL_3AXIS is used.
4. What PV_c21 is, and when VC_c2.z == 0.
5. Where add-self, saturate-pre-blend and F_OPAQUE's state live (C++ side).
6. Whether RenderRopes and RenderTrails both set S_DRAW_AS_TRAIL (the only two
   references to the "F_DRAW_AS_TRAIL" string, `particles.dll` 0x1802d76a9 and
   0x1802db458, sit beside "C_OP_RenderRopes", F_ADDRESS_V_CLAMP and
   F_PARTICLE_ORIENTATION), and what feeds T2.z, T7.x and T8.
7. When MBOIT passes are active.
8. Whether particle lights enter `g_BarnLights` (C++ side, section 10).
9. The PP/PV tonemap member names and the live TONEMAPSPACE.
10. Combos not dumped: texture layers 1-4, HSV shift, refract, particle shadows,
    motion vectors, D_SUPPORT_FRAME_CROP, D_BAKED_LIGHTING_FROM_PROBE.

## 10. The standard light (C++ side)

Binaries: `particles.dll` md5 3f9ae2ff51c610d29884b51aaf570a83, `client.dll`
cf8b7cd427f553732081739154c90588, `scenesystem.dll`
3b785e13dd52e34f56b9b544956eb7b1.

**A particle light never reaches the GPU as a point or spot light.** The scene
system converts it into a barn light, the same record a `light_barn` becomes,
and that conversion replaces the vpcf's falloff curve with plain 1/d², matched
in brightness at 15% of the range. Linearity and the 50%/0% falloff distances
only set overall brightness, never the curve's shape.

### Fields

`C_OP_RenderStandardLight`: vtable 0x180529808, constructor 0x180316320, schema
field table 0x1805c8740; offsets and defaults checked against the constructor's
disassembly. Each particle becomes a light description in the render function
0x1802ee5f0 (vtable slot 48).

| field | offset | default | use |
|---|---|---|---|
| m_nLightType | 0x230 | POINT (0) | POINT 0, SPOT 1, FX 2, CAPSULE 3. SPOT becomes light type 3, POINT/CAPSULE type 1. FX is a diffuse-only point light, no shadows or fog, linearity forced to 1 |
| m_nMaxAllowed | 0x234 (u16) | 32 | light slots = min(particles, this) |
| m_vecColorScale | 0x238 | literal 255,255,255 | combined with the particle tint (below) |
| m_nColorBlendType | 0x910 | MULTIPLY (0) | below |
| m_strLightStyle / m_flLightStyleTime | 0x918 / 0x920 | none / particle age | evaluated through the client particle query (slots 0x308, 0x310); multiplies RGB |
| m_flIntensity | 0xa98 | 1.0 | per particle; at most 0 skips the particle |
| m_bCastShadows | 0xc10 | false | passed through (off for FX) |
| m_flTheta / m_flPhi | 0xd90 / 0xf08 | 45 / 60 | degrees, **half angles** from the axis: theta inner, phi outer (the barn builder uses fov = 2·phi and half-angle = fov·π/360, 0x1804a7250) |
| m_flRadiusMultiplier | 0x1080 | **8.0** | **range = particle radius × multiplier**; 0 skips |
| m_nAttenuationStyle | 0x11f8 | **OLD (0)** | NEW = 1 |
| m_flFalloffLinearity | 0x1200 | 0 | clamped to [0,1]; OLD only |
| m_flFiftyPercentFalloff / m_flZeroPercentFalloff | 0x1378 / 0x14f0 | 0.5 / 1.0 | **fractions of the range**; NEW only |
| m_bRenderDiffuse / m_bRenderSpecular | 0x1668 / 0x1669 | true / true | passed through |
| m_lightCookie | 0x1670 | none | MurmurHash2 (seed 0x31415926), looked up in the scene system (slot 0x200, 0x1800c55f0) |
| m_nPriority | 0x1678 | 0 | never read outside the constructor and serialisers |
| m_nFogLightingMode | 0x167c | NONE (0) | DYNAMIC 2, DYNAMIC_NOSHADOWS 4 |
| m_flFogContribution | 0x1680 | 1.0 | clamped at least 0; only used when the fog mode is not NONE |

Position is the particle position; direction comes from its pitch/yaw/roll
(radians to degrees, then the angle-to-vector routine 0x18035db20). A particle
with radius or alpha 0 is skipped.

### Attenuation coefficients

- **OLD** (0x1803786f0): quadratic = 1 - linearity, linear = linearity,
  constant = 0; each is zeroed below 0.001, and if all are, constant = 1. The
  colour is multiplied by (quadratic·10000 + linear·100 + constant).
- **NEW** (0x180378580, which prints Source 1's warnings verbatim): d50 = range ×
  fifty, d0 = range × zero; 1/atten(d) is fitted through (0, 1), (d50, 2),
  (d0, 256) by Valve's monotonic quadratic solver (0x18035e120), which pulls the
  middle point toward a line in 0.05 steps when the slope check (2a + b) has the
  wrong sign; then scaled to exactly 50% at d50.
- **Range clamp** (0x1803774b0, threshold 0.001), only when constant = 0 (OLD):
  the range is cut to where luminance(colour)·atten(d) = 0.001.

client.dll 0x180b849b0 copies the description unchanged into a scene light
object.

### Colour

Particle tint (0-255 bytes) and colour scale, by blend type: 0 MULTIPLY scale ×
tint; 1 and 5 scale doubled, then × tint; 2 tint / scale; 3 tint + scale·255;
4 tint - scale·255; 6 screen; 7 / 8 per-channel max / min; 9 replace with scale;
10 average; 11 255 - tint·scale; 12 tint × luminance(scale). Clamped to 0-255,
then sRGB to linear (0x18035f2e0). Then
`RGB = lerp(colour, luminance, desaturation) · style · rendererTint · intensity · alpha`
(intensity and alpha confirmed in the disassembly), then the OLD factor above.

### Conversion to a barn light

The binner (scenesystem 0x1801a1460) passes every point and spot light through
0x1804a3ce0, controlled by `lb_convert_to_barn_lights_falloff_match_point`
(default 0.15, registered at 0x180011060). For a spot light:

- m = 0.15·range (100 if that is at most 0);
  `k = m² / (constant + linear·m + quadratic·m²)`; record colour = k × colour.
- position.w = size² = 1 (size defaults to 1, 0x1801bf620).
- frustum: near = size (1 unit), far = range, fov = 2·phi, aspect 1.
- inner radius (record 0xa0/0xa4) = tan(theta)/tan(phi); shape 1 (round);
  skirt 0; cookie copied.
- distance fade (record 0xc8/0xcc) = (R/(R - s), 1/(s - R)) with s = 0.75R:
  full to 75% of the range, zero at the range.
- angle fade = (1, 0, 0): no extra angular term.

The live buffer is `barnlightconstants` (320 entries × 0x1e0 = 480 bytes, created
at 0x18019dd0d). The path tracer converts scene lights with the same function
(0x18008c110; 0x18008cb80).

### Per-pixel result, settled against the game's shader

Section 8 gives the runtime evaluation. With this record it reduces to:

```
I(d, α) = k·C / max(d², 1)
        · smoothstep(0, 1, 4 - 4d/R)
        · smoothstep(0, 1, (1 - r)/(1 - t)),  r = tan α / tan φ,  t = tan θ / tan φ   (0 when r > 1)
```

The cone term is the round-barn branch of the runtime mask (VS0, record offset
224: `smoothstep(1/r', t/r', 1)` with r' = |clip.xy| and exponent 2/shape = 2),
which simplifies exactly to the expression above. It also equals vrad3's bake
evaluation (`Lighting/Bake/LightEval.cs`), so runtime and bake agree.

### Honoured or not

Max allowed: honoured (slot table 0x1802edef0; particles keep their slot by ID,
extras are dropped, unused slots are destroyed at 0x1802ee4e0). Priority:
ignored; the binner sorts by on-screen importance, camera distance, then
luminance (0x1801a9e70), at most 315 visible barn lights. Cookie, shadows
(a shadowed point light becomes six faces) and diffuse/specular: honoured. Fog:
only when the fog lighting mode is not NONE.

### The two flashlights

Defaults relied on, all from constructors: particle constant colour 0xFFFFFFFF;
`C_INIT_InitFloat` / `C_OP_SetFloat` output field 3 (radius);
`C_OP_DistanceBetweenCPsToCP` start 0, end 1, output 2, input 0-128, output 0-1,
clamped; `C_OP_SetControlPointToImpactPoint` CP in/out 1, trace length 1024.

| | GFL | kxm |
|---|---|---|
| theta / phi (half) | 0° / 35° (full 70°) | 40° / 80° (full 160°) |
| range | 60 × 8 (default) = 480 | 60 × 20 = 1200 |
| attenuation | NEW: 50% at 240, 0% at 480 | OLD (default), linearity 1: linear 1 |
| colour | 1, 1, 1 | intensity × 100 |
| intensity | 1.0 | particle float 10: starts 0.015, eases 12% per step to the min of the probes, 0.008 to 0.3 |
| fitted 1/atten | 0.0306513 + 1.27716e-4·d + 3.36579e-5·d² | n/a |
| match point m | 72 | 180 |
| k | 24187 | 180 |
| barn colour | about 24187 | 30 × 180 = 5400 (open space) |
| in game | 24187/d² (4.67 at 72, 1.48 at 128, 0.42 at 240) | 5400/d² (0.167 at 180, 0.0824 at 256) |
| distance fade | full to 360, 0 at 480 | full to 900, 0 at 1200 |
| cone | t = 0: mask = smoothstep(1 - tan α / tan 35°) | t = 0.148: full to 40°, 0.84 at 64°, 0.56 at 72°, 0 at 80° |
| dim case | n/a | intensity 0.008: colour 0.8, range clamp to 800, m = 120, barn colour 96, fade 600 to 800 |

kxm asked for a linear falloff, but in game it is 1/d² matched at 180 units.
GFL's alpha is a notched remap of how many copies of the system run: 1 for one
copy; with more, alpha goes to 0 and the light is skipped. Both: diffuse on, no
shadows, no cookie, max allowed 1; kxm turns specular off.

### UNSETTLED (light side)

1. The code that packs the 480-byte record. The formula above is confirmed in
   the runtime shader's shape, but the packed values are inferred from the
   converter, not read from the packer.
2. Float order inside the curve solver (last digits of the fitted coefficients).
3. Whether a literal colour scale evaluates as 1.0 or 255 per channel (no
   difference for white).
4. What feeds the renderer tint and desaturation inputs (0x1802f0110 copies them
   from the caller's +0x50/+0x38).
5. kxm's runtime probes: trace directions when none is set, and whether the 12%
   easing is per frame or per tick.
6. Whether GFL's notched test is inclusive at a count of exactly 1.
7. Which exposure and tone map the barn colour is seen through (section 9).

Machine-readable tables and the float32 port of both flashlights:
`light.json` and `scripts/flashlight.py` in the analysis scratch
(not committed; Valve-derived dumps stay out of the repo).

## 11. Schema, defaults and parameter inputs

Builds: `particles.dll` 3f9ae2ff51c610d29884b51aaf570a83, `client.dll`
cf8b7cd427f553732081739154c90588, `schemasystem.dll`
e2fb50a2f3b3e8fbb321b95af1ce0433, `tier0.dll` 450b7fcd7efcddc55039fb0e7fbf7400.
Image base 0x180000000. Done in a standalone process, never attached to cs2.

### Compiled files omit defaults

A compiled `.vpcf_c` leaves out every top-level field whose value equals the
class default. Of 113,540 present top-level fields in the 2,679 stock files
(2,622 csgo, 57 core), only 20 equal the current default, all on
CParticleSystemDefinition (`m_nAggregationMinAvailableParticles = 1`,
`m_flMaximumSimTime`), probably defaults that changed after those files were
built. So an absent field means "default", and the most common explicit value
is not the default (C_OP_FadeAndKill `m_flStartAlpha` is explicitly 0.0 in 97% of
files; its default is 1.0). Inside input structs the stock files do write every
sub-field (41,037 of 44,095 float inputs in full form), and there the most common
value of each scalar sub-field is its default. Some stock files carry fields the
schema no longer has (C_INIT_CreateWithinSphereTransform
`m_flEndCPGrowthTime`). The game is assumed to apply loaded KV3 on top of a
default-constructed object (consistent with the allocate-and-construct
manipulator path and with the compiler omitting defaults; the loader itself is
not decompiled).

### Where the schema and defaults come from

- **Schema:** `particles.dll!InstallSchemaBindings("SchemaSystem_001")` (export
  0x180429b50) fills static records in place: 555 classes, 103 enums. Class
  record: +0x08 name, +0x20 size, +0x24 field count, +0x28 align, +0x29 base
  count, +0x30 fields, +0x38 bases, +0x48 static metadata, +0x68 manipulator.
  Field record (0x20): name, CSchemaType*, offset, metadata count, metadata.
  Enum record: +0x18 size, +0x1c count, +0x20 enumerators (0x20 each).
- **Defaults:** every concrete class has an `MGetKV3ClassDefaults` metadata
  function that default-constructs it and serializes it to KV3 (C_OP_Decay's is
  0x180226f00). Called for 520 classes; the 29 abstract bases return null, and
  their fields hold the same default in every derived class (387 of 387
  operators).
- **Checked two ways, no disagreements:** constructing each class in place with
  its manipulator (action 4) and reading memory at each field offset (1,380
  fields, 63 classes; Color serializes as 3 components and drops alpha 255); and
  the constant stores in 16 decompiled constructors (158 agree, 0 disagree):
  InitFloat 0x1801b7750, RenderSprites 0x180350ef0, InstantaneousEmitter
  0x18010b720, CreateWithinSphereTransform 0x18017c720, PositionLock
  0x18022dd70, BasicMovement 0x1801c0870, RenderRopes 0x1802d7ac0,
  ContinuousEmitter 0x18010c450, SetFloat 0x180261d90, CreateWithinBox
  0x18017f3d0, RingWave 0x180173f20, plus Decay, InterpolateRadius,
  FadeAndKill, ColorInterpolate and AlphaDecay (inline in their manipulators).
  Shared: CParticleFunction 0x1800a0450, CParticleFloatInput 0x1803837b0,
  literal init 0x180384040, CParticleVecInput 0x18038d330.

Particle attribute names come from the pointer table at rva 0x4d7a10 (index =
ParticleAttributeIndex_t; 19 is NONE): 0 Position, 1 Life Duration, 2 Position
Previous, 3 Radius, 4 Roll, 5 Roll Speed, 6 Color, 7 Alpha, 8 Creation Time,
9 Sequence Number, 10 Trail Length, 11 Particle ID, 12 Yaw, 13 Second Sequence
Number, 14 Hitbox Index, 15 Hitbox Offset Position, 16 Alpha Alternate,
17 Scratch Vector, 18 Scratch Float 1, 20 Pitch, 21 Normal, 22 Glow RGB,
23 Glow Alpha, 26/27 Scratch float 2/3, 30 Scratch Vector 2, ... Roll, Roll
Speed, Yaw and Pitch (mask 0x101030) are stored in radians and read as degrees.
11, 14, 33 and 40-43 are int32.

### Float inputs

Evaluated four particles at a time: 0x18005b3b0 sets the map mode, 0x18005ad90
calls one evaluator per map type (DIRECT 0x18005ca10, MULT d230, REMAP da90,
REMAP_BIASED e500, CURVE f130, NOTCHED fb60, ROUND 0x1800603f0, MIN 60dc0,
MAX 61670, MOD 61f20). Collection inputs use the first value.

- **LITERAL** returns `m_flLiteralValue` unchanged under every map type.
- **RANDOM_UNIFORM / RANDOM_BIASED** ignore `m_nMapType`: forced to REMAP (or
  REMAP_BIASED) from [0, 1] to [min, max], clamped; the sign flip applies after
  the clamp when a second draw is below 0.5 (0x180090850). Draws come from a
  4,096-entry table (0x180598fa0); VARYING advances a counter per draw, CONSTANT
  uses the seed plus the particle's ID.
- **CONTROL_POINT_COMPONENT**: CP position component `clamp(m_nVectorComponent, 0, 3)`
  (3 reads past the vector).
- **COLLECTION_AGE**: the collection's current time (starts at minus the pre-sim
  time, plus dt each frame).
- **PARTICLE_AGE**: now minus creation time. **PARTICLE_AGE_NORMALIZED**: that
  over lifetime, 0 if lifetime is at most 0, not clamped.
- **PARTICLE_FLOAT**: the attribute (angles read as degrees, int attributes
  converted). Inside initializers INITIAL_FLOAT and INITIAL_VECTOR_COMPONENT read
  the current value.
- **CONCURRENT_DEF_COUNT**: the number of live collections of this `.vpcf`
  anywhere in the game, the evaluating one included, minus those whose emission
  stopped. Counter at definition +0x3b8: incremented in CParticleCollection::Init
  (0x180039298), decremented at shutdown (0x18003c5c0), toggled when emission
  stops or restarts (0x1800486e0).

Map types:
- MULT: `value * m_flMultFactor`, the only use of the factor; no map type has an
  offset term.
- REMAP: LOOPED wraps x with period `m_flInput1` (not in1 - in0); then
  `t = (x - in0)/(in1 - in0)` (a step at in0 when the range is zero); output
  `out0 + t*(out1 - out0)` clamped to [min(out0, out1), max(out0, out1)].
- NOTCHED: OutputInside when min(range) <= x <= max(range), both ends inclusive,
  else OutputOutside; input mode not applied.
- CURVE: under 2 points gives 0; otherwise a Hermite segment from the left
  point's outgoing slope and the right point's incoming slope, clamped to the
  domain's y range; LOOPED wraps with period `m_vDomainMaxs.x` (0x18036fb10).
  Input mode is read only by REMAP, REMAP_BIASED and CURVE.
- ROUND: nearest is half to even; FLOOR, CEIL. MIN and MAX against
  `m_flCompareValue`; MOD truncates.
- Bias (0x18006f1e0): STANDARD is Schlick `t/((1 - t)*k + 1)` with `k = 1/b - 2`,
  `b = clamp((p + 1)/2)`; GAIN its two-sided form; EXPONENTIAL `t^e` with
  `e = 1 - p` for p >= 0 and `20 - 19(p + 1)` for p < 0.

### Vector inputs

0x1800bfa80 / 0x1800bf0f0. LITERAL_COLOR is rgb/255 (alpha ignored); CP_VALUE is
CP position times `m_vCPValueScale`; FLOAT_INTERP_*:
`t = (f - in0)/(in1 - in0)` with no zero-range guard; CLAMPED clamps t, OPEN does
not, GRADIENT samples the gradient.

### Operator strength and time

0x18003f820 / 0x18003f6e0: strength = `m_flOpStrength` times the fade-in/fade-out
factor, passed to Operate; endcap gating and the time offset/scale seeds apply;
an oscillate period > 0 replaces operator time with `fmod(curTime/period, 1)`.

### Operators the flashlight and tracer use

- **C_OP_SetControlPointToPlayer** (0x1802811e0; game side client.dll
  CParticleSystemQuery, vtable 0x181c38028): CP = player position +
  `m_vecCP1Pos`, orientation = the eye transform. Position (0x180a47d10) supports
  ABS_ORIGIN, WORLDSPACE_CENTER and EYES; PARTICLE_FLASHLIGHT returns (0, 0, 0)
  and leaves the radius output unwritten (radius set to 0). Orientation
  (0x180a47ac0): with `m_bOrientToEyes` false it is re-based on world up
  (heading kept, pitch and roll dropped).
- **C_OP_SetControlPointToImpactPoint** (0x180283f50): at most once per
  `m_flUpdateRate`; traces from the CP along `m_vecTraceDir` (the CP forward if
  zero) for `m_flTraceLength`, starting `m_flStartOffset` along the ray; on a hit,
  or with `m_bSetToEndpoint`, position = endpoint - dir*`m_flOffset`, oriented to
  the surface normal. Needs a world trace.
- **C_OP_DistanceBetweenCPsToCP** (0x180280be0): clamped remap of the CP distance
  into a CP field (0x1802744e0), with set-once and set-parent modes. The LOS
  branch normalises the end CP's position vector, not end - start.
- **C_OP_SetControlPointFieldToScalarExpression** (0x180277070): 11 expressions
  (divide by zero gives 0), then the output remap, then a blend toward the
  current field by `m_flInterpolation` when it is not exactly 1.
- **C_OP_SetVectorAttributeToVectorExpression** (0x180203ec0): result times
  operator strength before the set method; DIVIDE uses an approximate reciprocal
  (zero becomes 1.19e-7), normalize an approximate rsqrt; colour and alpha
  outputs are clamped.
- **C_OP_AlphaDecay** (0x1801d3330): kills a particle when
  alpha*alphaAlternate <= `m_flMinAlpha` + FLT_EPSILON.
- **C_OP_SetFloat** (0x18020bd30, init 0x18020bbc0): each set method is
  `current + clamp(m_Lerp, 0, 1)*(target - current)`, then the output clamp, then
  a strength blend toward the current or initial value; RAMP_CURRENT uses
  strength*dt. Writing an angle attribute converts degrees to radians except for
  the two SCALE methods. Alpha clamps to [0, 1], radius and trail length to >= 0.
- **C_INIT_CreateWithinBox** (0x180136ec0): without `m_bLocalSpace`, a world-axis
  box offset from the CP position interpolated to the creation time; with it,
  the box is in the CP frame (x forward, y left, z up). Three table draws
  (index = counter + 2*seed offset), or a Halton sequence (bases 2, 3, 5) with
  distribute-evenly.
- **C_OP_PositionLock** (0x1801d8e90): moves each particle by
  `(CP delta*strength)*min(age, dt)/dt`, attenuated by `m_flRange` /
  `m_flRangeBias`, scaled by `m_vecScale`; `m_flPrevPosScale` moves the previous
  position too. `m_flJumpThreshold` applies only when `m_flStartTime_min` < 1 and
  is compared with the squared move distance.
- **C_INIT_RingWave** (0x1801251c0): angle = counter*(2π/particles-per-orbit),
  or random when not evenly distributed; the ring point is rotated by
  pitch/yaw/roll, a uniform point in a ball of radius `m_flThickness` (cube-root
  draw) is added, then the CP transform applies. Velocity points outward from the
  CP, scaled by the previous frame's dt; `m_bXYVelocityOnly` zeroes z.
- **C_INIT_CreateSequentialPathV2** (0x18014e4b0): quadratic Bezier start / mid /
  end; a given mid CP becomes `2*Mid - (S + E)/2` so the curve passes through it;
  bulge modes apply with no mid CP; per-axis jitter of plus or minus
  `m_fMaxDistance`; step 1/(N - 1); `m_bLoop` wraps, otherwise the path bounces.

### Collection layout and helpers

CParticleCollection offsets the evaluators read (not schema-bound; from the
decompiles cited above and in the analysis scratch):

<code>
+0x18          CParticleSystemDefinition** (definition handle)
+0x20 / +0x30  first child collection / next sibling (SetControlPoint* propagate to every child)
+0x28          parent collection
+0x40..0x4c    current time broadcast x4
+0x50          current time: -preSimTime at Init (0x180038c20), += dt each frame (0x180047af9)
+0x5c          time the endcap started
+0x60          operator time origin (operator time = max(0, curTime - this))
+0x64          this frame's dt
+0x68          previous non-zero dt (written only when dt != 0, 0x180047adb)
+0x74          active particle count
+0x168/+0x170  CP index -> slot byte table / control point array (0xE0 bytes each)
+0x2d8 + 8*i   attribute i stream; +0x460 + i its stride byte (>>2 = 16-byte blocks of 4 particles)
+0x4c8 + 8*i   attribute i initial-value stream; +0x650 + i its stride
+0x8bc         flags: 8 = emission stopped, 0x10 = endcap; +0x8c1 bit 6 = in endcap
+0x910/914/918 random-table counter / per-collection seed offset / per-frame operator seed

control point (0xE0):
+0x00 position   +0x0c previous position   +0x18 forward   +0x24 up   +0x30 left
+0x3c/+0x48/+0x54 previous forward / up / left
+0x90 flags (0 = never set; |1 position, |0x1c orientation)   +0x94 last change time (-1.0 = never)
</code>

Helpers: `SetControlPointPosition` 0x1800364e0 (propagates to every child
collection); `GetControlPointTransformAtTime` 0x1800c1e70 (position interpolated
to the given time, current orientation columns forward/left/up; used by
C_INIT_CreateWithinBox's local-space path); remap constant setup 0x18005b5b0
(`ctx, mapType, biasType, in0, in1, out0, out1, biasParam`, argument registers
confirmed in the disassembly). Integer attributes: mask 0xf00200004800 (11, 14,
33, 40-43), stored int32 and read through cvtdq2ps (0x18006f510 case 0x15).

### UNSETTLED (schema and inputs)

1. Where run-once pre-emission operators (`m_bRunOnce`) execute: the per-frame
   loop 0x180047cb0 skips them.
2. C_OP_PositionLock with `m_bLockRot`; vector RANDOM_UNIFORM / _OFFSET; PVEC
   types 6, 10, 18; noise inputs.
3. The random table contents and seed values (rules settled, bit-exact
   sequences not).
4. The world-up re-basing in client 0x181748070, and the per-instance
   collision-group context.
5. The client trace function (query slot 4) and the renderer operators' runtime
   maths beyond section 10.

Machine-readable results (schema.json: 64 classes with every field's type,
offset, default and evidence; inputs.json: enums, struct layouts and defaults,
evaluation rules, per-operator maths) are in the analysis scratch, not committed.
