// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CCompressedStaticQuaternion.cs; checked frame for frame against it (docs/ANIMATION.md).

using System.Numerics;
namespace Source2.Compiler.Anim;

/// <summary>
/// Decodes static compressed quaternion data that doesn't change per frame.
/// </summary>
internal class CCompressedStaticQuaternion : AnimationSegmentDecoder
{
    /// <inheritdoc/>
    public override void Read(int frameIndex, Frame outFrame)
    {
        for (var i = 0; i < RemapTable.Length; i++)
        {
            var compressedQuaternionBytes = Data.Slice(
                WantedElements[i] * SegmentHelpers.CompressedQuaternionSize,
                SegmentHelpers.CompressedQuaternionSize
            );

            var quaternion = SegmentHelpers.ReadQuaternion(compressedQuaternionBytes);

            outFrame.SetAttribute(RemapTable[i], ChannelAttribute, quaternion);
        }
    }
}
