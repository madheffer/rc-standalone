"""Write inputs.json: the decompiled evaluation rules of float/vector inputs, the operator
strength/time rules, and the per-operator maths for the flashlight/tracer set.
Every rule carries the function address it was read from (particles.dll md5
3f9ae2ff51c610d29884b51aaf570a83 unless noted; client.dll md5 cf8b7cd427f553732081739154c90588)."""
import json

raw = json.load(open('schema_raw.json'))
S = json.load(open('schema.json'))
kd = json.load(open('kv3_defaults.json'))
stats = json.load(open('input_stats.json'))
E = raw['enums']


def enum(n):
    return {x['name']: x['value'] for x in E[n]['enumerators']}


def layout(c):
    return [{'name': f['name'], 'type': f['type'], 'offset': f['offset'], 'offsetHex': f['offsetHex'], 'declaredIn': f['declaredIn']}
            for f in S['classes'][c]['fields']]


out = {
    'builds': {
        'particles.dll': '3f9ae2ff51c610d29884b51aaf570a83',
        'client.dll': 'cf8b7cd427f553732081739154c90588',
        'schemasystem.dll': 'e2fb50a2f3b3e8fbb321b95af1ce0433',
        'tier0.dll': '450b7fcd7efcddc55039fb0e7fbf7400',
        'imageBase': '0x180000000',
    },
    'enums': {n: enum(n) for n in ('ParticleFloatType_t', 'ParticleFloatMapType_t', 'ParticleFloatInputMode_t', 'ParticleFloatBiasType_t',
                                   'ParticleFloatRandomMode_t', 'ParticleFloatRoundType_t', 'ParticleVecType_t', 'ParticleSetMethod_t',
                                   'ParticleEndcapMode_t', 'ParticleEntityPos_t', 'ParticleTraceSet_t', 'ParticleParentSetMode_t',
                                   'ScalarExpressionType_t', 'VectorExpressionType_t', 'ParticleTransformType_t')},
    'enumRecordRvas': {n: E[n]['rva'] for n in ('ParticleFloatType_t', 'ParticleFloatMapType_t', 'ParticleVecType_t', 'ParticleSetMethod_t')},
    'particleAttributeNames': {
        'source': 'pointer table at rva 0x4d7a10 (index = ParticleAttributeIndex_t value); index 19 is NONE/Disabled',
        'names': ['Position', 'Life Duration', 'Position Previous', 'Radius', 'Roll', 'Roll Speed', 'Color', 'Alpha', 'Creation Time',
                  'Sequence Number', 'Trail Length', 'Particle ID', 'Yaw', 'Second Sequence Number / Alpha Window Threshold', 'Hitbox Index',
                  'Hitbox Offset Position', 'Alpha Alternate', 'Scratch Vector', 'Scratch Float 1', 'NONE/Disabled', 'Pitch', 'Normal',
                  'Glow RGB', 'Glow Alpha', 'Scene Object Pointer', 'Model Helper Pointer', 'Scratch float 2', 'Scratch float 3',
                  'Scene Object Pointer', 'Ref Counted Pointer', 'Scratch Vector 2', 'Bone Indices', 'Bone Weights', 'Parent Particle Index',
                  'Force Scale', 'Model Helper Pointer 2', 'Model Helper Pointer 3', 'Model Helper Pointer 4', 'Manual Animation Frame',
                  'Shader Extra Data 1', 'Shader Extra Data 2', 'Box Mins', 'Box Maxs', 'Box Angles', 'Box Flags', 'User Event States',
                  'Parent Particle ID', 'Rope Segment ID', 'Rope Segment Data'],
        'angleAttributesMask': {'mask': '0x101030', 'indices': [4, 5, 12, 20], 'rule': 'PF_TYPE_PARTICLE_FLOAT/INITIAL_FLOAT reading these returns value*57.29578 (radians->degrees); C_OP_SetFloat writing them multiplies the input by 0.0174533 unless set method is SCALE_INITIAL or SCALE_CURRENT', 'evidence': '0x18006f510 case 0x15; 0x18020bbc0'},
        'intAttributesMask': {'mask': '0xf00200004800', 'indices': [11, 14, 33, 40, 41, 42, 43], 'rule': 'stored as int32, read through cvtdq2ps (int->float)', 'evidence': '0x18006f510 case 0x15'},
    },
    'structLayouts': {c: {'size': S['classes'][c]['size'], 'fields': layout(c)} for c in
                      ('CParticleInput', 'CParticleFloatInput', 'CParticleVecInput', 'CParticleTransformInput', 'CPathParameters')},
    'structDefaults': {c: json.loads(kd[c]['json']) for c in
                       ('CParticleFloatInput', 'CParticleCollectionFloatInput', 'CPerParticleFloatInput', 'CParticleVecInput',
                        'CParticleCollectionVecInput', 'CPerParticleVecInput', 'CParticleRemapFloatInput')},
    'structDefaultsEvidence': 'MGetKV3ClassDefaults of each struct, called in-process; CParticleFloatInput ctor 0x1803837b0 (literal value = 2nd arg via InitLiteral 0x180384040 which writes m_nType=LITERAL, m_nMapType=DIRECT, m_flLiteralValue=arg); CParticleVecInput ctor 0x18038d330',
    'statistics': stats,
    'statisticsNote': 'Inside input structs the stock corpus writes every sub-field (41037 of 44095 float inputs are full form, the rest are the compact {m_nType, m_flLiteralValue} literal form), so here the per-sub-field mode is a real statistic: for every scalar sub-field it equals the KV3 default. The four nested CParticleFloatInput members of the vector input differ only because old files lack sub-fields added later (m_bReverseOrder, m_nRandomSeed, m_strSnapshotSubset, m_flNoCameraFallback, m_bUseBoundsCenter, m_nRoundType, m_flCompareValue).',
}

