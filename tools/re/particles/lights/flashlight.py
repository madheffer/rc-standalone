"""Port of the decompiled particle-light -> barn-light chain (float32), applied to the
GFL and kxm cs2fixes/simple_flashlight.vpcf. Writes ../light.json."""
import json, math, sys
import numpy as np
f32 = np.float32

def solve_inv_quad_monotonic(x1, y1, x2, y2, x3, y3):
    # particles 18035e120 (SolveInverseQuadraticMonotonic); returns (ok, a, b, c), y = a x^2 + b x + c
    x1, y1, x2, y2, x3, y3 = map(f32, (x1, y1, x2, y2, x3, y3))
    if x2 < x1: x1, y1, x2, y2 = x2, y2, x1, y1
    if x3 < x2: x2, y2, x3, y3 = x3, y3, x2, y2
    if x2 < x1: x1, y1, x2, y2 = x2, y2, x1, y1
    t = f32(0)
    det = (x1 - x2) * (x1 - x3) * (x2 - x3)
    a = b = c = f32(0)
    while True:
        if det == 0: return False, a, b, c
        lin = ((x2 - x1) * (y3 - y1)) / (x3 - x1) + y1
        ty2 = lin * t + (f32(1) - t) * y2
        a = ((ty2 - y1) * x3 + (y1 - y3) * x2 + (y3 - ty2) * x1) / det
        b = ((y1 - ty2) * x3 * x3 + (ty2 - y3) * x1 * x1 + x2 * x2 * (y3 - y1)) / det
        c = ((y1 * x3 - x1 * y3) * x2 * x2 + x1 * x3 * (x3 - x1) * ty2 + (x1 * x1 * y3 - x3 * x3 * y1) * x2) / det
        deriv = a + a + b
        if (y1 < y2) and (y2 < y3):
            if not (deriv < 0): return True, a, b, c
        elif (y1 > y2) and (y2 > y3):
            if not (deriv > 0): return True, a, b, c
        else:
            return True, a, b, c
        t = f32(t + f32(0.05))
        if not (t <= 1): return True, a, b, c

def new_style(d50, d0):
    # particles 180378580: returns (constant, linear, quadratic)
    d50, d0 = f32(d50), f32(d0)
    if d0 < d50: d0 = d50 + d50
    ok, a, b, c = solve_inv_quad_monotonic(0, 1, d50, 2, d0, 256)
    s = f32(2) / ((a * d50 + b) * d50 + c)
    return float(c * s), float(b * s), float(a * s), ok

def old_style(linearity):
    # particles 1803786f0 called with (q = 1 - lin, l = lin, c = 0); returns (c, l, q, colour scale)
    q, l, c = f32(1) - f32(linearity), f32(linearity), f32(0)
    eps = f32(0.001)
    if q < eps: q = f32(0)
    if l < eps: l = f32(0)
    if c < eps:
        c = f32(0)
        if l < eps and q < eps: c = f32(1)
    scale = q * f32(10000) + l * f32(100) + c
    return float(c), float(l), float(q), float(scale)

def srgb_to_linear(byte):
    v = min(max(byte / 255.0, 0.0), 1.0)
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

def smoothstep(e0, e1, x):
    t = min(max((x - e0) / (e1 - e0), 0.0), 1.0)
    return t * t * (3 - 2 * t)

