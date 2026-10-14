// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedAnimQuaternion.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Numerics;
namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes compressed quaternion animation data.
/// </summary>
internal class CCompressedAnimQuaternion : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        var offset = frameIndex * ElementCount;

        for (var i = 0; i < RemapTable.Length; i++)
        {
            var elementIndex = WantedElements[i];

            outFrame.SetAttribute(
                RemapTable[i],
                ChannelAttribute,
                SegmentHelpers.ReadQuaternion(Data.AsSpan(
                    (offset + elementIndex) * SegmentHelpers.CompressedQuaternionSize,
                    SegmentHelpers.CompressedQuaternionSize
                ))
            );
        }
    }
}
