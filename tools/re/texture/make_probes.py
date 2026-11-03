"""Write the remaining-algorithm .vtex probes (RcReference/textures/algorithms2) and their images into <out_dir>.

Compile them with rc_probe.py <out_dir> materials/s2calg2 <rc_out>; RC refuses some formats, which have no fixture.

make_probes.py <out_dir> <existing images dir>
"""
import os
import random
import shutil
import sys

from PIL import Image

out, images = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
REL = "materials/s2calg2"
for n in ("noisy64", "color64", "bumps64", "rough64", "mask64", "trans64"):
    shutil.copyfile(os.path.join(images, n + ".png"), os.path.join(out, n + ".png"))

rng = random.Random(1104)
im = Image.new("RGBA", (64, 16))
im.putdata([(rng.randrange(256), rng.randrange(256), rng.randrange(256), rng.randrange(256)) for _ in range(64 * 16)])
im.save(os.path.join(out, "rect64x16.png"))
im = Image.new("RGBA", (48, 40))
im.putdata([(rng.randrange(256), rng.randrange(256), rng.randrange(256), 0 if (x // 8 + y // 8) % 3 == 0 else rng.randrange(1, 256))
            for y in range(40) for x in range(48)])
im.save(os.path.join(out, "npot48x40.png"))
im = Image.new("L", (64, 64))
im.putdata([int(127.5 + 127.5 * __import__("math").sin(x * 0.3) * __import__("math").cos(y * 0.2)) for y in range(64) for x in range(64)])
im.save(os.path.join(out, "height64.png"))
im = Image.new("RGBA", (64, 64))
im.putdata([(rng.randrange(256), rng.randrange(256), rng.randrange(256), 0 if (x // 8 + y // 8) % 2 else 255) for y in range(64) for x in range(64)])
im.save(os.path.join(out, "cut64.png"))


def proc(alg, s="", v="0 0 0 0"):
    return (f'\t\t\t\t"CDmeImageProcessor"\n\t\t\t\t{{\n\t\t\t\t\t"m_algorithm" "string" "{alg}"\n'
            f'\t\t\t\t\t"m_stringArg" "string" "{s}"\n\t\t\t\t\t"m_vFloat4Arg" "vector4" "{v}"\n\t\t\t\t}}')


def vtex(inputs, channels, fmt="RGBA8888"):
    ins = []
    for name, file, space, procs in inputs:
        p = ",\n".join(proc(*x) for x in procs)
        ins.append(f'\t\t"CDmeInputTexture"\n\t\t{{\n\t\t\t"m_name" "string" "{name}"\n'
                   f'\t\t\t"m_fileName" "string" "{REL}/{file}"\n\t\t\t"m_colorSpace" "string" "{space}"\n'
                   f'\t\t\t"m_typeString" "string" "2D"\n\t\t\t"m_imageProcessorArray" "element_array"\n\t\t\t[\n'
                   + (p + "\n" if p else "") + "\t\t\t]\n\t\t}")
    chs = []
    for names, src, dst, alg, space in channels:
        n = ", ".join(f'"{x}"' for x in names)
        chs.append(f'\t\t"CDmeTextureOutputChannel"\n\t\t{{\n\t\t\t"m_inputTextureArray" "string_array" [ {n} ]\n'
                   f'\t\t\t"m_srcChannels" "string" "{src}"\n\t\t\t"m_dstChannels" "string" "{dst}"\n'
                   f'\t\t\t"m_mipAlgorithm" "CDmeImageProcessor"\n\t\t\t{{\n\t\t\t\t"m_algorithm" "string" "{alg}"\n'
                   f'\t\t\t\t"m_stringArg" "string" ""\n\t\t\t\t"m_vFloat4Arg" "vector4" "0 0 0 0"\n\t\t\t}}\n'
                   f'\t\t\t"m_outputColorSpace" "string" "{space}"\n\t\t}}')
    return ("<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->\n\"CDmeVtex\"\n{\n\t\"m_inputTextureArray\" \"element_array\"\n\t[\n"
            + ",\n".join(ins) + "\n\t]\n\t\"m_outputTypeString\" \"string\" \"2D\"\n"
            f"\t\"m_outputFormat\" \"string\" \"{fmt}\"\n\t\"m_textureOutputChannelArray\" \"element_array\"\n\t[\n"
            + ",\n".join(chs) + "\n\t]\n}\n")


def one(file, alg, space="linear", procs=(), fmt="RGBA8888", src="rgba"):
    return vtex([("a", file, space, list(procs))], [(["a"], src, src, alg, space)], fmt)


def two(f1, f2, alg, s1="linear", s2="linear", p1=(), p2=(), fmt="RGBA8888", space="linear"):
    return vtex([("a", f1, s1, list(p1)), ("b", f2, s2, list(p2))], [(["a", "b"], "rgba", "rgba", alg, space)], fmt)


P = {
    # Box<N>
    "box1_rgba": one("noisy64.png", "Box1"),
    "box2_rgba": one("noisy64.png", "Box2"),
    "box5_rgba": one("noisy64.png", "Box5"),
    "box9_rgba": one("noisy64.png", "Box9"),
    # MipSize
    "mipwidth_rgba": one("rect64x16.png", "MipWidth"),
    "mipheight_rgba": one("rect64x16.png", "MipHeight"),
    "mipdepth_rgba": one("rect64x16.png", "MipDepth"),
    # the rest of the dispatcher
    "multiplybox_rgba": two("color64.png", "mask64.png", "MultiplyBox"),
    "multiplynomip_rgba": two("color64.png", "mask64.png", "MultiplyNoMip"),
    "rgbm_rgba": one("color64.png", "RGBM"),
    "rgbm_srgb_rgba": one("color64.png", "RGBM", space="srgb"),
    "anisonormal_rgba": two("bumps64.png", "rough64.png", "AnisoNormal"),
    "anisonormalrough_rgba": two("bumps64.png", "rough64.png", "AnisoNormalRoughness"),
    "hemioctnormal_rgba": one("bumps64.png", "HemiOctNormal"),
    "hemiisoadj_rgba": two("bumps64.png", "rough64.png", "HemiOctIsoRoughnessAdjustable_RG_BA"),
    "hemianisoba_rgba": two("bumps64.png", "rough64.png", "HemiOctAnisoRoughness_RG_BA"),
    "alphaweighted_rgba": two("color64.png", "mask64.png", "AlphaWeighted"),
    "gradient2d_rgba": one("height64.png", "Mip2DGradient"),
    "wrapgaussian_rgba": one("noisy64.png", "WrapGaussian"),
    "autolevels_rgba": one("noisy64.png", "AutoLevels"),
    "heightcombine_rgba": two("height64.png", "noisy64.png", "HeightCombine"),
    "sharpen_rgba": one("noisy64.png", "Sharpen"),
    "sharpensoft_rgba": one("noisy64.png", "SharpenSoft"),
    "relaxedcones_rgba": one("height64.png", "RelaxedCones"),
    "depthmin_rgba": one("height64.png", "DepthPyramidMin"),
    "depthmax_rgba": one("height64.png", "DepthPyramidMax"),
    "nice_rgba": one("noisy64.png", "Nice"),
    "sh_rgba": one("color64.png", "SphericalHarmonicsR"),
    "artist_box_rgba": one("noisy64.png", "ArtistDefined"),
    # image processors
    "ycocg_rgba": one("color64.png", "Box", procs=[("ConvertToYCoCg",)]),
    "ycocg_srgb_rgba": one("color64.png", "Box", space="srgb", procs=[("ConvertToYCoCg",)]),
    "ycocg_dxt5": one("color64.png", "Box", procs=[("ConvertToYCoCg",)], fmt="DXT5"),
    "encodergbm_rgba": one("color64.png", "Box", procs=[("EncodeRGBM",)]),
    "fillpow2_rgba": one("npot48x40.png", "Box", procs=[("FillToPowerOfTwo",)]),
    "fillmult4_rgba": one("npot48x40.png", "Box", procs=[("FillToMultipleOfFour",)]),
    "scalepow2_rgba": one("npot48x40.png", "Box", procs=[("ScaleToPowerOfTwo",)]),
    "heighttonormal_rgba": one("height64.png", "Box", procs=[("HeightToNormal",)]),
    "dilate_rgba": one("cut64.png", "Box", procs=[("DilateColorInTransparentPixels",)]),
    "uniformrandom_rgba": one("noisy64.png", "Box", procs=[("UniformRandom32x32",)]),
    # output formats
}
# Groups reading only the channels a red-only / RGB-only / RG-only frame holds (11-04, second run).
P["mipwidth_r"] = one("rect64x16.png", "MipWidth", src="r")
P["mipheight_r"] = one("rect64x16.png", "MipHeight", src="r")
P["mipdepth_r"] = one("rect64x16.png", "MipDepth", src="r")
P["heighttonormal_rgb"] = one("height64.png", "Box", procs=[("HeightToNormal",)], src="rgb")
P["gradient2d_rg"] = one("height64.png", "Mip2DGradient", src="rg")
P["dilate_rgb"] = one("cut64.png", "Box", procs=[("DilateColorInTransparentPixels",)], src="rgb")
for fmt in ("RGBA16161616F", "RGBA16161616", "R32F", "RGB323232F", "RGBA32323232F", "RG1616F", "RG3232F", "R16F", "R16",
            "RG1616", "BC6H", "I8", "IA88", "A8", "BGRA8888", "ABGR8888", "ARGB8888", "BGRX8888", "RGBX8888", "RGB888", "BGR888",
            "RGB565", "BGR565", "BGRA4444", "UV88", "UVWQ8888", "DXT3", "DXT1_ONEBITALPHA", "DXT5_NM", "R11G11B10_FLOAT",
            "RGBA1010102", "BGRA1010102", "R8_UNORM", "R8_UINT", "R9G9B9E5_FLOAT", "RGBA8888_LINEAR", "BC7B"):
    P["fmt_" + fmt.lower()] = one("color64.png", "Box", fmt=fmt)
for name, text in P.items():
    with open(os.path.join(out, name + ".vtex"), "w", newline="\n") as f:
        f.write(text)
print(len(P), "probes in", out)
