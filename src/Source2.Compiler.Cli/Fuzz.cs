using System.Text;
using ValvePak;
using ValveResourceFormat;
using Source2.Compiler.Anim;
using Source2.Compiler.Kv3;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>fuzz &lt;pak01_dir.vpk&gt; [--iterations N] [--seed S] [--out DIR] [--only NAME]</c>: mutates real
/// inputs (stock files from the pak, a generated PNG, WAV, SVG and KV3 text) and drives every
/// entry point a host hands untrusted files to. A managed exception is the right answer to a
/// bad file; what this hunts is the rest: a call that runs past its time limit, or one that
/// allocates without bound. (A stack overflow ends the process, which shows as this command not
/// finishing; the input that did it is the last one written to <c>--out</c> before the call.)
/// </summary>
internal static class Fuzz
{
    private sealed record Target(string Name, byte[][] Seeds, Action<byte[]> Run);

    public static int Run(string[] args)
    {
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var iterations = int.Parse(Opt("--iterations") ?? "300");
        var rng = new Random(int.Parse(Opt("--seed") ?? "1"));
        var outDir = Opt("--out") ?? Path.Combine(Path.GetTempPath(), "s2c-fuzz");
        var only = Opt("--only");
        Directory.CreateDirectory(outDir);
        using var pak = new Package();
        pak.Read(args[0]);

        byte[][] Stock(string ext, int count) =>
            [.. pak.Entries[ext].Where((_, i) => i % 97 == 0).Take(count).Select(e => { pak.ReadEntry(e, out var b); return b; })];
        var kv3Texts = new[] { Kv3Sample(), "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n{\n\tm_a = \"x\"\n\tm_b = [ 1, 2.5, \"\"\"multi\nline\"\"\", #[ 00 01 ff ] ]\n\tm_c = resource:\"materials/x.vmat\"\n}\n" }
            .Select(Encoding.UTF8.GetBytes).ToArray();
        var models = Stock("vmdl_c", 12);
        var targets = new List<Target>
        {
            new("compile vdata", kv3Texts, b => Kv3SourceCompiler.Compile(b, ".vdata", "scripts/f.vdata")),
            new("compile vsndevts", kv3Texts, b => Kv3SourceCompiler.Compile(b, ".vsndevts", "soundevents/f.vsndevts")),
            new("decompile vpcf", Stock("vpcf_c", 12), b => Decompile(b, "f.vpcf_c")),
            new("decompile vmat", Stock("vmat_c", 12), b => Decompile(b, "f.vmat_c")),
            new("serialize vmat", Stock("vmat_c", 12), b => Reserialize(b, "f.vmat_c", r => ResourceBuilder.Serialize(r))),
            new("serialize vpcf", Stock("vpcf_c", 12), b => Reserialize(b, "f.vpcf_c", r => ResourceBuilder.Serialize(r))),
            new("serialize vmdl", models, b => Reserialize(b, "f.vmdl_c", r => ResourceBuilder.Serialize(r))),
            new("data-only vmdl", models, b => Reserialize(b, "f.vmdl_c", r => ResourceBuilder.SerializeModelDataOnly(r, b))),
            new("swap phys vmdl", models, b => ResourceBuilder.SwapPhysBlockBinary(b, b.AsSpan(0, Math.Min(64, b.Length)).ToArray())),
            new("analyze physics", models, b => ResourceBuilder.AnalyzePhysics(b)),
            new("resourcefile vmdl", models, b => ResourceFile.Read(b).Write()),
            new("resourcefile vtex", Stock("vtex_c", 12), b => ResourceFile.Read(b).Write()),
            new("resourcefile vpcf", Stock("vpcf_c", 12), b => { var f = ResourceFile.Read(b); if (f["RERL"] is { } r) ResourceRefs.Read(r.Data); f.Write(); }),
            new("particle paths", Stock("vpcf_c", 12), b => ResourceBuilder.RewriteParticlePaths(b, new Dictionary<string, string> { ["materials/"] = "materials/x/" })),
            new("modernize vsnd", Stock("vsnd_c", 12), b => ResourceBuilder.ModernizeVsnd(b)),
            new("kv3tree clip", Stock("vnmclip_c", 12), b => ClipRetime(b)),
            new("texture png", [Png(64, 64), Png(5, 300)], b => ResourceBuilder.BuildTexture(new ResourceBuilder.TextureDef { ImageBytes = b })),
            new("sound wav", [Wav()], b => ResourceBuilder.BuildSound(b)),
            new("svg", [Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"#f00\"/><path d=\"M0 0L16 16\"/></svg>")],
                b => ResourceBuilder.BuildPanoramaSvg(b)),
            new("mks", [Encoding.UTF8.GetBytes("sequence 0\nloop\nframe a.png 1\nframe b.png 2\n")], b => MksSource.Parse(b)),
        };

