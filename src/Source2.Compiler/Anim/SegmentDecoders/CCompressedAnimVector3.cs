// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedAnimVector3.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes compressed Vector3 animation data using half-precision floats.
/// </summary>
internal class CCompressedAnimVector3 : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var offset = frameIndex * ElementCount;
        var halfVectorData = MemoryMarshal.Cast<byte, Half3>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            var elementIndex = WantedElements[i];

            outFrame.SetAttribute(
                RemapTable[i],
                ChannelAttribute,
                halfVectorData[offset + elementIndex]
            );
        }
    }
}
