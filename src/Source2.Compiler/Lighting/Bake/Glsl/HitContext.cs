#pragma warning disable IDE1006, CS8981 // GLSL's names
namespace Source2.Compiler.Lighting.Bake.Glsl;

/// <summary>MaterialDataMinimal_t: the hit material in the payload (half floats on the GPU, rounded on the way in).</summary>
public struct MaterialDataMinimal_t
{
    public vec3 vNormalWs;
    public float flMetalness;
    public vec3 vAlbedo;
    public float flRoughness;
    public vec3 vEmissive;
    public float flReflectance;
    public uint nMaterialTypeAndFlags;
    public vec4 vMaterialBits;

    public MaterialDataMinimal_t(vec3 vNormalWs, float flMetalness, vec3 vAlbedo, float flRoughness, vec3 vEmissive, float flReflectance, uint nMaterialTypeAndFlags, vec4 vMaterialBits)
    {
        this.vNormalWs = vNormalWs;
        this.flMetalness = flMetalness;
        this.vAlbedo = vAlbedo;
        this.flRoughness = flRoughness;
        this.vEmissive = vEmissive;
        this.flReflectance = flReflectance;
        this.nMaterialTypeAndFlags = nMaterialTypeAndFlags;
        this.vMaterialBits = vMaterialBits;
    }
}

/// <summary>Payload_t: the ray payload the hit shaders fill.</summary>
public struct Payload_t
{
    public vec3 vSmoothPositionWs;
    public vec3 vFaceNormalWs;
    public vec3 vInterpolatedNormalWs;
    public float flHitT;
    public float flOpacityRng;
    public int nInstance;
    public bool bFlippedNormal;
    public MaterialDataMinimal_t material;

    public Payload_t(vec3 vSmoothPositionWs, vec3 vFaceNormalWs, vec3 vInterpolatedNormalWs, float flHitT, float flOpacityRng, int nInstance, bool bFlippedNormal, MaterialDataMinimal_t material)
    {
        this.vSmoothPositionWs = vSmoothPositionWs;
        this.vFaceNormalWs = vFaceNormalWs;
        this.vInterpolatedNormalWs = vInterpolatedNormalWs;
        this.flHitT = flHitT;
        this.flOpacityRng = flOpacityRng;
        this.nInstance = nInstance;
        this.bFlippedNormal = bFlippedNormal;
        this.material = material;
    }
}

/// <summary>PayloadShadow_t: a shadow ray's visibility.</summary>
public struct PayloadShadow_t
{
    public float m_flVisibility;

    public PayloadShadow_t(float m_flVisibility) => this.m_flVisibility = m_flVisibility;
}

/// <summary>
/// A transpiled ray generation shader: its buffers, uniforms, push constants
/// and payloads are fields under their GLSL names; the host fills them and
/// answers the ray tracing calls.
/// </summary>
public abstract class RaygenShader
{
    public uvec3 LaunchID, LaunchSize;

    /// <summary>imageSize(g_RandomSamples): the Halton volume's size (32 x 32 x layers).</summary>
    public ivec3 RandomSamplesSize;

    public abstract void Main();

    /// <summary>The NaN trap (RGTRAP builds): the first GLSL line whose assignment gave a NaN.</summary>
    public int TrapLine = -1;
    protected void Trap(float v, int line) { if (TrapLine < 0 && float.IsNaN(v)) TrapLine = line; }
    protected void Trap(vec2 v, int line) { if (TrapLine < 0 && (float.IsNaN(v.x) || float.IsNaN(v.y))) TrapLine = line; }
    protected void Trap(vec3 v, int line) { if (TrapLine < 0 && (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z))) TrapLine = line; }
    protected void Trap(vec4 v, int line) { if (TrapLine < 0 && (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsNaN(v.w))) TrapLine = line; }
    protected void Trap<T>(T v, int line) { }

    /// <summary>traceRayEXT: payload location 0 is the shader's Payload_t field, 1 its PayloadShadow_t.</summary>
    protected abstract void TraceRay(uint flags, uint mask, uint sbtOffset, uint sbtStride, uint missIndex, vec3 origin, float tMin, vec3 direction, float tMax, int payload);

    protected abstract vec4 ImageLoadRandom(ivec3 c);
    protected abstract uvec4 ImageLoadConverged(ivec3 c);
    protected abstract uvec4 TexelFetchChartIDs(ivec2 c);
    protected abstract vec4 TextureLod2D(int texture, int sampler, vec2 uv, float lod);
    protected abstract vec4 TextureLodCube(int texture, vec3 direction, float lod);
}

