using System.Buffers.Binary;
using System.Text.Json;
using Source2.Compiler.Compression;
using Source2.Compiler.Cubemaps;
using Source2.Compiler.Gpu;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The cubemap array against resourcecompiler's compile of known inputs (tools/re/cubemap_oracle.py's
/// folder, <c>CUBEORACLE=&lt;dir&gt;</c>, CS2 closed when it was made):
/// <list type="bullet">
/// <item>layout: the faces RC cut from each candidate EXR layout (mip None, RGBA32F) against
/// <see cref="CubemapArrayVtex.FacesFromStrip"/>;</item>
/// <item>float: the GGX mips RC wrote as RGBA32F (DX11 and -vulkan) against <see cref="GpuCubemapBlur"/>
/// on the same EXRs, float for float, and the radiance SH and reflectivity;</item>
/// <item>bc6h: the whole file against <see cref="CubemapArrayVtex.Build"/>.</item>
/// </list>
/// </summary>
public sealed class CubemapOracleTests(ITestOutputHelper output)
{
    private sealed record Texture(int Size, int Depth, int Mips, byte Format, ushort Flags, float[] Reflectivity, float[] Sh, byte[][] Mip);

    [Fact]
    public void AgainstResourceCompiler()
    {
        if (Environment.GetEnvironmentVariable("CUBEORACLE") is not { Length: > 0 } dir || CS2Fixtures.StockPak() is not { } stock)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "bin", "win64"));
        var shaderVpk = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "core", "shaders_vulkan_dir.vpk"));
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json")));
        var layout = results.RootElement.GetProperty("layout").GetString();
        output.WriteLine($"layout RC took: {layout ?? "none"}");

        // The face split: each successful layout compile's mip 0 against the strip cut of the marker EXR.
        foreach (var f in Directory.GetFiles(dir, "layout_*_dx11.vtex_c"))
        {
            var name = Path.GetFileName(f)["layout_".Length..^"_dx11.vtex_c".Length];
            var rc = Read(File.ReadAllBytes(f));
            var exr = LightmapTextures.Bitmap(Vrad3Exr.Read(File.ReadAllBytes(Path.Combine(dir, $"env_cubemap_{name}.exr"))));
            var faces = RcFaces(rc, 0, 0);
            var labels = string.Join(" ", faces.Select(face => $"{face[0]:G3}@({face[1]:G2},{face[2]:G2})"));
            output.WriteLine($"layout {name}: RC's face texel 0 holds (face + 1 @ x, y): {labels}");
            if (name == "strip_6x1")
            {
                var ours = CubemapArrayVtex.FacesFromStrip(exr);
                output.WriteLine($"  6 x 1 strip cut: {(faces.Zip(ours).All(p => p.First.AsSpan().SequenceEqual(p.Second)) ? "equal" : "DIFFERENT")}");
            }
        }
        if (layout != "strip_6x1")
        {
            output.WriteLine("RC did not take the 6 x 1 strip first: port the layout it took before the rest.");
            return;
        }

        var cubes = Directory.GetFiles(dir, "env_cubemap_1*.exr").Order(StringComparer.Ordinal)
            .Select(p => (Path: p, Faces: CubemapArrayVtex.FacesFromStrip(LightmapTextures.Bitmap(Vrad3Exr.Read(File.ReadAllBytes(p)))))).ToList();
        var size = results.RootElement.GetProperty("size").GetInt32();
        using var blur = new GpuCubemapBlur(shaderVpk);
        var filtered = cubes.Select(c => blur.Filter(c.Faces, size)).ToList();

        foreach (var api in new[] { "dx11", "vulkan" })
        {
            var path = Path.Combine(dir, $"float_{api}.vtex_c");
            if (!File.Exists(path))
                continue;
            var rc = Read(File.ReadAllBytes(path));
            long floats = 0, differ = 0;
            for (var m = 1; m < rc.Mips; m++)
                for (var c = 0; c < cubes.Count; c++)
                {
                    var theirs = RcFaces(rc, m, c);
                    for (var f = 0; f < 6; f++)
                        for (var i = 0; i < theirs[f].Length; i++)
                        {
                            floats++;
                            if (BitConverter.SingleToInt32Bits(theirs[f][i]) != BitConverter.SingleToInt32Bits(filtered[c][m][f][i]) && differ++ < 5)
                                output.WriteLine($"  {api} mip {m} cube {c} face {f} float {i}: ours {filtered[c][m][f][i]:R}, RC {theirs[f][i]:R}");
                        }
                }
            output.WriteLine($"float {api}: {rc.Mips} mips, {floats - differ} of {floats} filtered floats equal");
            var sh = cubes.SelectMany(c => CubemapRadianceSh.Project(c.Faces.Select(f => (Plane(f, 0), Plane(f, 1), Plane(f, 2))).ToList(), size)).ToArray();
            var shEqual = sh.Zip(rc.Sh).Count(p => BitConverter.SingleToInt32Bits(p.First) == BitConverter.SingleToInt32Bits(p.Second));
            output.WriteLine($"  SH: {shEqual} of {rc.Sh.Length} floats equal; reflectivity RC {string.Join(" ", rc.Reflectivity.Select(x => x.ToString("R")))}");
        }

        var bc6h = Path.Combine(dir, "bc6h_vulkan.vtex_c");
        if (File.Exists(bc6h))
        {
            var valve = File.ReadAllBytes(bc6h);
            var red2 = WorldPhysicsAuthorTests.Trees(valve)["RED2"];
            var deps = red2["m_InputDependencies"].Values.ToList();
            var searchPath = (string)deps[0]["m_SearchPath"]!;
            var vtexPath = (string)deps[^1]["m_RelativeFilename"]!;
            var vtexBytes = File.ReadAllBytes(Path.Combine(dir, "bc6h_vulkan.vtex"));
            var inputs = cubes.Select(c => new CubemapArrayVtex.Cube($"_buildcubemaps/{Path.GetFileName(c.Path)}", File.ReadAllBytes(c.Path), c.Faces)).ToList();
            var queue = new Queue<float[][][]>(filtered);
            var ours = CubemapArrayVtex.Build(inputs, size, _ => queue.Dequeue(), vtexPath, vtexBytes, searchPath, bin);
            var first = 0;
            while (first < Math.Min(ours.Length, valve.Length) && ours[first] == valve[first])
                first++;
            output.WriteLine($"bc6h -vulkan: ours {ours.Length} bytes, RC's {valve.Length}, first difference at {(first == valve.Length && ours.Length == valve.Length ? "none" : first.ToString())}");
            foreach (var line in WorldPhysicsAuthorTests.Compare(valve, ours).Take(10))
                output.WriteLine("  " + line);
        }
    }

    /// <summary>
    /// A dry run with no oracle (<c>CUBEBUILD=&lt;dir of env_cubemap_*.exr, 6 x 1 strips&gt;</c>): the whole array built
    /// as <see cref="CubemapArrayVtex.Build"/> does it, written beside them and read back by VRF.
    /// </summary>
    [Fact]
    public void BuildFromExrs()
    {
        if (Environment.GetEnvironmentVariable("CUBEBUILD") is not { Length: > 0 } dir || CS2Fixtures.StockPak() is not { } stock)
            return;
        var bin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "bin", "win64"));
        var shaderVpk = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stock)!, "..", "core", "shaders_vulkan_dir.vpk"));
        var inputs = Directory.GetFiles(dir, "env_cubemap_*.exr").Order(StringComparer.Ordinal).Select(p =>
        {
            var bytes = File.ReadAllBytes(p);
            return new CubemapArrayVtex.Cube($"_buildcubemaps/{Path.GetFileName(p)}", bytes, CubemapArrayVtex.FacesFromStrip(LightmapTextures.Bitmap(Vrad3Exr.Read(bytes))));
        }).ToList();
        var size = inputs[0].Faces[0].Length / 4 is var n ? (int)Math.Sqrt(n) : 0;
        using var blur = new GpuCubemapBlur(shaderVpk);
        var started = DateTime.UtcNow;
        var file = CubemapArrayVtex.Build(inputs, size, faces => blur.Filter(faces, size), "_buildcubemaps/env_cubemap_array.vtex",
            "dry run"u8.ToArray(), "csgo_addons/s2c_cubeprobe", bin);
        File.WriteAllBytes(Path.Combine(dir, "ours.vtex_c"), file);
        var t = Read(file);
        output.WriteLine($"{inputs.Count} cubes of {size}: {file.Length} bytes in {(DateTime.UtcNow - started).TotalSeconds:F1} s; {t.Mips} mips, flags 0x{t.Flags:x}, SH {t.Sh.Length}, reflectivity {string.Join(" ", t.Reflectivity)}");
        using var resource = new ValveResourceFormat.Resource();
        resource.Read(new MemoryStream(file));
        var texture = (ValveResourceFormat.ResourceTypes.Texture)resource.DataBlock!;
        output.WriteLine($"VRF: {texture.Width} x {texture.Height} x {texture.Depth}, {texture.Format}, {texture.NumMipLevels} mips, {texture.Flags}");
    }

    private static float[] Plane(float[] rgba, int channel)
    {
        var plane = new float[rgba.Length / 4];
        for (var i = 0; i < plane.Length; i++)
            plane[i] = rgba[i * 4 + channel];
        return plane;
    }

    // The six RGBA32F faces of cube c at mip m.
    private static float[][] RcFaces(Texture t, int m, int c)
    {
        Assert.Equal(14, t.Format);
        var side = Math.Max(1, t.Size >> m);
        var faceBytes = side * side * 16;
        return Enumerable.Range(0, 6).Select(f =>
        {
            var floats = new float[side * side * 4];
            Buffer.BlockCopy(t.Mip[m], (c * 6 + f) * faceBytes, floats, 0, faceBytes);
            return floats;
        }).ToArray();
    }

    private static Texture Read(byte[] file)
    {
        var blockOffset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        byte[]? data = null;
        var end = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 8 + blockOffset + i * 12;
            var offset = at + 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 8));
            if (System.Text.Encoding.ASCII.GetString(file, at, 4) == "DATA")
                (data, end) = (file.AsSpan(offset, size).ToArray(), offset + size);
        }
        Assert.NotNull(data);
        var d = data!;
        var reflectivity = Enumerable.Range(0, 4).Select(c => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(4 + 4 * c))).ToArray();
        int side = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(20)), depth = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(24)), mips = d[27];
        var format = d[26];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        int[]? stored = null;
        var lz4 = false;
        var sh = Array.Empty<float>();
        var extraAt = 32 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(32));
        for (var e = 0; e < BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(36)); e++)
        {
            var entry = extraAt + e * 12;
            var type = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(entry));
            var payload = entry + 4 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(entry + 4));
            if (type == 4)
            {
                lz4 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(payload)) != 0;
                var sizesAt = payload + 4 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(payload + 4));
                var n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(payload + 8));
                stored = Enumerable.Range(0, n).Select(m => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(sizesAt + 4 * m))).ToArray();
            }
            else if (type == 5)
            {
                var floatsAt = payload + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(payload));
                var n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(payload + 4));
                sh = Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(floatsAt + 4 * i))).ToArray();
            }
        }
        var bytesPerTexel = format == 14 ? 16 : 1;
        var mip = new byte[mips][];
        var at2 = end;
        for (var m = mips - 1; m >= 0; m--)
        {
            var s = Math.Max(format == 19 ? 4 : 1, side >> m);
            var raw = s * s * bytesPerTexel * 6 * depth;
            var len = stored?[m] ?? raw;
            mip[m] = new byte[raw];
            if (len == raw)
                file.AsSpan(at2, raw).CopyTo(mip[m]);
            else
                Lz4.Decode(file.AsSpan(at2, len), mip[m]);
            at2 += len;
        }
        _ = lz4;
        return new Texture(side, depth, mips, format, flags, reflectivity, sh, mip);
    }
}
