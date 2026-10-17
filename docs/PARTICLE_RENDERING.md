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
| 4 | n | normalize(cross(n, PV_c28)) |

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

Pending: `C_OP_RenderStandardLight` in `particles.dll`.

## 11. Schema, defaults and parameter inputs

Pending: class fields, defaults and input evaluation from `particles.dll`.
