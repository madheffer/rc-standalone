# Animation: reading a model's animations without VRF

`Source2.Compiler.Anim` reads every animation a compiled model carries, as ValveResourceFormat (VRF,
MIT) reads it. It is VRF's reader, ported file by file with its own types (`Animation`,
`SequenceAnimation`, `ClipAnimation`, `AnimationClip`, `Frame`, `FrameBone`, the ten segment decoders),
over `CompiledResource` and `Meshes.Skeleton` instead of VRF's resource classes.

```csharp
var model = CompiledModel.Read(bytes);
var animations = ModelAnimations.All(model, path => LoadCompiled(path)); // path has no _c
var frame = new Frame(model.Skeleton, ModelAnimations.FlexControllers(model));
foreach (var animation in animations)
    for (var f = 0; f < animation.FrameCount; f++)
    {
        frame.Clear(model.Skeleton);
        frame.FrameIndex = f;
        animation.DecodeFrame(frame);   // frame.Bones[i]: parent-space position, rotation, scale
    }
```

## Where animations come from (`ModelAnimations.All`)

In VRF's order:

1. **Embedded** (`CTRL.embedded_animation`): the group, animation and sequence blocks are named
   by index; with a sequence block, each S1 sequence takes its first referenced animation, then
   animations no sequence used are added.
2. **Animation groups** (`m_refAnimGroups`, `.vagrp_c`): the group's own ANIM block, or each file
   of `m_localHAnimArray`.
3. **Animation graph clips** (`m_animGraph2Refs`): every `.vnmclip` a graph's `m_resources` names,
   following nested graphs, each once.
4. **Included models** (`m_refAnimIncludeModels`): their animations, read with this model's
   skeleton. An include cycle is cut, which is vpkstuff's guard against the stack overflow VRF has.

A sequence's channels map to bones by name, case-insensitively. A segment whose decoder is unknown
is skipped, and those bones keep the bind pose.

## Proven

On 2026-10-14, every pak01 model's animations were compared against vpkstuff's VRF, frame for frame and
bone for bone: position, rotation, scale, flex data and movement. All matched exactly (scratch
`vrfparity anim`, figures in GROUND_TRUTH).

## Not ported yet

- **Additive tagging from animation graph 1** (`AnimationGraph1Additive`, about 300 lines in vpkstuff's VRF).
  It only sets `IsAdditive` on sequences an AG1 graph blends additively; the decoded frames are
  the same.
- **The `.vmorf` side file.** A model whose flex controllers live in a separate morph file reads
  none here. VRF takes them from `SetExternalMorphData`.

Unlike VRF, a segment header that names a missing channel or decoder, or lists more elements than
its container holds, throws `InvalidDataException`. So does a frame read past a segment's or a clip's
data.
