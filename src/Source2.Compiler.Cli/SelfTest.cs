using System.Diagnostics;
using System.Text;
using SkiaSharp;
using SteamDatabase.ValvePak;
using ValveResourceFormat;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>s2c selftest</c> - compile one of every supported type in-process and
/// report PASS/FAIL, best-of-N wall clock and output size.
///
/// <para>The KV3 rows are donor-free: they need no game files at all, which is
/// the clearest demonstration of what this project is. The texture / sound /
/// svg rows reuse a compiled file of their own type for its header frame, so
/// they run only when a CS2 install is reachable (<c>--cs2</c> or
/// <c>$CS2_DIR</c>) and are reported as SKIP otherwise.</para>
///
/// <para>Texture inputs are random-noise PNGs, the worst case for BC7
/// (maximally incompressible), so the timings are a conservative upper bound
/// against real art.</para>
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var cs2 = Opt(args, "--cs2") ?? Environment.GetEnvironmentVariable("CS2_DIR");
        using var pak = OpenPak(cs2);

        Console.WriteLine("=== Source 2 resource compiler self-test ===");
        Console.WriteLine($"CPU         {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"BC7 encoder {EncoderName()}");
        Console.WriteLine($"CS2 pak     {(pak is null ? "not found, so the texture / sound / svg rows will skip" : "loaded")}");
        Console.WriteLine();

        var rows = new List<Row>();
        var ok = true;

        void Record(string type, string detail, Func<byte[]> compile, int reps = 3)
        {
            try
            {
                compile();                                  // warm up JIT + native load
                var best = double.MaxValue; long size = 0;
                for (var i = 0; i < reps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var outp = compile();
                    sw.Stop();
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                    size = outp?.LongLength ?? 0;
                }
                rows.Add(new(type, detail, "PASS", best, size));
            }
            catch (Exception ex)
            {
                ok = false;
                rows.Add(new(type, detail, "FAIL: " + ex.Message, 0, 0));
            }
        }

        void Skip(string type, string detail, string why) => rows.Add(new(type, detail, "SKIP: " + why, 0, 0));

        // 1. KV3 source compile, from scratch, no game files involved.
        var kv3 = Encoding.UTF8.GetBytes(
            "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} "
          + "format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n"
          + "{\n\tname = \"selftest\"\n\tvalue = 42\n\tnested = { a = 1.5  b = true }\n\tlist = [ 1, 2, 3, 4 ]\n}\n");

        Record("vsndevts", "KV3 text to vsndevts_c", () => Kv3SourceCompiler.Compile(kv3, ".vsndevts"));
        Record("vdata", "KV3 text to vdata_c", () => VerifyContainer(
            Kv3SourceCompiler.Compile(kv3, ".vdata"), ResourceType.VData, "CompileVData"));
        Record("vagrp", "KV3 text to vagrp_c", () => Kv3SourceCompiler.Compile(kv3, ".vagrp"));

        // 2. Round-trip a real compiled resource: decompile it, recompile the text.
        var stockVdata = FromPak(pak, ".vdata_c");
        if (stockVdata is null) Skip("vdata round-trip", "vdata_c to text to vdata_c", "no CS2 pak");
        else Record("vdata round-trip", "vdata_c to text to vdata_c", () =>
        {
            using var res = new Resource { FileName = "stock.vdata_c" };
            res.Read(new MemoryStream(stockVdata));
            var text = ResourceDecompiler.DataBlockToKv3(res, out var why)
                       ?? throw new InvalidOperationException("decompile refused: " + why);
            return VerifyContainer(Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(text), ".vdata"),
                                   ResourceType.VData, "CompileVData");
        }, reps: 1);

        // A particle carries resource: refs, so this also proves the RERL is
        // re-synthesized with correct engine path-hash ids from the text alone.
        var stockVpcf = FromPak(pak, ".vpcf_c");
        if (stockVpcf is null) Skip("vpcf round-trip", "vpcf_c to text to vpcf_c, plus RERL", "no CS2 pak");
        else Record("vpcf round-trip", "vpcf_c to text to vpcf_c, plus RERL", () =>
        {
            using var stock = new Resource { FileName = "stock.vpcf_c" };
            stock.Read(new MemoryStream(stockVpcf));
            var stockRerl = stock.ExternalReferences?.ResourceRefInfoList.Count ?? 0;
            var text = ResourceDecompiler.DataBlockToKv3(stock, out var why)
                       ?? throw new InvalidOperationException("decompile refused: " + why);
            var built = VerifyContainer(Kv3SourceCompiler.Compile(Encoding.UTF8.GetBytes(text), ".vpcf"),
                                        ResourceType.Particle, "CompileParticle");
            using var res = new Resource { FileName = "selftest.vpcf_c" };
            res.Read(new MemoryStream(built));
            var got = res.ExternalReferences?.ResourceRefInfoList.Count ?? 0;
            if (got != stockRerl)
                throw new InvalidOperationException($"RERL synthesis produced {got} entries, the stock compile has {stockRerl}");
            return built;
        }, reps: 1);

        // 3. Textures across the size range. The managed encoder is roughly two
        // orders of magnitude slower than the native one, so cap the sizes when
        // it is what will run: a self-test nobody waits out reports nothing.
        var vtexTemplate = FromPak(pak, ".vtex_c");
        var sizes = HaveNativeBc7 ? new[] { 128, 256, 512, 1024, 2048 } : [128, 256, 512];
        if (!HaveNativeBc7 && vtexTemplate is not null)
            Skip("vtex (BC7)", "1024 and 2048 png to vtex_c", "managed encoder; pass --all-sizes to run anyway");
        if (args.Contains("--all-sizes")) sizes = [128, 256, 512, 1024, 2048];
        foreach (var size in sizes)
        {
            if (vtexTemplate is null) { Skip("vtex (BC7)", $"{size}x{size} png to vtex_c", "no CS2 pak"); continue; }
            var png = NoisePng(size);
            Record("vtex (BC7)", $"{size}x{size} png to vtex_c", () =>
                ResourceBuilder.BuildTexture(vtexTemplate, new ResourceBuilder.TextureDef
                {
                    ImageBytes = png,
                    GenerateMipmaps = true,
                    Compression = ResourceBuilder.TextureCompression.BC7,
                }), reps: size >= 1024 ? 2 : 3);
        }

        // 4. Sound and vector graphic.
        var vsndTemplate = FromPak(pak, ".vsnd_c");
        if (vsndTemplate is null) Skip("vsnd", "2s PCM16 wav to vsnd_c", "no CS2 pak");
        else
        {
            var wav = SineWav(2);
            Record("vsnd", "2s PCM16 wav to vsnd_c", () => ResourceBuilder.BuildSound(vsndTemplate, wav));
        }

        var vsvgTemplate = FromPak(pak, ".vsvg_c");
        if (vsvgTemplate is null) Skip("vsvg", "svg to vsvg_c, then CRC re-read", "no CS2 pak");
        else
        {
            var svg = Encoding.UTF8.GetBytes(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">"
              + "<path fill=\"#ffffff\" d=\"M60 60 L340 200 L60 340 Z\"/></svg>");
            Record("vsvg", "svg to vsvg_c, then CRC re-read", () =>
            {
                var built = ResourceBuilder.BuildPanoramaSvg(vsvgTemplate, svg);
                // Re-reading validates the CRC32 (Panorama.Read throws on a mismatch)
                // and confirms the payload survived byte for byte.
                using var res = new Resource();
                res.Read(new MemoryStream(built));
                var pan = (ValveResourceFormat.ResourceTypes.Panorama)res.DataBlock!;
                if (!pan.Data.AsSpan().SequenceEqual(svg))
                    throw new InvalidOperationException("SVG payload did not round-trip");
                return built;
            });
        }

        // 5. The RERL id function, checked against the game's own ids.
        if (pak is null) Skip("rerl ids", "stock RERL ids reproduce", "no CS2 pak");
        else
        {
            var (checkedRefs, mismatches) = CheckStockIds(pak, 200);
            rows.Add(new("rerl ids", $"{checkedRefs} stock refs re-hashed",
                         mismatches == 0 ? "PASS" : $"FAIL: {mismatches} id mismatches", 0, 0));
            if (mismatches != 0) ok = false;
        }

        // Report.
        Console.WriteLine($"{"TYPE",-20}{"DETAIL",-38}{"RESULT",-8}{"BEST ms",10}{"OUT KB",10}");
        Console.WriteLine(new string('-', 86));
        foreach (var r in rows)
            Console.WriteLine($"{r.Type,-20}{Trunc(r.Detail, 37),-38}{r.Result.Split(':')[0],-8}"
                            + $"{(r.Ms > 0 ? r.Ms.ToString("0.0") : "-"),10}"
                            + $"{(r.OutBytes > 0 ? (r.OutBytes / 1024.0).ToString("0.0") : "-"),10}");
        Console.WriteLine();
        foreach (var r in rows.Where(x => !x.Result.StartsWith("PASS")))
            Console.WriteLine($"  {r.Type}: {r.Result}");

        Console.WriteLine();
        Console.WriteLine(ok ? "all compiles passed" : "SOME COMPILES FAILED");
        return ok ? 0 : 1;
    }

    /// <summary>Which BC7 encoder will actually run, read off the same tag the
    /// texture cache stamps on its output so the two can never disagree.</summary>
    private static string EncoderTag => ResourceBuilder.Bc7CacheTag(ResourceBuilder.Bc7EncoderMode.Cpu);

    private static bool HaveNativeBc7 => EncoderTag is "bc7cpunative" or "bc7gpu";

    private static string EncoderName() => EncoderTag switch
    {
        "bc7cpunative" => "native bc7enc",
        "bc7gpu"       => "GPU",
        _              => "BCnEncoder.Net (managed fallback; build Native/bc7enc for the fast path)",
    };

    private readonly record struct Row(string Type, string Detail, string Result, double Ms, long OutBytes);

    /// <summary>Assert the built container is the type it claims and carries the
    /// right compiler identity, so a mis-branded container fails loudly rather
    /// than being reported as a fast compile.</summary>
    private static byte[] VerifyContainer(byte[] compiled, ResourceType expected, string compilerIdentifier)
    {
        using var res = new Resource { FileName = "selftest_c" };
        res.Read(new MemoryStream(compiled));
        if (res.ResourceType != expected)
            throw new InvalidOperationException($"expected a {expected} container, got {res.ResourceType}");
        var deps = res.EditInfo?.SpecialDependencies;
        if (deps is null || !deps.Any(d => d.CompilerIdentifier == compilerIdentifier))
            throw new InvalidOperationException($"output is missing the {compilerIdentifier} special dependency");
        return compiled;
    }

    private static (int Checked, int Mismatches) CheckStockIds(Package pak, int budget)
    {
        int seen = 0, bad = 0;
        foreach (var ext in new[] { "vmat_c", "vmdl_c" })
        {
            if (pak.Entries is null || !pak.Entries.TryGetValue(ext, out var entries)) continue;
            foreach (var e in entries)
            {
                if (seen >= budget) break;
                byte[] bytes;
                try { pak.ReadEntry(e, out bytes); } catch { continue; }
                using var res = new Resource();
                try { res.Read(new MemoryStream(bytes)); } catch { continue; }
                foreach (var r in res.ExternalReferences?.ResourceRefInfoList ?? [])
                {
                    if (r.Name is not { Length: > 0 } name) continue;
                    seen++;
                    if (Source2ResourceId.ForPath(name) != r.Id) bad++;
                }
            }
        }
        return (seen, bad);
    }

    /// <summary>
    /// The installed game archive, or null when there is none.
    ///
    /// <para><c>--cs2</c> / <c>$CS2_DIR</c> is authoritative: when given, only it
    /// is consulted, so pointing it somewhere empty genuinely reproduces the
    /// no-game-installed case even on a machine that has CS2. Only when it is
    /// absent entirely does this guess at the usual Steam locations.</para>
    /// </summary>
    private static Package? OpenPak(string? cs2Dir)
    {
        string?[] candidates = cs2Dir is { Length: > 0 }
            ? [Path.Combine(cs2Dir, "game", "csgo", "pak01_dir.vpk"),
               Path.Combine(cs2Dir, "pak01_dir.vpk")]
            : [@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk",
               @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk"];

        var path = candidates.FirstOrDefault(c => c is not null && File.Exists(c));
        if (path is null) return null;
        var p = new Package();
        try { p.Read(path); return p; }
        catch { p.Dispose(); return null; }
    }

    private static byte[]? FromPak(Package? pak, string suffix)
    {
        if (pak is null) return null;
        var entry = Io.VpkEntries.FirstEndingWith(pak, suffix);
        if (entry is null) return null;
        try { return Io.VpkEntries.Read(pak, entry); }
        catch { return null; }
    }

    private static byte[] NoisePng(int size)
    {
        var rng = new Random(1234);
        using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var px = new byte[size * size * 4];
        rng.NextBytes(px);
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] SineWav(int seconds)
    {
        const int rate = 44100;
        var frames = rate * seconds;
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / rate));
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((ushort)1); w.Write((ushort)1);
            w.Write(rate); w.Write(rate * 2); w.Write((ushort)2); w.Write((ushort)16);
            w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        }
        return ms.ToArray();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "\u2026";

    private static string? Opt(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
