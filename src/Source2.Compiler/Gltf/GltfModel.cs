using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Source2.Compiler.Gltf;

/// <summary>An accessor's component type (glTF <c>componentType</c>).</summary>
public enum GltfComponentType
{
    /// <summary>Unsigned byte.</summary>
    UnsignedByte = 5121,

    /// <summary>Unsigned short.</summary>
    UnsignedShort = 5123,

    /// <summary>Unsigned int.</summary>
    UnsignedInt = 5125,

    /// <summary>Float.</summary>
    Float = 5126,
}

/// <summary>A buffer view's target (glTF <c>target</c>).</summary>
public enum GltfBufferTarget
{
    /// <summary>No target (images, animation data).</summary>
    None = 0,

    /// <summary>Vertex attributes.</summary>
    ArrayBuffer = 34962,

    /// <summary>Indices.</summary>
    ElementArrayBuffer = 34963,
}

/// <summary>A contiguous run of the binary buffer.</summary>
public sealed class GltfBufferView
{
    internal GltfBufferView(int index, byte[] data, int stride, GltfBufferTarget target) => (Index, Data, ByteStride, Target) = (index, data, stride, target);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The bytes.</summary>
    public byte[] Data { get; }

    /// <summary>The stride between elements, 0 when tightly packed and unstated.</summary>
    public int ByteStride { get; }

    /// <summary>The target.</summary>
    public GltfBufferTarget Target { get; }
}

/// <summary>A typed view of a buffer view.</summary>
public sealed class GltfAccessor
{
    internal GltfAccessor(int index) => Index = index;

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>An optional name.</summary>
    public string? Name { get; set; }

    /// <summary>The buffer view.</summary>
    public required GltfBufferView BufferView { get; init; }

    /// <summary>The offset into the view.</summary>
    public int ByteOffset { get; init; }

    /// <summary>Elements.</summary>
    public required int Count { get; init; }

    /// <summary>The component type.</summary>
    public required GltfComponentType ComponentType { get; init; }

    /// <summary><c>SCALAR</c>, <c>VEC2</c>, <c>VEC3</c>, <c>VEC4</c> or <c>MAT4</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Per-component minimum, when stated.</summary>
    public float[]? Min { get; set; }

    /// <summary>Per-component maximum, when stated.</summary>
    public float[]? Max { get; set; }
}

/// <summary>A node: a transform, optionally a mesh and a skin, and children.</summary>
public sealed class GltfNode
{
    private readonly GltfModel _model;
    private Vector3 _translation;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _scale = Vector3.One;

    internal GltfNode(GltfModel model, int index, string? name, GltfNode? parent) => (_model, Index, Name, Parent) = (model, index, name, parent);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The name.</summary>
    public string? Name { get; set; }

    /// <summary>The parent node, null for a scene root.</summary>
    public GltfNode? Parent { get; }

    /// <summary>The children, in order.</summary>
    public List<GltfNode> Children { get; } = [];

    /// <summary>The mesh it draws.</summary>
    public GltfMesh? Mesh { get; set; }

    /// <summary>The skin its mesh is bound to.</summary>
    public GltfSkin? Skin { get; set; }

    /// <summary>The local translation.</summary>
    public Vector3 Translation { get => _translation; set => _translation = value; }

    /// <summary>The local rotation.</summary>
    public Quaternion Rotation { get => _rotation; set => _rotation = value; }

    /// <summary>The local scale.</summary>
    public Vector3 Scale { get => _scale; set => _scale = value; }

    /// <summary>The transform relative to the parent: scale, then rotation, then translation.</summary>
    public Matrix4x4 LocalMatrix
    {
        get => Matrix4x4.CreateScale(_scale) * Matrix4x4.CreateFromQuaternion(_rotation) * Matrix4x4.CreateTranslation(_translation);
        set
        {
            if (!Matrix4x4.Decompose(value, out _scale, out _rotation, out _translation))
                throw new ArgumentException("A node transform must decompose into scale, rotation and translation.", nameof(value));
        }
    }

    /// <summary>The transform relative to the scene; setting it sets the local transform under the parent's.</summary>
    public Matrix4x4 WorldMatrix
    {
        get => Parent is null ? LocalMatrix : LocalMatrix * Parent.WorldMatrix;
        set
        {
            if (Parent is null)
            {
                LocalMatrix = value;
                return;
            }
            Matrix4x4.Invert(Parent.WorldMatrix, out var inverse);
            LocalMatrix = value * inverse;
        }
    }