def flashlight(name, radius, radius_mult, intensity, alpha, tint, theta, phi, style, fifty=0.5, zero=1.0, linearity=0.0, match=0.15):
    R = radius * radius_mult
    col_lin = [srgb_to_linear(min(255, int(255 * t))) for t in tint]  # MULTIPLY blend with colour scale white
    C = [cl * intensity * alpha for cl in col_lin]
    if style == 'NEW':
        c, l, q, ok = new_style(R * min(max(fifty, 1.1920929e-07), max(zero, 2.3841858e-07) - 1.1920929e-07), R * max(zero, 2.3841858e-07))
        scale = 1.0
    else:
        c, l, q, scale = old_style(linearity)
        ok = True
        C = [x * scale for x in C]
    R_unclamped = R
    if c == 0.0:
        # particles 1803774b0 with threshold 0.001: range = min(range, distance where lum * atten = 0.001)
        lum = 0.2126 * C[0] + 0.7152 * C[1] + 0.0722 * C[2]
        if lum > 0:
            kk = 0.001 / lum
            A, B, Cc = kk * q, kk * l, kk * c - 1.0
            if A == 0:
                roots = [-Cc / B] if B != 0 else [0.0]
            else:
                disc = B * B - 4 * A * Cc
                roots = [(math.sqrt(disc) - B) / (2 * A), (-B - math.sqrt(disc)) / (2 * A)] if disc >= 0 else [0.0]
            dthr = max(0.0, max(roots))
        else:
            dthr = 0.0
        if 0 < R <= dthr: dthr = R
        R = dthr
    m = R * match
    k = (m * m) / (m * m * q + m * l + c)
    t = math.tan(math.radians(theta)) / math.tan(math.radians(phi))
    s = R - R * 0.25
    fade = (R / (R - s), 1.0 / (s - R))
    def atten_quadratic(d): return 1.0 / (c + l * d + q * d * d)
    def barn(d, alpha_deg=0.0):
        if d < 1.0: dd = 1.0
        else: dd = d * d
        dist = smoothstep(0, 1, fade[0] + fade[1] * d)
        r = math.tan(math.radians(alpha_deg)) / math.tan(math.radians(phi))
        cone = 0.0 if r > 1 else (smoothstep(0, 1, (1 - r) / (1 - t)) if t < 1 else (1.0 if r <= 1 else 0.0))
        return k / dd * dist * cone
    samples = {}
    for d in (16, 32, 64, 0.15 * R, 128, 256, 0.5 * R, 0.75 * R, 0.875 * R, R):
        samples['%g' % d] = {'barn_scalar': barn(d), 'barn_rgb': [x * barn(d) for x in C], 'desc_quadratic_atten_times_C': [x * atten_quadratic(d) for x in C]}
    cone = {('%g' % a): barn(0.5 * R, a) / barn(0.5 * R, 0.0) for a in [round(phi * i / 10.0, 3) for i in range(11)]}
    return {
        'name': name, 'range_before_threshold_clamp': R_unclamped, 'type': 'PARTICLE_LIGHT_TYPE_SPOT -> CLightDesc type 3 -> barn LightShape luminaire 0 (punctual)',
        'range_units': R, 'theta_inner_half_deg': theta, 'phi_outer_half_deg': phi, 'full_cone_deg': 2 * phi,
        'desc_color_linear': C, 'attenuation_style': style,
        'desc_atten_constant_linear_quadratic': [c, l, q], 'solver_ok': ok,
        'falloff_match_distance': m, 'barn_k': k,
        'barn_color_m_vColor': [x * k for x in C],
        'barn_position_w_size2': 1.0, 'near_plane': 1.0, 'far_plane': R,
        'm_vInnerRadius': [t, t], 'm_flShape': 1.0, 'm_vSkirt': [0.0, 0.0],
        'm_vDistanceFade': list(fade), 'distance_fade_start_end': [s, R],
        'm_vAngleFade': [1.0, 0.0, 0.0],
        'samples_on_axis': samples, 'cone_mask_relative_at_half_range': cone,
    }

