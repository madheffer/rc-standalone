// atitest23 <bgra.raw> <w> <h> <fmt: dxt1|dxt5|ati1n|ati2n> <perceptual 0|1> <out> [weighting adaptive square3d steps]
// Compressonator 2.3's CompressonatorXCodec.cpp built with USE_SSE, driven the way
// ati_compress_wrapper.dll drives it (CCodec_DXTC byte path).
#include "Common.h"
#include "CompressonatorXCodec.h"
#include <cstdio>
#include <vector>
#include <string>
#define RG 5
#define GG 6
#define BG 5
#define ConstructColour(r, g, b) (((r) << 11) | ((g) << 5) | (b))
static float g_base[3], g_w[3];
static bool g_weighting = true, g_adaptive = true, g_3d = false; static int g_steps = 1;
// CCodec_DXTC::CalculateColourWeightings(CMP_BYTE block[]) as compiled: block[k] is a byte.
static float* Weights(const CMP_BYTE* block)
{
    if (!g_weighting) return NULL;
    if (g_adaptive) {
        float medianR = 0.0f, medianG = 0.0f, medianB = 0.0f;
        for (CMP_DWORD k = 0; k < 16; k++) {
            CMP_DWORD R = (block[k] & 0xff0000) >> 16, G = (block[k] & 0xff00) >> 8, B = block[k] & 0xff;
            medianR += R; medianG += G; medianB += B;
        }
        medianR /= 16; medianG /= 16; medianB /= 16;
        float largest = max(max(medianR, medianG), medianB);
        if (largest > 0) { medianR /= largest; medianG /= largest; medianB /= largest; }
        else medianR = medianG = medianB = 1.0f;
        float s = 1.0f / (g_base[0] + g_base[1] + g_base[2]);
        g_w[0] = g_base[0] * s; g_w[1] = g_base[1] * s; g_w[2] = g_base[2] * s;
        g_w[0] = ((g_w[0] * 3 * medianR) + g_w[0]) * 0.25f;
        g_w[1] = ((g_w[1] * 3 * medianG) + g_w[1]) * 0.25f;
        g_w[2] = ((g_w[2] * 3 * medianB) + g_w[2]) * 0.25f;
        s = 1.0f / (g_w[0] + g_w[1] + g_w[2]);
        g_w[0] *= s; g_w[1] *= s; g_w[2] *= s;
    }
    return g_w;
}
static void RGBBlock(CMP_BYTE* rgbBlock, CMP_DWORD compressedBlock[2], float* w, bool bDXT1)
{
    int RC = 2, GC = 1, BC = 0;
    if (bDXT1) {
        CMP_BYTE nEndpoints[2][3][2]; CMP_BYTE nIndices[2][16];
        double fError3 = CompRGBBlock((DWORD*)rgbBlock, 16, RG, GG, BG, nEndpoints[0], nIndices[0], 3, true, g_3d, (CMP_BYTE)g_steps, w, false, 128);
        double fError4 = (fError3 == 0.0) ? FLT_MAX : CompRGBBlock((DWORD*)rgbBlock, 16, RG, GG, BG, nEndpoints[1], nIndices[1], 4, true, g_3d, (CMP_BYTE)g_steps, w, false, 128);
        unsigned int nMethod = (fError3 <= fError4) ? 0 : 1;
        unsigned int c0 = ConstructColour((nEndpoints[nMethod][RC][0] >> (8-RG)), (nEndpoints[nMethod][GC][0] >> (8-GG)), (nEndpoints[nMethod][BC][0] >> (8-BG)));
        unsigned int c1 = ConstructColour((nEndpoints[nMethod][RC][1] >> (8-RG)), (nEndpoints[nMethod][GC][1] >> (8-GG)), (nEndpoints[nMethod][BC][1] >> (8-BG)));
        if ((nMethod == 1 && c0 <= c1) || (nMethod == 0 && c0 > c1)) compressedBlock[0] = c1 | (c0<<16); else compressedBlock[0] = c0 | (c1<<16);
        compressedBlock[1] = 0;
        for (int i = 0; i < 16; i++) compressedBlock[1] |= (nIndices[nMethod][i] << (2*i));
    } else {
        CMP_BYTE nEndpoints[3][2]; CMP_BYTE nIndices[16];
        CompRGBBlock((DWORD*)rgbBlock, 16, RG, GG, BG, nEndpoints, nIndices, 4, true, g_3d, (CMP_BYTE)g_steps, w, false, 128);
        unsigned int c0 = ConstructColour((nEndpoints[RC][0] >> (8-RG)), (nEndpoints[GC][0] >> (8-GG)), (nEndpoints[BC][0] >> (8-BG)));
        unsigned int c1 = ConstructColour((nEndpoints[RC][1] >> (8-RG)), (nEndpoints[GC][1] >> (8-GG)), (nEndpoints[BC][1] >> (8-BG)));
        if (c0 <= c1) compressedBlock[0] = c1 | (c0<<16); else compressedBlock[0] = c0 | (c1<<16);
        compressedBlock[1] = 0;
        for (int i = 0; i < 16; i++) compressedBlock[1] |= (nIndices[i] << (2*i));
    }
}
static void AlphaBlock(CMP_BYTE a[16], CMP_DWORD out[2])
{
    BYTE ep[2][2], idx[2][16];
    float e8 = CompBlock1X(a, 16, ep[0], idx[0], 8, false, true, 8, 0, true);
    float e6 = (e8 == 0.f) ? FLT_MAX : CompBlock1X(a, 16, ep[1], idx[1], 6, true, true, 8, 0, true);
    int m = (e8 <= e6) ? 0 : 1;
    out[0] = ((int)ep[m][0]) | (((int)ep[m][1]) << 8);
    out[1] = 0;
    for (int i = 0; i < 16; i++) {
        if (i < 5) out[0] |= (idx[m][i] & 0x7) << (16 + (i * 3));
        else if (i > 5) out[1] |= (idx[m][i] & 0x7) << (2 + (i - 6) * 3);
        else { out[0] |= (idx[m][i] & 0x1) << 31; out[1] |= (idx[m][i] & 0x6) >> 1; }
    }
}
static void AlphaBlockF(CODECFLOAT a[16], CMP_DWORD out[2])
{
    BYTE ep[2][2], idx[2][16];
    float e8 = CompBlock1X(a, 16, ep[0], idx[0], 8, false, true, 8, 0, true);
    float e6 = (e8 == 0.f) ? FLT_MAX : CompBlock1X(a, 16, ep[1], idx[1], 6, true, true, 8, 0, true);
    int m = (e8 <= e6) ? 0 : 1;
    out[0] = ((int)ep[m][0]) | (((int)ep[m][1]) << 8);
    out[1] = 0;
    for (int i = 0; i < 16; i++) {
        if (i < 5) out[0] |= (idx[m][i] & 0x7) << (16 + (i * 3));
        else if (i > 5) out[1] |= (idx[m][i] & 0x7) << (2 + (i - 6) * 3);
        else { out[0] |= (idx[m][i] & 0x1) << 31; out[1] |= (idx[m][i] & 0x6) >> 1; }
    }
}
// ati1nf / ati2nf: input is float32 RGBA per pixel, compressed through the float path.
static int MainFloat(char** argv)
{
    FILE* f = fopen(argv[1], "rb"); int w = atoi(argv[2]), h = atoi(argv[3]);
    std::vector<float> px(w * h * 4); fread(px.data(), 4, px.size(), f); fclose(f);
    std::string fmt = argv[4];
    FILE* o = fopen(argv[6], "wb");
    for (int by = 0; by < (h + 3) / 4; by++)
        for (int bx = 0; bx < (w + 3) / 4; bx++) {
            CODECFLOAT ch[4][16];
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) {
                int vw = w - bx * 4 < 4 ? w - bx * 4 : 4, vh = h - by * 4 < 4 ? h - by * 4 : 4;
                int sx = bx * 4 + x % vw, sy = by * 4 + y % vh;
                for (int c = 0; c < 4; c++) ch[c][y*4+x] = px[(sy * w + sx) * 4 + c];
            }
            CMP_DWORD out[4];
            if (fmt == "ati1nf") { AlphaBlockF(ch[0], out); fwrite(out, 4, 2, o); }
            else { AlphaBlockF(ch[0], out); AlphaBlockF(ch[1], out + 2); fwrite(out, 4, 4, o); }
        }
    fclose(o);
    return 0;
}
int main(int argc, char** argv)
{
    if (std::string(argv[4]).back() == 'f') return MainFloat(argv);
    FILE* f = fopen(argv[1], "rb"); int w = atoi(argv[2]), h = atoi(argv[3]);
    std::vector<uint32_t> px(w * h); fread(px.data(), 4, px.size(), f); fclose(f);
    std::string fmt = argv[4]; bool perc = atoi(argv[5]) != 0;
    bool square = false;
    if (argc > 7) g_weighting = atoi(argv[7]) != 0;
    if (argc > 8) g_adaptive = atoi(argv[8]) != 0;
    if (argc > 9) square = atoi(argv[9]) != 0;
    if (argc > 10) g_3d = atoi(argv[10]) != 0;
    if (argc > 11) g_steps = atoi(argv[11]);
    float wr = perc ? 0.3086f : 1.0f, wg = perc ? 0.6094f : 1.0f, wb = perc ? 0.0820f : 1.0f;
    if (square) { wr *= wr; wg *= wg; wb *= wb; }
    g_base[0] = g_w[0] = wr; g_base[1] = g_w[1] = wg; g_base[2] = g_w[2] = wb;
    FILE* o = fopen(argv[6], "wb");
    for (int by = 0; by < (h + 3) / 4; by++)
        for (int bx = 0; bx < (w + 3) / 4; bx++) {
            uint32_t blk[16]; CMP_BYTE ch[4][16];
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) {
                // a partial block repeats its own valid pixels (the DLL's block reader)
                int vw = w - bx * 4 < 4 ? w - bx * 4 : 4, vh = h - by * 4 < 4 ? h - by * 4 : 4;
                int sx = bx * 4 + x % vw, sy = by * 4 + y % vh;
                uint32_t v = px[sy * w + sx]; blk[y * 4 + x] = v;
                ch[0][y*4+x] = v & 0xff; ch[1][y*4+x] = (v >> 8) & 0xff; ch[2][y*4+x] = (v >> 16) & 0xff; ch[3][y*4+x] = v >> 24;
            }
            CMP_DWORD out[4];
            if (fmt == "dxt1") { RGBBlock((CMP_BYTE*)blk, out, Weights((CMP_BYTE*)blk), true); fwrite(out, 4, 2, o); }
            else if (fmt == "dxt5") { AlphaBlock(ch[3], out); RGBBlock((CMP_BYTE*)blk, out + 2, Weights((CMP_BYTE*)blk), false); fwrite(out, 4, 4, o); }
            else if (fmt == "ati1n") { AlphaBlock(ch[2], out); fwrite(out, 4, 2, o); }
            else if (fmt == "ati2n") { AlphaBlock(ch[2], out); AlphaBlock(ch[1], out + 2); fwrite(out, 4, 4, o); }
        }
    fclose(o);
    return 0;
}