    /// <summary>A child node.</summary>
    public GltfNode CreateNode(string? name = null)
    {
        var child = _model.AddNode(name, this);
        Children.Add(child);
        return child;
    }
}

/// <summary>A scene: its root nodes.</summary>
public sealed class GltfScene
{
    private readonly GltfModel _model;

    internal GltfScene(GltfModel model, string? name) => (_model, Name) = (model, name);

    /// <summary>The name.</summary>
    public string? Name { get; set; }

    /// <summary>The root nodes.</summary>
    public List<GltfNode> Nodes { get; } = [];

    /// <summary>A root node.</summary>
    public GltfNode CreateNode(string? name = null)
    {
        var node = _model.AddNode(name, null);
        Nodes.Add(node);
        return node;
    }
}

/// <summary>A mesh: its primitives.</summary>
public sealed class GltfMesh
{
    internal GltfMesh(int index, string? name) => (Index, Name) = (index, name);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The name.</summary>
    public string? Name { get; set; }

    /// <summary>The primitives, in order.</summary>
    public List<GltfPrimitive> Primitives { get; } = [];

    /// <summary>Extras written with the mesh.</summary>
    public JsonObject? Extras { get; set; }

    /// <summary>A new primitive.</summary>
    public GltfPrimitive CreatePrimitive()
    {
        var primitive = new GltfPrimitive();
        Primitives.Add(primitive);
        return primitive;
    }
}

/// <summary>One draw: vertex attributes, indices, a material, morph targets.</summary>
public sealed class GltfPrimitive
{
    /// <summary>Vertex attributes by semantic, in the order they were set.</summary>
    public List<KeyValuePair<string, GltfAccessor>> Attributes { get; } = [];

    /// <summary>The indices.</summary>
    public GltfAccessor? Indices { get; set; }

    /// <summary>The material.</summary>
    public GltfMaterial? Material { get; set; }

    /// <summary>The topology: 0 points, 4 triangles.</summary>
    public int Mode { get; set; } = 4;

    /// <summary>Morph targets, each attribute to accessor.</summary>
    public List<Dictionary<string, GltfAccessor>> Targets { get; } = [];

    /// <summary>Set (or replace) an attribute.</summary>
    public void SetAttribute(string semantic, GltfAccessor accessor)
    {
        var at = Attributes.FindIndex(a => a.Key == semantic);
        if (at >= 0)
            Attributes[at] = new(semantic, accessor);
        else
            Attributes.Add(new(semantic, accessor));
    }

    /// <summary>An attribute's accessor, or null.</summary>
    public GltfAccessor? GetAttribute(string semantic) => Attributes.Find(a => a.Key == semantic).Value;
}

/// <summary>A skin: joints and their inverse bind matrices.</summary>
public sealed class GltfSkin
{
    internal GltfSkin(int index) => Index = index;

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The joints, in vertex joint index order.</summary>
    public List<GltfNode> Joints { get; } = [];

    /// <summary>The inverse bind matrices, one per joint.</summary>
    public GltfAccessor? InverseBindMatrices { get; set; }
}

/// <summary>A texture reference from a material.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="TexCoord">The texture coordinate set.</param>
public sealed record GltfTextureInfo(GltfTexture Texture, int TexCoord = 0);

/// <summary>A metallic-roughness material, with the unlit and specular extensions.</summary>
public sealed class GltfMaterial
{
    internal GltfMaterial(int index, string? name) => (Index, Name) = (index, name);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The name.</summary>
    public string? Name { get; set; }

    /// <summary>The base colour factor (linear RGBA).</summary>
    public Vector4 BaseColorFactor { get; set; } = Vector4.One;

    /// <summary>The base colour texture.</summary>
    public GltfTextureInfo? BaseColorTexture { get; set; }

    /// <summary>The metallic factor.</summary>
    public float MetallicFactor { get; set; } = 1f;

    /// <summary>The roughness factor.</summary>
    public float RoughnessFactor { get; set; } = 1f;

    /// <summary>The metallic (B) and roughness (G) texture.</summary>
    public GltfTextureInfo? MetallicRoughnessTexture { get; set; }

