using System.Text;
using Source2.Compiler;
using Source2.Compiler.Cli;
using ValveResourceFormat;

// s2c - drive the standalone Source 2 resource compiler from a shell.
//
// Nothing here shells out. Every subcommand is a direct call into
// Source2.Compiler, which is the whole point: these are compiles that would
// otherwise need Valve's resourcecompiler.exe and a Workshop Tools install.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

try
{
    return args[0] switch
    {
        "compile"   => Commands.Compile(args[1..]),
        "decompile" => Commands.Decompile(args[1..]),
        "texture"   => Commands.Texture(args[1..]),
        "sheet"     => Commands.Sheet(args[1..]),
        "svg"       => Commands.Svg(args[1..]),
        "sound"     => Commands.Sound(args[1..]),
        "id"        => Commands.ResourceId(args[1..]),
        "inspect"   => Commands.Inspect(args[1..]),
        "selftest"  => SelfTest.Run(args[1..]),
        _           => Unknown(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"unknown command '{cmd}'");
    Usage();
    return 2;
}

static void Usage()
{
    Console.WriteLine("""
    s2c - a Source 2 resource compiler that does not use resourcecompiler.exe

      s2c compile   <in.vdata|.vsndevts|.vpcf|.vagrp> [-o <out_c>] [--ship-as <path>]
                    KV3 text source -> compiled container, authored from scratch.

      s2c decompile <in.*_c> [-o <out.txt>]
                    Compiled resource -> KV3 text (the DATA block's tree).

      s2c texture   <image.png|jpg|...> -o <out.vtex_c> [--template <any.vtex_c>]
                    [--format bc7|bc5|bc4|bc3|bc1|rgba] [--no-mips] [--max-dim N]
                    Image -> compiled texture: mip chain, block compression and
                    the full stock extradata set the CS2 streamer expects.
                    Needs no template; pass one only to carry its encoding
                    semantics (a packed normal map's mip algorithm, say).

      s2c sheet     <in.mks> [-o <out.vtex_c>] [--format ...] [--ship-as <path>]
                    Sprite-sheet script -> animated texture: packs the frames
                    into an atlas and writes the SHEET block a particle system
                    animates through. Frame images resolve next to the script.

      s2c svg       <in.svg> -o <out.vsvg_c> --template <any.vsvg_c>
                    Raw SVG -> compiled Panorama vector graphic.

      s2c sound     <in.wav> -o <out.vsnd_c> --template <any.vsnd_c>
                    Uncompressed PCM WAV -> compiled sound container.

      s2c id        <resource/path.vtex>
                    Print the 64-bit RERL id the engine looks that path up by.

      s2c inspect   <in.*_c>
                    Container facts: resource version, blocks, RED2 compiler
                    identity + input dependencies, RERL entries and their ids.

      s2c selftest  [--cs2 <CS2 install dir>]
                    Compile one of every supported type and report PASS/timing.
                    The donor-free KV3 rows always run; the texture / sound /
                    svg rows need a structural template, which is lifted from
                    the game's own pak01 when --cs2 (or $CS2_DIR) is given.

    Templates: the binary types (vtex/vsnd/vsvg) reuse an existing compiled file
    of the same type for its header frame only - never for its metadata, which is
    authored fresh. Any file of that type works, including one from your own
    CS2 install. See the README, and docs/RC_PARITY.md for the measured
    differences against resourcecompiler.exe.
    """);
}
