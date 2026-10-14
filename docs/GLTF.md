# glTF export without VRF or SharpGLTF

`Gltf.GltfModelExporter` exports a compiled model (`.vmdl_c`) or mesh (`.vmesh_c`) to GLB. It is
ValveResourceFormat's `GltfModelExporter` (MIT) ported onto a glTF writer of our own (`Gltf.GltfModel`),
with vpkstuff's patches to it. So it replaces the VRF exporter with the options vpkstuff uses:

```csharp
var exporter = new GltfModelExporter(path => LoadCompiled(path))   // path has no _c
{
    ShaderLoader = ShaderPrograms.FromPackages(@"...\csgo\shaders_pc_dir.vpk", @"...\core\shaders_pc_dir.vpk"),
    ExportSkeleton = true,          // the skeleton as a skin
    ExportMaterials = true,         // default
    AdaptTextures = true,           // default: textures repacked into glTF's channels
    PreviewTextureMaxSize = 1024,   // optional: embedded textures downscaled
};
exporter.MeshFilter.Add("body");    // optional
byte[] glb = exporter.Export(CompiledModel.Read(bytes), "models/x.vmdl");
```

## What it writes

| piece | rule (VRF's) |
|---|---|
| space | Y up and metres, baked into the data. Positions are scaled by 0.0254 and rotated. Directions and tangents are rotated. Bone translations are scaled, and the roots carry the rotation. |
| meshes | Every embedded mesh on any LoD (vpkstuff's patch: some characters keep the body on LoD 1), then the referenced meshes of the lowest LoD. Only `MeshFilter`'s names, when it is set. A mesh with no scene objects is skipped. One primitive per draw call. |
| attributes | POSITION (with bounds), NORMAL and TANGENT (CS2's packed frame unpacked; a vector off unit length becomes -Z), TEXCOORD_n and COLOR_n numbered across a draw call's buffers, scalars and other semantics as `_NAME[_n]`, non-finite floats as 0. |
| skin | JOINTS_0/1 (unsigned short) and WEIGHTS_0/1 through the mesh's bone remap table. A zero weight's joint becomes 0, and duplicate joints merge their weights. Joints without weights get equal weights. Bind matrices are taken with the skeleton at the origin. |
| materials | One per material path and tint. Alpha mode comes from F_TRANSLUCENT / F_ALPHA_TEST (glass is translucent), the cutoff from `g_flAlphaTestReference`, double-sided from F_RENDER_BACKFACES. The base colour is the tint lerped by `g_flModelTintAmount`, times `g_vColorTint`, linearised. Metalness is `g_flMetalness`, roughness 1. Overlay materials push their positions out along the normals. |
| textures | Each material texture's inputs come from the shader's texture processors (`TextureInputs`), or VRF's table of known shaders. Base colour, normal, specular and emissive are each PNG from the decoded texture; colour alpha is kept for translucent, alpha-tested, effects and additive materials when the texture has any. Occlusion and metallic-roughness pack into one ORM PNG, named `<first>_orm_<token>.png`. A missing texture is white. Images are embedded. |
| extras | Each material's `vmat`: name, shader, int / float / vector / texture params, and its dynamic expressions decompiled by `Shaders.VfxEval`. |

Kept as VRF writes it: an F_UNLIT material is not marked unlit. VRF sets the extension, then
SharpGLTF's `InitializePBRMetallicRoughness` removes it.

## Proven

On 2026-10-14, checked against vpkstuff's VRF exporter run with vpkstuff's options (materials, skeleton,
adapted textures, embedded images, no animations). Both GLBs were read back with SharpGLTF and compared:
node tree and world matrices, skins and bind matrices, every accessor's values bit for bit, indices,
materials and their parameters, every image byte for byte, and the extras. The scratch harness is
`vrfparity gltf`. The sample was every tenth pak01 model: 1,312 models, 1,836 primitives and 6,956
images, all identical. The one difference found along the way is fixed: VRF names an ORM texture after
paths sorted with culture rules, not ordinally. The pieces underneath are exact on all of pak01:
- the materials, including every decompiled dynamic expression (22,009);
- every material texture's shader inputs (166,872);
- the vertex data and texture pixels (docs/MESHES.md, docs/TEXTURES.md).

## Not exported yet

- **Animations.** VRF's `AnimationWriter` is the next port. It uses `Anim/`, which already decodes
  every pak01 frame as VRF does.
- **Morph targets.** They need the MRPH/`.vmorf` delta atlas decoded.
- **Physics, worlds, entity lumps, maps, nav meshes, single clips and skeletons.**
- **Not ported:** the `.gltf` text form with satellite PNGs (vpkstuff embeds), and the 2 GiB split
  of VRF's text form.
