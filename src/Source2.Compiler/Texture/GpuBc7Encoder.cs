namespace Source2.Compiler;

/// <summary>
/// The seam for a GPU BC7 encoder. Not implemented: <see cref="Available"/> is
/// hard-false, so <see cref="ResourceBuilder.BuildTexture(ResourceBuilder.TextureDef)"/>
/// never routes here and asking for the GPU encoder quietly
/// falls back to the CPU encoder.
///
/// <para>BC7 is by far the most expensive step in compiling a texture, so a host
/// with a GPU has a large win available here. Implementing it means filling in
/// <see cref="EncodeBgra"/> and turning <see cref="Available"/> into a real
/// capability probe; nothing else has to change.</para>
/// </summary>
public static class GpuBc7Encoder
{
    /// <summary>Whether the GPU encoder can run. Always false until one exists.</summary>
    public static bool Available => false;

    /// <summary>
    /// Encode a BGRA8888 image to a BC7 block stream. Same contract as
    /// <see cref="Bc7Native.EncodeBgra"/>: 16 bytes per 4x4 block, and the
    /// dimensions need not be multiples of 4 (the trailing edge is clamped to
    /// fill the last block row and column).
    /// </summary>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
        => throw new NotImplementedException(
            "GPU BC7 encoding is not implemented. ResourceBuilder only routes here when "
            + "GpuBc7Encoder.Available is true, which is always false.");
}
