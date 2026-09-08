namespace Source2.Compiler;

/// <summary>
/// GPU-side BC7 block encoder — the planned replacement for the CPU
/// <see cref="Bc7Native"/> path once the compositor is branched out to a
/// host with a GPU.
///
/// <para>BC7 block compression is the skin pipeline's heaviest CPU cost
/// (~15–20 s of a cold heavy job). Running it on the GPU — the same device
/// already doing the composite — collapses that to well under a second.</para>
///
/// <para><b>Deliberately not implemented yet.</b> The backend currently runs
/// inside a Docker container with no GPU / OpenGL access, so
/// <see cref="Available"/> is hard-false. <see cref="ResourceBuilder.BuildTexture(ResourceBuilder.TextureDef)"/>
/// checks <see cref="Available"/> before routing here, so setting
/// <c>BC7_ENCODER=gpu</c> today is harmless — it transparently falls back to the
/// CPU encoder. This class is the wired-in seam: when the compositor moves to a
/// GPU host, implement <see cref="EncodeBgra"/> and turn <see cref="Available"/>
/// into a real capability probe — no other code changes.</para>
///
/// <para>Implementation options at branch-out time:</para>
/// <list type="bullet">
///   <item>a BC7 compute-shader kernel run through the existing
///         <c>a headless GL context</c> GL context (keeps the dependency surface to
///         the GL stack already in the project), or</item>
///   <item>a P/Invoke into a native GPU encoder (Intel ISPC <c>bc7e</c>,
///         <c>betsy</c>, or a D3D/Vulkan compute encoder) — mirrors how
///         <see cref="Bc7Native"/> wraps the CPU <c>bc7enc</c>.</item>
/// </list>
/// </summary>
public static class GpuBc7Encoder
{
    /// <summary>
    /// True when the GPU BC7 encoder is built and a usable GPU/GL context is
    /// present. Hard-false today — the encoder kernel is not implemented and the
    /// container has no GPU. At branch-out, replace this with a real probe
    /// (encoder present AND <c>a headless GL context</c> constructs successfully).
    /// </summary>
    public static bool Available => false;

    /// <summary>
    /// Encode a BGRA8888 image to a BC7 block stream — identical contract to
    /// <see cref="Bc7Native.EncodeBgra"/>: 16 B per 4×4 block,
    /// <paramref name="width"/>/<paramref name="height"/> need not be multiples
    /// of 4 (the trailing edge is clamped to fill the last block row/column).
    /// This is the implementation seam for the GPU path.
    /// </summary>
    /// <remarks><see cref="ResourceBuilder.BuildTexture(ResourceBuilder.TextureDef)"/> only calls
    /// this when <see cref="Available"/> is true, so the throw below is an
    /// unreachable guard until the encoder is implemented.</remarks>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
        => throw new NotImplementedException(
            "GPU BC7 encoding is not implemented yet. ResourceBuilder only routes "
            + "here when GpuBc7Encoder.Available is true, which is hard-false until "
            + "the compositor is branched onto a GPU host — see the class summary.");
}
