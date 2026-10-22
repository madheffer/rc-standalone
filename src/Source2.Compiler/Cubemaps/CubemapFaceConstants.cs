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

    /// <summary>The inverse of the full world-to-projection (projection times world-to-view, translation included).</summary>
    private static float[] ProjectionToWorld(CubemapFaceViews.View v)
    {
        float[] worldToView4 = [.. v.WorldToView, 0f, 0f, 0f, 1f];
        var m = CubemapFaceViews.Mul4(v.Projection, worldToView4);
        Matrix4x4.Invert(new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]), out var inv);
        return [inv.M11, inv.M12, inv.M13, inv.M14, inv.M21, inv.M22, inv.M23, inv.M24, inv.M31, inv.M32, inv.M33, inv.M34, inv.M41, inv.M42, inv.M43, inv.M44];
    }
}