out['collectionFields'] = {
    'note': 'CParticleCollection offsets read by the evaluators (not schema-bound; from the decompiles cited)',
    '0x18': 'definition handle; **(coll+0x18) = CParticleSystemDefinition*',
    '0x20/0x30': 'first child collection / next sibling (SetControlPoint* propagate to every child recursively)',
    '0x28': 'parent collection',
    '0x40..0x4c': 'current time broadcast x4',
    '0x50': 'current time: set to -preSimTime at Init (0x180038c20), += dt each frame (0x180047af9)',
    '0x5c': 'time endcap started', '0x60': 'operator time origin (operator time = max(0, curTime - this))',
    '0x64': 'this frame dt', '0x68': 'previous non-zero dt (written only when dt != 0, 0x180047adb)',
    '0x74': 'active particle count',
    '0x168/0x170': 'CP index -> slot byte table / control point array (0xE0 bytes each)',
    '0x2d8 + 8*i': 'attribute i stream (current); 0x460 + i = its stride byte (>>2 = 16-byte blocks per 4 particles)',
    '0x4c8 + 8*i': 'attribute i initial-value stream; 0x650 + i = its stride',
    '0x8bc': 'flags: 8 = emission stopped, 0x10 = endcap',
    '0x8c1 bit 6': 'in endcap',
    '0x910/0x914/0x918': 'random-table counter / per-collection random seed offset / per-frame op seed',
    'controlPoint(0xE0)': {'0x00': 'position', '0x0c': 'previous position', '0x18': 'forward', '0x24': 'up', '0x30': 'left',
                           '0x3c/0x48/0x54': 'previous forward/up/left', '0x90': 'flags (0 = never set; |1 position, |0x1c orientation)',
                           '0x94': 'last change time (FLT -1.0 sentinel = never set)'},
}

out['randomTable'] = {
    'table': '4096 floats at rva 0x598fa0 (DAT_180598fa0), index & 0xfff',
    'VARYING': 'r = T[(coll.0x914 + coll.0x910++) & 0xfff]  (a new draw every evaluation)',
    'CONSTANT_collection': 'r = T[(coll.0x914 + seed) & 0xfff], seed = opSeedBlock[input+0x88] + coll.0x918',
    'CONSTANT_perParticle': 'r = T[(coll.0x914 + seed + particle.attr11(Particle ID)) & 0xfff]',
    'signFlipDraw': 'second draw at +0x25 (constant) or next counter (varying); flip when r2 < 0.5',
    'evidence': '0x18008f5c0 (direct map), 0x180090850 (remap map)',
    'status': 'UNSETTLED for bit-exact reproduction: the table contents and seed block are runtime data (not extracted); the rule is settled, the numbers are not',
}

