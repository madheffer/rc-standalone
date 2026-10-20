using System.Runtime.InteropServices;
using Source2.Compiler.Lighting;
using Source2.Compiler.RcTexture;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our bc7e build (<see cref="Bc7e"/>) against the game's own bc7e.dll called in-process
/// (bc7e_init, bc7e_init_params(level, perceptual, params), bc7e_compress(blocks, out, pixels, params)),
/// on the blocks a lightmap build hands the encoder.
/// </summary>
public sealed unsafe class Bc7eOracleTests(ITestOutputHelper output)
{
    private static string Bin => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, "..", "bin", "win64"));

    /// <summary>The game's bc7e.dll at <paramref name="level"/>, or null when it cannot be loaded.</summary>
    internal static Func<byte[], bool, byte[]>? ValveBc7e(int level)
    {
        if (CS2Fixtures.StockPak() is null || !NativeLibrary.TryLoad(Path.Combine(Bin, "bc7e.dll"), out var lib))
            return null;
        var init = (delegate* unmanaged<void>)NativeLibrary.GetExport(lib, "bc7e_init");
        var initParams = (delegate* unmanaged<int, byte, void*, void>)NativeLibrary.GetExport(lib, "bc7e_init_params");
        var compress = (delegate* unmanaged<uint, ulong*, uint*, void*, void>)NativeLibrary.GetExport(lib, "bc7e_compress");
        init();
        return (blocks, perceptual) =>
        {
            var p = stackalloc byte[4096];
            initParams(level, (byte)(perceptual ? 1 : 0), p);
            var n = blocks.Length / 64;
            var o = new byte[n * 16];
            fixed (byte* src = blocks)
            fixed (byte* dst = o)
                compress((uint)n, (ulong*)dst, (uint*)src, p);
            return o;
        };
    }

    /// <summary>
    /// BC7ORACLE=&lt;vrad3 folder&gt;|&lt;map&gt;|&lt;addon&gt;[|name=value;...]: every BC7 block the lightmap
    /// outputs encode, ours against the game's bc7e.dll at level 6 on the same input.
    /// </summary>
    [Fact]
    public void LightmapBlocksMatchTheGamesDll()
    {
        if (Environment.GetEnvironmentVariable("BC7ORACLE") is not { Length: > 0 } spec || ValveBc7e(6) is not { } valve)
            return;
        var p = spec.Split('|');
        var arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (p.Length > 3 ? p[3] : "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=') is [var k, var v])
                arguments[k] = v.Contains('.') ? float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : int.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        var calls = new List<(byte[] Input, byte[] Ours)>();
        Bc7e.Observer = (i, o) => { lock (calls) calls.Add((i, o)); };
        try
        {
            LightmapOutputs.FromVrad3(p[0], p[1], p[2], Bin, [], LightmapOutputs.Options.From(arguments));
        }
        finally
        {
            Bc7e.Observer = null;
        }
        int total = 0, differ = 0;
        foreach (var (input, ours) in calls)
        {
            var theirs = valve(input, false);
            var perceptual = valve(input, true);
            for (var b = 0; b < ours.Length / 16; b++)
            {
                total++;
                if (ours.AsSpan(b * 16, 16).SequenceEqual(theirs.AsSpan(b * 16, 16)))
                    continue;
                if (differ++ < 12)
                    output.WriteLine($"call of {input.Length / 64} blocks, block {b}: ours {Convert.ToHexString(ours, b * 16, 16)} dll {Convert.ToHexString(theirs, b * 16, 16)}"
                                     + $" (perceptual dll {Convert.ToHexString(perceptual, b * 16, 16)}) input {Convert.ToHexString(input, b * 64, 64)}");
            }
        }
        output.WriteLine($"{calls.Count} calls, {total} blocks, {differ} differ from the game's bc7e.dll");
        Assert.Equal(0, differ);
    }
}