/// <summary>PackedRtxInstanceData_t: per TLAS instance (scenesystem 180094d30).</summary>
public struct PackedRtxInstanceData_t_natural
{
    public uint nSrgbColor;
    public uint nTriangleGeometry;
    /// <summary>Byte offset of the instance's material data in g_MaterialData.</summary>
    public uint nMaterialData;
    public uint nMaterialFlags_nInstanceFlags;
    public float fEmissiveBoost;
    public uint nInstanceFlags;
    public uint _pad1;
    public uint _pad2;
}

public struct _Array_natural_vector_int_4_8
{
    public ivec4[] data;
}

/// <summary>
/// GeometryInfo_t: a mesh's buffers as the hit shaders read them. x/y/z/w of the
/// buffer vectors: the vertex streams 0-2 and the index buffer; a field info is
/// (byte offset in the vertex, DXGI format, stream, 0).
/// </summary>
public struct GeometryInfo_t_natural
{
    public ivec4 m_nVertexAndIndexBuffers;
    public ivec4 m_nBufferBindOffsetBytes;
    public ivec4 m_nBufferStrideInBytes;
    public ivec4 m_vNormalFieldInfo;
    public ivec4 m_vTangentFieldInfo;
    public _Array_natural_vector_int_4_8 m_vTexCoordFieldInfo;
    public _Array_natural_vector_int_4_8 m_vColorFieldInfo;
}

/// <summary>A bindless texture: mips of linear RGBA (sRGB textures decoded, as the GPU's sRGB views do).</summary>
public sealed class BindlessTexture(int width, int height, vec4[][] mips)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public vec4[][] Mips { get; } = mips;
}

/// <summary>
/// A sampler's addressing (0 wrap, 1 mirror, 2 clamp, 3 border, 4 mirror once), its min / mag filter
/// (point when Linear is false), its mip filter (Mipmaps: linear, else nearest), W addressing and the
/// anisotropy (0 none). Every state rendersystem made in vrad3 had LOD 0..15, no bias, transparent
/// black border (tools/vrad3/capture_samplers.py).
/// </summary>
public readonly record struct BindlessSampler(int AddressU, int AddressV, bool Linear, bool Mipmaps, int AddressW = 0, int MaxAniso = 0);

/// <summary>One field of a shader's _RTX_MATERIAL_DATA_: its GLSL type and byte offset in the material's block (matrices row-major).</summary>
public readonly record struct MaterialField(string Name, string Type, int Offset);

/// <summary>A transpiled hit group: one static combo of a material shader's _vulkan_60_rtx program.</summary>
public abstract class HitShader
{
    public abstract string Name { get; }
    public abstract long Combo { get; }
    /// <summary>The material data block the shader reads at the instance's nMaterialData.</summary>
    public abstract MaterialField[] Layout { get; }
    public abstract void ClosestHit(HitContext ctx);
}

/// <summary>
/// What a transpiled hit shader sees: the ray tracing built-ins of the hit,
/// the scene's buffers (instances, geometry records, vertex and index buffers,
/// material data) and the bindless textures and samplers.
/// </summary>
public sealed class HitContext
{
    public Payload_t Payload;
    public bool Ignored;

    public int InstanceCustomIndex, InstanceID, PrimitiveID;
    public mat4x3 ObjectToWorld, WorldToObject;
    public float RayTmax;
    public uint HitKind;
    public vec2 Barycentrics;
    public vec3 WorldRayOrigin, WorldRayDirection;
    public float GameTime;

