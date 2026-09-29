using SkiaSharp;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>TEXDUMP=&lt;vpk&gt;|&lt;path substring&gt;|&lt;out dir&gt;</c>): every
/// texture in a package whose path holds the substring, decoded to PNG, with
/// its format, size and average colour (for checking what a map renders from).
/// </summary>
public class TextureDumpProbe(ITestOutputHelper output)
{
    /// <summary><c>TEXFILE=&lt;file.vtex_c&gt;[;...]</c>: loose texture files, format, size, average and corner colour.</summary>
    [Fact]
    public void Files()
    {
        if (Environment.GetEnvironmentVariable("TEXFILE") is not { Length: > 0 } spec)
            return;
        foreach (var file in spec.Split(';'))
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(File.ReadAllBytes(file)));
            var texture = (Texture)resource.DataBlock!;
            using var bitmap = texture.GenerateBitmap();
            double r = 0, g = 0, b = 0, a = 0;
            var n = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    (r, g, b, a) = (r + c.Red, g + c.Green, b + c.Blue, a + c.Alpha);
                    n++;
                }
            output.WriteLine($"{Path.GetFileName(file)}: {texture.Format} {texture.Width}x{texture.Height} mips {texture.NumMipLevels} flags {texture.Flags}, average rgba {r / n:F1} {g / n:F1} {b / n:F1} {a / n:F1}, pixel0 {bitmap.GetPixel(0, 0)}");
        }
    }

    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("TEXDUMP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        Directory.CreateDirectory(p[2]);
        using var package = new Package();
        package.Read(p[0]);
        foreach (var entry in package.Entries.GetValueOrDefault("vtex_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains(p[1], StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var texture = (Texture)resource.DataBlock!;
            try
            {
                using var bitmap = texture.GenerateBitmap();
                double r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 256))
                    for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 256))
                    {
                        var c = bitmap.GetPixel(x, y);
                        (r, g, b, a) = (r + c.Red, g + c.Green, b + c.Blue, a + c.Alpha);
                        n++;
                    }
                var file = Path.Combine(p[2], Path.GetFileNameWithoutExtension(path) + ".png");
                using (var fs = File.Create(file))
                    bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);
                output.WriteLine($"{path}: {texture.Format} {texture.Width}x{texture.Height}x{texture.Depth}, average rgba {r / n:F0} {g / n:F0} {b / n:F0} {a / n:F0}");
            }
            catch (Exception e)
            {
                output.WriteLine($"{path}: {texture.Format} {texture.Width}x{texture.Height}: {e.Message}");
            }
        }
    }
}
