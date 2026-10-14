// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedStaticFullVector3.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes static full-precision Vector3 data that doesn't change per frame.
/// </summary>
internal class CCompressedStaticFullVector3 : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var vectorData = MemoryMarshal.Cast<byte, Vector3>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, vectorData[WantedElements[i]]);
        }
    }
}