    /// <summary>The normal texture.</summary>
    public GltfTextureInfo? NormalTexture { get; set; }

    /// <summary>The occlusion texture (R).</summary>
    public GltfTextureInfo? OcclusionTexture { get; set; }

    /// <summary>The emissive texture.</summary>
    public GltfTextureInfo? EmissiveTexture { get; set; }

    /// <summary>The emissive factor.</summary>
    public Vector3 EmissiveFactor { get; set; }

    /// <summary><c>OPAQUE</c>, <c>MASK</c> or <c>BLEND</c>.</summary>
    public string AlphaMode { get; set; } = "OPAQUE";

    /// <summary>The cutoff, written for <c>MASK</c>.</summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    /// <summary>Whether back faces are drawn.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>KHR_materials_unlit.</summary>
    public bool Unlit { get; set; }

    /// <summary>KHR_materials_specular: whether the extension is written.</summary>
    public bool Specular { get; set; }

    /// <summary>KHR_materials_specular <c>specularTexture</c> (A).</summary>
    public GltfTextureInfo? SpecularTexture { get; set; }

    /// <summary>KHR_materials_specular <c>specularColorFactor</c>.</summary>
    public Vector3 SpecularColorFactor { get; set; } = Vector3.One;

    /// <summary>Extras written with the material.</summary>
    public JsonObject? Extras { get; set; }
}

/// <summary>A sampler: filters and wrapping.</summary>
public sealed class GltfSampler
{
    internal GltfSampler(int index) => Index = index;

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>Magnification filter (9729 linear).</summary>
    public int MagFilter { get; init; } = 9729;

    /// <summary>Minification filter (9987 linear mipmap linear).</summary>
    public int MinFilter { get; init; } = 9987;

    /// <summary>S wrapping (10497 repeat).</summary>
    public int WrapS { get; init; } = 10497;

    /// <summary>T wrapping (10497 repeat).</summary>
    public int WrapT { get; init; } = 10497;
}

/// <summary>An image, embedded in the binary buffer.</summary>
public sealed class GltfImage
{
    internal GltfImage(int index, string? name) => (Index, Name) = (index, name);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The name.</summary>
    public string? Name { get; set; }

    /// <summary>The encoded image.</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>Its media type.</summary>
    public string MimeType { get; set; } = "image/png";
}

/// <summary>A texture: an image and a sampler.</summary>
public sealed class GltfTexture
{
    internal GltfTexture(int index, GltfImage image, GltfSampler? sampler) => (Index, Image, Sampler) = (index, image, sampler);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The name.</summary>
    public string? Name { get; set; }

    /// <summary>The image.</summary>
    public GltfImage Image { get; }

    /// <summary>The sampler.</summary>
    public GltfSampler? Sampler { get; }
}

/// <summary>An animation: samplers (time to value) and the node properties they drive.</summary>
public sealed class GltfAnimation
{
    internal GltfAnimation(int index, string name) => (Index, Name) = (index, name);

    /// <summary>Its index in the model.</summary>
    public int Index { get; }

    /// <summary>The name.</summary>
    public string Name { get; }

    /// <summary>Samplers: input times, output values, interpolation.</summary>
    public List<(GltfAccessor Input, GltfAccessor Output, string Interpolation)> Samplers { get; } = [];

    /// <summary>Channels: sampler index, node, path (<c>translation</c>, <c>rotation</c>, <c>scale</c>, <c>weights</c>).</summary>
    public List<(int Sampler, GltfNode Node, string Path)> Channels { get; } = [];

    /// <summary>Extras written with the animation.</summary>
    public JsonObject? Extras { get; set; }
}

/// <summary>
/// A glTF 2.0 document built in memory and written as GLB (one JSON chunk, one binary chunk) by this
/// library alone: the subset of glTF the model exporter writes (docs/GLTF.md).
/// </summary>
public sealed class GltfModel
{
    /// <summary>The <c>asset.generator</c>.</summary>
    public string Generator { get; set; } = "Source2.Compiler";

    /// <summary>The scenes; the first is the default.</summary>
    public List<GltfScene> Scenes { get; } = [];

    /// <summary>Every node.</summary>
    public List<GltfNode> Nodes { get; } = [];

