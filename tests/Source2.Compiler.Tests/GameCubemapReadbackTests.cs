using Source2.Compiler.Cubemaps;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The step between the face render and the EXR, against the game: the six images a cubehost capture reads back
/// for one cube (tools/cubehost/rdc_readback.py, <c>CUBEREADBACK=&lt;dir&gt;</c>) through
/// <see cref="CubemapFaceExr.Build"/> equal the EXR the game wrote for that cube in the same run
/// (<c>CUBEREADBACK_EXR=&lt;path&gt;</c>, from run_probe.py's intermediates), byte for byte.
/// </summary>
public sealed class GameCubemapReadbackTests(ITestOutputHelper output)
{
    [Fact]
    public void ReadbacksMakeTheGamesExr()
    {
        if (Environment.GetEnvironmentVariable("CUBEREADBACK") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("CUBEREADBACK_EXR") is not { Length: > 0 } exrPath)
            return;
        var game = File.ReadAllBytes(exrPath);
        var gameExr = Vrad3Exr.Read(game);
        var faceSize = gameExr.Height;
        var faces = new List<Half[]>();
        var renderSize = 0;
        for (var f = 0; f < 6; f++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, $"readback_{f}.bin"));
            renderSize = (int)Math.Sqrt(bytes.Length / 8);
            Assert.Equal(renderSize * renderSize * 8, bytes.Length);
            faces.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes).ToArray());
        }
        output.WriteLine($"readbacks {renderSize}^2 RGBA16F; the game's EXR {gameExr.Width} x {gameExr.Height}, channels {string.Join("", gameExr.Channels)}");
        var ours = CubemapFaceExr.Build(faces, renderSize, faceSize);
        if (!ours.AsSpan().SequenceEqual(game))
        {
            var ourExr = Vrad3Exr.Read(ours);
            foreach (var c in gameExr.Channels)
            {
                var a = gameExr.Plane(c);
                var b = ourExr.Plane(c);
                var diff = 0;
                var firstAt = -1;
                for (var i = 0; i < a.Length; i++)
                    if (BitConverter.HalfToUInt16Bits(a[i]) != BitConverter.HalfToUInt16Bits(b[i]))
                    {
                        diff++;
                        if (firstAt < 0)
                            firstAt = i;
                    }
                output.WriteLine($"  {c}: {diff} of {a.Length} halves differ"
                                 + (firstAt < 0 ? "" : $", first at x {firstAt % gameExr.Width} y {firstAt / gameExr.Width} (face {firstAt % gameExr.Width / faceSize}): game {BitConverter.HalfToUInt16Bits(a[firstAt]):x4}, ours {BitConverter.HalfToUInt16Bits(b[firstAt]):x4}"));
            }
            // Where the texels differ, per face: count and bounds in the face's own (unflipped) rows.
            var p0 = gameExr.Plane(gameExr.Channels[0]);
            var q0 = ourExr.Plane(gameExr.Channels[0]);
            for (var f = 0; f < 6; f++)
            {
                int n = 0, sx = -1, sy = -1, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
                for (var y = 0; y < faceSize; y++)
                for (var x = 0; x < faceSize; x++)
                {
                    var i = y * gameExr.Width + f * faceSize + x;
                    if (BitConverter.HalfToUInt16Bits(p0[i]) == BitConverter.HalfToUInt16Bits(q0[i]))
                        continue;
                    if (n++ == 0)
                        (sx, sy) = (x, faceSize - 1 - y);
                    var fy = faceSize - 1 - y;
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, fy), Math.Max(x1, x), Math.Max(y1, fy));
                }
                if (n > 0)
                    output.WriteLine($"  face {f}: {n} texels, x {x0}..{x1}, y {y0}..{y1}, first ({sx}, {sy})");
            }
        }
        Assert.Equal(game, ours);
    }
}