F = {}
F['dispatch'] = {
    'rule': 'An input is evaluated into a 4-wide stream ctx. FUN_18005b3b0 (0x18005b3b0) reads m_nType and m_nMapType and writes the map mode at ctx+0x60; FUN_18005ad90 (0x18005ad90) then calls one of ten evaluators by map mode. Collection inputs use lane 0 only (collection loops read *stream).',
    'mapModeEvaluators': {'0 DIRECT': '0x18005ca10', '1 MULT': '0x18005d230', '2 REMAP': '0x18005da90', '3 REMAP_BIASED': '0x18005e500',
                          '4 CURVE': '0x18005f130', '5 NOTCHED': '0x18005fb60', '6 ROUND': '0x1800603f0', '7 MIN': '0x180060dc0',
                          '8 MAX': '0x180061670', '9 MOD': '0x180061f20'},
    'typeOverridesMap': 'PF_TYPE_RANDOM_UNIFORM forces map mode REMAP with in=[0,1], out=[m_flRandomMin, m_flRandomMax]; PF_TYPE_RANDOM_BIASED forces REMAP_BIASED with the same ranges and m_nBiasType/m_flBiasParameter. m_nMapType is ignored for both (0x18005b3b0).',
    'literalBypass': 'PF_TYPE_LITERAL returns m_flLiteralValue unchanged under EVERY map mode (case 0 of all ten evaluators): m_flMultFactor, remap, curve and notch are never applied to a literal.',
    'initializerSubstitution': 'When the owning function is an initializer (vfunc +0xd0 returns 2; 0x1800b8920; operators return 0, emitters 1) PF_TYPE_PARTICLE_INITIAL_FLOAT(22)->PARTICLE_FLOAT(21) and PARTICLE_INITIAL_VECTOR_COMPONENT(24)->PARTICLE_VECTOR_COMPONENT(23) (0x18005acd0).',
    'collectionOnlyTypes': 'For a collection-level evaluation types outside {0..8,10..18} yield 0 (0x18005ad90 switch).',
}
F['sourceTypes'] = {
    'PF_TYPE_LITERAL (0)': 'm_flLiteralValue (+0x18)',
    'PF_TYPE_NAMED_VALUE (1)': 'FUN_18006f450(coll, &m_NamedValue)',
    'PF_TYPE_RANDOM_UNIFORM (2)': 'r from the random table (see randomTable), then REMAP [0,1]->[min,max] clamped to [min(min,max), max(min,max)], then sign flip if m_bHasRandomSignFlip and r2 < 0.5 (flip applied AFTER remap+clamp, 0x180090850)',
    'PF_TYPE_COLLECTION_AGE (4)': 'coll.curTime(+0x50) - ctx+0x6c; ctx+0x6c is 0 at every call site read, so = collection time since start',
    'PF_TYPE_ENDCAP_AGE (5)': 'in endcap ? curTime - endcapStart(+0x5c) : 0',
    'PF_TYPE_CONTROL_POINT_COMPONENT (6)': 'CP[m_nControlPoint].position[clamp(m_nVectorComponent,0,3)] (index 3 reads past the 3-vector: avoid)',
    'PF_TYPE_CONTROL_POINT_CHANGE_AGE (7)': 'curTime - CP.lastChangeTime(+0x94)',
    'PF_TYPE_CONTROL_POINT_SPEED (8)': '|CP.pos - CP.prevPos| / (dt != 0 ? dt : 0.1)',
    'PF_TYPE_CONTROL_POINT_DISTANCE (9)': 'per particle |particle.position - CP.pos|',
    'PF_TYPE_PARTICLE_DETAIL_LEVEL (10)': 'm_flLOD0..3 selected by global detail level (0x1803845a0); NOT mapped under CURVE (broadcast as-is, 0x18005f1a2)',
    'PF_TYPE_CONCURRENT_DEF_COUNT (11)': '(float) definition.0x3b8. Incremented (lock inc) in CParticleCollection::Init 0x180038c20 (0x180039298) for every collection created from this definition, decremented on shutdown 0x18003c5c0 unless emission is stopped, decremented when emission stops and re-incremented when it restarts (0x1800486e0, flag 8 at coll+0x8bc). So: the number of live collections of this .vpcf definition, game-wide, whose emission is not stopped, including the evaluating one.',
    'PF_TYPE_CLOSEST_CAMERA_DISTANCE (12)': 'root collection +0x8c8 (requests it via flag 0x8c2|=2); -1 sentinel -> m_flNoCameraFallback',
    'PF_TYPE_SNAPSHOT_COUNT (13)': 'snapshot particle count (0 if no snapshot CP)',
    'PF_TYPE_SNAPSHOT_CHANGED (14)': '1 if the snapshot changed, else 0',
    'PF_TYPE_CONTROL_POINT_IS_SET (15)': 'CP.lastChangeTime != -1 ? 1 : 0',
    'PF_TYPE_RENDERER_CAMERA_DISTANCE (16)': '|cameraPos - (m_bUseBoundsCenter ? 0.5*(boundsMin+boundsMax) : CP.pos)|',
    'PF_TYPE_PARTICLE_AGE (19)': 'curTime - attr8(Creation Time)',
    'PF_TYPE_PARTICLE_AGE_NORMALIZED (20)': '(curTime - creationTime) / attr1(Life Duration); lifetime <= 0 -> 0. Not clamped to [0,1] in the source; clamping comes from the map mode.',
    'PF_TYPE_PARTICLE_FLOAT (21)': 'attribute m_nScalarAttribute (+0x64); angle attributes x57.29578, int attributes int->float',
    'PF_TYPE_PARTICLE_INITIAL_FLOAT (22)': 'same, initial-value stream',
    'PF_TYPE_PARTICLE_VECTOR_COMPONENT (23/24)': 'vector attribute m_nVectorAttribute component clamp(m_nVectorComponent,0,3)',
    'PF_TYPE_PARTICLE_SPEED (25)': '|pos - prevPos| / (dt != 0 ? dt : 0.1)',
    'evidence': '0x18005ca10 (collection types), 0x18006f510 (per-particle types)',
}
F['mapTypes'] = {
    'PF_MAP_TYPE_DIRECT (0)': 'value = source',
    'PF_MAP_TYPE_MULT (1)': 'value = source * m_flMultFactor (m_flMultFactor is used by no other map type)',
    'PF_MAP_TYPE_REMAP (2)': 'x = source; if m_nInputMode == LOOPED: x = (x/in1 - floor(x/in1)) * in1 (period is m_flInput1 alone, not in1-in0); t = (in1 == in0) ? (x >= in0 ? 1 : 0) : (x - in0)/(in1 - in0); value = clamp(out0 + t*(out1 - out0), min(out0,out1), max(out0,out1))',
    'PF_MAP_TYPE_REMAP_BIASED (3)': 'as REMAP to t, then t = min(t,1), t = Bias(t), value = clamp(out0 + t*(out1-out0), lo, hi)',
    'PF_MAP_TYPE_CURVE (4)': 'x = source; if LOOPED: x = frac(x/P)*P with P = m_Curve.m_vDomainMaxs.x; value = curve(x)',
    'PF_MAP_TYPE_NOTCHED (5)': 'value = (min(m_flNotchedRangeMin,Max) <= x <= max(...)) ? m_flNotchedOutputInside : m_flNotchedOutputOutside (both ends inclusive; m_nInputMode not applied)',
    'PF_MAP_TYPE_ROUND (6)': 'NEAREST = cvtps2dq (round half to even), FLOOR = roundps 1, CEIL = roundps 2 (m_nRoundType)',
    'PF_MAP_TYPE_MIN (7)': 'min(x, m_flCompareValue)', 'PF_MAP_TYPE_MAX (8)': 'max(x, m_flCompareValue)',
    'PF_MAP_TYPE_MOD (9)': 'x - trunc(x / m_flCompareValue) * m_flCompareValue (C fmod sign rule)',
    'inputMode': 'm_nInputMode is read only by REMAP, REMAP_BIASED and CURVE (0x18005da90, 0x18005e500, 0x18005f130); CLAMPED clamps via the output clamp, LOOPED wraps the input as above',
    'setup': 'remap constants written by FUN_18005b5b0(ctx, mapType, biasType, in0, in1, out0, out1, biasParam) at 0x18005b5b0 (argument registers confirmed in the disassembly at 0x18005b55a)',
}
F['bias'] = {
    'precompute (0x18005b5b0)': 'STANDARD/GAIN: b = clamp((m_flBiasParameter + 1) * 0.5, 0, 1); b <= 0 or b >= 1 -> constant t = 0 or 1; else k = 1/b - 2. EXPONENTIAL: p >= 0 -> e = 1 - clamp(p,0,1); p < 0 -> e = 20 - 19*clamp(p+1,0,1)',
    'apply (0x18006f1e0)': 'STANDARD: t/((1-t)*k + 1). GAIN: t < 0.5 ? 0.5*Bias(2t) : 1 - 0.5*Bias(2-2t). EXPONENTIAL: e <= 0 -> 1; else t^e (log/exp pair, clamped min(e,20)); t <= 0 -> 0, t >= 1 -> 1',
}
F['curve'] = {
    'evaluator': '0x18036fb10 (wrapped by 0x18006f380)',
    'rule': 'fewer than 2 points -> 0. Points are {x, y, m_flSlopeIncoming, m_flSlopeOutgoing}. Binary search for the segment, t = clamp((x - x0)/dx, 0, 1) (dx == 0 -> no divide), Hermite: y = y0 + m0*dx*t + (3dy - (2m0 + m1)dx) t^2 + ((m0 + m1)dx - 2dy) t^3 with m0 = p0.SlopeOutgoing, m1 = p1.SlopeIncoming; result clamped to [m_vDomainMins.y, m_vDomainMaxs.y]. m_tangents is editor data, unused at runtime.',
}
F['notched'] = F['mapTypes']['PF_MAP_TYPE_NOTCHED (5)']
F['multFactor'] = 'Only PF_MAP_TYPE_MULT applies m_flMultFactor; no offset term exists in any map type (offsets are expressed as out0/out1).'
out['floatInput'] = F

