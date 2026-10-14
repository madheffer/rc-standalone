using Source2.Compiler.Anim;
using Source2.Compiler.Meshes;
using ValvePak;
using ValveKeyValue;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// A model's animations read without ValveResourceFormat (every pak01 model's animations matched VRF
/// frame for frame when this was written; docs/ANIMATION.md).
/// </summary>
public class AnimationTests
{
    private static ModelAnimations.CompiledFileLoader Loader(Package pak) => path =>
    {
        var entry = pak.FindEntry(path + "_c");
        if (entry is null)
            return null;
        pak.ReadEntry(entry, out var bytes);
        return bytes;
    };

    [Fact]
    public void StockModels_DecodeEveryFrameToFiniteBones()
    {
        if (CS2Fixtures.StockPak() is not { } path)
            return;
        using var pak = new Package();
        pak.Read(path);
        var load = Loader(pak);
        var models = 0;
        foreach (var entry in Io.VpkEntries.ByExtension(pak)["vmdl_c"].Where(e => e.GetFullPath().StartsWith("models/props", StringComparison.Ordinal))
            .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pak.ReadEntry(entry, out var bytes);
            var model = CompiledModel.Read(bytes);
            var animations = ModelAnimations.All(model, load);
            if (animations.Count == 0)
                continue;
            var frame = new Frame(model.Skeleton, ModelAnimations.FlexControllers(model));
            foreach (var animation in animations.Take(5))
            {
                Assert.False(string.IsNullOrEmpty(animation.Name));
                for (var f = 0; f < animation.FrameCount; f++)
                {
                    frame.Clear(model.Skeleton);
                    frame.FrameIndex = f;
                    animation.DecodeFrame(frame);
                    foreach (var bone in frame.Bones)
                        Assert.True(float.IsFinite(bone.Position.X) && float.IsFinite(bone.Angle.W) && float.IsFinite(bone.Scale), $"{entry.GetFullPath()} {animation.Name} {f}");
                }
            }
            if (++models == 10)
                break;
        }
        Assert.True(models > 0);
    }

    // An animation data tree with one segment of one static-vector3 element for bone 0.
    private static (KVObject Data, KVObject DecodeKey) OneSegment(byte[] container)
    {
        var decoders = KVObject.Array();
        var decoder = KVObject.Collection();
        decoder.Add("m_szName", new KVObject("CCompressedStaticFullVector3"));
        decoders.Add(decoder);

        var segments = KVObject.Array();
        var segment = KVObject.Collection();
        segment.Add("m_container", KVObject.Blob(container));
        segment.Add("m_nLocalChannel", new KVObject(0));
        segments.Add(segment);

        var anim = KVObject.Collection();
        anim.Add("m_name", new KVObject("test"));
        anim.Add("fps", new KVObject(30f));
        var flags = KVObject.Collection();
        anim.Add("m_flags", flags);
        var pData = KVObject.Collection();
        pData.Add("m_nFrames", new KVObject(1));
        var blocks = KVObject.Array();
        var block = KVObject.Collection();
        block.Add("m_nStartFrame", new KVObject(0));
        block.Add("m_nEndFrame", new KVObject(0));
        var indices = KVObject.Array();
        indices.Add(new KVObject(0));
        block.Add("m_segmentIndexArray", indices);
        blocks.Add(block);
        pData.Add("m_frameblockArray", blocks);
        anim.Add("m_pData", pData);
        anim.Add("m_movementArray", KVObject.Array());
        anim.Add("m_eventArray", KVObject.Array());
        anim.Add("m_activityArray", KVObject.Array());
        anim.Add("m_sequenceParams", KVObject.Collection());
        var anims = KVObject.Array();
        anims.Add(anim);

        var data = KVObject.Collection();
        data.Add("m_decoderArray", decoders);
        data.Add("m_segmentArray", segments);
        data.Add("m_animArray", anims);

        var channel = KVObject.Collection();
        var names = KVObject.Array();
        names.Add(new KVObject("root"));
        channel.Add("m_szElementNameArray", names);
        var elementIndices = KVObject.Array();
        elementIndices.Add(new KVObject(0));
        channel.Add("m_nElementIndexArray", elementIndices);
        channel.Add("m_szVariableName", new KVObject("Position"));
        var channels = KVObject.Array();
        channels.Add(channel);
        var decodeKey = KVObject.Collection();
        decodeKey.Add("m_dataChannelArray", channels);
        return (data, decodeKey);
    }

    private static Skeleton OneBone()
    {
        var skeleton = KVObject.Collection();
        var names = KVObject.Array();
        names.Add(new KVObject("root"));
        skeleton.Add("m_boneName", names);
        var parents = KVObject.Array();
        parents.Add(new KVObject(-1));
        skeleton.Add("m_nParent", parents);
        var flags = KVObject.Array();
        flags.Add(new KVObject(0));
        skeleton.Add("m_nFlag", flags);
        var positions = KVObject.Array();
        var p = KVObject.Array();
        foreach (var v in new[] { 0f, 0f, 0f })
            p.Add(new KVObject(v));
        positions.Add(p);
        skeleton.Add("m_bonePosParent", positions);
        var rotations = KVObject.Array();
        var r = KVObject.Array();
        foreach (var v in new[] { 0f, 0f, 0f, 1f })
            r.Add(new KVObject(v));
        rotations.Add(r);
        skeleton.Add("m_boneRotParent", rotations);
        var model = KVObject.Collection();
        model.Add("m_modelSkeleton", skeleton);
        return Skeleton.FromModelData(model);
    }

    [Fact]
    public void Segment_DecodesItsBone_AndATruncatedOneIsInvalidData()
    {
        var skeleton = OneBone();
        // Header: decoder 0, cardinality 1, one element (bone 0), length; then the element id; then x, y, z.
        byte[] good = [0, 0, 1, 0, 1, 0, 20, 0, 0, 0, .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(2f), .. BitConverter.GetBytes(3f)];
        var (data, key) = OneSegment(good);
        var animation = SequenceAnimation.FromData(data, key, skeleton, []).Single();
        var frame = new Frame(skeleton, []) { FrameIndex = 0 };
        animation.DecodeFrame(frame);
        Assert.Equal(new System.Numerics.Vector3(1, 2, 3), frame.Bones[0].Position);

        var (shortData, shortKey) = OneSegment(good[..13]);
        var truncated = SequenceAnimation.FromData(shortData, shortKey, skeleton, []).Single();
        Assert.Throws<InvalidDataException>(() => truncated.DecodeFrame(new Frame(skeleton, []) { FrameIndex = 0 }));

        var (headerOnly, headerKey) = OneSegment([0, 0, 1, 0, 9, 0]);
        Assert.Throws<InvalidDataException>(() => SequenceAnimation.FromData(headerOnly, headerKey, skeleton, []).ToList());
    }
}
