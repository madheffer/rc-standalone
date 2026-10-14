// Ported from ValveResourceFormat's GltfModelExporter (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// with vpkstuff's patches (LoD selection, skeleton-only export, colour alpha for translucent and alpha-tested
// materials, preview texture size, dynamic expressions in the material extras), onto our own glTF writer.

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SkiaSharp;
using Source2.Compiler.Anim;
using Source2.Compiler.Materials;
using Source2.Compiler.Meshes;
using Source2.Compiler.Textures;
using ValveKeyValue;

namespace Source2.Compiler.Gltf;

/// <summary>
/// Exports a compiled model (<c>.vmdl_c</c>) or mesh (<c>.vmesh_c</c>) to GLB without ValveResourceFormat
/// or SharpGLTF, as VRF's <c>GltfModelExporter</c> exports it: meshes in glTF space (Y up, metres), the
/// skeleton as a skin, materials with their textures adapted to glTF's channels and embedded as PNG
/// (docs/GLTF.md). Animations, morph targets, physics and maps are not exported yet.
/// </summary>
public sealed class GltfModelExporter
{
    private const float SourceToGltfScale = 0.0254f;
    private static readonly Quaternion SourceToGltfRotation = Quaternion.CreateFromYawPitchRoll(0, MathF.PI / -2f, MathF.PI / -2f);
    private static readonly Matrix4x4 TransformSourceToGltf = Matrix4x4.CreateScale(SourceToGltfScale) * Matrix4x4.CreateFromQuaternion(SourceToGltfRotation);
    private static readonly Matrix4x4 TransformGltfToSource = Invert(TransformSourceToGltf);

    // https://github.com/KhronosGroup/glTF-Validator/blob/master/lib/src/errors.dart
    private const float UnitLengthThresholdVec3 = 0.00674f;
    private const float OverlayNormalOffsetDistance = 0.01f / 0.0254f;

    private static readonly Dictionary<string, (ChannelMapping Channel, string Name)[]> GltfTextureMappings = new()
    {
        ["BaseColor"] = [(ChannelMapping.RGB, "TextureColor"), (ChannelMapping.Alpha, "TextureTranslucency")],
        ["Normal"] = [(ChannelMapping.RGB, "TextureNormal")],
        ["MetallicRoughness"] = [(ChannelMapping.Green, "TextureRoughness"), (ChannelMapping.Blue, "TextureMetalness")],
        ["SpecularFactor"] = [(ChannelMapping.Alpha, "TextureSpecularMask")],
        ["Occlusion"] = [(ChannelMapping.Red, "TextureAmbientOcclusion")],
        ["Emissive"] = [(ChannelMapping.RGB, "TextureSelfIllumMask")],
    };

    private static readonly HashSet<string> SupportedGltfChannels = [.. GltfTextureMappings.Values.SelectMany(x => x).Select(x => x.Name)];

    private readonly ModelAnimations.CompiledFileLoader _load;
    private readonly Dictionary<string, GltfMesh> _exportedMeshes = [];
    private readonly Dictionary<(string Path, Vector4 Tint), (GltfMaterial Material, bool IsOverlay)> _exportedMaterials = [];
    private readonly Dictionary<string, GltfTexture> _exportedTextures = [];
    private GltfSampler? _sampler;

    /// <summary>An exporter that loads referenced files (meshes, materials, textures) through <paramref name="load"/>.</summary>
    public GltfModelExporter(ModelAnimations.CompiledFileLoader load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
    }

    /// <summary>
    /// The shaders' compiled programs, which say which inputs each material texture packs; without them
    /// VRF's table of known shaders is used (<see cref="ShaderPrograms.FromPackages"/>).
    /// </summary>
    public Func<string, ShaderPrograms?>? ShaderLoader { get; init; }

    /// <summary>Export the skeleton as a skin (VRF's default is off unless animations are exported).</summary>
    public bool ExportSkeleton { get; set; }

    /// <summary>Export materials and their textures.</summary>
    public bool ExportMaterials { get; set; } = true;

    /// <summary>Repack textures into glTF's channels (base colour, normal, metallic-roughness...).</summary>
    public bool AdaptTextures { get; set; } = true;

    /// <summary>When positive, embedded textures are downscaled to at most this many pixels on a side.</summary>
    public int PreviewTextureMaxSize { get; set; }

    /// <summary>When not empty, only meshes whose last name part is listed are exported.</summary>
    public HashSet<string> MeshFilter { get; } = [];

    /// <summary>The <c>asset.generator</c> written.</summary>
    public string Generator { get; set; } = "Source2.Compiler";