V = {
    'setup': '0x1800bfa80 (per 4 particles fetch 0x1800bf0f0)',
    'collectionVsParticle': 'collection evaluation (2nd arg 0) of PARTICLE_VECTOR/INITIAL_VECTOR/VELOCITY/GRAVITY returns 0',
    'initializerSubstitution': 'in an initializer PVEC_TYPE_PARTICLE_INITIAL_VECTOR(4) -> PARTICLE_VECTOR(3)',
    'types': {
        'PVEC_TYPE_LITERAL (0)': 'm_vLiteralValue',
        'PVEC_TYPE_LITERAL_COLOR (1)': '(r,g,b) of m_LiteralColor * (1/255) (0.0039216); alpha ignored; then treated as a literal',
        'PVEC_TYPE_NAMED_VALUE (2)': 'named value (m_bFollowNamedValue selects the follow variant)',
        'PVEC_TYPE_PARTICLE_VECTOR (3)': 'attribute m_nVectorAttribute * m_vVectorAttributeScale (componentwise)',
        'PVEC_TYPE_PARTICLE_INITIAL_VECTOR (4)': 'initial stream * m_vVectorAttributeScale',
        'PVEC_TYPE_PARTICLE_VELOCITY (5)': '(pos - prevPos) * (1/dt) * m_vVectorAttributeScale',
        'PVEC_TYPE_CP_VALUE (7)': 'CP[m_nControlPoint].position * m_vCPValueScale (componentwise)',
        'PVEC_TYPE_CP_RELATIVE_POSITION (8)': 'CP matrix (forward, left, up, pos) * m_vCPRelativePosition',
        'PVEC_TYPE_CP_RELATIVE_DIR (9)': 'CP rotation * m_vCPRelativeDir',
        'PVEC_TYPE_FLOAT_COMPONENTS (11)': '(m_FloatComponentX, Y, Z) each a float input stream',
        'PVEC_TYPE_FLOAT_INTERP_CLAMPED (12)': 't = clamp((f - m_flInterpInput0)/(m_flInterpInput1 - m_flInterpInput0), 0, 1); v = m_vInterpOutput0 + t*(m_vInterpOutput1 - m_vInterpOutput0); f = m_FloatInterp; no guard when input1 == input0',
        'PVEC_TYPE_FLOAT_INTERP_OPEN (13)': 'same without the clamp',
        'PVEC_TYPE_FLOAT_INTERP_GRADIENT (14)': 't as OPEN, then m_Gradient sampled at t (0x1800c2070)',
        'PVEC_TYPE_CP_DELTA (17)': 'CP[m_nControlPoint] - CP[m_nDeltaControlPoint] positions',
        'PVEC_TYPE_RANDOM_UNIFORM (15) / OFFSET (16)': 'UNSETTLED: min + r*(max-min) per component is visible for one lane only in the decompile (0x1800c1c70); not verified in the disassembly',
    },
}
out['vectorInput'] = V

