namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The engine's own geometry a cubemap face draws besides the world (ledger 152): the sky's unit cube, the
/// per-instance id stream and the depth downsamples' quad.
/// </summary>
public static class CubemapFaceMeshes
{
    /// <summary>The sky cube's eight corners, x y z (scenesystem 20261023 .rdata 1805ec900).</summary>
    public static readonly float[] SkyCubeVertices =
    [
        -0.5f, -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,
        -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f,
    ];

    /// <summary>
    /// Its twelve triangles as 16-bit indices: the int table that follows the corners (1805ec960) holds them
    /// one-based (1 3 2, 1 4 3, ...).
    /// </summary>
    public static readonly ushort[] SkyCubeIndices = [.. new[] { 1, 3, 2, 1, 4, 3, 1, 5, 8, 1, 8, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 3, 4, 8, 3, 8, 7, 5, 6, 7, 5, 7, 8 }.Select(i => (ushort)(i - 1))];

    /// <summary>The per-instance stream (vertex binding 1, 4 bytes): instance i reads i, so a draw's first instance picks its record.</summary>
    public static byte[] InstanceIds(int bytes)
    {
        var data = new byte[bytes];
        for (var i = 0; i < bytes / 4; i++)
            BitConverter.TryWriteBytes(data.AsSpan(i * 4), (uint)i);
        return data;
    }

    /// <summary>
    /// A depth downsample's 4-vertex strip (32-byte vertices; the program reads only the position, two floats at 0):
    /// (-1, -1), (1, -1), (-1, 1), (1, 1). The other six floats of each vertex are not read (measured: the
    /// captures hold the far plane's corners there) and are left 0.
    /// </summary>
    public static byte[] DownsampleQuad()
    {
        var data = new byte[4 * 32];
        float[] xy = [-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f];
        for (var v = 0; v < 4; v++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(v * 32), xy[v * 2]);
            BitConverter.TryWriteBytes(data.AsSpan(v * 32 + 4), xy[v * 2 + 1]);
        }
        return data;
    }
}
