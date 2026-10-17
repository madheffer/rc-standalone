"""hit_to_compute.py <shader dir> <static combo> <out.comp>: a vrad3 closest-hit shader as a compute shader.

Rewrites ShaderDumpProbe's SPIRV-Cross GLSL of tools/re/out/shaders/<shader>/s<combo>_f0.hlsl
so the GPU itself evaluates one hit per invocation (HitShaderOracleTests, the GPU oracle for the
transpiled hit groups):
  - descriptor sets 3, 4, 5 become 0, 1, 2 (the uniform block of set 5 gets binding 0);
  - the bindless texture and sampler arrays become unsized (partially bound);
  - the hit attributes and the payload become globals, filled from g_HitIn[gl_GlobalInvocationID.x]
    and written back to g_HitOut as 32 words;
  - every ray tracing built-in reads the invocation's input record;
  - ignoreIntersectionEXT sets the record's ignored flag and returns, terminateRayEXT returns.
"""
import os
import re
import sys

SHADERS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out", "shaders")

PROLOGUE = """
layout(local_size_x = 64) in;
struct HitIn_t
{
    uvec4 ids;           // InstanceCustomIndex, InstanceID, PrimitiveID, HitKind
    vec4 baryTmaxTime;   // barycentrics, RayTmax, game time
    vec4 o2w[4];         // ObjectToWorld columns
    vec4 w2o[4];         // WorldToObject columns
    vec4 origin;
    vec4 dir;
    vec4 smoothHitT;     // payload vSmoothPositionWs, flHitT
    vec4 misc;           // payload flOpacityRng, nInstance (bits), bFlippedNormal (bits)
};
layout(set = 0, binding = 60, std430) readonly buffer HitInputs { HitIn_t h[]; } g_HitIn;
layout(set = 0, binding = 61, std430) buffer HitOutputs { uint w[]; } g_HitOut;
uint g_gid;
uint g_ignored;
// The dump writes HLSL's saturate (SPIR-V's clamp to 0..1).
float saturate(float x) { return clamp(x, 0.0, 1.0); }
vec2 saturate(vec2 x) { return clamp(x, vec2(0.0), vec2(1.0)); }
vec3 saturate(vec3 x) { return clamp(x, vec3(0.0), vec3(1.0)); }
vec4 saturate(vec4 x) { return clamp(x, vec4(0.0), vec4(1.0)); }
#define gl_InstanceCustomIndexEXT int(g_HitIn.h[g_gid].ids.x)
#define gl_InstanceID int(g_HitIn.h[g_gid].ids.y)
#define gl_PrimitiveID int(g_HitIn.h[g_gid].ids.z)
#define gl_HitKindEXT g_HitIn.h[g_gid].ids.w
#define gl_RayTmaxEXT g_HitIn.h[g_gid].baryTmaxTime.z
#define gl_ObjectToWorldEXT mat4x3(g_HitIn.h[g_gid].o2w[0].xyz, g_HitIn.h[g_gid].o2w[1].xyz, g_HitIn.h[g_gid].o2w[2].xyz, g_HitIn.h[g_gid].o2w[3].xyz)
#define gl_WorldToObjectEXT mat4x3(g_HitIn.h[g_gid].w2o[0].xyz, g_HitIn.h[g_gid].w2o[1].xyz, g_HitIn.h[g_gid].w2o[2].xyz, g_HitIn.h[g_gid].w2o[3].xyz)
#define gl_WorldRayOriginEXT g_HitIn.h[g_gid].origin.xyz
#define gl_WorldRayDirectionEXT g_HitIn.h[g_gid].dir.xyz
#define gl_ObjectRayOriginEXT (gl_WorldToObjectEXT * vec4(g_HitIn.h[g_gid].origin.xyz, 1.0))
#define gl_ObjectRayDirectionEXT (gl_WorldToObjectEXT * vec4(g_HitIn.h[g_gid].dir.xyz, 0.0))
"""