out['operatorStrengthAndTime'] = {
    'shouldRun (0x18003f820)': [
        'skip if m_bDisableOperator; tools-state check (m_nToolsState) applies only with a tools context',
        'm_nOpEndCapState: -1 always; 0 only when not in endcap; 1 only in endcap (coll+0x8c1 bit 6)',
        'strength s = m_flOpStrength (collection input). If no fade times are configured (op+0x1b9 precomputed) or s <= 0, strength = s',
        'else t = operator time; a=StartFadeIn, b=max(EndFadeIn,a), c=max(StartFadeOut,b), d=max(EndFadeOut,c) (using raw EndFadeOut for the cut-off): t < a -> 0; raw EndFadeOut > 0 and t > it -> 0; f = 1; if t < b and a < b: f = min(1,(t-a)/(b-a)); if c < t and c < d: f = min(f,(t-d)/(c-d)); strength = max(0,s)*f',
        'operator runs only when strength > 0; strength is passed to Operate as xmm2',
    ],
    'operatorTime (0x18003f6e0)': [
        't = max(0, curTime - coll+0x60)',
        'if m_bNormalizeToStopTime and m_nOpEndCapState == ENDCAP_ON: t = max(0, t - endcapStart)',
        'if m_nOpTimeOffsetSeed != 0: t = max(0, t + lerp(OffsetMin, OffsetMax, T[(c914 + seed) & 0xfff]))',
        'if m_nOpTimeScaleSeed != 0 and t > StartFadeIn: t = StartFadeIn + (t - StartFadeIn) / max(1e-4, lerp(ScaleMin, ScaleMax, T[(c914+seed)&0xfff]))',
        'if m_flOpFadeOscillatePeriod > 0: t = fmod(curTime / period, 1.0) (absolute curTime, replaces everything above)',
    ],
    'preEmissionRunOnce': 'The per-frame pre-emission loop (0x180047cb0..0x180047f3f) skips operators with m_bRunOnce (CParticleFunctionPreEmission+0x1e0) set. UNSETTLED: where run-once pre-emission operators are executed was not located.',
}

