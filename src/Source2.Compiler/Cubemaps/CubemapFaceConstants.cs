using System.Buffers.Binary;
using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The constant blocks of the cubemap face render, laid out as Valve's shaders declare them (the member
/// names and offsets come from the DX11 programs' reflection in shaders_pc_dir.vpk, tools/re/dxbc_rdef.py; the
/// Vulkan SPIR-V uses the same layouts without names). Which words the face actually depends on was measured
/// by perturbing each one in the replay (CubemapFaceReplayTests.Sensitivity, ledger 120); those are computed
/// as the game computes them, the rest as the capture shows them for a cubemap build.
/// </summary>
public static class CubemapFaceConstants
{
    /// <summary>
    /// PerViewConstantBuffer_t (640 bytes) for a face view. <paramref name="minDepth"/> / <paramref name="maxDepth"/>
    /// are the viewport's depth range: 0 .. 0.95 for the world, 1 .. 1 for the sky. The face depends on
    /// g_matWorldToProjection (camera-relative), g_vWorldToCameraOffset and g_vCameraPositionWs only.
    /// </summary>
    public static byte[] PerView(CubemapFaceViews.View v, int size, float minDepth, float maxDepth, int windowWidth = 1280, int windowHeight = 720)
    {
        var b = new byte[640];
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), value);
        void I(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);
        void Transposed(int offset, float[] m)
        {
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                    F(offset + (r * 4 + c) * 4, m[c * 4 + r]);
        }
        float[] worldToView4 = [.. v.WorldToView, 0f, 0f, 0f, 1f];
        Transposed(0, v.CameraRelative);                                 // g_matWorldToProjection
        Transposed(64, ProjectionToWorld(v));                            // g_matProjectionToWorld
        Transposed(128, worldToView4);                                   // g_matWorldToView
        for (var i = 0; i < 16; i++)                                     // g_matViewToProjection, as stored
            F(192 + i * 4, v.Projection[i]);
        var p = v.Projection;                                            // g_vInvProjLowerRight2x2
        var det = p[10] * p[15] - p[11] * p[14];
        F(256, p[15] / det);
        F(260, -p[11] / det);
        F(264, -p[14] / det);
        F(268, p[10] / det);
        F(284, -0f);                                                     // g_vClipPlane0.w
        F(288, 1f);                                                      // g_flToneMapScalarLinear
        F(292, 1f);                                                      // g_flInvToneMapScalarLinear
        // +296 g_flRealTime, +300 g_flGameTime: the clocks, 0 here.
        F(304, 1f);                                                      // g_vViewportToGBufferRatio
        F(308, 1f);
        var invRange = 1f / (maxDepth - minDepth);
        F(312, invRange);                                                // g_fInvViewportZRange
        F(316, -(minDepth * invRange));                                  // g_fMinViewportZScaled
        F(328, size);                                                    // g_vViewportSize
        F(332, size);
        F(336, 1f / size);                                               // g_vInvViewportSize
        F(340, 1f / size);
        F(344, size);                                                    // g_vRenderTargetSize
        F(348, size);
        F(352, 1f / size);                                               // g_vInvGBufferSize
        F(356, 1f / size);
        F(368, minDepth);                                                // g_flViewportMinZ
        F(372, maxDepth);                                                // g_flViewportMaxZ
        F(376, v.Near);                                                  // g_flNearPlane
        F(380, v.Far);                                                   // g_flFarPlane
        F(384, -v.Origin.X);                                             // g_vWorldToCameraOffset
        F(388, -v.Origin.Y);
        F(392, -v.Origin.Z);
        const float d2r = 0.017453292f;
        F(400, v.Angles.X * d2r);                                        // g_vCameraAngles
        F(404, v.Angles.Y * d2r);
        F(408, v.Angles.Z * d2r);
        F(416, v.Origin.X);                                              // g_vCameraPositionWs
        F(420, v.Origin.Y);
        F(424, v.Origin.Z);
        F(432, v.CameraToWorld[2]);                                      // g_vCameraUpDirWs: camera-to-world column 2
        F(436, v.CameraToWorld[6]);
        F(440, v.CameraToWorld[10]);
        F(448, v.CameraToWorld[0]);                                      // g_vCameraDirWs: column 0
        F(452, v.CameraToWorld[4]);
        F(456, v.CameraToWorld[8]);
        F(460, 1f);                                                      // g_flInvMSAASampleCount
        F(464, v.Far);                                                   // g_vDepthPsToVsConversion
        F(468, v.Far - v.Near);
        F(472, -v.Far);
        I(476, 1);                                                       // g_nMSAASampleCount
        F(480, 2048f);                                                   // g_vMorphTextureAtlasSize
        F(484, 2048f);
        // +488 g_tCompositeMorphAtlasTextureIndex: a bindless slot, 0 here. Fog (+496 .. +527): off.
        F(528, 1f / windowWidth);                                        // g_vFrameBufferCopyInvSizeAndUvScale
        F(532, 1f / windowHeight);
        F(536, (float)size / windowWidth);
        F(540, (float)size / windowHeight);
        F(544, -v.Origin.X);                                             // g_vPrevWorldToCameraOffset
        F(548, -v.Origin.Y);
        F(552, -v.Origin.Z);
        Transposed(560, v.CameraRelative);                               // g_matPrevWorldToProjection
        F(624, 1f);                                                      // g_vWireframeColor
        F(628, 1f);
        F(632, 1f);
        F(636, 1f);
        return b;
    }

    /// <summary>
    /// An env_cubemap_fog as the client hands it to the renderer: the attribute 0x74a6158f (start, end, LOD bias,
    /// falloff exponent) and 0xd0dd9453 (the height vector, max opacity last), client 180206e50.
    /// </summary>
    public sealed record CubeFog(float Start, float End, float LodBias, float Exponent, Vector4 Height, Matrix4x4 SkyWsToOs);

    /// <summary>
    /// PerViewConstantBufferCsgo_t (656 bytes; client CreatePerViewConstantBufferCsgo, renderingpipeline_csgo.cpp,
    /// 1811fef80 in the 10-15 build). The face depends on g_bOtherFxEnabled.x (the pass flag word's bit 1, set
    /// for the cubemap view), g_bOtherEnabled3 staying 0, g_flMinSpecLightmapSize (gameinfo
    /// ResourceCompiler/BakedLighting/MinSpecLightmapSize as a float, 181206bfe), the cube fog terms and
    /// g_vHighPrecisionLightingOffsetWs (the camera). The cube fog terms are 181217e20's: offset (-start) / (end -
    /// start), scale 1 / (end - start), LOD bias, exponent; culling (start^2, +inf, the height sign, max opacity);
    /// with the height vector's x 0 (start equals end, as on probe01) the height terms are the default (1,
    /// 1e-7, 0, log2 - 3). The general height path (181217e20's other branch) needs the entity-to-vector mapping,
    /// not read yet, and throws. Words the face does not read are the capture's for a cubemap build.
    /// </summary>
    public static byte[] Csgo(CubemapFaceViews.View v, CubeFog? fog, float minSpecLightmapSize, int windowWidth = 1280, int windowHeight = 720)
    {
        var b = new byte[656];
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), value);
        void I(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);
        int[] flags = [1, 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1];                     // g_bFogTypeEnabled, g_bOtherFxEnabled, g_bOtherEnabled2
        for (var i = 0; i < flags.Length; i++)
            I(i * 4, flags[i]);
        F(144, 1f);                                                            // g_flEnvRainStrength
        F(148, 1f);                                                            // g_flEnvPuddleRippleStrength
        F(156, 1f);                                                            // g_flEnvWetnessCoverage
        I(176, 255);                                                           // g_vBlueNoiseMask
        I(180, 255);
        float[] worldToView4 = [.. v.WorldToView, 0f, 0f, 0f, 1f];             // g_matPrimaryViewWorldToProjection
        var full = CubemapFaceViews.Mul4(v.Projection, worldToView4);
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                F(192 + (r * 4 + c) * 4, full[c * 4 + r]);
        F(256, 1f / windowWidth);                                              // g_vAoProxyDepthInvTextureSize
        F(260, 1f / windowHeight);
        F(264, 0.5f);                                                          // g_flAoProxyDownres
        F(268, minSpecLightmapSize);                                           // g_flMinSpecLightmapSize
        F(272, 0.70710683f);                                                   // g_vWindDirection
        F(276, 0.70710683f);
        F(288, 0.4f);                                                          // g_vWindStrengthFreqMulHighStrength
        F(292, 0.03f);
        F(296, 0.003f);
        F(300, 0.4f);
        F(416, float.PositiveInfinity);                                        // g_vGradientFogCullingParams: no gradient fog
        F(420, float.NegativeInfinity);
        if (fog != null)
        {
            var range = fog.End - fog.Start;
            F(432, -fog.Start / range);                                        // g_vCubeFog_Offset_Scale_Bias_Exponent
            F(436, 1f / range);
            F(440, fog.LodBias);
            F(444, fog.Exponent);
            var h0 = fog.Height.Y + fog.Height.X;
            if (fog.Height.Y != h0)
                throw new NotSupportedException("cube height fog with a height range: the entity-to-vector mapping is not read");
            F(448, 1f);                                                        // g_vCubeFog_Height_Offset_Scale_Exponent_Log2Mip
            F(452, 1e-07f);
            F(460, -3f);
            var m = fog.SkyWsToOs;                                             // g_matvCubeFogSkyWsToOs
            float[] rows = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            for (var i = 0; i < 16; i++)
                F(464 + i * 4, rows[i]);
            F(528, fog.Start * fog.Start);                                     // g_vCubeFogCullingParams_MaxOpacity
            F(532, float.PositiveInfinity);
            F(536, 1f);                                                        // the sign; this branch writes 1
            F(540, fog.Height.W);
            F(544, 1f);                                                        // g_vCubeFog_ExposureBias
        }
        else
        {
            F(528, float.PositiveInfinity);
            F(532, float.NegativeInfinity);
            F(536, 1f);
        }
        F(560, v.Origin.X);                                                    // g_vHighPrecisionLightingOffsetWs
        F(564, v.Origin.Y);
        F(568, v.Origin.Z);
        F(584, 1f);                                                            // g_flScopeMagnification
        F(588, 1f);                                                            // g_flMixedResolutionViewportScale
        F(592, 0.01f);                                                         // g_flMBOIT_Overestimation
        F(596, 5e-06f);                                                        // g_flMBOIT_Bias
        F(600, 1f);                                                            // g_flMBOIT_Scale
        F(604, MathF.Log(v.Near));                                             // g_flMBOIT_LogNear (V_logf)
        F(608, MathF.Log(v.Far));                                              // g_flMBOIT_LogFar
        I(612, 1);                                                             // g_bIsToolsView
        F(624, 0.0001f);                                                       // g_flToolsVisCubemapReflectionRoughness
        F(628, 0.0018407785f);                                                 // g_flCablePixelRadiusScale
        F(632, 0.25f);                                                         // g_flBeginMixingRoughness
        F(640, 1.1f);                                                          // g_vPlayerVisibilityParams
        F(644, 0.5882353f);
        F(648, 0.6f);
        return b;
    }

    /// <summary>
    /// BakedLightingConstantBuffer_t (64 bytes): g_vLightmapUvScale = the world's m_worldLightingInfo
    /// m_vLightmapUvScale and the lightmaps' size (read from the capture against probe01; the writer, in
    /// worldrenderer, is not read yet), g_bBakedShadowsGamma20. The texture indices are bindless slots, 0 here.
    /// </summary>
    public static byte[] BakedLighting(Vector2 lightmapUvScale, int lightmapWidth, int lightmapHeight, bool bakedShadowsGamma20)
    {
        var b = new byte[64];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), lightmapUvScale.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), lightmapUvScale.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), lightmapWidth);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), lightmapHeight);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), bakedShadowsGamma20 ? 1 : 0);
        return b;
    }

    /// <summary>g_transformBuffer: one row-major 3 x 4 (48 bytes) per transform.</summary>
    public static byte[] Transforms(IReadOnlyList<float[]> transforms)
    {
        var b = new byte[transforms.Count * 48];
        for (var t = 0; t < transforms.Count; t++)
            for (var i = 0; i < 12; i++)
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(t * 48 + i * 4), transforms[t][i]);
        return b;
    }

    /// <summary>The identity 3 x 4 a world node aggregate draws with.</summary>
    public static readonly float[] Identity3x4 = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f];

    /// <summary>
    /// g_instanceBuffer: 32 bytes per instance. A world node aggregate's draws each have their own record, as the
    /// capture shows them: (tint 0xffffffff, its transform's index, 0xffffffff, 0, 0, 0x00100000, 0, 1.0f); of
    /// these the face reads the transform index (the replay with these records equals the capture). Instance 0
    /// is the capture's null record (the sky's, which its shaders do not read).
    /// </summary>
    public static byte[] Instances(IReadOnlyList<int> transformIndices)
    {
        var b = new byte[(transformIndices.Count + 1) * 32];
        uint[] none = [0xffffffff, 0, 0xffffffff, 0xffffffff, 0xffffffff, 0, 0, 0x3f800000];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), none[i]);
        for (var n = 0; n < transformIndices.Count; n++)
        {
            uint[] r = [0xffffffff, (uint)transformIndices[n], 0xffffffff, 0, 0, 0x00100000, 0, 0x3f800000];
            for (var i = 0; i < 8; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((n + 1) * 32 + i * 4), r[i]);
        }
        return b;
    }

    /// <summary>
    /// PerViewConstantBuffer_t for a sun cascade (an orthographic view into the shadow atlas tile at
    /// <paramref name="x"/>, <paramref name="y"/>). The cascade's csm_depth_only draws read the camera-relative
    /// g_matWorldToProjection (the projection, through the identity jitter multiply, times the view's rotation)
    /// and g_vWorldToCameraOffset (minus the camera: the view's translation taken back through its rotation).
    /// The other words follow the capture for a cascade view.
    /// </summary>
    public static byte[] CascadePerView(CubemapShadowCascades.Cascade c, Vector3 cameraDirection, int x, int y, int size, int atlasWidth, int atlasHeight)
    {
        var b = new byte[640];
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), value);
        void I(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);
        void Transposed(int offset, float[] m)
        {
            for (var r = 0; r < 4; r++)
                for (var k = 0; k < 4; k++)
                    F(offset + (r * 4 + k) * 4, m[k * 4 + r]);
        }
        float[] identity = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];
        var projection = CubemapFaceViews.Mul4(identity, c.Projection);
        var v = c.View;
        float[] rotation = [v[0], v[1], v[2], 0f, v[4], v[5], v[6], 0f, v[8], v[9], v[10], 0f, 0f, 0f, 0f, 1f];
        var relative = CubemapFaceViews.Mul4(projection, rotation);
        // The camera: the sun's normalised direction times the view's depth offset (the capture's values).
        var depthOffset = v[11];
        var camera = new Vector3(cameraDirection.X * depthOffset, cameraDirection.Y * depthOffset, cameraDirection.Z * depthOffset);
        Transposed(0, relative);                                       // g_matWorldToProjection
        Transposed(128, v);                                            // g_matWorldToView
        for (var i = 0; i < 16; i++)                                   // g_matViewToProjection
            F(192 + i * 4, projection[i]);
        F(256, c.Depth);                                               // g_vInvProjLowerRight2x2 as the capture shows it
        F(268, 1f);
        F(284, -0f);                                                   // g_vClipPlane0.w
        F(288, 1f);
        F(292, 1f);
        F(304, (float)size / atlasWidth);                              // g_vViewportToGBufferRatio
        F(308, (float)size / atlasHeight);
        F(312, 1f);                                                    // g_fInvViewportZRange (0 .. 1)
        F(316, -0f);
        F(320, x);                                                     // g_vViewportOffset
        F(324, y);
        F(328, size);                                                  // g_vViewportSize
        F(332, size);
        F(336, 1f / size);
        F(340, 1f / size);
        F(344, atlasWidth);                                            // g_vRenderTargetSize
        F(348, atlasHeight);
        F(352, 1f / atlasWidth);                                       // g_vInvGBufferSize
        F(356, 1f / atlasHeight);
        F(372, 1f);                                                    // g_flViewportMaxZ
        F(380, c.Depth);                                               // g_flFarPlane
        F(384, -camera.X);                                             // g_vWorldToCameraOffset
        F(388, -camera.Y);
        F(392, -camera.Z);
        F(416, camera.X);                                              // g_vCameraPositionWs
        F(420, camera.Y);
        F(424, camera.Z);
        F(448, -cameraDirection.X);                                    // g_vCameraDirWs
        F(452, -cameraDirection.Y);
        F(456, -cameraDirection.Z);
        F(460, 1f);
        F(468, c.Depth);                                               // g_vDepthPsToVsConversion (0, far, -far)
        F(472, -c.Depth);
        I(476, 1);
        F(480, 2048f);
        F(484, 2048f);
        F(528, 1f / 1280f);                                            // g_vFrameBufferCopyInvSizeAndUvScale
        F(532, 1f / 720f);
        F(536, (float)size / 1280f);
        F(540, (float)size / 720f);
        F(544, -camera.X);                                             // g_vPrevWorldToCameraOffset
        F(548, -camera.Y);
        F(552, -camera.Z);
        Transposed(560, relative);                                     // g_matPrevWorldToProjection
        F(624, 1f);
        F(628, 1f);
        F(632, 1f);
        F(636, 1f);
        return b;
    }

    /// <summary>A light probe volume as the light block carries it (env_combined_light_probe_volume).</summary>
    public sealed record LightProbeVolume(Vector3 Origin, Vector3 BoxMins, Vector3 BoxMaxs, Vector3 EdgeFadeDists);

    /// <summary>The sun as the light block carries it.</summary>
    public sealed record Sun(Vector3 Direction, Vector3 Color, float CrossFade, float DistanceFade, Vector3 SkyColorLinear, float SkyIntensity);

    /// <summary>
    /// PerViewLightingConstantBufferGpu_t (scenesystem 1801af7b0; the Vulkan block is 32256 bytes, the cascade
    /// arrays 4 long). In progress: the fields decoded so far, the rest left 0, compared word for word in
    /// CubemapLightBlockTests. Cascades: g_mWorldToShadowCascade = ((jitter x P) V) T(lighting offset), transposed
    /// (checked on all four of probe01's); atlas offsets (tile centre / atlas, half tile / atlas, y negated).
    /// </summary>
    public static byte[] LightBlock(Sun sun, LightProbeVolume? volume, IReadOnlyList<CubemapShadowCascades.Cascade> cascades, int[][] tiles, int tileSize,
                                    int atlasWidth, int atlasHeight, Vector3 lightingOffset, float maxShadowDistance, int frame = 0)
    {
        var b = new byte[32256];
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), value);
        void I(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);
        // g_vShadow3x3PCFTermC0..C3: the kernel weights (scenesystem 1806006e0) and texel steps.
        float[] c0 = [0.0037453184f, 0.026217228f, 0.014981274f, 0.074906364f, 0.123595506f, 0.205992505f, 0f, 0f];
        for (var i = 0; i < 8; i++)
            F(i * 4, c0[i]);
        float[] steps = [-2f, -1f, 1f, 2f];
        for (var i = 0; i < 4; i++)
        {
            F(32 + i * 4, steps[i] / atlasWidth);
            F(48 + i * 4, steps[i] / atlasHeight);
        }
        F(88, 0.00390625f);                                            // g_vScreenSpaceDitherParams.z (1 / 256); x, y random, w from a convar
        F(92, 0.003921569f);
        F(100, 1f);                                                    // g_vCubemapNormalizationParams
        // g_vAmbientLightingSH: z and w of each channel, the sky colour times intensity times 0.5.
        var sh = new[] { sun.SkyColorLinear.X, sun.SkyColorLinear.Y, sun.SkyColorLinear.Z }.Select(c => c * sun.SkyIntensity * 0.5f).ToArray();
        for (var i = 0; i < 3; i++)
        {
            F(112 + i * 16 + 8, sh[i]);
            F(112 + i * 16 + 12, sh[i]);
        }
        var lum = sh[2] * 0.0722f + sh[1] * 0.7152f + sh[0] * 0.2126f;  // g_vAmbientLightingNormalizationSH
        var lum95 = lum * 0.95f;
        F(168, lum <= lum95 ? lum : lum95);
        F(172, lum);
        float[] ao = [0.5547002f, 0f, 0.8320503f, 0f, -0.5547002f, 0f, 0.8320503f, 0f, 0f, 0.5547002f, 0.8320503f, 0f, 0f, -0.5547002f, 0.8320503f, 0f,
                      0.3f, 0.3f, 0.3f, 0.3f, 1f, 1f, 1f, 1f, 0.2f, 0f, 0f, 0f];
        for (var i = 0; i < ao.Length; i++)
            F(176 + i * 4, ao[i]);
        I(308, 8192);                                                  // g_vLightCullParams
        I(324, 8192);                                                  // g_vEnvMapCullParams
        I(340, 8192);                                                  // g_vLPVCullParams
        I(344, volume != null ? 1 : 0);
        I(352, 3);                                                     // g_vTileCullParams
        I(356, 128);
        I(360, 128);
        I(364, 8);
        F(368, 0.8192f);                                               // g_vDepthCullParams
        F(372, 8191f);
        F(384, 256f);                                                  // g_vEnvironmentMapSizeConstants
        F(388, 5f);
        F(392, 8f);
        if (volume != null)
        {
            // g_VisibleLPV[0]: world to volume (a translation), the box padded by 0.1, 1 / edge fade (FLT_MAX for 0).
            float[] rows = [1f, 0f, -0f, -volume.Origin.X, 0f, 1f, 0f, -volume.Origin.Y, 0f, 0f, 1f, -volume.Origin.Z];
            for (var i = 0; i < 12; i++)
                F(18832 + i * 4, rows[i]);
            F(18880, volume.BoxMins.X - 0.1f);
            F(18884, volume.BoxMins.Y - 0.1f);
            F(18888, volume.BoxMins.Z - 0.1f);
            F(18896, volume.BoxMaxs.X + 0.1f);
            F(18900, volume.BoxMaxs.Y + 0.1f);
            F(18904, volume.BoxMaxs.Z + 0.1f);
            float Inv(float d) => d != 0f ? 1f / d : 3.4028235e38f;
            F(18912, Inv(volume.EdgeFadeDists.X));
            F(18916, Inv(volume.EdgeFadeDists.Y));
            F(18920, Inv(volume.EdgeFadeDists.Z));
        }
        F(31120, sun.Direction.X);                                     // g_vFastPathSunLightDir
        F(31124, sun.Direction.Y);
        F(31128, sun.Direction.Z);
        F(31132, 0.04f);
        F(31136, sun.Color.X);                                         // g_vFastPathSunLightColor
        F(31140, sun.Color.Y);
        F(31144, sun.Color.Z);
        F(31152, 1f);                                                  // g_vFastPathSunLightBakedShadowMask
        I(31168, -1);
        I(31172, -1);
        I(31176, 65535);
        I(31248, cascades.Count - 1);                                  // g_nShadowCascadeCount (the viewmodel cascade not counted)
        I(31252, 2);
        F(31256, -1.526e-05f);                                         // g_flShadowCascadeReceiverDepthBias
        F(31260, -0.00015f);
        for (var i = 0; i < 4; i++)
            F(31264 + i * 4, 1f);                                      // g_vShadowCascadeSampleThreshold
        var cf = Math.Clamp(sun.CrossFade, 0.001f, 0.5f);
        F(31280, -(1f - cf) / cf);                                     // split lerp
        F(31284, 1f / cf);
        var df = Math.Clamp(sun.DistanceFade, 0.001f, 0.5f);
        var fadeRange = maxShadowDistance * df;
        F(31288, -((1f - df) * maxShadowDistance) / fadeRange);        // z lerp
        F(31292, 1f / fadeRange);
        float[] identity = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];
        float[] shift = [1f, 0f, 0f, lightingOffset.X, 0f, 1f, 0f, lightingOffset.Y, 0f, 0f, 1f, lightingOffset.Z, 0f, 0f, 0f, 1f];
        for (var k = 0; k < cascades.Count && k < 4; k++)
        {
            var m = CubemapFaceViews.Mul4(CubemapFaceViews.Mul4(CubemapFaceViews.Mul4(identity, cascades[k].Projection), cascades[k].View), shift);
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                    F(31296 + k * 64 + (r * 4 + c) * 4, m[c * 4 + r]);
            var half = tileSize * 0.5f;
            F(31808 + k * 16, (float)tiles[k][0] / atlasWidth + half / atlasWidth);   // g_vShadowCascadeAtlasOffset
            F(31812 + k * 16, (float)tiles[k][1] / atlasHeight + half / atlasHeight);
            F(31816 + k * 16, half / atlasWidth);
            F(31820 + k * 16, -(half / atlasHeight));
        }
        I(31960, volume != null ? 1 : 0);                              // g_nNumLPVs
        for (var i = 0; i < 16; i++)
        {
            // g_vShadowSampleRotations: V_sincosf(i / 16 * 2 pi + (frame mod 32) * golden ratio).
            var a = i * 0.0625f * 6.2831855f + (frame & 31) * 1.618034f;
            F(31968 + i * 16, Simulation.CrtMath.Sin(a));
            F(31972 + i * 16, Simulation.CrtMath.Cos(a));
        }
        return b;
    }

    /// <summary>The inverse of the full world-to-projection (projection times world-to-view, translation included).</summary>
    private static float[] ProjectionToWorld(CubemapFaceViews.View v)
    {
        float[] worldToView4 = [.. v.WorldToView, 0f, 0f, 0f, 1f];
        var m = CubemapFaceViews.Mul4(v.Projection, worldToView4);
        Matrix4x4.Invert(new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]), out var inv);
        return [inv.M11, inv.M12, inv.M13, inv.M14, inv.M21, inv.M22, inv.M23, inv.M24, inv.M31, inv.M32, inv.M33, inv.M34, inv.M41, inv.M42, inv.M43, inv.M44];
    }
}
