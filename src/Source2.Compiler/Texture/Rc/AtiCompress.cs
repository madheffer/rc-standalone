// Port of the DXTC block code in Compressonator 2.3 (GPUOpen-Tools/compressonator, tag V2.3.2953,
// Compressonator/Source/Codec/ATI/CompressonatorXCodec.cpp), SSE2 paths, as Valve's
// ati_compress_wrapper.dll runs them. Copyright (c) 2016 Advanced Micro Devices, Inc.
// Copyright (c) 2004-2006 ATI Technologies Inc. MIT licence:
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions: The above copyright notice
// and this permission notice shall be included in all copies or substantial portions of the
// Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

namespace Source2.Compiler.RcTexture;

/// <summary>
/// DXT1, DXT5, ATI1N and ATI2N blocks exactly as resourcecompiler writes them: it hands each mip
/// to <c>ATI_TC_ConvertTexture</c> as ARGB8888 with channel weighting and adaptive weighting on.
/// Every float operation keeps the order and width of the compiled SSE2 code (<c>cvtps2dq</c>
/// rounds to nearest even; nothing is fused). Three details of Valve's DLL are kept on purpose,
/// see docs/TEXTURES.md: squared weights, the adaptive weighting that reads bytes as pixels,
/// and the alpha refinement whose error sums only lanes 0 and 1.
/// </summary>
public static class AtiCompress
{
    /// <summary>The block formats this encoder writes.</summary>
    public enum Format
    {
        /// <summary>DXT1 (BC1): RGB, 8 bytes per block.</summary>
        Dxt1,

        /// <summary>DXT5 (BC3): RGB and interpolated alpha, 16 bytes per block.</summary>
        Dxt5,

        /// <summary>ATI1N (BC4): one channel, 8 bytes per block.</summary>
        Ati1n,

        /// <summary>ATI2N (BC5): two channels, 16 bytes per block.</summary>
        Ati2n,
    }

    /// <summary>Bytes per 4x4 block.</summary>
    public static int BlockSize(Format format) => format is Format.Dxt1 or Format.Ati1n ? 8 : 16;

    /// <summary>
    /// Compress a BGRA8 image (B, G, R, A in memory, as the ARGB8888 buffer RC passes). A partial
    /// edge block repeats its own valid pixels. <paramref name="perceptual"/> selects RC's
    /// 0.3086 / 0.6094 / 0.0820 weights for DXT (RC uses them for sRGB output); ATI formats
    /// ignore it.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> bgra, int width, int height, Format format, bool perceptual)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length < (long)width * height * 4)
            throw new ArgumentException("The image is shorter than width * height * 4.", nameof(bgra));