    /// <summary>Every mesh.</summary>
    public List<GltfMesh> Meshes { get; } = [];

    /// <summary>Every accessor.</summary>
    public List<GltfAccessor> Accessors { get; } = [];

    /// <summary>Every buffer view.</summary>
    public List<GltfBufferView> BufferViews { get; } = [];

    /// <summary>Every material.</summary>
    public List<GltfMaterial> Materials { get; } = [];

    /// <summary>Every texture.</summary>
    public List<GltfTexture> Textures { get; } = [];

    /// <summary>Every image.</summary>
    public List<GltfImage> Images { get; } = [];

    /// <summary>Every sampler.</summary>
    public List<GltfSampler> Samplers { get; } = [];

    /// <summary>Every skin.</summary>
    public List<GltfSkin> Skins { get; } = [];

    /// <summary>Every animation.</summary>
    public List<GltfAnimation> Animations { get; } = [];

    /// <summary>A scene.</summary>
    public GltfScene CreateScene(string? name)
    {
        var scene = new GltfScene(this, name);
        Scenes.Add(scene);
        return scene;
    }

    internal GltfNode AddNode(string? name, GltfNode? parent)
    {
        var node = new GltfNode(this, Nodes.Count, name, parent);
        Nodes.Add(node);
        return node;
    }

    /// <summary>A mesh.</summary>
    public GltfMesh CreateMesh(string? name)
    {
        var mesh = new GltfMesh(Meshes.Count, name);
        Meshes.Add(mesh);
        return mesh;
    }

    /// <summary>A material.</summary>
    public GltfMaterial CreateMaterial(string? name)
    {
        var material = new GltfMaterial(Materials.Count, name);
        Materials.Add(material);
        return material;
    }

    /// <summary>A sampler.</summary>
    public GltfSampler CreateSampler()
    {
        var sampler = new GltfSampler(Samplers.Count);
        Samplers.Add(sampler);
        return sampler;
    }

    /// <summary>An image.</summary>
    public GltfImage CreateImage(string? name)
    {
        var image = new GltfImage(Images.Count, name);
        Images.Add(image);
        return image;
    }

    /// <summary>A texture.</summary>
    public GltfTexture CreateTexture(GltfImage image, GltfSampler? sampler)
    {
        var texture = new GltfTexture(Textures.Count, image, sampler);
        Textures.Add(texture);
        return texture;
    }

    /// <summary>An animation.</summary>
    public GltfAnimation CreateAnimation(string name)
    {
        var animation = new GltfAnimation(Animations.Count, name);
        Animations.Add(animation);
        return animation;
    }

    /// <summary>The animation of that name, created when there is none.</summary>
    public GltfAnimation UseAnimation(string name) => Animations.Find(a => a.Name == name) ?? CreateAnimation(name);

    /// <summary>A buffer view over <paramref name="data"/>.</summary>
    public GltfBufferView CreateBufferView(byte[] data, int stride = 0, GltfBufferTarget target = GltfBufferTarget.None)
    {
        var view = new GltfBufferView(BufferViews.Count, data, stride, target);
        BufferViews.Add(view);
        return view;
    }

    /// <summary>An accessor.</summary>
    public GltfAccessor CreateAccessor(GltfBufferView view, int count, GltfComponentType componentType, string type, int byteOffset = 0)
    {
        var accessor = new GltfAccessor(Accessors.Count) { BufferView = view, Count = count, ComponentType = componentType, Type = type, ByteOffset = byteOffset };
        Accessors.Add(accessor);
        return accessor;
    }