out = {
    'binaries': {
        'particles.dll': '3f9ae2ff51c610d29884b51aaf570a83',
        'client.dll': 'cf8b7cd427f553732081739154c90588',
        'scenesystem.dll': '3b785e13dd52e34f56b9b544956eb7b1',
    },
    'C_OP_RenderStandardLight_fields': [
        ['m_nLightType', '0x230', 'int enum', 'POINT(0)', 'POINT 0, SPOT 1, FX 2, CAPSULE 3'],
        ['m_nMaxAllowed', '0x234', 'read as u16', '32', 'slot cap: min(particle count, m_nMaxAllowed) lights'],
        ['m_vecColorScale', '0x238', 'CParticleCollectionVecInput', 'LITERAL_COLOR 255,255,255', 'per collection'],
        ['m_nColorBlendType', '0x910', 'enum', 'MULTIPLY(0)', ''],
        ['m_strLightStyle', '0x918', 'vdata choice (scripts/light_styles.vdata)', 'empty', 'resolved via IParticleSystemQuery+0x308'],
        ['m_flLightStyleTime', '0x920', 'float input (per particle)', 'PF_TYPE_PARTICLE_AGE (type 19)', 'evaluated via IParticleSystemQuery+0x310'],
        ['m_flIntensity', '0xa98', 'float input (per particle)', '1.0', 'multiplies colour; <=0 skips the particle'],
        ['m_bCastShadows', '0xc10', 'bool', 'false', 'desc+0x3d; forced false for FX'],
        ['m_bDynamicBounce', '0xc11', 'bool', 'false', 'desc+0x75 = 2 if set; attribute 0xedc61ed4 on the light'],
        ['m_flBounceScale', '0xc18', 'float input', '1.0', 'attribute 0x266c41fb (vec4)'],
        ['m_flTheta', '0xd90', 'float input', '45.0', 'inner cone HALF angle, degrees, desc+0x30'],
        ['m_flPhi', '0xf08', 'float input', '60.0', 'outer cone HALF angle, degrees, desc+0x34'],
        ['m_flRadiusMultiplier', '0x1080', 'float input', '8.0', 'range = particle radius * this, desc+0x1c; 0 skips all'],
        ['m_nAttenuationStyle', '0x11f8', 'enum', 'LIGHT_STYLE_OLD(0)', 'NEW = 1'],
        ['m_flFalloffLinearity', '0x1200', 'float input', '0.0', 'clamped [0,1]; OLD only'],
        ['m_flFiftyPercentFalloff', '0x1378', 'float input', '0.5', 'fraction of range; NEW only'],
        ['m_flZeroPercentFalloff', '0x14f0', 'float input', '1.0', 'fraction of range; NEW only'],
        ['m_bRenderDiffuse', '0x1668', 'bool', 'true', 'desc+0x78'],
        ['m_bRenderSpecular', '0x1669', 'bool', 'true', 'desc+0x79'],
        ['m_lightCookie', '0x1670', 'string', 'empty', 'MurmurHash2(seed 0x31415926) -> +0x1820 -> ISceneSystem vfunc 0x200 -> desc+0x38'],
        ['m_nPriority', '0x1678', 'int', '0', 'NEVER READ outside ctor/serialisers'],
        ['m_nFogLightingMode', '0x167c', 'enum (byte read)', 'NONE(0)', 'desc+0x77; DYNAMIC 2, DYNAMIC_NOSHADOWS 4'],
        ['m_flFogContribution', '0x1680', 'float input', '1.0', 'clamped >=0, desc+0x90'],
        ['m_nCapsuleLightBehavior', '0x17f8', 'enum', 'FOLLOW_DIRECTION(0)', 'capsule only'],
        ['m_flCapsuleLength', '0x17fc', 'float', '100.0', 'capsule only'],
    ],
    'flashlights': [
        flashlight('GFL cs2fixes/simple_flashlight.vpcf', radius=60, radius_mult=8.0, intensity=1.0, alpha=1.0, tint=(1, 1, 1), theta=0.0, phi=35.0, style='NEW'),
        flashlight('kxm cs2fixes/simple_flashlight.vpcf (open space, intensity attr 10 settled at 0.3)', radius=60, radius_mult=20.0, intensity=0.3, alpha=1.0, tint=(1, 1, 1), theta=40.0, phi=80.0, style='OLD', linearity=1.0),
        flashlight('kxm cs2fixes/simple_flashlight.vpcf (floor, intensity 0.008)', radius=60, radius_mult=20.0, intensity=0.008, alpha=1.0, tint=(1, 1, 1), theta=40.0, phi=80.0, style='OLD', linearity=1.0),
    ],
}
json.dump(out, open(sys.argv[1], 'w'), indent=1)
for fl in out['flashlights']:
    print(fl['name'])
    for k in ('range_units', 'desc_color_linear', 'desc_atten_constant_linear_quadratic', 'falloff_match_distance', 'barn_k', 'barn_color_m_vColor', 'm_vInnerRadius', 'm_vDistanceFade', 'distance_fade_start_end'):
        print('  ', k, fl[k])
    for d, v in fl['samples_on_axis'].items():
        print('    d=%-7s barn_rgb=%.6g  desc_quadratic_rgb=%.6g' % (d, v['barn_rgb'][0], v['desc_quadratic_atten_times_C'][0]))
    print('   cone', {a: round(x, 4) for a, x in fl['cone_mask_relative_at_half_range'].items()})