O = {}
O['C_OP_SetControlPointToPlayer'] = {
    'operate': '0x1802811e0', 'vtable': '0x180517cf0',
    'math': [
        'radius = 0; pos = query->GetLocalPlayerPos(owner, m_nPosition, &radius)  (IParticleSystemQuery slot 17 / +0x88)',
        'SetControlPointPosition(m_nCP1, pos + m_vecCP1Pos) (0x1800364e0, propagates to every child collection)',
        'if m_nPosition == PARTICLE_FLASHLIGHT: SetControlPointField(m_nRadiusCP, m_nRadiusCPField, radius)',
        'xf = query->GetLocalPlayerEyeTransform(owner, !m_bOrientToEyes) (slot 19 / +0x98); (fwd,left,up) = rotate (1,0,0),(0,1,0),(0,0,1) by xf.rotation (0x180360de0); SetControlPointOrientationFLU(m_nCP1, fwd, left, up) (0x18003ec90)',
    ],
    'client (client.dll CParticleSystemQuery vtable 0x181c38028)': {
        'GetLocalPlayerPos 0x180a47d10': 'pawn = owner if it is a player pawn else the local player. ABS_ORIGIN -> GetAbsOrigin; WORLDSPACE_CENTER -> vfunc +0x2b0 (WorldSpaceCenter); EYES -> vfunc +0x568 (EyePosition); FLASHLIGHT and anything else -> (0,0,0). The radius out-parameter is never written, so the radius field is set to 0.',
        'GetLocalPlayerEyeTransform 0x180a47ac0': 'transform from EyePosition (+0x568) and EyeAngles (+0x570) with scale 1; when the bool (= !m_bOrientToEyes) is true it is re-based on world up (0,0,1) by 0x181748070 (keeps the forward heading, removes pitch/roll). Interpretation of 0x181748070 as an up-vector re-basis is from its decompile (cross products with (0,0,1) then matrix->quaternion); UNSETTLED in detail.',
    },
}
O['C_OP_SetControlPointToImpactPoint'] = {
    'operate': '0x180283f50',
    'math': [
        'per-instance ctx: [0] collision group, [1] next update time. If curTime < next: return. next = m_flUpdateRate >= 0 ? curTime + m_flUpdateRate : FLT_MAX',
        'dir = m_vecTraceDir; if dir == 0: dir = CP[m_nCPIn].forward (previous-frame forward when curTime == curTime - dt in float)',
        'len = m_flTraceLength (collection input). mask = m_bIncludeWater ? 0xdb001 : 0xc3001; trace set ALL/STATIC/STATIC_AND_KEYFRAMED/DYNAMIC -> 0xf/1/3/6',
        'normal mode: start = CP[m_nCPIn].pos + dir*m_flStartOffset; end = start + dir*len; query->TraceLine (slot 4 / +0x20)',
        'm_bTraceToClosestSurface: trace from CP pos (+StartOffset along each axis) along the six axes (+-X,+-Y,+-Z, table 0x1805c0450) of length len and keep the hit closest to the CP',
        'hit (fraction < 1) or m_bSetToEndpoint: pos = trace.endpos - dir*m_flOffset; SetControlPointPosition(m_nCPOut, pos); orientation FLU = (n, cross(Z,n)/|..|, cross(n, left)) with n = trace plane normal (VectorVectors 0x18035ed10; n vertical -> left=(0,1,0), up=(-n.z,0,0))',
        'no hit and not SetToEndpoint: if m_vecTraceDir != 0 the orientation of m_nCPOut is set from -m_vecTraceDir the same way; position unchanged; if m_vecTraceDir == 0 nothing is written',
    ],
    'webNote': 'needs a world trace; without one only the m_bSetToEndpoint (fraction 1, endpos = end) behaviour can be reproduced exactly',
}
O['C_OP_DistanceBetweenCPsToCP'] = {
    'operate': '0x180280be0',
    'math': [
        'm_bSetOnce: run once per instance (ctx flag)',
        'target collection: m_nSetParent NO -> self, IMMEDIATE -> parent, ROOT -> top-most parent',
        'd = |CP[m_nStartCP].pos - CP[m_nEndCP].pos|',
        'm_bLOS: trace start -> (m_flMaxTraceLength != -1 and < d ? start + normalize(CP[end].pos)*MaxTraceLength : CP[end].pos); NOTE the code normalises the END POSITION vector, not (end - start); if fraction != 1: d = d * fraction * m_flLOSScale',
        'out = (m_flInputMin == m_flInputMax) ? (d - m_flInputMax < 0 ? m_flOutputMin : m_flOutputMax) : m_flOutputMin + clamp((d - InputMin)/(InputMax - InputMin), 0, 1)*(m_flOutputMax - m_flOutputMin)',
        'SetControlPointField(target, m_nOutputCP, m_nOutputCPField, out) (0x1802744e0: field 0..2 = x/y/z of the CP position, starting from (0,0,0) if the CP was never set; field 3 is accepted and discarded; children updated)',
    ],
}
O['C_OP_SetControlPointFieldToScalarExpression'] = {
    'operate': '0x180277070',
    'math': [
        'a = m_flInput1, b = m_flInput2 (collection inputs, lane 0)',
        'r: ADD a+b, SUBTRACT a-b, MUL a*b, DIVIDE b==0 ? 0 : a/b, INPUT_1 a, MIN minss(a,b), MAX maxss(a,b), MOD fmodf(a,b), EQUAL a==b ?1:0, GT a>b ?1:0, LT a<b ?1:0, other 0 (jump table 0x1802783ec)',
        'r is mapped by m_flOutputRemap (CParticleRemapFloatInput, map type only, jump table 0x180278418): DIRECT none, MULT r*m_flMultFactor, REMAP/REMAP_BIASED/CURVE/NOTCHED/ROUND/MIN/MAX/MOD exactly as for float inputs',
        'i = m_flInterpolation; if i != 1.0: r = RemapValClamped(i, 0, 1, currentField, r) = currentField + clamp(i,0,1)*(r - currentField) (0x180059680; currentField read by 0x180122f70)',
        'SetControlPointField(m_nOutputCP, m_nOutVectorField, r)',
    ],
}
O['C_OP_SetVectorAttributeToVectorExpression'] = {
    'operate': '0x180203ec0',
    'math': [
        'a = m_vInput1, b = m_vInput2 (per-particle vector inputs), s = operator strength',
        'r: ADD a+b, SUB a-b, MUL a*b, DIVIDE a * rcp(b) where rcp is rcpps + one Newton step and any |b_i| < FLT_MIN has FLT_EPSILON OR-ed into its bits (b=0 -> 1.19e-7), INPUT_1 a, MIN/MAX componentwise, CROSSPRODUCT cross(a,b), LERP a + clamp(m_flLerp,0,1)*(b-a) (jump table 0x180204fbc)',
        'r = r * s (every expression, before the set method)',
        'set method on attribute m_nOutputField: REPLACE r; SCALE_INITIAL r*init; ADD_TO_INITIAL r+init; RAMP_CURRENT cur + r*dt; SCALE_CURRENT r*cur; ADD_TO_CURRENT r+cur',
        'if the output attribute is in mask 0xc100c0 (Color 6, Alpha 7, Alpha Alternate 16, Glow RGB 22, Glow Alpha 23) clamp to the per-op min/max (op+0x1120 / +0x1150, set at init)',
        'm_bNormalizedOutput: r *= rsqrtps(|r|^2 with the same tiny-value guard) (approximate, no Newton step)',
    ],
}
O['C_OP_AlphaDecay'] = {
    'operate': '0x1801d3330',
    'math': ['kill particle when attr7(Alpha) * attr16(Alpha Alternate) <= m_flMinAlpha + FLT_EPSILON (1.1920929e-7)'],
}
O['C_OP_Decay'] = {
    'operate': '0x1801d0f70',
    'math': ['kill when lifetime <= 0 or lifetime <= curTime - creationTime (m_bRopeDecay selects a rope-aware variant, not decoded)'],
}
O['C_OP_SetFloat'] = {
    'operate': '0x18020bd30', 'initFn': '0x18020bbc0 (vtable slot 6)',
    'init': [
        'clamp [lo,hi]: output in Alpha/Alpha Alternate -> [0,1]; Radius/Trail Length -> [0,FLT_MAX]; else [-FLT_MAX,FLT_MAX]',
        'k = 1; if output is Roll/Roll Speed/Yaw/Pitch and method is not SCALE_INITIAL/SCALE_CURRENT: k = 0.0174533 (degrees -> radians)',
    ],
    'math': [
        'v = m_InputValue, L = clamp(m_Lerp, 0, 1), s = strength, cur = current value, init = initial value',
        'REPLACE: o = cur + L*(k v - cur); clamp; s < 1 -> o = o*s + cur*(1-s)',
        'SCALE_INITIAL: o = cur + L*(k v init - cur); clamp; s < 1 -> o = o*s + init*(1-s)',
        'ADD_TO_INITIAL: o = cur + L*((k v + init) - cur); clamp; s < 1 -> o*s + init*(1-s)',
        'RAMP_CURRENT: o = cur + L * v * (s * k * dt); clamp (no strength blend)',
        'SCALE_CURRENT: o = cur + L*(k v cur - cur); clamp; s < 1 -> o*s + cur*(1-s)',
        'ADD_TO_CURRENT: o = cur + L*((k v + cur) - cur); clamp; s < 1 -> o*s + cur*(1-s)',
    ],
}
O['C_INIT_CreateWithinBox'] = {
    'initScalar': '0x180136ec0',
    'math': [
        'mn = m_vecMin, mx = m_vecMax (per-particle vector inputs); cp = clamp(m_nControlPointNumber, 0, 63)',
        'offset = (mx - mn) * (r0, r1, r2): if !m_randomnessParameters.m_bDistributeEvenly, r_i = T[(c910 + 2*c914 + i) & 0xfff] (c914 is added twice in the code) and c910 += 3; else Halton(index = (m_nSeed < 0 ? m_nSeed + c914 : m_nSeed) + particleIndex, masked 0x7fffffff) with base 2 for x, 3 for y, 5 for z',
        '!m_bLocalSpace: p = cpPosAtCreation + mn + offset, where cpPosAtCreation = prevPos + (pos - prevPos) * max(0,(dt - (curTime - creationTime))/dt) (dt == 0 -> pos); the box is world-axis aligned',
        'm_bLocalSpace: M = GetControlPointTransformAtTime(cp, creationTime) (0x1800c1e70: interpolated position, current orientation columns forward/left/up); p = M * (mn + offset): local x = forward, y = left, z = up',
        'writes Position and, if the init flags request it (flag 4), Position Previous = p',
    ],
}
O['C_OP_PositionLock'] = {
    'operate': '0x1801d8e90',
    'math': [
        'return if m_TransformInput is CONTROL_POINT and that CP was never set (flags & 0x1d == 0)',
        'T = transform from m_TransformInput; P = T.position. Per-instance ctx holds the previous P and rotation; first run initialises them to the current values',
        'delta = (P - Pprev) * strength',
        'default path (m_flStartTime_min >= 1): per particle f = min(curTime - creationTime, dt) / dt; d_i = delta * f',
        '  if m_flRange != 0: g = min(1, |pos + d_i - P| / m_flRange); with b = m_flRangeBias (collection input, Schlick bias as for float inputs: b<=0 -> 0, b>=1 -> 1, else g/((1-g)(1/b-2)+1)); d_i *= (1 - bias(g))',
        '  !m_bLockRot: pos += d_i * m_vecScale; if m_flPrevPosScale != 0 and m_nFieldOutputPrev != 19: prev += d_i * m_vecScale * m_flPrevPosScale',
        '  m_bLockRot: the particle is also carried by the rotation delta Rcur * inverse(Rprev) about P (0x18035d240 invert, 0x18035c9a0 concat); exact per-component form UNSETTLED (decompile only)',
        'age-window path (m_flStartTime_min < 1): per frame random start/end in [min,max] shaped by the _exp values (thread RNG 0x18036ba50 / 0x18036bf70, exponent * 4.0 as int); w = 1 - clamp((n - start)/(end - start), 0, 1) with n = age/lifetime, skipped (w = 1) when the CP moved more than m_flJumpThreshold (compared against the SQUARED move distance); d_i scaled by w. m_flJumpThreshold is used only on this path.',
        'store P and rotation for next frame',
    ],
}
O['C_INIT_RingWave'] = {
    'initScalar': '0x1801251c0',
    'math': [
        'ppo = m_flParticlesPerOrbit; if !m_bEvenDistribution or ppo == -1: ppo = (float) active particle count; step = 2pi / max(1, ppo)',
        'per particle: counter (per-instance, starts at 0, ++ before use; reset if it would overflow); angle = counter * (m_bEvenDistribution ? step : T[c910++]*2pi)',
        'ring point q = (R cos(angle), R sin(angle), 0), R = m_flInitialRadius; q = AngleMatrix(m_flPitch, m_flYaw, m_flRoll) * q (0x18035c380, 0x18035ec80)',
        'thickness: u_i = T_i*0.9999 + 0.0001 (3 draws, c910 += 3); z = 2u0 - 1; phi = 2pi*u1; rr = m_flThickness * u2^(1/3); q += rr*(sqrt(1-z^2) cos phi, sqrt(1-z^2) sin phi, z)',
        'world: p = CP.rotation * q * CP.scale + CP.position (transform from m_TransformInput)',
        'velocity: dir = normalize(p - CP.position); v = speedMin + (speedMax - speedMin)*T[c910++]; prev = p - dir * v * coll.prevDt(+0x68); m_bXYVelocityOnly -> prev.z = p.z',
        'writes Position and (init flag 4) Position Previous',
    ],
    'status': 'formula shape settled from decompile + disassembly at 0x1801257d0..0x18012593e; the inline sin/cos approximation constants and exact random draw order for thickness are from the decompile only',
}
O['C_INIT_CreateSequentialPathV2'] = {
    'initScalar': '0x18014e4b0', 'stepSetup': '0x18014ddb0', 'pathPoints': '0x18004d170', 'segmentInit': '0x18014e3c0',
    'math': [
        'per-instance state {t, step, cachedN}; N = m_flNumToAssign; when N changes: step = N > 1 ? 1/(N-1) : 0 (x segment count with CP pairs)',
        'S = CP[start].pos at creation time + m_vStartPointOffset; E = CP[end].pos at creation time + m_vEndOffset',
        'mid: if m_nMidControlPointNumber < 0: M = S + (E-S)*m_flMidPoint; m_nBulgeControl 0: M += (rand*2-1)*m_flBulge per axis (T[c914+0..2], fixed per collection); 1 (start CP) / 2 (end CP): M += forward/|forward| * |E-S| * m_flBulge * (1 - |dot(normalize(E-S), forward)|); else M = 2*CP[mid] - 0.5*(S+E) (so the curve passes through the mid CP)',
        'M += m_vMidPointOffset',
        'p = quadratic Bezier lerp(lerp(S,M,u), lerp(M,E,u), u) with u = t (with m_bCPPairs: start = startCP + dir*int(t), u = frac(t))',
        'p += (-D + 2D*r_i) per axis, D = m_fMaxDistance, r = T[c910+c914+0..2], c910 += 3; writes Position and Position Previous',
        'm_bSaveOffset: attr15 = (u, startCP, endCP)',
        't += step; if step > 1e-5: snap t to P if |t-P| < 1e-5 else to 0 if |t| < 1e-5 (P = 1, or the CP-pair count)',
        'out of [0,P]: overshoot = step <= 0 ? -t : t - P; m_bLoop: t = overshoot (and t = 0 if the previous t was exactly P and the endpoints differ); else bounce: t = step >= 0 ? P - overshoot : overshoot, step = -step',
    ],
}
out['operators'] = O
json.dump(out, open('inputs.json', 'w'), indent=1)
print('inputs.json written', len(json.dumps(out)))
