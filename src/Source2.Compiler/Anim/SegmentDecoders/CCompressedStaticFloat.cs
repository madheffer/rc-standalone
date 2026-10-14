// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedStaticFloat.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes static float data that doesn't change per frame.
/// </summary>
internal class CCompressedStaticFloat : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var floatData = MemoryMarshal.Cast<byte, float>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, floatData[WantedElements[i]]);
        }
    }
}
