// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedFullVector3.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes full-precision Vector3 animation data.
/// </summary>
internal class CCompressedFullVector3 : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var offset = frameIndex * ElementCount;
        var vectorData = MemoryMarshal.Cast<byte, Vector3>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, vectorData[offset + WantedElements[i]]);
        }
    }
}
