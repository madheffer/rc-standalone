// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/ModelAnimation/Skeleton.cs, Bone.cs and Resource/Enums/ModelSkeletonBoneFlags.cs,
// so a host moving off VRF reads the same skeleton. Changes: a model with no skeleton gives an
// empty one, and a bone whose bind pose cannot be inverted is an InvalidDataException.
using System.Diagnostics;
using System.Numerics;
using ValveKeyValue;

namespace Source2.Compiler.Meshes;

/// <summary>A model skeleton bone's flags (<c>m_nFlag</c>).</summary>
[Flags]
public enum ModelSkeletonBoneFlags
{
    /// <summary>No bone flags set.</summary>
    NoBoneFlags = 0x0,
    /// <summary>Used by a flex driver.</summary>
    BoneFlexDriver = 0x4,
    /// <summary>Used by cloth simulation.</summary>
    Cloth = 0x8,
    /// <summary>Used by physics.</summary>
    Physics = 0x10,
    /// <summary>Used by an attachment point.</summary>
    Attachment = 0x20,
    /// <summary>Used by animation.</summary>
    Animation = 0x40,
    /// <summary>Used by a mesh.</summary>
    Mesh = 0x80,
    /// <summary>Used by a hitbox.</summary>
    Hitbox = 0x100,
    /// <summary>Used as a retarget source.</summary>
    RetargetSrc = 0x200,
    /// <summary>Used by vertex data at LOD 0.</summary>
    BoneUsedByVertexLod0 = 0x400,
    /// <summary>Used by vertex data at LOD 1.</summary>
    BoneUsedByVertexLod1 = 0x800,
    /// <summary>Used by vertex data at LOD 2.</summary>
    BoneUsedByVertexLod2 = 0x1000,
    /// <summary>Used by vertex data at LOD 3.</summary>
    BoneUsedByVertexLod3 = 0x2000,
    /// <summary>Used by vertex data at LOD 4.</summary>
    BoneUsedByVertexLod4 = 0x4000,
    /// <summary>Used by vertex data at LOD 5.</summary>
    BoneUsedByVertexLod5 = 0x8000,
    /// <summary>Used by vertex data at LOD 6.</summary>
    BoneUsedByVertexLod6 = 0x10000,
    /// <summary>Used by vertex data at LOD 7.</summary>
    BoneUsedByVertexLod7 = 0x20000,
    /// <summary>Read by bone merge.</summary>
    BoneMergeRead = 0x40000,
    /// <summary>Written by bone merge.</summary>
    BoneMergeWrite = 0x80000,
    /// <summary>Rotation pre-aligned; no further alignment during blending.</summary>
    BlendPrealigned = 0x100000,
    /// <summary>A rigid (fixed) length.</summary>
    RigidLength = 0x200000,
    /// <summary>Driven procedurally at runtime.</summary>
    Procedural = 0x400000,
    /// <summary>Procedural cloth: <see cref="Cloth"/> and <see cref="Procedural"/> (not a native flag).</summary>
    ProceduralCloth = Cloth | Procedural,
}

/// <summary>A bone of a <see cref="Skeleton"/>.</summary>
[DebuggerDisplay("{Name} (Index: {Index})")]
public sealed class Bone
{
    /// <summary>A bone at <paramref name="index"/> with its parent-space bind transform.</summary>
    /// <exception cref="InvalidDataException">The bind pose cannot be inverted.</exception>
    public Bone(int index, string name, Vector3 position, Quaternion rotation, ModelSkeletonBoneFlags flags)
    {
        Index = index;
        Name = name;
        Flags = flags;
        Position = position;
        Angle = rotation;
        BindPose = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        InverseBindPose = Matrix4x4.Invert(BindPose, out var inverse)
            ? inverse
            : throw new InvalidDataException($"Bone {name}'s bind pose cannot be inverted.");
    }

    /// <summary>The bone's index in <see cref="Skeleton.Bones"/>.</summary>
    public int Index { get; }

    /// <summary>The bone's flags.</summary>
    public ModelSkeletonBoneFlags Flags { get; }

    /// <summary>The parent bone, or null for a root.</summary>
    public Bone? Parent { get; private set; }

    /// <summary>The child bones.</summary>
    public List<Bone> Children { get; } = [];

    /// <summary>The bone's name.</summary>
    public string Name { get; }

    /// <summary>Position in parent space.</summary>
    public Vector3 Position { get; }

    /// <summary>Rotation in parent space.</summary>
    public Quaternion Angle { get; }

    /// <summary>The parent-space bind transform (rotation, then translation).</summary>
    public Matrix4x4 BindPose { get; }

    /// <summary>The inverse of <see cref="BindPose"/>.</summary>
    public Matrix4x4 InverseBindPose { get; }