EPILOGUE = """
void main()
{
    g_gid = gl_GlobalInvocationID.x;
    if (g_gid >= uint(g_HitIn.h.length()))
        return;
    g_ignored = 0u;
    HitIn_t i = g_HitIn.h[g_gid];
    attrs.barycentrics = i.baryTmaxTime.xy;
    payload.vSmoothPositionWs = i.smoothHitT.xyz;
    payload.vFaceNormalWs = vec3(0.0);
    payload.vInterpolatedNormalWs = vec3(0.0);
    payload.flHitT = i.smoothHitT.w;
    payload.flOpacityRng = i.misc.x;
    payload.nInstance = int(floatBitsToUint(i.misc.y));
    payload.bFlippedNormal = floatBitsToUint(i.misc.z) != 0u;
    payload.material = MaterialDataMinimal_t(f16vec3(0.0), float16_t(0.0), f16vec3(vec3(1.0, 0.0, 1.0)), float16_t(1.0), f16vec3(0.0), float16_t(0.040008544921875), 0u, f16vec4(0.0));
    hit_main();
    uint o = g_gid * 32u;
    g_HitOut.w[o + 0u] = floatBitsToUint(payload.vSmoothPositionWs.x);
    g_HitOut.w[o + 1u] = floatBitsToUint(payload.vSmoothPositionWs.y);
    g_HitOut.w[o + 2u] = floatBitsToUint(payload.vSmoothPositionWs.z);
    g_HitOut.w[o + 3u] = floatBitsToUint(payload.vFaceNormalWs.x);
    g_HitOut.w[o + 4u] = floatBitsToUint(payload.vFaceNormalWs.y);
    g_HitOut.w[o + 5u] = floatBitsToUint(payload.vFaceNormalWs.z);
    g_HitOut.w[o + 6u] = floatBitsToUint(payload.vInterpolatedNormalWs.x);
    g_HitOut.w[o + 7u] = floatBitsToUint(payload.vInterpolatedNormalWs.y);
    g_HitOut.w[o + 8u] = floatBitsToUint(payload.vInterpolatedNormalWs.z);
    g_HitOut.w[o + 9u] = floatBitsToUint(payload.flHitT);
    g_HitOut.w[o + 10u] = floatBitsToUint(payload.flOpacityRng);
    g_HitOut.w[o + 11u] = uint(payload.nInstance);
    g_HitOut.w[o + 12u] = payload.bFlippedNormal ? 1u : 0u;
    g_HitOut.w[o + 13u] = floatBitsToUint(float(payload.material.vNormalWs.x));
    g_HitOut.w[o + 14u] = floatBitsToUint(float(payload.material.vNormalWs.y));
    g_HitOut.w[o + 15u] = floatBitsToUint(float(payload.material.vNormalWs.z));
    g_HitOut.w[o + 16u] = floatBitsToUint(float(payload.material.flMetalness));
    g_HitOut.w[o + 17u] = floatBitsToUint(float(payload.material.vAlbedo.x));
    g_HitOut.w[o + 18u] = floatBitsToUint(float(payload.material.vAlbedo.y));
    g_HitOut.w[o + 19u] = floatBitsToUint(float(payload.material.vAlbedo.z));
    g_HitOut.w[o + 20u] = floatBitsToUint(float(payload.material.flRoughness));
    g_HitOut.w[o + 21u] = floatBitsToUint(float(payload.material.vEmissive.x));
    g_HitOut.w[o + 22u] = floatBitsToUint(float(payload.material.vEmissive.y));
    g_HitOut.w[o + 23u] = floatBitsToUint(float(payload.material.vEmissive.z));
    g_HitOut.w[o + 24u] = floatBitsToUint(float(payload.material.flReflectance));
    g_HitOut.w[o + 25u] = payload.material.nMaterialTypeAndFlags;
    g_HitOut.w[o + 26u] = floatBitsToUint(float(payload.material.vMaterialBits.x));
    g_HitOut.w[o + 27u] = floatBitsToUint(float(payload.material.vMaterialBits.y));
    g_HitOut.w[o + 28u] = floatBitsToUint(float(payload.material.vMaterialBits.z));
    g_HitOut.w[o + 29u] = floatBitsToUint(float(payload.material.vMaterialBits.w));
    g_HitOut.w[o + 30u] = g_ignored;
    g_HitOut.w[o + 31u] = 0u;
}
"""


def convert(src):
    src = src[src.index("#version"):]
    src = src.replace("#extension GL_EXT_ray_tracing : require\n", "")
    src = re.sub(r"layout\(set = 3,", "layout(set = 0,", src)
    src = re.sub(r"layout\(set = 4,", "layout(set = 1,", src)
    src = re.sub(r"layout\(set = 5, binding = (\d+)", r"layout(set = 2, binding = \1", src)
    src = re.sub(r"layout\(set = 5\) uniform (\w+) (\w+);", r"layout(set = 2, binding = 0) uniform \1_Block { \1 \2; };", src)
    src = re.sub(r"(uniform\s+(?:texture2D|texture2DArray|texture3D|textureCube|sampler)\s+\w+)\[\d+\];", r"\1[];", src)
    src = re.sub(r"hitAttributeEXT\s+", "", src)
    src = re.sub(r"layout\(location = \d+\)\s+rayPayloadInEXT\s+", "", src)
    src = re.sub(r"\bignoreIntersectionEXT\s*;", "{ g_ignored = 1u; return; }", src)
    src = re.sub(r"\bterminateRayEXT\s*;", "return;", src)
    if re.search(r"\b(traceRayEXT|executeCallableEXT|reportIntersectionEXT)\b", src):
        raise SystemExit("the shader traces or calls: not supported by the oracle")
    main = src.index("void main()")
    src = src[:main] + "void hit_main()" + src[main + len("void main()"):]
    # the prologue after the last struct / buffer declaration, before the first function
    first_fn = src.index("void hit_main()")
    head, body = src[:first_fn], src[first_fn:]
    return head + PROLOGUE + body + EPILOGUE


if __name__ == "__main__":
    shader, combo, out = sys.argv[1], sys.argv[2], sys.argv[3]
    path = os.path.join(SHADERS, shader, f"s{combo}_f0.hlsl")
    text = convert(open(path, encoding="utf-8").read())
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(out)