    /// <summary>A float accessor over <paramref name="values"/>, each <paramref name="type"/> wide, in its own vertex view.</summary>
    public GltfAccessor CreateFloatAccessor<T>(T[] values, string type, GltfBufferTarget target = GltfBufferTarget.ArrayBuffer, bool bounds = false)
        where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var accessor = CreateAccessor(CreateBufferView(bytes, 0, target), values.Length, GltfComponentType.Float, type);
        if (bounds && values.Length > 0)
        {
            var floats = MemoryMarshal.Cast<T, float>(values.AsSpan());
            var width = floats.Length / values.Length;
            var min = new float[width];
            var max = new float[width];
            System.Array.Fill(min, float.MaxValue);
            System.Array.Fill(max, float.MinValue);
            for (var i = 0; i < floats.Length; i++)
            {
                min[i % width] = Math.Min(min[i % width], floats[i]);
                max[i % width] = Math.Max(max[i % width], floats[i]);
            }
            accessor.Min = min;
            accessor.Max = max;
        }
        return accessor;
    }

    /// <summary>A 32-bit index accessor in its own element view.</summary>
    public GltfAccessor CreateIndexAccessor(int[] indices)
    {
        var bytes = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray();
        return CreateAccessor(CreateBufferView(bytes, 0, GltfBufferTarget.ElementArrayBuffer), indices.Length, GltfComponentType.UnsignedInt, "SCALAR");
    }

    /// <summary>
    /// A skin over <paramref name="joints"/> whose inverse bind matrices bind them as they stand now,
    /// relative to <paramref name="meshWorldMatrix"/>.
    /// </summary>
    public GltfSkin CreateSkin(IReadOnlyList<GltfNode> joints, Matrix4x4 meshWorldMatrix)
    {
        var skin = new GltfSkin(Skins.Count);
        Skins.Add(skin);
        var matrices = new Matrix4x4[joints.Count];
        for (var i = 0; i < joints.Count; i++)
        {
            skin.Joints.Add(joints[i]);
            Matrix4x4.Invert(joints[i].WorldMatrix, out var inverse);
            var bind = meshWorldMatrix * inverse;
            // glTF wants an affine matrix exactly; inversion leaves a hair in the last column.
            (bind.M14, bind.M24, bind.M34, bind.M44) = (0, 0, 0, 1);
            matrices[i] = bind;
        }
        skin.InverseBindMatrices = CreateFloatAccessor(matrices, "MAT4", GltfBufferTarget.None);
        return skin;
    }

    /// <summary>The document as GLB.</summary>
    /// <exception cref="NotSupportedException">The binary data would exceed what one GLB can hold.</exception>
    public byte[] ToGlb()
    {
        // Lay out the binary chunk: every view, then every image, each on a 4-byte boundary.
        var offsets = new long[BufferViews.Count];
        var imageViews = new (long Offset, int Length)[Images.Count];
        long size = 0;
        for (var i = 0; i < BufferViews.Count; i++)
        {
            offsets[i] = size;
            size = Align4(size + BufferViews[i].Data.Length);
        }
        for (var i = 0; i < Images.Count; i++)
        {
            imageViews[i] = (size, Images[i].Content.Length);
            size = Align4(size + Images[i].Content.Length);
        }
        if (size >= int.MaxValue - 1024)
            throw new NotSupportedException($"The binary data ({size} bytes) is too large for one GLB.");

        var json = WriteJson(offsets, imageViews, size);
        var jsonLength = (int)Align4(json.Length);
        var total = 12 + 8 + jsonLength + 8 + (int)size;
        var glb = new byte[total];
        var span = glb.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x46546C67); // glTF
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0x4E4F534A); // JSON
        json.CopyTo(span[20..]);
        span.Slice(20 + json.Length, jsonLength - json.Length).Fill((byte)' ');
        var bin = 20 + jsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(span[bin..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(bin + 4)..], 0x004E4942); // BIN
        for (var i = 0; i < BufferViews.Count; i++)
            BufferViews[i].Data.CopyTo(span[(bin + 8 + (int)offsets[i])..]);
        for (var i = 0; i < Images.Count; i++)
            Images[i].Content.CopyTo(span[(bin + 8 + (int)imageViews[i].Offset)..]);
        return glb;
    }

    private static long Align4(long n) => (n + 3) & ~3L;

    private byte[] WriteJson(long[] offsets, (long Offset, int Length)[] imageViews, long binSize)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", Generator);
            w.WriteEndObject();

            var extensions = new SortedSet<string>(StringComparer.Ordinal);
            if (Materials.Any(m => m.Unlit))
                extensions.Add("KHR_materials_unlit");
            if (Materials.Any(m => m.Specular))
                extensions.Add("KHR_materials_specular");
            if (extensions.Count > 0)
            {
                w.WriteStartArray("extensionsUsed");
                foreach (var e in extensions)
                    w.WriteStringValue(e);
                w.WriteEndArray();
            }

            if (Scenes.Count > 0)
            {
                w.WriteNumber("scene", 0);
                w.WriteStartArray("scenes");
                foreach (var scene in Scenes)
                {
                    w.WriteStartObject();
                    if (scene.Name is not null)
                        w.WriteString("name", scene.Name);
                    Indices(w, "nodes", scene.Nodes.Select(n => n.Index));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            WriteArray(w, "nodes", Nodes, node =>
            {
                if (node.Name is not null)
                    w.WriteString("name", node.Name);
                if (node.Children.Count > 0)
                    Indices(w, "children", node.Children.Select(c => c.Index));
                if (node.Mesh is not null)
                    w.WriteNumber("mesh", node.Mesh.Index);
                if (node.Skin is not null)
                    w.WriteNumber("skin", node.Skin.Index);
                if (node.Translation != Vector3.Zero)
                    Floats(w, "translation", node.Translation.X, node.Translation.Y, node.Translation.Z);
                if (node.Rotation != Quaternion.Identity)
                    Floats(w, "rotation", node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W);
                if (node.Scale != Vector3.One)
                    Floats(w, "scale", node.Scale.X, node.Scale.Y, node.Scale.Z);
            });

            WriteArray(w, "meshes", Meshes, mesh =>
            {
                if (mesh.Name is not null)
                    w.WriteString("name", mesh.Name);
                w.WriteStartArray("primitives");
                foreach (var p in mesh.Primitives)
                {
                    w.WriteStartObject();
                    w.WriteStartObject("attributes");
                    foreach (var (semantic, accessor) in p.Attributes)
                        w.WriteNumber(semantic, accessor.Index);
                    w.WriteEndObject();
                    if (p.Indices is not null)
                        w.WriteNumber("indices", p.Indices.Index);
                    if (p.Material is not null)
                        w.WriteNumber("material", p.Material.Index);
                    if (p.Mode != 4)
                        w.WriteNumber("mode", p.Mode);
                    if (p.Targets.Count > 0)
                    {
                        w.WriteStartArray("targets");
                        foreach (var target in p.Targets)
                        {
                            w.WriteStartObject();
                            foreach (var (semantic, accessor) in target)
                                w.WriteNumber(semantic, accessor.Index);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Extras(w, mesh.Extras);
            });

            WriteArray(w, "skins", Skins, skin =>
            {
                Indices(w, "joints", skin.Joints.Select(j => j.Index));
                if (skin.InverseBindMatrices is not null)
                    w.WriteNumber("inverseBindMatrices", skin.InverseBindMatrices.Index);
            });

            WriteArray(w, "materials", Materials, m =>
            {
                if (m.Name is not null)
                    w.WriteString("name", m.Name);
                w.WriteStartObject("pbrMetallicRoughness");
                if (m.BaseColorFactor != Vector4.One)
                    Floats(w, "baseColorFactor", m.BaseColorFactor.X, m.BaseColorFactor.Y, m.BaseColorFactor.Z, m.BaseColorFactor.W);
                TextureInfo(w, "baseColorTexture", m.BaseColorTexture);
                if (m.MetallicFactor != 1f)
                    w.WriteNumber("metallicFactor", m.MetallicFactor);
                if (m.RoughnessFactor != 1f)
                    w.WriteNumber("roughnessFactor", m.RoughnessFactor);
                TextureInfo(w, "metallicRoughnessTexture", m.MetallicRoughnessTexture);
                w.WriteEndObject();
                TextureInfo(w, "normalTexture", m.NormalTexture);
                TextureInfo(w, "occlusionTexture", m.OcclusionTexture);
                TextureInfo(w, "emissiveTexture", m.EmissiveTexture);
                if (m.EmissiveFactor != Vector3.Zero)
                    Floats(w, "emissiveFactor", m.EmissiveFactor.X, m.EmissiveFactor.Y, m.EmissiveFactor.Z);
                if (m.AlphaMode != "OPAQUE")
                    w.WriteString("alphaMode", m.AlphaMode);
                if (m.AlphaMode == "MASK" && m.AlphaCutoff != 0.5f)
                    w.WriteNumber("alphaCutoff", m.AlphaCutoff);
                if (m.DoubleSided)
                    w.WriteBoolean("doubleSided", true);
                if (m.Unlit || m.Specular)
                {
                    w.WriteStartObject("extensions");
                    if (m.Unlit)
                    {
                        w.WriteStartObject("KHR_materials_unlit");
                        w.WriteEndObject();
                    }
                    if (m.Specular)
                    {
                        w.WriteStartObject("KHR_materials_specular");
                        TextureInfo(w, "specularTexture", m.SpecularTexture);
                        if (m.SpecularColorFactor != Vector3.One)
                            Floats(w, "specularColorFactor", m.SpecularColorFactor.X, m.SpecularColorFactor.Y, m.SpecularColorFactor.Z);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                Extras(w, m.Extras);
            });

            WriteArray(w, "textures", Textures, t =>
            {
                if (t.Name is not null)
                    w.WriteString("name", t.Name);
                w.WriteNumber("source", t.Image.Index);
                if (t.Sampler is not null)
                    w.WriteNumber("sampler", t.Sampler.Index);
            });

            WriteArray(w, "images", Images, image =>
            {
                if (image.Name is not null)
                    w.WriteString("name", image.Name);
                w.WriteNumber("bufferView", BufferViews.Count + image.Index);
                w.WriteString("mimeType", image.MimeType);
            });

            WriteArray(w, "samplers", Samplers, s =>
            {
                w.WriteNumber("magFilter", s.MagFilter);
                w.WriteNumber("minFilter", s.MinFilter);
                w.WriteNumber("wrapS", s.WrapS);
                w.WriteNumber("wrapT", s.WrapT);
            });

            WriteArray(w, "animations", Animations, a =>
            {
                w.WriteString("name", a.Name);
                w.WriteStartArray("channels");
                foreach (var (sampler, node, path) in a.Channels)
                {
                    w.WriteStartObject();
                    w.WriteNumber("sampler", sampler);
                    w.WriteStartObject("target");
                    w.WriteNumber("node", node.Index);
                    w.WriteString("path", path);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("samplers");
                foreach (var (input, output, interpolation) in a.Samplers)
                {
                    w.WriteStartObject();
                    w.WriteNumber("input", input.Index);
                    w.WriteNumber("output", output.Index);
                    w.WriteString("interpolation", interpolation);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Extras(w, a.Extras);
            });

            WriteArray(w, "accessors", Accessors, a =>
            {
                if (a.Name is not null)
                    w.WriteString("name", a.Name);
                w.WriteNumber("bufferView", a.BufferView.Index);
                if (a.ByteOffset != 0)
                    w.WriteNumber("byteOffset", a.ByteOffset);
                w.WriteNumber("componentType", (int)a.ComponentType);
                w.WriteNumber("count", a.Count);
                w.WriteString("type", a.Type);
                if (a.Min is not null)
                    Floats(w, "min", a.Min);
                if (a.Max is not null)
                    Floats(w, "max", a.Max);
            });

            w.WriteStartArray("bufferViews");
            for (var i = 0; i < BufferViews.Count; i++)
            {
                var v = BufferViews[i];
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                if (offsets[i] != 0)
                    w.WriteNumber("byteOffset", offsets[i]);
                w.WriteNumber("byteLength", v.Data.Length);
                if (v.ByteStride != 0)
                    w.WriteNumber("byteStride", v.ByteStride);
                if (v.Target != GltfBufferTarget.None)
                    w.WriteNumber("target", (int)v.Target);
                w.WriteEndObject();
            }
            foreach (var (offset, length) in imageViews)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", offset);
                w.WriteNumber("byteLength", length);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", binSize);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteArray<T>(Utf8JsonWriter w, string name, List<T> items, Action<T> write)
    {
        if (items.Count == 0)
            return;
        w.WriteStartArray(name);
        foreach (var item in items)
        {
            w.WriteStartObject();
            write(item);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Indices(Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
            w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static void Floats(Utf8JsonWriter w, string name, params float[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
            w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static void TextureInfo(Utf8JsonWriter w, string name, GltfTextureInfo? info)
    {
        if (info is null)
            return;
        w.WriteStartObject(name);
        w.WriteNumber("index", info.Texture.Index);
        if (info.TexCoord != 0)
            w.WriteNumber("texCoord", info.TexCoord);
        w.WriteEndObject();
    }

    private static void Extras(Utf8JsonWriter w, JsonObject? extras)
    {
        if (extras is null || extras.Count == 0)
            return;
        w.WritePropertyName("extras");
        extras.WriteTo(w);
    }
}