        int bw = (width + 3) / 4, bh = (height + 3) / 4, bs = BlockSize(format);
        var output = new byte[bw * bh * bs];
        var src = bgra.ToArray();
        var weights = BaseWeights(format, perceptual);
        Parallel.For(0, bh, () => new Encoder(), (by, _, enc) =>
        {
            Span<uint> block = stackalloc uint[16];
            for (int bx = 0; bx < bw; bx++)
            {
                int vw = Math.Min(4, width - bx * 4), vh = Math.Min(4, height - by * 4);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = bx * 4 + x % vw, sy = by * 4 + y % vh;
                        block[y * 4 + x] = BitConverter.ToUInt32(src, (sy * width + sx) * 4);
                    }
                enc.EncodeBlock(block, output.AsSpan((by * bw + bx) * bs, bs), format, weights);
            }
            return enc;
        }, _ => { });
        return output;
    }

    // CCodec_DXTC::SetParameter stores each weight squared; RC passes 0.3086 / 0.6094 / 0.0820
    // when perceptual, 1 otherwise, and always 1 for the ATI formats.
    private static float[] BaseWeights(Format format, bool perceptual)
    {
        if (!perceptual || format is Format.Ati1n or Format.Ati2n)
            return [1f, 1f, 1f];
        float r = 0.3086f, g = 0.6094f, b = 0.0820f;
        return [r * r, g * g, b * b];
    }

    private const int RC = 2, GC = 1, BC = 0, AC = 3;
    private const float MaxError = 128000f;
    private const float GblSchStep = 0.018f, GblSchExt = 0.1f, LclSchStep = 0.6f;
    private static readonly float[] sMvF = [0f, -1f, 1f, -2f, 2f, -3f, 3f, -4f, 4f];
    private static readonly uint[] dwRndAmount = [0, 0, 0, 0, 1, 1, 2, 2, 3];
    private static readonly byte[] nByteBitsMask = [0x00, 0x80, 0xc0, 0xe0, 0xf0, 0xf8, 0xfc, 0xfe, 0xff];

    // _mm_cvtps_epi32 under the default MXCSR: nearest even, 0x80000000 when out of range.
    private static int Cvt(float x)
        => float.IsNaN(x) || x >= 2147483648f || x < -2147483648f ? int.MinValue : (int)MathF.Round(x, MidpointRounding.ToEven);

    // The Windows min / max macros and _mm_min_ps / _mm_max_ps: a when the comparison holds, else b.
    private static float Min(float a, float b) => a < b ? a : b;
    private static float Max(float a, float b) => a > b ? a : b;

    /// <summary>Per-thread scratch, so a block allocates nothing.</summary>
    private sealed class Encoder
    {
        private readonly float[] _w = new float[3];
        private readonly byte[] _alpha = new byte[16];
        private readonly byte[] _idx = new byte[16];
        private readonly byte[] _idx2 = new byte[16];

        public void EncodeBlock(ReadOnlySpan<uint> block, Span<byte> dst, Format format, float[] baseWeights)
        {
            switch (format)
            {
                case Format.Dxt1:
                    RgbBlock(block, dst, Weights(block, baseWeights), dxt1: true);
                    break;
                case Format.Dxt5:
                    for (int i = 0; i < 16; i++) _alpha[i] = (byte)(block[i] >> 24);
                    AlphaBlock(_alpha, dst[..8]);
                    RgbBlock(block, dst[8..], Weights(block, baseWeights), dxt1: false);
                    break;
                case Format.Ati1n:
                    for (int i = 0; i < 16; i++) _alpha[i] = (byte)(block[i] >> 16);
                    AlphaBlock(_alpha, dst);
                    break;
                default:
                    for (int i = 0; i < 16; i++) _alpha[i] = (byte)(block[i] >> 16);
                    AlphaBlock(_alpha, dst[..8]);
                    for (int i = 0; i < 16; i++) _alpha[i] = (byte)(block[i] >> 8);
                    AlphaBlock(_alpha, dst[8..]);
                    break;
            }
        }

        // CCodec_DXTC::CalculateColourWeightings(CMP_BYTE block[]) as compiled: block[k] is one
        // byte, so the red and green medians stay 0 and blue sums the first 16 bytes.
        private float[] Weights(ReadOnlySpan<uint> block, float[] baseWeights)
        {
            float medianR = 0f, medianG = 0f, medianB = 0f;
            for (int k = 0; k < 16; k++)
            {
                uint b = (block[k / 4] >> (8 * (k % 4))) & 0xff;
                medianB += b;
            }
            medianR /= 16; medianG /= 16; medianB /= 16;
            float largest = Max(Max(medianR, medianG), medianB);
            if (largest > 0) { medianR /= largest; medianG /= largest; medianB /= largest; }
            else medianR = medianG = medianB = 1f;
            float s = 1f / (baseWeights[0] + baseWeights[1] + baseWeights[2]);
            _w[0] = baseWeights[0] * s; _w[1] = baseWeights[1] * s; _w[2] = baseWeights[2] * s;
            _w[0] = ((_w[0] * 3 * medianR) + _w[0]) * 0.25f;
            _w[1] = ((_w[1] * 3 * medianG) + _w[1]) * 0.25f;
            _w[2] = ((_w[2] * 3 * medianB) + _w[2]) * 0.25f;
            s = 1f / (_w[0] + _w[1] + _w[2]);
            _w[0] *= s; _w[1] *= s; _w[2] *= s;
            return _w;
        }

        // ---- colour (CCodec_DXTC::CompressRGBBlock, speed normal) ----

        private readonly byte[] _ep3 = new byte[6], _ep4 = new byte[6];

        private void RgbBlock(ReadOnlySpan<uint> block, Span<byte> dst, float[] w, bool dxt1)
        {
            uint c0, c1;
            byte[] ep, idx;
            if (dxt1)
            {
                double e3 = CompRgbBlock(block, _ep3, _idx, 3, w);
                double e4 = e3 == 0.0 ? float.MaxValue : CompRgbBlock(block, _ep4, _idx2, 4, w);
                bool four = !(e3 <= e4);
                ep = four ? _ep4 : _ep3; idx = four ? _idx2 : _idx;
                c0 = Colour(ep, 0); c1 = Colour(ep, 1);
                uint word = (four && c0 <= c1) || (!four && c0 > c1) ? c1 | (c0 << 16) : c0 | (c1 << 16);
                Write(dst, word, idx);
                return;
            }
            CompRgbBlock(block, _ep4, _idx2, 4, w);
            c0 = Colour(_ep4, 0); c1 = Colour(_ep4, 1);
            Write(dst, c0 <= c1 ? c1 | (c0 << 16) : c0 | (c1 << 16), _idx2);
        }

        // ConstructColour(r >> 3, g >> 2, b >> 3); ep holds [channel * 2 + endpoint].
        private static uint Colour(byte[] ep, int e)
            => ((uint)(ep[RC * 2 + e] >> 3) << 11) | ((uint)(ep[GC * 2 + e] >> 2) << 5) | (uint)(ep[BC * 2 + e] >> 3);

        private static void Write(Span<byte> dst, uint colours, byte[] idx)
        {
            uint bits = 0;
            for (int i = 0; i < 16; i++) bits |= (uint)idx[i] << (2 * i);
            BitConverter.TryWriteBytes(dst, colours);
            BitConverter.TryWriteBytes(dst[4..], bits);
        }

        private readonly float[] _rpt = new float[64];
        private readonly float[] _blkIn = new float[64 * 4];
        private readonly uint[] _sorted = new uint[16], _unique = new uint[16];
        private readonly float[] _rslt = new float[6];

        // CompRGBBlock(CMP_DWORD*): unique colours with repeat counts, the SSE2 fit, then
        // clusterisation of the original 16 pixels.
        private float CompRgbBlock(ReadOnlySpan<uint> block, byte[] ep, byte[] idx, int nPts, float[] w)
        {
            Array.Clear(_rpt); Array.Clear(_blkIn);
            for (int i = 0; i < 16; i++) _sorted[i] = block[i] | 0xff000000;
            // QSortIntCmp: every value has the top byte set, so signed order is unsigned order.
            Array.Sort(_sorted, 0, 16);
            int unique = 0;
            uint newP = _unique[0] = _sorted[0];
            _rpt[0] = 1f;
            for (int i = 1; i < 16; i++)
            {
                if (newP != _sorted[i]) { unique++; newP = _unique[unique] = _sorted[i]; _rpt[unique] = 1f; }
                else _rpt[unique] += 1f;
            }
            unique++;
            for (int i = 0; i < unique; i++)
            {
                _blkIn[i * 4 + RC] = (_unique[i] >> 16) & 0xff;
                _blkIn[i * 4 + GC] = (_unique[i] >> 8) & 0xff;
                _blkIn[i * 4 + BC] = _unique[i] & 0xff;
                _blkIn[i * 4 + AC] = 255f;
            }
            CompressRgbBlockXSse2(_rslt, _blkIn, _rpt, unique, nPts, w);
            for (int c = 0; c < 3; c++)
                for (int e = 0; e < 2; e++)
                    ep[c * 2 + e] = (byte)(int)_rslt[c * 2 + e];
            return Clstr(block, ep, idx, nPts, w);
        }

        private readonly float[] _prj0 = new float[64], _prj = new float[64], _prjErr = new float[64];
        private readonly float[] _preMRep = new float[64], _rmpIndxs = new float[64];
        private readonly float[] _blk = new float[64 * 4], _blkSh = new float[64 * 4];
        private readonly float[] _lineDir = new float[3], _lineDir0 = new float[3], _lineDirG = new float[3], _mdl = new float[3];
        private readonly float[] _onGrid = new float[6];

        private void CompressRgbBlockXSse2(float[] rslt, float[] blkIn, float[] rpt, int uniq, int nPts, float[] w)
        {
            for (int i = 0; i < uniq; i++)
                for (int j = 0; j < 3; j++)
                    _blk[i * 4 + j] = blkIn[i * 4 + j] / 255f;
            int cycles = (uniq + 3) / 4;
            bool done = false;
            if (uniq <= 2)
            {
                for (int j = 0; j < 3; j++) { rslt[j * 2] = blkIn[j]; rslt[j * 2 + 1] = blkIn[(uniq - 1) * 4 + j]; }
                done = true;
            }
            if (!done)
            {
                FindAxis(_blkSh, _lineDir0, _mdl, out bool small, _blk, rpt, uniq);
                if (small)
                {
                    for (int j = 0; j < 3; j++) { rslt[j * 2] = blkIn[j]; rslt[j * 2 + 1] = blkIn[(uniq - 1) * 4 + j]; }
                    done = true;
                }
            }
            if (!done)
            {
                float errG = 10000000f;
                float posG0 = 0f, posG1 = 0f;
                for (int j = 0; j < 3; j++) _lineDir[j] = _lineDir0[j];
                for (; ; )
                {
                    float bnd0 = 1000f, bnd1 = -1000f;
                    Array.Clear(_prj0); Array.Clear(_prj); Array.Clear(_prjErr); Array.Clear(_preMRep);
                    for (int i = 0; i < uniq; i++)
                    {
                        float s0 = _blkSh[i * 4], s1 = _blkSh[i * 4 + 1], s2 = _blkSh[i * 4 + 2];
                        float p = s0 * _lineDir[0] + s1 * _lineDir[1] + s2 * _lineDir[2];
                        _prj0[i] = _prj[i] = p;
                        _prjErr[i] = (s0 - _lineDir[0] * p) * (s0 - _lineDir[0] * p)
                            + (s1 - _lineDir[1] * p) * (s1 - _lineDir[1] * p)
                            + (s2 - _lineDir[2] * p) * (s2 - _lineDir[2] * p);
                        bnd0 = Min(bnd0, p);
                        bnd1 = Max(bnd1, p);
                    }
                    float scl0 = bnd0 - (bnd1 - bnd0) * 0.125f;
                    float scl1 = bnd1 + (bnd1 - bnd0) * 0.125f;
                    float scl2 = (scl1 - scl0) * (scl1 - scl0);
                    float overScl = 1f / (scl1 - scl0);
                    for (int i = 0; i < uniq; i++)
                    {
                        _prj[i] = (_prj[i] - scl0) * overScl;
                        _preMRep[i] = rpt[i] * scl2;
                    }
                    bnd0 = (bnd0 - scl0) * overScl;
                    bnd1 = (bnd1 - scl0) * overScl;

                    float err = MaxError;
                    const float stp = 0.025f;
                    float lS = bnd0 - 2f * stp > 0f ? bnd0 - 2f * stp : 0f;
                    float hE = bnd1 + 2f * stp < 1f ? bnd1 + 2f * stp : 1f;
                    float pos0 = 0f, pos1 = 0f;
                    float lP = lS;
                    for (int l = 0; l < 8; l++, lP += stp)
                    {
                        float hP = hE;
                        for (int h = 0; h < 8; h++, hP -= stp)
                        {
                            float e = RampSrchWSse2(cycles, lP, hP, nPts);
                            if (e < err) { err = e; pos0 = lP; pos1 = hP; }
                        }
                    }
                    pos0 = pos0 * (scl1 - scl0) + scl0;
                    pos1 = pos1 * (scl1 - scl0) + scl0;

                    if (!((double)err + 0.001 < errG))
                        break;
                    errG = err;
                    _lineDirG[0] = _lineDir[0]; _lineDirG[1] = _lineDir[1]; _lineDirG[2] = _lineDir[2];
                    posG0 = pos0; posG1 = pos1;

                    float step = (pos1 - pos0) / (float)(nPts - 1);
                    float rstep = 1f / step;
                    float overBlkTp = 1f / (float)(nPts - 1);
                    float indxAvrg = (float)(nPts - 1) / 2f;
                    for (int i = 0; i < cycles * 4; i++)
                    {
                        float v = Cvt((_prj0[i] - pos0) * rstep);
                        v = Min(Max(v, 0f), (float)(nPts - 1));
                        _rmpIndxs[i] = overBlkTp * (v - indxAvrg);
                    }

                    float crs0 = 0f, crs1 = 0f, crs2 = 0f, len = 0f;
                    for (int i = 0; i < uniq; i++)
                    {
                        float preMlt = _rmpIndxs[i] * rpt[i];
                        len += _rmpIndxs[i] * preMlt;
                        crs0 += _blkSh[i * 4] * preMlt;
                        crs1 += _blkSh[i * 4 + 1] * preMlt;
                        crs2 += _blkSh[i * 4 + 2] * preMlt;
                    }
                    _lineDir[0] = _lineDir[1] = _lineDir[2] = 0f;
                    if (len > 0f)
                    {
                        _lineDir[0] = crs0 / len; _lineDir[1] = crs1 / len; _lineDir[2] = crs2 / len;
                        float len2 = MathF.Sqrt(_lineDir[0] * _lineDir[0] + _lineDir[1] * _lineDir[1] + _lineDir[2] * _lineDir[2]);
                        _lineDir[0] /= len2; _lineDir[1] /= len2; _lineDir[2] /= len2;
                    }
                }
                for (int j = 0; j < 3; j++)
                {
                    rslt[j * 2] = (posG0 * _lineDirG[j] + _mdl[j]) * 255f;
                    rslt[j * 2 + 1] = (posG1 * _lineDirG[j] + _mdl[j]) * 255f;
                }
            }
            MkRmpOnGrid(_onGrid, rslt);
            RefineSse2(rslt, _onGrid, blkIn, rpt, uniq, nPts, w, refineSteps: 1);
        }

        // RampSrchWSS2E: four values per step, lanes summed (0 + 2) + (1 + 3).
        private float RampSrchWSse2(int cycles, float lo, float hi, int nPts)
        {
            float step = (hi - lo) / (nPts - 1);
            float rstep = 1f / step;
            Span<float> lane = stackalloc float[4];
            for (int i = 0; i < cycles; i++)
                for (int k = 0; k < 4; k++)
                {
                    int n = i * 4 + k;
                    float v = Cvt((_prj[n] - lo) * rstep);
                    v = v * step + lo;
                    v = Min(Max(v, lo), hi);
                    float d = _prj[n] - v;
                    d *= d;
                    lane[k] += _prjErr[n] + _preMRep[n] * d;
                }
            return (lane[0] + lane[2]) + (lane[1] + lane[3]);
        }

        private static void FindAxis(float[] outBlk, float[] dir, float[] center, out bool small, float[] inBlk, float[] rpt, int n)
        {
            Span<float> crrl = stackalloc float[3];
            Span<float> rgb2 = stackalloc float[3];
            dir[0] = dir[1] = dir[2] = center[0] = center[1] = center[2] = 0f;
            float numPoints = 0f;
            for (int i = 0; i < n; i++)
            {
                center[0] += inBlk[i * 4] * rpt[i];
                center[1] += inBlk[i * 4 + 1] * rpt[i];
                center[2] += inBlk[i * 4 + 2] * rpt[i];
                numPoints += rpt[i];
            }
            center[0] /= numPoints; center[1] /= numPoints; center[2] /= numPoints;
            for (int i = 0; i < n; i++)
            {
                outBlk[i * 4] = inBlk[i * 4] - center[0];
                outBlk[i * 4 + 1] = inBlk[i * 4 + 1] - center[1];
                outBlk[i * 4 + 2] = inBlk[i * 4 + 2] - center[2];
                for (int j = 0; j < 3; j++)
                {
                    rgb2[j] += outBlk[i * 4 + j] * outBlk[i * 4 + j] * rpt[i];
                    crrl[j] += outBlk[i * 4 + j] * outBlk[i * 4 + (j + 1) % 3] * rpt[i];
                }
            }
            int i0 = 0, i1;
            float mxRgb2 = 0f;
            int k = 0;
            // EPS and EPS2 are unparenthesised macros: numPoints * (2/255) * (2/255), left to right.
            float fEps = numPoints * (2f / 255f) * (2f / 255f);
            for (int j = 0; j < 3; j++)
            {
                if (rgb2[j] >= fEps) k++;
                else rgb2[j] = 0f;
                if (mxRgb2 < rgb2[j]) { mxRgb2 = rgb2[j]; i0 = j; }
            }
            float fEps2 = numPoints * 3f * (2f / 255f) * (2f / 255f);
            small = true;
            for (int j = 0; j < 3; j++) small &= rgb2[j] < fEps2;
            if (small) return;

            if (k == 1)
                dir[i0] = 1f;
            else if (k == 2)
            {
                i1 = rgb2[(i0 + 1) % 3] > 0f ? (i0 + 1) % 3 : (i0 + 2) % 3;
                float crl = i1 == (i0 + 1) % 3 ? crrl[i0] : crrl[(i0 + 2) % 3];
                dir[i1] = crl / rgb2[i0];
                dir[i0] = 1f;
            }
            else
            {
                float maxDet = 100000f;
                for (int j = 0; j < 3; j++)
                {
                    float det = rgb2[j] * rgb2[(j + 1) % 3] - crrl[j] * crrl[j];
                    if (maxDet < det) { maxDet = det; i0 = j; }
                }
                float vc10 = crrl[(i0 + 2) % 3], vc11 = crrl[(i0 + 1) % 3];
                float m00 = rgb2[(i0 + 1) % 3], m11 = rgb2[i0], m01 = -crrl[i0], m10 = m01;
                float vc0 = m00 * vc10 + m01 * vc11;
                float vc1 = m10 * vc10 + m11 * vc11;
                vc0 /= maxDet; vc1 /= maxDet;
                dir[i0] = 1f;
                dir[(i0 + 1) % 3] = 1f;
                dir[(i0 + 2) % 3] = vc0 + vc1;
            }
            float len = MathF.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
            for (int j = 0; j < 3; j++) dir[j] = len > 0f ? dir[j] / len : 0f;
        }

        // Channel bits are 5 / 6 / 5 for R / G / B; arrays are [channel * 2 + endpoint].
        private static int Bits(int channel) => channel == GC ? 6 : 5;

        private static void MkRmpOnGrid(float[] outRmp, float[] mnMx)
        {
            for (int j = 0; j < 3; j++)
            {
                float f1 = 1 << Bits(j), f0 = 1 << (8 - Bits(j));
                for (int k = 0; k < 2; k++)
                {
                    float v = MathF.Floor(mnMx[j * 2 + k]);
                    if (v <= 0f) v = 0f;
                    else
                    {
                        v += MathF.Floor(128f / f1) - MathF.Floor(v / f1);
                        v = Min(v, 255f);
                    }
                    outRmp[j * 2 + k] = MathF.Floor(v / f0) * f0;
                }
            }
        }

        private static bool MkWkRmpPts(float[] outPts, float[] inPts)
        {
            bool eq = true;
            for (int j = 0; j < 3; j++) eq &= inPts[j * 2] == inPts[j * 2 + 1];
            for (int j = 0; j < 3; j++)
            {
                float f = 1 << Bits(j);
                for (int k = 0; k < 2; k++)
                {
                    float v = inPts[j * 2 + k] + MathF.Floor(inPts[j * 2 + k] / f);
                    v = Max(v, 0f);
                    outPts[j * 2 + k] = Min(v, 255f);
                }
            }
            return eq;
        }

        // Rmp is [channel * 16 + point].
        private static void BldClrRmp(float[] rmp, int channel, float in0, float in1, int nPts)
        {
            int o = channel * 16;
            rmp[o] = in0;
            rmp[o + nPts - 1] = in1;
            if (nPts % 2 != 0) rmp[o + nPts] = 1000000f;
            for (int e = 1; e < nPts - 1; e++)
                rmp[o + e] = MathF.Floor((rmp[o] * (nPts - 1 - e) + rmp[o + nPts - 1] * e + dwRndAmount[nPts]) / (float)(nPts - 1));
        }

        private static void BldRmp(float[] rmp, float[] inPts, int nPts)
        {
            for (int j = 0; j < 3; j++) BldClrRmp(rmp, j, inPts[j * 2], inPts[j * 2 + 1], nPts);
        }

        private static float ClstrErr(float[] blk, float[] rpt, float[] rmp, int n, int nPts, bool constRamp, float[] w)
        {
            float err = 0f;
            int rmpL = constRamp ? 1 : nPts;
            for (int i = 0; i < n; i++)
            {
                float shortest = 99999999999f;
                for (int r = 0; r < rmpL; r++)
                {
                    float dr = blk[i * 4 + RC] - rmp[RC * 16 + r], dg = blk[i * 4 + GC] - rmp[GC * 16 + r], db = blk[i * 4 + BC] - rmp[BC * 16 + r];
                    float d = dr * dr * w[0] + dg * dg * w[1] + db * db * w[2];
                    if (d < shortest) shortest = d;
                }
                err += shortest * rpt[i];
            }
            return err;
        }

        private readonly float[] _rmp = new float[4 * 16];
        private readonly float[] _wk = new float[6], _inp = new float[6], _inp0 = new float[6];
        private readonly float[] _rmpErr = new float[64 * 4];

        // RefineSSE2: jitter red, then green, then blue endpoints by one grid step.
        private void RefineSse2(float[] outPts, float[] inPts, float[] blk, float[] rpt, int n, int nPts, float[] w, int refineSteps)
        {
            float wR = w[0], wG = w[1], wB = w[2];
            for (int i = 0; i < 6; i++) _inp0[i] = _inp[i] = outPts[i] = inPts[i];
            Array.Clear(_rmp);
            bool eq = MkWkRmpPts(_wk, _inp);
            BldRmp(_rmp, _wk, nPts);
            float bestE = ClstrErr(blk, rpt, _rmp, n, nPts, eq, w);
            if (bestE == 0f || refineSteps == 0)
                return;

            int start = -Math.Min(refineSteps, 8), end = Math.Min(refineSteps, 8);
            int cycles = (n + 3) / 4;

            SetRmpErr(blk, n, nPts, GC, wG, BC, wB);
            bestE = Channel(RC, wR, bestE);
            Rebuild();
            SetRmpErr(blk, n, nPts, RC, wR, BC, wB);
            bestE = Channel(GC, wG, bestE);
            Rebuild();
            SetRmpErr(blk, n, nPts, RC, wR, GC, wG);
            Channel(BC, wB, bestE);
            for (int i = 0; i < 6; i++) outPts[i] = _inp[i];

            void Rebuild()
            {
                MkWkRmpPts(_wk, _inp);
                BldRmp(_rmp, _wk, nPts);
            }

            float Channel(int ch, float weight, float best)
            {
                float fctr = 1 << (8 - Bits(ch));
                float bst0 = _inp0[ch * 2], bst1 = _inp0[ch * 2 + 1];
                Span<float> lane = stackalloc float[4];
                for (int i = start; i <= end; i++)
                    for (int j = start; j <= end; j++)
                    {
                        _inp[ch * 2] = Min(Max(_inp0[ch * 2] + i * fctr, 0f), 255f);
                        _inp[ch * 2 + 1] = Min(Max(_inp0[ch * 2 + 1] + j * fctr, 0f), 255f);
                        MkWkRmpPts(_wk, _inp);
                        BldClrRmp(_rmp, ch, _wk[ch * 2], _wk[ch * 2 + 1], nPts);
                        // Each colour's minimum over the four ramp lanes lands in lane (colour % 4).
                        lane.Clear();
                        for (int k = 0; k < cycles; k++)
                            for (int c = 0; c < 4; c++)
                            {
                                int idx = k * 4 + c;
                                float cv = blk[idx * 4 + ch];
                                float m = float.MaxValue;
                                for (int r = 0; r < 4; r++)
                                {
                                    float d = cv - _rmp[ch * 16 + r];
                                    d *= d;
                                    d *= weight;
                                    float e = d + _rmpErr[idx * 4 + r];
                                    m = Min(m, e);
                                }
                                lane[c] += m * rpt[idx];
                            }
                        float mse = lane[0] + lane[1] + lane[2] + lane[3];
                        if (mse < best) { bst0 = _inp[ch * 2]; bst1 = _inp[ch * 2 + 1]; best = mse; }
                    }
                _inp[ch * 2] = bst0;
                _inp[ch * 2 + 1] = bst1;
                return best;
            }
        }

        private void SetRmpErr(float[] blk, int n, int nPts, int a, float wa, int b, float wb)
        {
            Array.Clear(_rmpErr);
            for (int i = 0; i < n; i++)
                for (int r = 0; r < nPts; r++)
                {
                    float da = _rmp[a * 16 + r] - blk[i * 4 + a];
                    float db = _rmp[b * 16 + r] - blk[i * 4 + b];
                    _rmpErr[i * 4 + r] = da * da * wa + db * db * wb;
                }
        }

        private readonly float[] _clBlk = new float[16 * 4];

        // Clstr(CMP_DWORD*): the endpoints the way a decoder orders them, then each pixel to its
        // nearest ramp point.
        private float Clstr(ReadOnlySpan<uint> block, byte[] ep, byte[] idx, int nPts, float[] w)
        {
            uint c0 = ConstructColor(ep, 0), c1 = ConstructColor(ep, 1);
            int e0 = 0, e1 = 1;
            if (((nPts & 1) == 0 && c0 <= c1) || ((nPts & 1) != 0 && c0 > c1)) { e0 = 1; e1 = 0; }
            for (int j = 0; j < 3; j++) { _inp[j * 2] = ep[j * 2 + e0]; _inp[j * 2 + 1] = ep[j * 2 + e1]; }
            for (int i = 0; i < 16; i++)
            {
                _clBlk[i * 4 + RC] = (block[i] & 0xff0000) >> 16;
                _clBlk[i * 4 + GC] = (block[i] & 0xff00) >> 8;
                _clBlk[i * 4 + BC] = block[i] & 0xff;
            }
            bool eq = MkWkRmpPts(_wk, _inp);
            Array.Clear(_rmp);
            BldRmp(_rmp, _wk, nPts);
            float err = 0f;
            int rmpL = eq ? 1 : nPts;
            for (int i = 0; i < 16; i++)
            {
                float shortest = 99999999999f;
                int shortestIndex = 0;
                for (int r = 0; r < rmpL; r++)
                {
                    float dr = _clBlk[i * 4 + RC] - _rmp[RC * 16 + r], dg = _clBlk[i * 4 + GC] - _rmp[GC * 16 + r], db = _clBlk[i * 4 + BC] - _rmp[BC * 16 + r];
                    float d = dr * dr * w[0] + dg * dg * w[1] + db * db * w[2];
                    if (d < shortest) { shortest = d; shortestIndex = r; }
                }
                err += shortest;
                if (shortestIndex == nPts - 1) shortestIndex = 1;
                else if (shortestIndex != 0) shortestIndex++;
                idx[i] = (byte)shortestIndex;
            }
            return err;
        }

        private static uint ConstructColor(byte[] ep, int e)
            => ((uint)(ep[RC * 2 + e] & nByteBitsMask[5]) << (6 + 5 - 3))
             | ((uint)(ep[GC * 2 + e] & nByteBitsMask[6]) << (5 - 2))
             | ((uint)(ep[BC * 2 + e] & nByteBitsMask[5]) >> 3);

        // ---- alpha / ATI1N / ATI2N (CCodec_DXTC::CompressAlphaBlock) ----

        private readonly byte[] _aIdx8 = new byte[16], _aIdx6 = new byte[16];
        private readonly float[] _fBlk = new float[64];

        private void AlphaBlock(byte[] values, Span<byte> dst)
        {
            for (int i = 0; i < 16; i++) _fBlk[i] = values[i] / 255f;
            float e8 = CompBlock1X(_fBlk, out byte a0, out byte a1, _aIdx8, 8, fixedRampPoints: false);
            byte b0 = 0, b1 = 0;
            float e6 = e8 == 0f ? float.MaxValue : CompBlock1X(_fBlk, out b0, out b1, _aIdx6, 6, fixedRampPoints: true);
            bool six = !(e8 <= e6);
            byte p0 = six ? b0 : a0, p1 = six ? b1 : a1;
            var idx = six ? _aIdx6 : _aIdx8;
            ulong bits = p0 | ((ulong)p1 << 8);
            for (int i = 0; i < 16; i++) bits |= (ulong)(idx[i] & 7) << (16 + 3 * i);
            BitConverter.TryWriteBytes(dst, bits);
        }

        private readonly float[] _ramp = new float[2];

        private float CompBlock1X(float[] blk, out byte ep0, out byte ep1, byte[] idx, int nPts, bool fixedRampPoints)
        {
            CompBlock1(_ramp, blk, 16, nPts, fixedRampPoints);
            float err = Clstr1(idx, blk, _ramp, 16, nPts, fixedRampPoints);
            ep0 = (byte)(int)_ramp[0];
            ep1 = (byte)(int)_ramp[1];
            return err;
        }

        private readonly float[] _uniq = new float[64], _rep = new float[64], _sortedF = new float[64];

        // CompBlock1 with _IntPrc 8, _FracPrc 0 and a fixed ramp (the integer grid).
        private void CompBlock1(float[] rmpPnts, float[] blk, int n, int nPts, bool fixedRampPoints)
        {
            const float intFctr = 256f;
            float ramp0, ramp1;
            Array.Clear(_uniq); Array.Clear(_rep);
            Array.Copy(blk, _sortedF, n);
            Array.Sort(_sortedF, 0, n);
            float newP = -2f;
            int unique = 0;
            if (fixedRampPoints)
            {
                for (int i = 0; i < n; i++)
                {
                    if (newP != _sortedF[i])
                    {
                        newP = _sortedF[i];
                        // 1.5/255. and 253.5/255. are doubles: the comparison is in double.
                        if ((double)newP <= 1.5 / 255.0) { }
                        else if ((double)newP >= 253.5 / 255.0) { }
                        else { _uniq[unique] = _sortedF[i]; _rep[unique] = 1f; unique++; }
                    }
                    else if (unique > 0 && _uniq[unique - 1] == newP)
                        _rep[unique - 1] += 1f;
                }
                if (unique <= 2)
                {
                    if (unique == 2)
                    {
                        ramp0 = MathF.Floor(_uniq[0] * (intFctr - 1) + 0.5f);
                        ramp1 = MathF.Floor(_uniq[1] * (intFctr - 1) + 0.5f);
                    }
                    else if (unique == 1)
                    {
                        ramp0 = MathF.Floor(_uniq[0] * (intFctr - 1) + 0.5f);
                        ramp1 = ramp0 + 1f;
                    }
                    else { ramp0 = 128f; ramp1 = ramp0 + 1f; }
                    Finish(rmpPnts, ramp0, ramp1);
                    return;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    if (newP != _sortedF[i]) { _uniq[unique] = newP = _sortedF[i]; _rep[unique] = 1f; unique++; }
                    else _rep[unique - 1] += 1f;
                }
                if (unique <= 2)
                {
                    ramp0 = MathF.Floor(_uniq[0] * (intFctr - 1) + 0.5f);
                    ramp1 = unique == 1 ? ramp0 + 1f : MathF.Floor(_uniq[1] * (intFctr - 1) + 0.5f);
                    Finish(rmpPnts, ramp0, ramp1);
                    return;
                }
            }

            float minEx = _uniq[0], maxEx = _uniq[unique - 1];
            const float minBnd = 0f, maxBnd = 1f;
            float minR = minEx, maxR = maxEx;
            float gblL = 0f, gblR = 0f;
            float cntr = (minR + maxR) / 2;
            float gblErr = MaxError;
            if (!(maxEx - minEx <= 48f / intFctr))
            {
                float gblLlb = minBnd > minR - GblSchExt ? minBnd : minR - GblSchExt;
                float gblRrb = maxBnd < maxR + GblSchExt ? maxBnd : maxR + GblSchExt;
                float gblLrb = cntr < minR + GblSchExt ? cntr : minR + GblSchExt;
                float gblRlb = cntr > maxR - GblSchExt ? cntr : maxR - GblSchExt;
                for (float stepL = gblLlb; stepL < gblLrb; stepL += GblSchStep)
                    for (float stepR = gblRrb; gblRlb <= stepR; stepR -= GblSchStep)
                    {
                        float schErr = RmpSrch1Sse2(unique, stepL, stepR, nPts, fourLanes: true);
                        if (schErr < gblErr) { gblErr = schErr; gblL = stepL; gblR = stepR; }
                    }
                minR = gblL;
                maxR = gblR;
            }
            float mStep = LclSchStep / intFctr;
            Refine1(gblErr, ref minR, ref maxR, mStep, minBnd, maxBnd, unique, nPts);
            minEx = minR * (intFctr - 1);
            maxEx = maxR * (intFctr - 1);
            Finish(rmpPnts, MathF.Floor(minEx + 0.5f), MathF.Floor(maxEx + 0.5f));
        }

        private static void Finish(float[] rmpPnts, float ramp0, float ramp1)
        {
            if (ramp0 == ramp1)
            {
                if (ramp1 < 255f) ramp1++;
                else ramp1--;
            }
            rmpPnts[0] = ramp0;
            rmpPnts[1] = ramp1;
        }

        // RmpSrch1SSE2. Where Refine1 inlines it, Valve's DLL sums only lanes 0 and 1.
        private float RmpSrch1Sse2(int n, float minEx, float maxEx, int nPts, bool fourLanes)
        {
            float step = (maxEx - minEx) / (nPts - 1);
            float rstep = 1f / step;
            Span<float> lane = stackalloc float[4];
            for (int i = 0; i < (n + 3) / 4; i++)
                for (int k = 0; k < 4; k++)
                {
                    int j = i * 4 + k;
                    float v = Cvt((_uniq[j] - minEx) * rstep);
                    v = v * step + minEx;
                    v = Min(Max(v, minEx), maxEx);
                    float d = _uniq[j] - v;
                    d *= d;
                    lane[k] += _rep[j] * d;
                }
            return fourLanes ? (lane[0] + lane[2]) + (lane[1] + lane[3]) : lane[0] + lane[1];
        }

        private float Refine1(float maxError, ref float minExRef, ref float maxExRef, float mStep, float minBnd, float maxBnd, int n, int nPts)
        {
            float maxerror = maxError, minEx = minExRef, maxEx = maxExRef;
            int bestmode;
            do
            {
                float crMin0 = minEx, crMax0 = maxEx;
                bestmode = -1;
                for (int mode = 0; mode < 9; mode++)
                {
                    float crMin = minEx + mStep * sMvF[mode / 3];
                    float crMax = maxEx + mStep * sMvF[mode % 3];
                    crMin = Max(crMin, minBnd);
                    crMax = Min(crMax, maxBnd);
                    float error = RmpSrch1Sse2(n, crMin, crMax, nPts, fourLanes: false);
                    if (error < maxerror) { maxerror = error; bestmode = mode; crMin0 = crMin; crMax0 = crMax; }
                }
                if (bestmode != -1) { minEx = crMin0; maxEx = crMax0; }
            } while (bestmode != -1);
            minExRef = minEx;
            maxExRef = maxEx;
            return maxerror;
        }

        private readonly float[] _alphaRamp = new float[16];

        private float Clstr1(byte[] idx, float[] blk, float[] ramp, int n, int nPts, bool fixedRampPoints)
        {
            float err = 0f;
            for (int i = 0; i < n; i++) idx[i] = 0;
            if (ramp[0] == ramp[1]) return err;
            GetRmp1(_alphaRamp, ramp, nPts, fixedRampPoints);
            if (fixedRampPoints) nPts += 2;
            float overIntFctr = 1f / (256f - 1f);
            for (int i = 0; i < nPts; i++) _alphaRamp[i] *= overIntFctr;
            for (int i = 0; i < n; i++)
            {
                float shortest = 10000000f;
                float acur = blk[i];
                for (int j = 0; j < nPts; j++)
                {
                    float d = acur - _alphaRamp[j];
                    d *= d;
                    if (d < shortest) { shortest = d; idx[i] = (byte)j; }
                }
                err += shortest;
            }
            return err;
        }

        private static void GetRmp1(float[] rampDat, float[] ramp, int nPts, bool fixedRampPoints)
        {
            if (ramp[0] == ramp[1]) return;
            if ((!fixedRampPoints && ramp[0] <= ramp[1]) || (fixedRampPoints && ramp[0] > ramp[1]))
                (ramp[0], ramp[1]) = (ramp[1], ramp[0]);
            rampDat[0] = ramp[0];
            rampDat[1] = ramp[1];
            // BldRmp1 with FracFctr 1
            for (int e = nPts; e < 16; e++) rampDat[e] = 100000f;
            rampDat[0] = ramp[0];
            rampDat[1] = ramp[1];
            for (int e = 1; e < nPts - 1; e++)
                rampDat[e + 1] = (rampDat[0] * (nPts - 1 - e) + rampDat[1] * e) / (float)(nPts - 1);
            if (fixedRampPoints)
            {
                rampDat[nPts] = 0f;
                rampDat[nPts + 1] = 1f * 256f - 1f;
            }
            for (int i = 0; i < nPts; i++)
                rampDat[i] = MathF.Floor(rampDat[i] + 0.5f);
        }
    }
}