        var failed = 0;
        foreach (var t in targets.Where(t => only is null || t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
        {
            var exceptions = new Dictionary<string, int>();
            int ok = 0, hangs = 0, hogs = 0;
            for (var i = 0; i < iterations; i++)
            {
                var input = Mutate(t.Seeds[rng.Next(t.Seeds.Length)], rng);
                var file = Path.Combine(outDir, $"{t.Name.Replace(' ', '_')}.last");
                File.WriteAllBytes(file, input);
                var before = GC.GetTotalAllocatedBytes();
                var task = Task.Run(() => t.Run(input));
                bool finished;
                try { finished = task.Wait(TimeSpan.FromSeconds(20)); }
                catch (AggregateException) { finished = true; }
                if (!finished && !task.IsCompleted)
                {
                    hangs++;
                    File.Copy(file, Path.Combine(outDir, $"{t.Name.Replace(' ', '_')}.hang{hangs}"), overwrite: true);
                    continue;
                }
                var allocated = GC.GetTotalAllocatedBytes() - before;
                if (allocated > 2L << 30)
                {
                    hogs++;
                    File.Copy(file, Path.Combine(outDir, $"{t.Name.Replace(' ', '_')}.hog{hogs}"), overwrite: true);
                }
                if (task.Exception is { } ex)
                {
                    var inner = ex.InnerException ?? ex;
                    var key = inner.GetType().Name;
                    exceptions[key] = exceptions.GetValueOrDefault(key) + 1;
                }
                else ok++;
            }
            Console.WriteLine($"{t.Name}: {iterations} inputs, {ok} accepted, {hangs} hung, {hogs} allocated over 2 GB; exceptions: "
                + string.Join(", ", exceptions.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
            failed += hangs + hogs;
        }
        return failed == 0 ? 0 : 1;
    }

    private static byte[] Mutate(byte[] seed, Random rng)
    {
        var b = seed.ToList();
        var edits = rng.Next(1, 9);
        for (var e = 0; e < edits && b.Count > 0; e++)
        {
            var at = rng.Next(b.Count);
            switch (rng.Next(6))
            {
                case 0: b[at] ^= (byte)(1 << rng.Next(8)); break;
                case 1: b[at] = (byte)rng.Next(256); break;
                case 2: b.RemoveRange(at, Math.Min(b.Count - at, rng.Next(1, 64))); break;
                case 3: b.InsertRange(at, Enumerable.Range(0, rng.Next(1, 32)).Select(_ => (byte)rng.Next(256))); break;
                case 4: b.RemoveRange(at, b.Count - at); break;   // truncate
                case 5: // a large plausible integer where a size or count lives
                    var v = BitConverter.GetBytes(rng.Next(2) == 0 ? int.MaxValue - rng.Next(1000) : rng.Next(1 << 20, 1 << 30));
                    for (var k = 0; k < 4 && at + k < b.Count; k++) b[at + k] = v[k];
                    break;
            }
        }
        return [.. b];
    }

    private static void Decompile(byte[] bytes, string name)
    {
        using var res = new Resource { FileName = name };
        res.Read(new MemoryStream(bytes));
        ResourceDecompiler.DataBlockToKv3(res, out _);
    }

    private static void Reserialize(byte[] bytes, string name, Func<Resource, byte[]> write)
    {
        using var res = new Resource { FileName = name };
        res.Read(new MemoryStream(bytes));
        write(res);
    }

    private static void ClipRetime(byte[] bytes) => NmClipEdit.Rewrite(bytes, t => NmClipEdit.Retime(t, 1.5));

    private static string Kv3Sample() =>
        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n"
        + "{\n\tgeneric_data_type = \"prop_data\"\n\tk = { base = \"\" blocklos = false n = [ 0, 1, 5000000000 ] f = 0.5 }\n\ts = \"false\"\n}\n";

    private static byte[] Png(int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SkiaSharp.SKColor((byte)(x * 7), (byte)(y * 5), (byte)(x ^ y), 255));
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Wav()
    {
        var samples = 4410;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(3000 * ((i % 100) / 50.0 - 1)));
        return ms.ToArray();
    }
}
