// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedFullQuaternion.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Runtime.InteropServices;
using System.Numerics;

namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes full-precision quaternion animation data.
/// </summary>
internal class CCompressedFullQuaternion : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var offset = frameIndex * ElementCount;
        var quaternionData = MemoryMarshal.Cast<byte, Quaternion>(Data);

        for (var i = 0; i < RemapTable.Length; i++)
        {
            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, quaternionData[offset + WantedElements[i]]);
        }
    }
}