    /// <summary>Export a model as GLB.</summary>
    /// <param name="model">The model.</param>
    /// <param name="resourceName">Its path; names the scene and the skeleton.</param>
    public byte[] Export(CompiledModel model, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            var gltf = CreateModel(resourceName, out var scene);
            LoadModel(gltf, scene, model, resourceName, Matrix4x4.Identity, Vector4.One);
            return gltf.ToGlb();
        }
        finally
        {
            Reset();
        }
    }

    /// <summary>Export a mesh file as GLB.</summary>
    public byte[] Export(CompiledMesh mesh, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        try
        {
            var gltf = CreateModel(resourceName, out var scene);
            AddMeshNode(gltf, scene, Path.GetFileName(resourceName), Vector4.One, mesh.Data, mesh.Vbib, joints: null, boneRemapTable: null);
            return gltf.ToGlb();
        }
        finally
        {
            Reset();
        }
    }

    private void Reset()
    {
        _exportedMeshes.Clear();
        _exportedMaterials.Clear();
        _exportedTextures.Clear();
        _sampler = null;
    }

    private GltfModel CreateModel(string resourceName, out GltfScene scene)
    {
        var gltf = new GltfModel { Generator = Generator };
        scene = gltf.CreateScene(Path.GetFileName(resourceName));
        _sampler = gltf.CreateSampler();
        return gltf;
    }

    private void LoadModel(GltfModel gltf, GltfScene scene, CompiledModel model, string name, Matrix4x4 transform, Vector4 tintColor)
    {
        var meshes = LoadModelMeshes(model, name).ToList();
        var skeleton = model.Skeleton;
        var (skeletonNode, joints) = ExportSkeleton ? CreateSkeleton(scene, skeleton, name) : (null, null);
        var nodeTransform = TransformGltfToSource * transform * TransformSourceToGltf;
        foreach (var (data, vbib, meshIndex, meshName) in meshes)
        {
            if (MeshFilter.Count > 0 && !MeshFilter.Contains(meshName.Split('.')[^1]))
                continue;
            var node = AddMeshNode(gltf, scene, meshName, tintColor, data, vbib, joints, model.RemapTable(meshIndex));
            if (node is not null)
                node.WorldMatrix = nodeTransform;
        }
        // Set last: a skinned mesh's bind matrices were taken with the skeleton at the origin.
        if (skeletonNode is not null)
            skeletonNode.WorldMatrix = nodeTransform;
    }

    // Every embedded mesh on any LoD (vpkstuff: some characters keep arms on LoD 0 and the body on
    // LoD 1), then the referenced meshes of the lowest LoD.
    private IEnumerable<(KVObject Data, Vbib Vbib, int MeshIndex, string Name)> LoadModelMeshes(CompiledModel model, string name)
    {
        var masks = model.Data.GetIntegerArray("m_refLODGroupMasks");
        var embedded = model.Meshes;
        for (var i = 0; i < embedded.Count; i++)
        {
            var mask = i < masks.Length ? masks[i] : 1L;
            if (mask != 0)
                yield return (embedded[i].Data, embedded[i].Vbib, embedded[i].MeshIndex, string.Concat(name, ".", embedded[i].Name));
        }
        var combined = masks.Aggregate(0L, (a, m) => a | m);
        var lowest = combined == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount((ulong)combined);
        foreach (var (meshIndex, meshName, _) in model.ReferenceMeshes)
        {
            if (meshIndex < masks.Length && (masks[meshIndex] & (1L << lowest)) == 0)
                continue;
            if (_load(meshName) is not { } bytes)
                continue;
            var mesh = CompiledMesh.Read(bytes);
            yield return (mesh.Data, mesh.Vbib, meshIndex, Path.GetFileNameWithoutExtension(meshName));
        }
    }

    private static (GltfNode? Node, GltfNode[]? Joints) CreateSkeleton(GltfScene scene, Skeleton skeleton, string modelName)
    {
        if (skeleton.Bones.Length == 0)
            return (null, null);
        var skeletonNode = scene.CreateNode(modelName);
        var joints = new GltfNode[skeleton.Bones.Length];
        foreach (var root in skeleton.Roots)
            CreateBones(root, skeletonNode, joints, isRoot: true);
        return (skeletonNode, joints);
    }

    private static void CreateBones(Bone bone, GltfNode parent, GltfNode[] joints, bool isRoot)
    {
        var (translation, rotation) = BakeConversion(bone.Position, bone.Angle, isRoot);
        var node = parent.CreateNode(bone.Name);
        node.Translation = translation;
        node.Rotation = rotation;
        joints[bone.Index] = node;
        foreach (var child in bone.Children)
            CreateBones(child, node, joints, isRoot: false);
    }

    // The conversion is baked into the geometry: the scale folds into translations and the rotation
    // into the skeleton roots, so the armature is identity-scaled and the bind matrices are clean.
    private static (Vector3 Translation, Quaternion Rotation) BakeConversion(Vector3 translation, Quaternion rotation, bool isRoot)
    {
        translation *= SourceToGltfScale;
        if (isRoot)
        {
            translation = Vector3.Transform(translation, SourceToGltfRotation);
            rotation = SourceToGltfRotation * rotation;
        }
        return (translation, rotation);
    }

    private static Matrix4x4 Invert(Matrix4x4 matrix)
    {
        Matrix4x4.Invert(matrix, out var inverse);
        return inverse;
    }

    private GltfNode? AddMeshNode(GltfModel gltf, GltfScene scene, string name, Vector4 tintColor, KVObject meshData, Vbib vbib,
        GltfNode[]? joints, int[]? boneRemapTable)
    {
        if ((meshData.GetArray("m_sceneObjects") ?? []).Count == 0)
            return null;
        var node = scene.CreateNode(name);
        if (_exportedMeshes.TryGetValue(name, out var existing))
        {
            node.Mesh = existing;
            return node;
        }
        var mesh = CreateMesh(gltf, name, meshData, vbib, boneRemapTable, tintColor);
        _exportedMeshes.Add(name, mesh);
        if (joints is null || !mesh.Primitives.All(p => p.GetAttribute("JOINTS_0") is not null))
        {
            node.Mesh = mesh;
            return node;
        }
        node.Mesh = mesh;
        node.Skin = gltf.CreateSkin(joints, Matrix4x4.Identity);
        return null;   // placed through the skeleton
    }

    private GltfMesh CreateMesh(GltfModel gltf, string meshName, KVObject meshData, Vbib vbib, int[]? boneRemapTable, Vector4 tintColor)
    {
        var mesh = gltf.CreateMesh(meshName);
        var boneWeightCount = meshData.GetSubCollection("m_skeleton")?.GetInt32Property("m_nBoneWeightCount") ?? 0;
        var accessors = CreateVertexBufferAccessors(gltf, vbib, boneRemapTable != null ? boneWeightCount : 0, boneRemapTable);
        foreach (var sceneObject in meshData.GetArray("m_sceneObjects") ?? [])
            foreach (var drawCall in sceneObject.GetArray("m_drawCalls") ?? [])
                CreatePrimitiveFromDrawCall(gltf, drawCall, mesh, vbib, accessors, tintColor);
        return mesh;
    }

    private static Dictionary<string, GltfAccessor>[] CreateVertexBufferAccessors(GltfModel gltf, Vbib vbib, int boneWeightCount, int[]? boneRemapTable)
    {
        return [.. vbib.VertexBuffers.Select(vertexBuffer =>
        {
            var accessors = new Dictionary<string, GltfAccessor>();
            if (vertexBuffer.ElementCount == 0)
                return accessors;
            var attributeCounters = new Dictionary<string, int>();
            ushort[]? joints = null;
            Vector4[]? weights = null;
            foreach (var attribute in vertexBuffer.InputLayoutFields.OrderBy(i => i.SemanticIndex).ThenBy(i => i.Offset))
            {
                if (attribute.SemanticName is "BLENDINDICES" or "BLENDWEIGHT" or "BLENDWEIGHTS")
                {
                    if (boneWeightCount > 0)
                    {
                        if (attribute.SemanticName is "BLENDINDICES")
                            joints = Vbib.GetBlendIndicesArray(vertexBuffer, attribute, boneRemapTable);
                        else
                            weights = Vbib.GetBlendWeightsArray(vertexBuffer, attribute);
                    }
                    continue;
                }
                var elementCount = Vbib.GetFormatInfo(attribute.Format, attribute.SemanticName).ElementCount;
                var accessorName = attribute.SemanticName switch
                {
                    "TEXCOORD" when elementCount == 2 => "TEXCOORD",
                    "COLOR" => attribute.SemanticIndex == 0 ? "COLOR" : "_COLOR",
                    "POSITION" => "POSITION",
                    "NORMAL" => "NORMAL",
                    "TANGENT" => "TANGENT",
                    _ => $"_{attribute.SemanticName}",
                };
                // None of the glTF attributes is a scalar.
                if (elementCount == 1 && accessorName[0] != '_')
                    accessorName = $"_{accessorName}";
                attributeCounters.TryGetValue(accessorName, out var attributeCounter);
                attributeCounters[accessorName] = attributeCounter + 1;
                if (attribute.SemanticIndex > 0 && accessorName[0] == '_')
                    accessorName = $"{accessorName}_{attribute.SemanticIndex}";   // application-specific: keep the index
                else if (attribute.SemanticName is "TEXCOORD" or "COLOR")
                    accessorName = $"{accessorName}_{attributeCounter}";          // glTF wants 0, 1, 2...
                else if (attributeCounter > 0)
                    throw new NotSupportedException($"Got attribute \"{attribute.SemanticName}\" more than once, but that is not supported.");

                if (attribute.SemanticName == "NORMAL")
                {
                    var (normals, tangents) = Vbib.GetNormalTangentArray(vertexBuffer, attribute);
                    FixZeroLengthVectors(normals);
                    BakeDirections(normals);
                    if (tangents.Length > 0)
                    {
                        FixZeroLengthVectors(tangents);
                        BakeTangents(tangents);
                        accessors["NORMAL"] = Accessor(gltf, normals, "VEC3");
                        accessors["TANGENT"] = Accessor(gltf, tangents, "VEC4");
                    }
                    else
                    {
                        accessors[accessorName] = Accessor(gltf, normals, "VEC3");
                    }
                    continue;
                }
                switch (elementCount)
                {
                    case 1:
                        accessors[accessorName] = Accessor(gltf, Vbib.GetScalarAttributeArray(vertexBuffer, attribute), "SCALAR");
                        break;
                    case 2:
                        accessors[accessorName] = Accessor(gltf, Vbib.GetVector2AttributeArray(vertexBuffer, attribute), "VEC2");
                        break;
                    case 3:
                    {
                        var vectors = Vbib.GetVector3AttributeArray(vertexBuffer, attribute);
                        if (accessorName == "POSITION")
                            BakePositions(vectors);
                        accessors[accessorName] = Accessor(gltf, vectors, "VEC3", bounds: accessorName == "POSITION");
                        break;
                    }
                    case 4:
                    {
                        var vectors = Vbib.GetVector4AttributeArray(vertexBuffer, attribute);
                        if (accessorName == "TANGENT")
                        {
                            FixZeroLengthVectors(vectors);
                            BakeTangents(vectors);
                        }
                        accessors[accessorName] = Accessor(gltf, vectors, "VEC4");
                        break;
                    }
                    default:
                        throw new NotSupportedException($"Attribute \"{attribute.SemanticName}\" has {elementCount} components");
                }
            }

            if (joints != null)
                AddJointsAndWeights(gltf, accessors, joints, weights, (int)vertexBuffer.ElementCount, boneWeightCount);
            return accessors;
        })];
    }

    private static void AddJointsAndWeights(GltfModel gltf, Dictionary<string, GltfAccessor> accessors, ushort[] joints, Vector4[]? weights, int vertexCount, int boneWeightCount)
    {
        var eightBones = boneWeightCount > 4;
        var jointCount = eightBones ? 8 : 4;
        if (weights == null)
        {
            // Some models have joints but no weights: share the weight between the bones.
            var baseWeight = 1f / boneWeightCount;
            var weights0 = new Vector4(boneWeightCount > 0 ? baseWeight : 0, boneWeightCount > 1 ? baseWeight : 0,
                boneWeightCount > 2 ? baseWeight : 0, boneWeightCount > 3 ? baseWeight : 0);
            if (eightBones)
            {
                var weights1 = new Vector4(boneWeightCount > 4 ? baseWeight : 0, boneWeightCount > 5 ? baseWeight : 0,
                    boneWeightCount > 6 ? baseWeight : 0, boneWeightCount > 7 ? baseWeight : 0);
                weights = new Vector4[vertexCount * 2];
                for (var i = 0; i < weights.Length; i += 2)
                    (weights[i], weights[i + 1]) = (weights0, weights1);
            }
            else
            {
                weights = [.. Enumerable.Repeat(weights0, vertexCount)];
            }
        }
        FixDuplicateJoints(joints, MemoryMarshal.Cast<Vector4, float>(weights.AsSpan()), jointCount);

        var jointBytes = new byte[2 * joints.Length];
        var jointShorts = MemoryMarshal.Cast<byte, ushort>(jointBytes.AsSpan());
        var view = gltf.CreateBufferView(jointBytes, 8, GltfBufferTarget.ArrayBuffer);
        if (eightBones)
        {
            SplitEightBoneJoints(joints, jointShorts);
            accessors["JOINTS_0"] = gltf.CreateAccessor(view, joints.Length / 8, GltfComponentType.UnsignedShort, "VEC4");
            accessors["JOINTS_1"] = gltf.CreateAccessor(view, joints.Length / 8, GltfComponentType.UnsignedShort, "VEC4", byteOffset: joints.Length);
            var weights0 = new Vector4[weights.Length / 2];
            var weights1 = new Vector4[weights.Length / 2];
            for (var i = 0; i < weights0.Length; i++)
                (weights0[i], weights1[i]) = (weights[2 * i], weights[2 * i + 1]);
            accessors["WEIGHTS_0"] = Accessor(gltf, weights0, "VEC4");
            accessors["WEIGHTS_1"] = Accessor(gltf, weights1, "VEC4");
        }
        else
        {
            joints.CopyTo(jointShorts);
            accessors["JOINTS_0"] = gltf.CreateAccessor(view, joints.Length / 4, GltfComponentType.UnsignedShort, "VEC4");
            accessors["WEIGHTS_0"] = Accessor(gltf, weights, "VEC4");
        }
    }

    private void CreatePrimitiveFromDrawCall(GltfModel gltf, KVObject drawCall, GltfMesh mesh, Vbib vbib, Dictionary<string, GltfAccessor>[] vertexBufferAccessors, Vector4 parentTintColor)
    {
        var indexBuffer = vbib.IndexBuffers[drawCall.GetSubCollection("m_indexBuffer")!.GetInt32Property("m_hBuffer")];
        var materialPath = drawCall.GetStringProperty("m_material") ?? drawCall.GetStringProperty("m_pMaterial") ?? "";
        var primitive = mesh.CreatePrimitive();

        // Each vertex buffer numbers its TEXCOORDs and COLORs from 0, so number them across buffers.
        var texcoordCounter = 0;
        var colorCounter = 0;
        foreach (var vertexBufferInfo in drawCall.GetArray("m_vertexBuffers") ?? [])
        {
            foreach (var (key, accessor) in vertexBufferAccessors[vertexBufferInfo.GetInt32Property("m_hBuffer")])
            {
                var semantic = key.StartsWith("TEXCOORD_", StringComparison.Ordinal) ? $"TEXCOORD_{texcoordCounter++}"
                    : key.StartsWith("COLOR_", StringComparison.Ordinal) ? $"COLOR_{colorCounter++}"
                    : key;
                primitive.SetAttribute(semantic, accessor);
            }
        }

        var primitiveType = drawCall.GetEnumValue<RenderPrimitiveType>("m_nPrimitiveType");
        if (primitiveType != RenderPrimitiveType.RENDER_PRIM_TRIANGLES)
            throw new NotSupportedException($"Unknown PrimitiveType in drawCall! {primitiveType}");
        primitive.Indices = gltf.CreateIndexAccessor(ReadIndices(indexBuffer, drawCall.GetInt32Property("m_nStartIndex"),
            drawCall.GetInt32Property("m_nIndexCount"), drawCall.GetInt32Property("m_nBaseVertex")));

        if (!ExportMaterials)
            return;
        var tint = parentTintColor;
        if (drawCall.ContainsKey("m_vTintColor"))
        {
            var drawCallTint = new Vector4(drawCall.GetSubCollection("m_vTintColor")!.ToVector3(), 1.0f);
            if (drawCall.ContainsKey("m_flAlpha"))
                drawCallTint.W = drawCall.GetFloatProperty("m_flAlpha");
            tint *= drawCallTint;
        }
        if (_exportedMaterials.TryGetValue((materialPath, tint), out var existing))
        {
            primitive.Material = existing.Material;
            if (existing.IsOverlay)
                OffsetPositionsByNormals(gltf, primitive);
            return;
        }
        if (_load(materialPath) is not { } materialBytes)
            return;
        var renderMaterial = CompiledMaterial.Read(materialBytes);
        var isOverlay = IsOverlay(renderMaterial);
        var material = gltf.CreateMaterial(Path.GetFileNameWithoutExtension(materialPath));
        primitive.Material = material;
        _exportedMaterials.Add((materialPath, tint), (material, isOverlay));
        GenerateMaterial(gltf, material, renderMaterial, tint);
        if (isOverlay)
            OffsetPositionsByNormals(gltf, primitive);
    }

    /// <summary>A draw call's indices with the base vertex added.</summary>
    public static int[] ReadIndices(OnDiskBufferData indexBuffer, int start, int count, int baseVertex)
    {
        var indices = new int[count];
        var size = (int)indexBuffer.ElementSizeInBytes;
        if ((long)(start + count) * size > indexBuffer.Data.Length || start < 0 || count < 0)
            throw new InvalidDataException($"Indices {start}..{start + count} run past the {indexBuffer.ElementCount}-index buffer.");
        if (size == 4)
        {
            var source = MemoryMarshal.Cast<byte, int>(indexBuffer.Data.AsSpan(start * 4, count * 4));
            for (var i = 0; i < count; i++)
                indices[i] = source[i] + baseVertex;
        }
        else if (size == 2)
        {
            var source = MemoryMarshal.Cast<byte, ushort>(indexBuffer.Data.AsSpan(start * 2, count * 2));
            for (var i = 0; i < count; i++)
                indices[i] = baseVertex + source[i];
        }
        return indices;
    }

    private static GltfAccessor Accessor<T>(GltfModel gltf, T[] values, string type, bool bounds = false) where T : unmanaged
    {
        var floats = MemoryMarshal.Cast<T, float>(values.AsSpan());
        for (var i = 0; i < floats.Length; i++)
            if (!float.IsFinite(floats[i]))
                floats[i] = 0f;
        return gltf.CreateFloatAccessor(values, type, bounds: bounds);
    }

    private static void BakePositions(Span<Vector3> positions)
    {
        for (var i = 0; i < positions.Length; i++)
            positions[i] = Vector3.Transform(positions[i], TransformSourceToGltf);
    }

    private static void BakeDirections(Span<Vector3> directions)
    {
        for (var i = 0; i < directions.Length; i++)
            directions[i] = Vector3.Transform(directions[i], SourceToGltfRotation);
    }

    private static void BakeTangents(Span<Vector4> tangents)
    {
        for (var i = 0; i < tangents.Length; i++)
        {
            var rotated = Vector3.Transform(new Vector3(tangents[i].X, tangents[i].Y, tangents[i].Z), SourceToGltfRotation);
            tangents[i] = new Vector4(rotated, tangents[i].W);
        }
    }

    private static void FixZeroLengthVectors(Span<Vector4> vectors)
    {
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            if (Math.Abs(new Vector3(v.X, v.Y, v.Z).Length() - 1.0f) > UnitLengthThresholdVec3)
                vectors[i] = new Vector4(0, 0, -1, v.W);
        }
    }

    private static void FixZeroLengthVectors(Span<Vector3> vectors)
    {
        for (var i = 0; i < vectors.Length; i++)
            if (Math.Abs(vectors[i].Length() - 1.0f) > UnitLengthThresholdVec3)
                vectors[i] = -Vector3.UnitZ;
    }

    private static void SplitEightBoneJoints(ReadOnlySpan<ushort> joints, Span<ushort> output)
    {
        var joints0 = 0;
        var joints1 = joints.Length / 2;
        for (var i = 0; i < joints.Length; i += 8)
        {
            joints[i..(i + 4)].CopyTo(output[joints0..]);
            joints[(i + 4)..(i + 8)].CopyTo(output[joints1..]);
            joints0 += 4;
            joints1 += 4;
        }
    }

    // A zero weight's joint becomes 0; a joint repeated later in a vertex's group is merged into the
    // first, its weight added and the rest shifted left. VRF's order of operations, kept exactly.
    private static void FixDuplicateJoints(Span<ushort> joints, Span<float> weights, int jointCount)
    {
        for (var i = 0; i < joints.Length; i += jointCount)
        {
            for (var j = 0; j < jointCount; j++)
                if (weights[i + j] == 0)
                    joints[i + j] = 0;
            for (var j = jointCount - 2; j >= 0; j--)
            {
                for (var k = jointCount - 1; k > j; k--)
                {
                    if (joints[i + j] != joints[i + k])
                        continue;
                    for (var l = k; l < jointCount - 1; l++)
                        joints[i + l] = joints[i + l + 1];
                    joints[i + jointCount - 1] = 0;
                    weights[i + j] += weights[i + k];
                    for (var l = k; l < jointCount - 1; l++)
                        weights[i + l] = weights[i + l + 1];
                    weights[i + jointCount - 1] = 0;
                }
            }
        }
    }

    private static void OffsetPositionsByNormals(GltfModel gltf, GltfPrimitive primitive)
    {
        var positions = primitive.GetAttribute("POSITION");
        var normals = primitive.GetAttribute("NORMAL");
        if (positions is null || normals is null)
            return;
        var p = MemoryMarshal.Cast<byte, Vector3>(positions.BufferView.Data.AsSpan());
        var n = MemoryMarshal.Cast<byte, Vector3>(normals.BufferView.Data.AsSpan());
        var updated = new Vector3[positions.Count];
        for (var i = 0; i < updated.Length; i++)
            updated[i] = p[i] + n[i] * OverlayNormalOffsetDistance;
        primitive.SetAttribute("POSITION", Accessor(gltf, updated, "VEC3", bounds: true));
    }

    // :MaterialIsOverlay
    private static bool IsOverlay(CompiledMaterial material) =>
        material.IntParams.GetValueOrDefault("F_OVERLAY") == 1
        || material.IntParams.GetValueOrDefault("F_DEPTHBIAS") == 1 || material.IntParams.GetValueOrDefault("F_DEPTH_BIAS") == 1
        || material.ShaderName.EndsWith("static_overlay.vfx", StringComparison.Ordinal) || material.ShaderName is "citadel_overlay.vfx";

    private record struct RemapInstruction(string ChannelName, ChannelMapping ValveChannel, ChannelMapping GltfChannel);

    private void GenerateMaterial(GltfModel gltf, GltfMaterial material, CompiledMaterial renderMaterial, Vector4 tint)
    {
        renderMaterial.IntParams.TryGetValue("F_TRANSLUCENT", out var isTranslucent);
        renderMaterial.IntParams.TryGetValue("F_ALPHA_TEST", out var isAlphaTest);
        if (renderMaterial.ShaderName.EndsWith("_glass.vfx", StringComparison.InvariantCulture))
            isTranslucent = 1;
        material.AlphaMode = isTranslucent > 0 ? "BLEND" : isAlphaTest > 0 ? "MASK" : "OPAQUE";
        if (isAlphaTest > 0 && renderMaterial.FloatParams.TryGetValue("g_flAlphaTestReference", out var alphaTestReference))
            material.AlphaCutoff = alphaTestReference;
        if (renderMaterial.IntParams.TryGetValue("F_RENDER_BACKFACES", out var doubleSided) && doubleSided > 0)
            material.DoubleSided = true;
        // VRF marks F_UNLIT materials unlit, but its next call (SharpGLTF's InitializePBRMetallicRoughness)
        // drops the extension again, so what it writes is lit; kept that way (docs/GLTF.md).

        // Non-metallic unless the material says otherwise.
        float metalValue = 0;
        if (renderMaterial.FloatParams.TryGetValue("g_flMetalness", out var flMetalness))
            metalValue = float.Clamp(flMetalness, 0, 1);
        var baseColor = tint;
        if (renderMaterial.FloatParams.TryGetValue("g_flModelTintAmount", out var tintAmount))
            baseColor = Vector4.Lerp(Vector4.One, baseColor, tintAmount);
        if (renderMaterial.VectorParams.TryGetValue("g_vColorTint", out var colorTint))
        {
            baseColor *= colorTint;
            baseColor.W = 1; // the tint only affects colour
        }
        baseColor = new Vector4(SrgbGammaToLinear(new Vector3(baseColor.X, baseColor.Y, baseColor.Z)), baseColor.W);
        baseColor = Vector4.Clamp(baseColor, Vector4.Zero, Vector4.One);

        var channels = new Dictionary<string, List<RemapInstruction>>();
        var needsSpecular = false;
        var shader = ShaderLoader?.Invoke(renderMaterial.ShaderName);
        foreach (var textureKey in renderMaterial.TextureParams.Keys)
        {
            var instructions = RemapToGltf(TextureInputs.For(textureKey, renderMaterial, shader));
            channels[textureKey] = instructions;
            needsSpecular |= instructions.Any(i => i.ChannelName == "SpecularFactor");
        }
        material.Specular = needsSpecular;
        material.BaseColorFactor = baseColor;
        material.MetallicFactor = metalValue;
        material.RoughnessFactor = 1f;

        var bitmaps = new Dictionary<string, SKBitmap>();
        try
        {
            if (!AdaptTextures)
            {
                var names = new Dictionary<string, string>();
                foreach (var (textureKey, texturePath) in renderMaterial.TextureParams)
                {
                    var textureName = Path.GetFileName(texturePath);
                    names[textureKey] = textureName;
                    if (!_exportedTextures.TryGetValue(textureName, out var texture))
                    {
                        texture = NewTexture(gltf, textureName);
                        texture.Image.Content = Preview(Png(Bitmap(texturePath, bitmaps), ChannelMapping.RGBA));
                    }
                    if (channels[textureKey].Count > 0)
                        TieTexture(material, texture, channels[textureKey][0].ChannelName, false);
                }
                WriteExtras(material, renderMaterial, names);
                return;
            }

            if (needsSpecular && renderMaterial.VectorParams.TryGetValue("g_vSpecularColor", out var specularColor))
                material.SpecularColorFactor = new Vector3(specularColor.X, specularColor.Y, specularColor.Z);

            var remap = new Dictionary<string, List<RemapInstruction>>();
            foreach (var (textureKey, texturePath) in renderMaterial.TextureParams)
                if (channels[textureKey].Count > 0)
                    remap[texturePath] = channels[textureKey];

            // Occlusion and metallic-roughness pack into one ORM texture (R, G, B).
            var ormRedForOcclusion = remap.Values.SelectMany(i => i).Any(i => i.ChannelName == "MetallicRoughness");
            var orm = new Dictionary<string, List<RemapInstruction>>();
            foreach (var (texturePath, instructions) in remap)
            {
                var ormInstructions = instructions.Where(i => i.ChannelName is "Occlusion" or "MetallicRoughness").ToList();
                if (ormInstructions.Count == 0)
                    continue;
                orm[texturePath] = ormInstructions;
                foreach (var instruction in ormInstructions)
                    instructions.Remove(instruction);
            }

            foreach (var (texturePath, instructions) in remap)
            {
                if (instructions.Count == 0)
                    continue;
                var main = instructions[0];
                var textureName = Path.GetFileName(texturePath);
                if (!_exportedTextures.TryGetValue(textureName, out var texture))
                {
                    texture = NewTexture(gltf, textureName);
                    texture.Image.Content = MainTexturePng(texturePath, main, renderMaterial, isTranslucent > 0, isAlphaTest > 0, bitmaps);
                }
                TieTexture(material, texture, main.ChannelName, ormRedForOcclusion);
            }

            if (orm.Count > 0)
            {
                var paths = orm.Keys.ToArray();
                Array.Sort(paths, StringComparer.InvariantCulture); // VRF sorts with culture rules ("_" before letters)
                var hash = StringToken.Get(string.Join("|", paths));
                var ormName = Path.GetFileNameWithoutExtension(paths[0]) + $"_orm_{hash}.png";
                if (!_exportedTextures.TryGetValue(ormName, out var texture))
                {
                    texture = NewTexture(gltf, ormName);
                    using var packer = new TexturePacker(new SKColor(255, 255, 0, 255));
                    foreach (var (texturePath, instructions) in orm)
                    {
                        var bitmap = Bitmap(texturePath, bitmaps);
                        foreach (var instruction in instructions)
                            packer.Collect(bitmap, instruction.ValveChannel.Count == 1 ? instruction.ValveChannel : ChannelMapping.Red,
                                instruction.GltfChannel.Count == 1 ? instruction.GltfChannel : ChannelMapping.Red);
                    }
                    texture.Image.Content = Preview(Png(packer.Bitmap ?? throw new InvalidOperationException("Failed to create ORM texture bitmap"), ChannelMapping.RGBA));
                }
                TieTexture(material, texture, "MetallicRoughness", ormRedForOcclusion);
            }
            WriteExtras(material, renderMaterial, renderMaterial.TextureParams);
        }
        finally
        {
            foreach (var bitmap in bitmaps.Values)
                bitmap.Dispose();
        }
    }

    private GltfTexture NewTexture(GltfModel gltf, string name)
    {
        var image = gltf.CreateImage(name);
        var texture = gltf.CreateTexture(image, _sampler);
        texture.Name = name;
        _exportedTextures[name] = texture;
        return texture;
    }

    private byte[] MainTexturePng(string texturePath, RemapInstruction main, CompiledMaterial renderMaterial, bool translucent, bool alphaTest, Dictionary<string, SKBitmap> bitmaps)
    {
        var channel = main.ValveChannel;
        // Some apps such as Blender do not like the excess alpha channel.
        if (main.ValveChannel == ChannelMapping.RGBA && main.GltfChannel == ChannelMapping.RGB)
            channel = ChannelMapping.RGB;
        var bitmap = Bitmap(texturePath, bitmaps);
        // vpkstuff: translucent, alpha-tested, effects and additive materials carve their coverage out of
        // the colour texture's own alpha; keep it when the texture has one, or a cutout exports solid.
        var wantsColorAlpha = translucent || alphaTest
            || renderMaterial.ShaderName.EndsWith("csgo_effects.vfx", StringComparison.Ordinal)
            || renderMaterial.IntParams.GetValueOrDefault("F_ADDITIVE_BLEND") > 0;
        if (wantsColorAlpha && channel == ChannelMapping.RGB && main.ChannelName == "BaseColor" && bitmap.AlphaType != SKAlphaType.Opaque)
        {
            var sx = Math.Max(1, bitmap.Width / 32);
            var sy = Math.Max(1, bitmap.Height / 32);
            for (var y = 0; y < bitmap.Height && channel == ChannelMapping.RGB; y += sy)
            {
                for (var x = 0; x < bitmap.Width; x += sx)
                {
                    if (bitmap.GetPixel(x, y).Alpha < 250)
                    {
                        channel = ChannelMapping.RGBA;
                        break;
                    }
                }
            }
        }
        return Preview(Png(bitmap, channel));
    }

    private byte[] Preview(byte[] png) => PreviewTextureMaxSize > 0 ? DownscalePreviewPng(png, PreviewTextureMaxSize) : png;

    // A missing texture (an optional stock dependency not bundled with an upload) is white, so occlusion
    // and base colour do not bake to black. A high dynamic range texture decodes to 8 bits.
    private SKBitmap Bitmap(string texturePath, Dictionary<string, SKBitmap> bitmaps)
    {
        if (bitmaps.TryGetValue(texturePath, out var bitmap))
            return bitmap;
        if (_load(texturePath) is { } bytes)
        {
            var texture = CompiledTexture.Read(bytes);
            bitmap = texture.GenerateBitmap(codec: texture.IsHighDynamicRange ? texture.CodecFromEditInfo() | TextureCodec.ForceLDR : TextureCodec.Auto);
        }
        else
        {
            bitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            bitmap.Erase(SKColors.White);
        }
        bitmaps[texturePath] = bitmap;
        return bitmap;
    }

    private static void TieTexture(GltfMaterial material, GltfTexture texture, string channel, bool ormRedForOcclusion)
    {
        var info = new GltfTextureInfo(texture);
        switch (channel)
        {
            case "BaseColor":
                material.BaseColorTexture = info;
                break;
            case "Normal":
                material.NormalTexture = info;
                break;
            case "Occlusion":
                material.OcclusionTexture = info;
                break;
            case "Emissive":
                material.EmissiveTexture = info;
                break;
            case "SpecularFactor":
                if (material.Specular)
                    material.SpecularTexture = info;
                break;
            case "MetallicRoughness":
                material.MetallicRoughnessTexture = info;
                material.MetallicFactor = 1.0f; // the texture holds the metalness
                if (ormRedForOcclusion)
                    material.OcclusionTexture = info;
                break;
        }
    }

    // VRF's RemapValveChannelsToGltf, with its layered-name comparison.
    private static List<RemapInstruction> RemapToGltf(List<(ChannelMapping Channel, string Name)> inputs)
    {
        var instructions = new List<RemapInstruction>();
        foreach (var (gltfType, gltfInputs) in GltfTextureMappings)
        {
            if (inputs.Count == gltfInputs.Length && inputs.Zip(gltfInputs).All(p => p.First.Channel == p.Second.Channel && NamesMatch(p.First.Name, p.Second.Name)))
            {
                instructions.Add(new RemapInstruction(gltfType, ChannelMapping.RGBA, ChannelMapping.RGBA));
                break;
            }
            foreach (var gltfInput in gltfInputs)
                foreach (var input in inputs)
                    if (NamesMatch(input.Name, gltfInput.Name))
                        instructions.Add(new RemapInstruction(gltfType, input.Channel, gltfInput.Channel));
        }
        foreach (var input in inputs)
            if (NamesMatch(input.Name, "TextureMetalnessMask"))
                instructions.Add(new RemapInstruction("MetallicRoughness", input.Channel, ChannelMapping.Blue));
        return instructions;
    }

    private static readonly string[] LayerNameConventions = ["Texture{0}A", "Texture{0}1", "Texture{0}0", "TextureLayer1{0}"];

    // VRF's LayeredTextureNameComparer over the supported glTF inputs: TextureColor1 matches TextureColor.
    private static bool NamesMatch(string layered, string unlayered)
    {
        if (layered.Equals(unlayered, StringComparison.Ordinal))
            return true;
        var valid = SupportedGltfChannels.Contains(unlayered);
        if (!valid && !SupportedGltfChannels.Contains(layered))
            return false;
        if (!valid)
            (layered, unlayered) = (unlayered, layered);
        if (unlayered.StartsWith("Texture", StringComparison.Ordinal))
            unlayered = unlayered[7..];
        return LayerNameConventions.Any(template => string.Equals(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, unlayered), layered, StringComparison.Ordinal));
    }

    private static void WriteExtras(GltfMaterial material, CompiledMaterial renderMaterial, Dictionary<string, string> textures)
    {
        var ints = new JsonObject();
        foreach (var (key, value) in renderMaterial.IntParams)
            ints[key] = value;
        var floats = new JsonObject();
        foreach (var (key, value) in renderMaterial.FloatParams)
            floats[key] = value;
        var vectors = new JsonObject();
        foreach (var (key, value) in renderMaterial.VectorParams)
            vectors[key] = new JsonArray(value.X, value.Y, value.Z, value.W);
        var textureParams = new JsonObject();
        foreach (var (key, value) in textures)
            textureParams[key] = value;
        var dynamics = new JsonObject();
        foreach (var (key, value) in renderMaterial.DynamicExpressions)
            dynamics[key] = value;
        material.Extras = new JsonObject
        {
            ["vmat"] = new JsonObject
            {
                ["Name"] = renderMaterial.Name,
                ["ShaderName"] = renderMaterial.ShaderName,
                ["IntParams"] = ints,
                ["FloatParams"] = floats,
                ["VectorParams"] = vectors,
                ["TextureParams"] = textureParams,
                ["DynamicParams"] = dynamics,
            },
        };
    }

    // sRGB gamma to linear, as VRF's ColorSpace computes it (pow through exp/log, less a hair for exp(0)).
    private static Vector3 SrgbGammaToLinear(Vector3 gamma)
    {
        var linear = gamma / 12.92f;
        var curve = Vector3.FusedMultiplyAdd(gamma, new Vector3(1f / 1.055f), new Vector3(0.055f / 1.055f));
        curve = Vector3.Exp(2.4f * Vector3.Log(curve));
        curve -= new Vector3(0.0000001f);
        return new Vector3(
            gamma.X <= 0.04045f ? linear.X : curve.X,
            gamma.Y <= 0.04045f ? linear.Y : curve.Y,
            gamma.Z <= 0.04045f ? linear.Z : curve.Z);
    }

    /// <summary>
    /// A bitmap's channels as PNG, as VRF's <c>TextureExtract.ToPngImageChannels</c> writes them: one
    /// channel as grey, RG or RGB with alpha dropped, RGBA as is, anything else swizzled.
    /// </summary>
    public static byte[] Png(SKBitmap bitmap, ChannelMapping channel)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (channel.Count == 1)
        {
            using var grey = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var greyPixels = grey.PeekPixels();
            using var pixels = bitmap.PeekPixels();
            var target = greyPixels.GetPixelSpan<byte>();
            var source = pixels.GetPixelSpan<SKColor>();
            for (var i = 0; i < source.Length; i++)
                target[i] = Component(source[i], channel[0]);
            return EncodePng(greyPixels);
        }
        if (channel == ChannelMapping.RG || channel == ChannelMapping.RGB)
        {
            using var copy = new SKBitmap(bitmap.Info);
            using var copyPixels = copy.PeekPixels();
            using var pixels = bitmap.PeekPixels();
            pixels.GetPixelSpan<SKColor>().CopyTo(copyPixels.GetPixelSpan<SKColor>());
            using var opaque = copyPixels.WithAlphaType(SKAlphaType.Opaque);
            return EncodePng(opaque);
        }
        if (channel == ChannelMapping.RGBA)
        {
            using var pixels = bitmap.PeekPixels();
            return EncodePng(pixels);
        }
        // Swizzled channels, e.g. alpha-green DXT5nm
        var info = bitmap.Info.WithAlphaType(channel.Count < 4 ? SKAlphaType.Opaque : SKAlphaType.Unpremul).WithColorType(SKColorType.Rgba8888);
        using var swizzled = new SKBitmap(info);
        using var swizzledPixels = swizzled.PeekPixels();
        using var sourcePixels = bitmap.PeekPixels();
        var targetColors = swizzledPixels.GetPixelSpan<SKColor>();
        var sourceColors = sourcePixels.GetPixelSpan<SKColor>();
        for (var i = 0; i < sourceColors.Length; i++)
        {
            var color = (uint)targetColors[i];
            for (var j = 0; j < channel.Count; j++)
                color |= (uint)Component(sourceColors[i], channel[j]) << (j * 8);
            targetColors[i] = new SKColor(color);
        }
        return EncodePng(swizzledPixels);
    }

    private static byte Component(SKColor color, byte channel) => channel switch
    {
        ChannelMapping.R => color.Red,
        ChannelMapping.G => color.Green,
        ChannelMapping.B => color.Blue,
        _ => color.Alpha,
    };

    private static byte[] EncodePng(SKPixmap pixels)
    {
        using var png = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, zLibLevel: 4));
        return png is null ? [] : png.ToArray();
    }

    // vpkstuff: a PNG downscaled to at most maxDim on its longest side; the original when it is small
    // enough, when the smaller one is not smaller, or when it does not decode.
    private static byte[] DownscalePreviewPng(byte[] png, int maxDim)
    {
        try
        {
            using var bmp = SKBitmap.Decode(png);
            if (bmp is null)
                return png;
            var longest = Math.Max(bmp.Width, bmp.Height);
            if (longest <= maxDim)
                return png;
            var scale = (double)maxDim / longest;
            var width = Math.Max(1, (int)Math.Round(bmp.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bmp.Height * scale));
            using var resized = bmp.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (resized is null)
                return png;
            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null || data.Size == 0 || data.Size >= png.Length)
                return png;
            return data.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return png;
        }
    }

    // VRF's TextureExtract.TexturePacker: single channels gathered into one bitmap, scaled to the largest input.
    private sealed class TexturePacker(SKColor defaultColor) : IDisposable
    {
        private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

        public SKBitmap? Bitmap { get; private set; }

        public void Collect(SKBitmap source, ChannelMapping sourceChannel, ChannelMapping targetChannel)
        {
            using var sourcePixels = source.PeekPixels();
            if (Bitmap is null)
            {
                Bitmap = new SKBitmap(sourcePixels.Width, sourcePixels.Height, true);
                if (defaultColor != SKColors.Black)
                {
                    using var pixels = Bitmap.PeekPixels();
                    pixels.GetPixelSpan<SKColor>().Fill(defaultColor);
                }
            }
            if (Bitmap.Width < sourcePixels.Width || Bitmap.Height < sourcePixels.Height)
            {
                var larger = new SKBitmap(sourcePixels.Width, sourcePixels.Height, true);
                using (Bitmap)
                {
                    using var oldPixels = Bitmap.PeekPixels();
                    using var newPixels = larger.PeekPixels();
                    if (!oldPixels.ScalePixels(newPixels, Sampling))
                        throw new InvalidOperationException("Failed to scale up the packed texture.");
                }
                Bitmap = larger;
            }
            else if (Bitmap.Width > sourcePixels.Width || Bitmap.Height > sourcePixels.Height)
            {
                using var scaled = new SKBitmap(Bitmap.Width, Bitmap.Height, true);
                using var scaledPixels = scaled.PeekPixels();
                if (!sourcePixels.ScalePixels(scaledPixels, Sampling))
                    throw new InvalidOperationException("Failed to scale up an input of the packed texture.");
                using var target = Bitmap.PeekPixels();
                CopyChannel(scaledPixels, sourceChannel, target, targetChannel);
                return;
            }
            using var targetPixels = Bitmap.PeekPixels();
            CopyChannel(sourcePixels, sourceChannel, targetPixels, targetChannel);
        }

        private static void CopyChannel(SKPixmap source, ChannelMapping sourceChannel, SKPixmap target, ChannelMapping targetChannel)
        {
            var from = source.GetPixelSpan<SKColor>();
            var to = target.GetPixelSpan<SKColor>();
            for (var i = 0; i < from.Length; i++)
            {
                var value = Component(from[i], sourceChannel[0]);
                to[i] = targetChannel[0] switch
                {
                    ChannelMapping.R => to[i].WithRed(value),
                    ChannelMapping.G => to[i].WithGreen(value),
                    ChannelMapping.B => to[i].WithBlue(value),
                    _ => to[i].WithAlpha(value),
                };
            }
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }
}

/// <summary>A draw call's primitive topology (<c>m_nPrimitiveType</c>).</summary>
public enum RenderPrimitiveType
{
#pragma warning disable CS1591
    RENDER_PRIM_POINTS = 0x0,
    RENDER_PRIM_LINES = 0x1,
    RENDER_PRIM_LINES_WITH_ADJACENCY = 0x2,
    RENDER_PRIM_LINE_STRIP = 0x3,
    RENDER_PRIM_LINE_STRIP_WITH_ADJACENCY = 0x4,
    RENDER_PRIM_TRIANGLES = 0x5,
    RENDER_PRIM_TRIANGLES_WITH_ADJACENCY = 0x6,
    RENDER_PRIM_TRIANGLE_STRIP = 0x7,
    RENDER_PRIM_TRIANGLE_STRIP_WITH_ADJACENCY = 0x8,
    RENDER_PRIM_INSTANCED_QUADS = 0x9,
    RENDER_PRIM_HETEROGENOUS = 0xA,
#pragma warning restore CS1591
}