    public required PackedRtxInstanceData_t_natural[] Instances { get; init; }
    public required GeometryInfo_t_natural[] Geometries { get; init; }
    public required float[][] VerticesF { get; init; }
    public required uint[][] VerticesU { get; init; }
    public required uint[][] Indices { get; init; }
    public required float[] MatF { get; init; }
    public required int[] MatI { get; init; }
    public required uint[] MatU { get; init; }
    public required BindlessTexture[] Textures { get; init; }
    public required BindlessSampler[] Samplers { get; init; }

    /// <summary>The sampler a shader names directly (g_Sampler, set 3's immutable sampler; capture_samplers.py): linear, nearest mip, wrap.</summary>
    public static readonly BindlessSampler DefaultSampler = new(0, 0, true, false);

    public vec4 TextureLod(int texture, int sampler, vec2 uv, float lod) => Sample(texture, sampler, uv, lod);

    /// <summary>textureGrad: the mip from the larger gradient's length in texels (log2), as the GPU picks it.</summary>
    public vec4 TextureGrad(int texture, int sampler, vec2 uv, vec2 ddx, vec2 ddy)
    {
        if (texture < 0 || texture >= Textures.Length)
            return default;
        var t = Textures[texture];
        var dx = new vec2(ddx.x * t.Width, ddx.y * t.Height);
        var dy = new vec2(ddy.x * t.Width, ddy.y * t.Height);
        var rho = MathF.Max(GL.dot(dx, dx), GL.dot(dy, dy));
        var lod = rho > 0f ? 0.5f * MathF.Log2(rho) : 0f;
        return Sample(texture, sampler, uv, lod);
    }

    public vec4 Texture(int texture, int sampler, vec2 uv) => Sample(texture, sampler, uv, 0f);

    private vec4 Sample(int texture, int sampler, vec2 uv, float lod)
    {
        if (texture < 0 || texture >= Textures.Length)
            return default;
        var t = Textures[texture];
        var s = sampler >= 0 && sampler < Samplers.Length ? Samplers[sampler] : DefaultSampler;
        if (!s.Mipmaps || t.Mips.Length == 1)
            return Bilinear(t, 0, uv, s);
        lod = Math.Clamp(lod, 0f, t.Mips.Length - 1);
        var l0 = (int)MathF.Floor(lod);
        var f = lod - l0;
        var a = Bilinear(t, l0, uv, s);
        if (f == 0f || l0 + 1 >= t.Mips.Length)
            return a;
        return GL.mix(a, Bilinear(t, l0 + 1, uv, s), f);
    }

    private static vec4 Bilinear(BindlessTexture t, int mip, vec2 uv, BindlessSampler s)
    {
        var w = Math.Max(1, t.Width >> mip);
        var h = Math.Max(1, t.Height >> mip);
        var texels = t.Mips[mip];
        vec4 T(int i, int j) => texels[Address(j, h, s.AddressV) * w + Address(i, w, s.AddressU)];
        var x = uv.x * w - 0.5f;
        var y = uv.y * h - 0.5f;
        if (!s.Linear)
            return T((int)MathF.Floor(x + 0.5f), (int)MathF.Floor(y + 0.5f));
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        return GL.mix(GL.mix(T(x0, y0), T(x0 + 1, y0), fx), GL.mix(T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx), fy);
    }

    private static int Address(int i, int n, int mode)
    {
        switch (mode)
        {
            case 1:
            {
                var p = ((i % (2 * n)) + 2 * n) % (2 * n);
                return p < n ? p : 2 * n - 1 - p;
            }
            case 2 or 3:
                return Math.Clamp(i, 0, n - 1);
            case 4:
                return Math.Clamp(i < 0 ? -i - 1 : i, 0, n - 1);
            default:
                return ((i % n) + n) % n;
        }
    }
}