    /// <summary>Whether the bone is driven by procedural cloth.</summary>
    public bool IsProceduralCloth => (Flags & ModelSkeletonBoneFlags.ProceduralCloth) == ModelSkeletonBoneFlags.ProceduralCloth;

    /// <summary>Whether the bone is a cloth control node, static nodes included.</summary>
    public bool IsClothControlNode => (Flags & ModelSkeletonBoneFlags.Cloth) == ModelSkeletonBoneFlags.Cloth;

    /// <summary>Make <paramref name="parent"/> this bone's parent.</summary>
    public void SetParent(Bone parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!Children.Contains(parent))
        {
            Parent = parent;
            parent.Children.Add(this);
        }
    }
}

/// <summary>A skeleton: bones in a hierarchy, looked up by name or by <see cref="StringToken"/>.</summary>
public sealed class Skeleton
{
    private readonly Dictionary<uint, int> _boneHashToIndex = [];

    private Skeleton()
    {
    }

    /// <summary>The skeleton's name (the model's <c>m_name</c>, or a .vnmskel's <c>m_ID</c>).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The bones with no parent.</summary>
    public Bone[] Roots { get; private set; } = [];

    /// <summary>Every bone, by index.</summary>
    public Bone[] Bones { get; private set; } = [];

    /// <summary>The first procedural cloth bone, or null.</summary>
    public Bone? ClothSimulationRoot { get; private set; }

    /// <summary>Each bone's authored bounding sphere radius in model space (0 for a bone without geometry).</summary>
    public float[] BoneSpheres { get; private set; } = [];

    /// <summary>The bone with token <paramref name="hash"/>, or null.</summary>
    public Bone? this[uint hash] => GetBoneIndex(hash) is var index and >= 0 ? Bones[index] : null;

    /// <summary>The bone called <paramref name="name"/> (ignoring case), or null.</summary>
    public Bone? this[string name] => this[StringToken.Get(name)];

    /// <summary>The index of the bone with token <paramref name="hash"/>, or -1.</summary>
    public int GetBoneIndex(uint hash) => _boneHashToIndex.TryGetValue(hash, out var index) ? index : -1;

    /// <summary>The index of the bone called <paramref name="name"/> (ignoring case), or -1.</summary>
    public int GetBoneIndex(string name) => GetBoneIndex(StringToken.Get(name));

    /// <summary>The skeleton of a model's DATA tree (<c>m_modelSkeleton</c>); empty when it has none.</summary>
    public static Skeleton FromModelData(KVObject modelData)
    {
        ArgumentNullException.ThrowIfNull(modelData);
        var skeleton = modelData.GetSubCollection("m_modelSkeleton") is { IsCollection: true } data ? FromModelSkeleton(data) : new Skeleton();
        skeleton.Name = modelData.GetStringProperty("m_name") ?? string.Empty;
        return skeleton;
    }

    private static Skeleton FromModelSkeleton(KVObject data)
    {
        var names = data.GetArray<string>("m_boneName") ?? [];
        var parents = data.GetIntegerArray("m_nParent");
        var flags = data.GetIntegerArray("m_nFlag");
        var positions = data.GetArray("m_bonePosParent") ?? [];
        var rotations = data.GetArray("m_boneRotParent") ?? [];
        if (parents.Length < names.Length || positions.Count < names.Length || rotations.Count < names.Length)
            throw new InvalidDataException($"The model skeleton names {names.Length} bones but its other arrays are shorter.");

        var s = new Skeleton { Bones = new Bone[names.Length] };
        if (data.ContainsKey("m_boneSphere"))
            s.BoneSpheres = data.GetFloatArray("m_boneSphere");
        for (var i = 0; i < names.Length; i++)
        {
            var bone = new Bone(i, names[i], positions[i].ToVector3(), rotations[i].ToQuaternion(),
                i < flags.Length ? (ModelSkeletonBoneFlags)flags[i] : ModelSkeletonBoneFlags.NoBoneFlags);
            s.Bones[i] = bone;
            if ((bone.Flags & ModelSkeletonBoneFlags.ProceduralCloth) == ModelSkeletonBoneFlags.Cloth && s.ClothSimulationRoot is null)
                s.ClothSimulationRoot = bone;
        }
        s.SetBoneParents(parents);
        return s;
    }

