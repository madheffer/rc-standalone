// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedFullFloat.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes full-precision float animation data.
/// </summary>
internal class CCompressedFullFloat : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var offset = frameIndex * ElementCount;
        var floatData = MemoryMarshal.Cast<byte, float>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, floatData[offset + WantedElements[i]]);
        }
    }
}
