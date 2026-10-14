// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedStaticVector3.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes static Vector3 data using half-precision floats that don't change per frame.
/// </summary>
internal class CCompressedStaticVector3 : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    /// <remarks>
    /// Reads static half-precision Vector3 values that remain constant across all frames.
    /// </remarks>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var halfVectorData = MemoryMarshal.Cast<byte, Half3>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, halfVectorData[WantedElements[i]]);
        }
    }
}