    /// <summary>The skeleton of a compiled NM skeleton's DATA tree (a <c>.vnmskel_c</c>).</summary>
    public static Skeleton FromSkeletonData(KVObject nmSkeleton)
    {
        ArgumentNullException.ThrowIfNull(nmSkeleton);
        var names = nmSkeleton.GetArray<string>("m_boneIDs") ?? [];
        var parents = nmSkeleton.GetIntegerArray("m_parentIndices");
        var transforms = nmSkeleton.GetArray("m_parentSpaceReferencePose") ?? [];
        if (parents.Length < names.Length || transforms.Count < names.Length)
            throw new InvalidDataException($"The NM skeleton names {names.Length} bones but its other arrays are shorter.");

        var s = new Skeleton { Name = nmSkeleton.GetStringProperty("m_ID") ?? string.Empty, Bones = new Bone[names.Length] };
        for (var i = 0; i < names.Length; i++)
        {
            var transform = transforms[i].ToTransform();
            s.Bones[i] = new Bone(i, names[i], transform.Position, transform.Rotation, ModelSkeletonBoneFlags.NoBoneFlags);
        }
        s.SetBoneParents(parents);
        return s;
    }

    /// <summary>
    /// A skeleton from a mesh's own render skeleton (<c>m_skeleton</c> of an MDAT), to skin a mesh that
    /// has no owning model. Bones keep their order and are parented by name (a name refers to its first
    /// bone; one naming an unknown parent is a root). Null when the render skeleton has no bones.
    /// </summary>
    public static Skeleton? FromRenderSkeleton(KVObject renderSkeleton)
    {
        ArgumentNullException.ThrowIfNull(renderSkeleton);
        var entries = renderSkeleton.GetArray("m_bones");
        if (entries is not { Count: > 0 })
            return null;

        var nameToIndex = new Dictionary<string, int>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
            nameToIndex.TryAdd(entries[i].GetStringProperty("m_boneName") ?? string.Empty, i);

        var modelSpace = new Matrix4x4[entries.Count];
        var parents = new long[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var inverse = entries[i].GetSubCollection("m_invBindPose").ToMatrix4x4();
            modelSpace[i] = Matrix4x4.Invert(inverse, out var bind) ? bind : Matrix4x4.Identity;
            var parentName = entries[i].GetStringProperty("m_parentName");
            parents[i] = !string.IsNullOrEmpty(parentName) && nameToIndex.TryGetValue(parentName, out var p) && p != i ? p : -1;
        }

        var s = new Skeleton { Bones = new Bone[entries.Count], BoneSpheres = new float[entries.Count] };
        for (var i = 0; i < entries.Count; i++)
        {
            s.BoneSpheres[i] = entries[i].GetFloatProperty("m_flSphereRadius");
            var parent = parents[i];
            var local = parent >= 0 && Matrix4x4.Invert(modelSpace[parent], out var parentInverse)
                ? modelSpace[i] * parentInverse
                : modelSpace[i];
            Matrix4x4.Decompose(local, out _, out var rotation, out var translation);
            s.Bones[i] = new Bone(i, entries[i].GetStringProperty("m_boneName") ?? string.Empty, translation, rotation, ModelSkeletonBoneFlags.NoBoneFlags);
        }
        s.SetBoneParents(parents);
        return s;
    }

    private void SetBoneParents(long[] parents)
    {
        var roots = new List<Bone>();
        foreach (var bone in Bones)
        {
            var parent = parents[bone.Index];
            if (parent == -1)
            {
                roots.Add(bone);
                continue;
            }
            if (parent < 0 || parent >= Bones.Length)
                throw new InvalidDataException($"Bone {bone.Name} names parent {parent}, outside the skeleton's {Bones.Length} bones.");
            bone.SetParent(Bones[parent]);
        }
        Roots = [.. roots];
        for (var i = 0; i < Bones.Length; i++)
            _boneHashToIndex[StringToken.Get(Bones[i].Name)] = i;
    }

    /// <summary>
    /// Each bone's model-space transform from <paramref name="local"/> parent-space transforms (one per
    /// bone; null for the bind pose), walking down from the roots. <paramref name="world"/> is indexed by
    /// bone index and at least as long as <see cref="Bones"/>.
    /// </summary>
    public void ComputeWorldPose(ReadOnlySpan<Matrix4x4> local, Span<Matrix4x4> world)
    {
        if (world.Length < Bones.Length)
            throw new ArgumentException($"world holds {world.Length} transforms for {Bones.Length} bones.", nameof(world));
        if (!local.IsEmpty && local.Length < Bones.Length)
            throw new ArgumentException($"local holds {local.Length} transforms for {Bones.Length} bones.", nameof(local));
        // Iterative so a deep chain cannot exhaust the stack: parents come before children.
        var stack = new Stack<(Bone Bone, Matrix4x4 Parent)>();
        foreach (var root in Roots)
            stack.Push((root, Matrix4x4.Identity));
        var visited = 0;
        while (stack.Count > 0)
        {
            if (++visited > Bones.Length)
                throw new InvalidDataException("The skeleton's parent links form a cycle.");
            var (bone, parent) = stack.Pop();
            var transform = (local.IsEmpty ? bone.BindPose : local[bone.Index]) * parent;
            world[bone.Index] = transform;
            foreach (var child in bone.Children)
                stack.Push((child, transform));
        }
    }
}
