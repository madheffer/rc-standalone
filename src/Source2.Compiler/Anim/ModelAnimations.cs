// The sources and their order follow ValveResourceFormat's Model.GetAllAnimations, AnimationGroupLoader and
// AnimationGraphLoader (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat).

using Source2.Compiler.Meshes;
using ValveKeyValue;

namespace Source2.Compiler.Anim;

/// <summary>
/// A compiled model's animations without ValveResourceFormat: its embedded sequences, the animation
/// groups it references (<c>m_refAnimGroups</c>), the clips of its animation graphs
/// (<c>m_animGraph2Refs</c>) and the animations of the models it includes (<c>m_refAnimIncludeModels</c>),
/// in that order, as VRF's <c>Model.GetAllAnimations</c> lists them.
/// </summary>
public static class ModelAnimations
{
    /// <summary>
    /// Loads a referenced compiled file by its resource path (<c>models/x.vagrp</c>, no <c>_c</c>), or
    /// returns null when it is not there.
    /// </summary>
    public delegate byte[]? CompiledFileLoader(string path);

    /// <summary>The model's flex controllers, from its MRPH block (none without one).</summary>
    public static FlexController[] FlexControllers(CompiledModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var morph = model.Resource.Has("MRPH") ? model.Resource.Kv3("MRPH") : null;
        return [.. (morph?.GetArray("m_FlexControllers") ?? []).Select(d => new FlexController(
            d.GetStringProperty("m_szName") ?? "", d.GetStringProperty("m_szType") ?? "", d.GetFloatProperty("min"), d.GetFloatProperty("max")))];
    }

    /// <summary>
    /// The sequences embedded in the model (<c>CTRL.embedded_animation</c>), read with
    /// <paramref name="skeleton"/> (the model's own when null).
    /// </summary>
    /// <exception cref="InvalidDataException">A block the CTRL names is missing or not KV3.</exception>
    public static IEnumerable<SequenceAnimation> Embedded(CompiledModel model, Skeleton? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var embedded = model.Control?.GetSubCollection("embedded_animation");
        if (embedded is null)
            return [];
        skeleton ??= model.Skeleton;
        var flex = FlexControllers(model);
        var group = Block(model, embedded.GetIntegerProperty("group_data_block"));
        var decodeKey = group.GetSubCollection("m_decodeKey") ?? throw new InvalidDataException("The animation group has no m_decodeKey.");
        var animData = Block(model, embedded.GetIntegerProperty("anim_data_block"));
        var sequenceBlock = embedded.GetIntegerProperty("seqgroup_data_block");
        if (sequenceBlock > 0)
            return SequenceAnimation.FromSequenceData(Block(model, sequenceBlock), animData, decodeKey, skeleton, flex);
        return SequenceAnimation.FromData(animData, decodeKey, skeleton, flex);
    }

    private static KVObject Block(CompiledModel model, long index)
    {
        if (index < 0 || index >= model.Resource.File.Blocks.Count)
            throw new InvalidDataException($"The model's CTRL names block {index}; it has {model.Resource.File.Blocks.Count}.");
        return model.Resource.Kv3At((int)index) ?? throw new InvalidDataException($"Block {index} is not KV3.");
    }

    /// <summary>
    /// The sequences of an animation group (<c>.vagrp_c</c>): its own ANIM block when it has one,
    /// otherwise each animation it lists (<c>m_localHAnimArray</c>) that <paramref name="load"/> finds.
    /// </summary>
    public static IEnumerable<SequenceAnimation> FromGroup(byte[] group, CompiledFileLoader load, Skeleton skeleton, FlexController[] flexControllers)
    {
        ArgumentNullException.ThrowIfNull(load);
        var resource = CompiledResource.Read(group);
        var data = resource.Kv3("DATA");
        if (data is null)
            return [];
        var decodeKey = data.GetSubCollection("m_decodeKey") ?? throw new InvalidDataException("The animation group has no m_decodeKey.");
        if (resource.Has("ANIM") && resource.Kv3("ANIM") is { } anim)
            return SequenceAnimation.FromData(anim, decodeKey, skeleton, flexControllers);
        var animations = new List<SequenceAnimation>();
        foreach (var path in (data.GetArray<string>("m_localHAnimArray") ?? []).Where(a => !string.IsNullOrEmpty(a)))
        {
            if (load(path) is { } bytes)
                animations.AddRange(SequenceAnimation.FromResource(CompiledResource.Read(bytes), decodeKey, skeleton, flexControllers));
        }
        return animations;
    }

    /// <summary>
    /// The clips (<c>.vnmclip</c>) the model's animation graphs reference, following graphs that
    /// reference graphs, each once, in first-reference order.
    /// </summary>
    public static IReadOnlyList<string> GraphClipNames(CompiledModel model, CompiledFileLoader load)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(load);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clips = new List<string>();
        foreach (var graph in model.Data.GetArray("m_animGraph2Refs") ?? [])
            Collect(graph.GetStringProperty("m_hGraph") ?? "");
        return clips;

        void Collect(string graph)
        {
            if (!visited.Add(graph) || load(graph) is not { } bytes)
                return;
            foreach (var resource in CompiledResource.Read(bytes).Kv3("DATA")?.GetArray<string>("m_resources") ?? [])
            {
                if (resource.EndsWith(".vnmclip", StringComparison.OrdinalIgnoreCase))
                {
                    if (visited.Add(resource))
                        clips.Add(resource);
                }
                else if (resource.EndsWith(".vnmgraph", StringComparison.OrdinalIgnoreCase))
                {
                    Collect(resource);
                }
            }
        }
    }

    /// <summary>
    /// Every animation of the model: embedded, then its animation groups', then its graph clips, then
    /// those of the models it includes (read with this model's skeleton; an include cycle is cut).
    /// A clip that fails to read is skipped, as VRF skips it.
    /// </summary>
    public static IReadOnlyList<Animation> All(CompiledModel model, CompiledFileLoader load)
        => All(model, load, model.Skeleton, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static List<Animation> All(CompiledModel model, CompiledFileLoader load, Skeleton skeleton, HashSet<string> includeStack)
    {
        ArgumentNullException.ThrowIfNull(load);
        var flex = FlexControllers(model);
        var animations = new List<Animation>(Embedded(model, skeleton));
        foreach (var path in model.Data.GetArray<string>("m_refAnimGroups") ?? [])
        {
            if (load(path) is { } group)
                animations.AddRange(FromGroup(group, load, skeleton, flex));
        }
        foreach (var clip in GraphClipNames(model, load))
        {
            try
            {
                if (load(clip) is { } bytes)
                    animations.Add(new ClipAnimation(AnimationClip.Read(bytes, clip)));
            }
            catch (InvalidDataException)
            {
            }
        }
        foreach (var include in model.Data.GetArray<string>("m_refAnimIncludeModels") ?? [])
        {
            if (string.IsNullOrEmpty(include) || !includeStack.Add(include))
                continue;
            try
            {
                if (load(include) is { } bytes)
                    animations.AddRange(All(CompiledModel.Read(bytes), load, skeleton, includeStack));
            }
            finally
            {
                includeStack.Remove(include);
            }
        }
        return animations;
    }
}
