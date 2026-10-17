using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// resourcecompiler's light probe atlas images (LPVAtlas 1): every volume's
/// lpv_&lt;id&gt;_ambientcube.exr and lpv_&lt;id&gt;_dlshd.exr copied into one
/// atlas at the places <see cref="ProbeAtlas.Pack"/> gives (one-based, past
/// the border), the atlas zero-filled first, volumes in order (a later one
/// overwrites).
/// <list type="bullet">
/// <item>Ambient cube (1801f64f0): (X Z) by (Y 7), seven bands. Per axis the
/// atlas range from (place - 1) &amp; ~3 to (place + size + 1 + 3) &amp; ~3 (4
/// for 0), each texel the volume's texel at clamp(atlas - place, 0, size - 1).</item>
/// <item>Direct light shadows (1801f69c0): (X Z) by Y; the range place - 1
/// to place + size, clamped the same way.</item>
/// </list>
/// The atlas size is <see cref="ProbeAtlas.PackWithSize"/>'s: the allocator's dims
/// of the last fit rounded up to 4, which can exceed the packed boxes' extent
/// (<see cref="Size"/>, kept for callers that only have the places).
/// </summary>
public static class LightProbeAtlasImages
{
    /// <summary>One volume: its grid size, its atlas place, and its two images (RGBA floats).</summary>
    public sealed record Volume(int X, int Y, int Z, ProbeAtlas.Place Place, float[] AmbientCube, float[] Shadows);

    /// <summary>The packed boxes' extent (each box's place - 1 plus (size + 5) &amp; ~3 per axis); not the atlas size when there are two volumes or more.</summary>
    public static (int X, int Y, int Z) Size(IReadOnlyList<Volume> volumes)
    {
        static int Cells(int size) => size == -2 ? 4 : (size + 5) & ~3;
        int ax = 0, ay = 0, az = 0;
        foreach (var v in volumes)
        {
            ax = Math.Max(ax, v.Place.X - 1 + Cells(v.X));
            ay = Math.Max(ay, v.Place.Y - 1 + Cells(v.Y));
            az = Math.Max(az, v.Place.Z - 1 + Cells(v.Z));
        }
        return (ax, ay, az);
    }

    public static float[] AmbientCube((int X, int Y, int Z) atlas, IReadOnlyList<Volume> volumes)
    {
        var w = atlas.X * atlas.Z;
        var image = new float[w * atlas.Y * 7 * 4];
        static int End(int place, int size) => place + size + 1 is 0 ? 4 : (place + size + 1 + 3) & ~3;
        foreach (var v in volumes)
        {
            int x0 = (v.Place.X - 1) & ~3, y0 = (v.Place.Y - 1) & ~3, z0 = (v.Place.Z - 1) & ~3;
            int x1 = End(v.Place.X, v.X), y1 = End(v.Place.Y, v.Y), z1 = End(v.Place.Z, v.Z);
            var vw = v.X * v.Z;
            for (var z = z0; z < z1; z++)
                for (var band = 0; band < 7; band++)
                    for (var y = y0; y < y1; y++)
                        for (var x = x0; x < x1; x++)
                        {
                            var sx = Math.Min(Math.Max(x - v.Place.X, 0), v.X - 1);
                            var sy = Math.Min(Math.Max(y - v.Place.Y, 0), v.Y - 1);
                            var sz = Math.Min(Math.Max(z - v.Place.Z, 0), v.Z - 1);
                            var from = ((v.Y * band + sy) * vw + sz * v.X + sx) * 4;
                            var to = ((band * atlas.Y + y) * w + z * atlas.X + x) * 4;
                            Array.Copy(v.AmbientCube, from, image, to, 4);
                        }
        }
        return image;
    }

    public static float[] Shadows((int X, int Y, int Z) atlas, IReadOnlyList<Volume> volumes)
    {
        var w = atlas.X * atlas.Z;
        var image = new float[w * atlas.Y * 4];
        foreach (var v in volumes)
        {
            var vw = v.X * v.Z;
            for (var dz = -1; dz < v.Z + 1; dz++)
                for (var dy = -1; dy < v.Y + 1; dy++)
                    for (var dx = -1; dx < v.X + 1; dx++)
                    {
                        var sx = Math.Min(Math.Max(dx, 0), v.X - 1);
                        var sy = Math.Min(Math.Max(dy, 0), v.Y - 1);
                        var sz = Math.Min(Math.Max(dz, 0), v.Z - 1);
                        var from = (sy * vw + sz * v.X + sx) * 4;
                        var to = ((v.Place.Y + dy) * w + (v.Place.Z + dz) * atlas.X + v.Place.X + dx) * 4;
                        Array.Copy(v.Shadows, from, image, to, 4);
                    }
        }
        return image;
    }

    /// <summary>An EXR's channels as RGBA floats (R, G, B, A interleaved; a missing channel 0).</summary>
    public static float[] Rgba(Vrad3Exr exr)
    {
        var n = exr.Width * exr.Height;
        var rgba = new float[n * 4];
        var names = new[] { "R", "G", "B", "A" };
        for (var c = 0; c < 4; c++)
        {
            if (!exr.Channels.Contains(names[c]))
                continue;
            var plane = exr.Plane(names[c]);
            for (var i = 0; i < n; i++)
                rgba[i * 4 + c] = (float)plane[i];
        }
        return rgba;
    }
}
