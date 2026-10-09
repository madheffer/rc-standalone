using Source2.Compiler.Io;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Maps;

/// <summary>
/// The scene the light precompute traces (the map world's ray tracing
/// environment at world +0x2bd0, resourcecompiler 0923): a two level
/// structure. Every raytrace owner (a world mesh, a static prop's model, a
/// cable, terrain, a referenced map's world) keeps its own environment in
/// its own space; the world's holds one instance of it per placement, with
/// the placement's 3x4 and its inverse (FUN_18100fd50, FUN_181c13ac0,
/// FUN_181c19970). The owner's flag word sits in front of its environment
/// and a ray's mask skips the whole object; each triangle carries its own
/// flag word. See <see cref="Trace(Vector3, Vector3, uint)"/> for the ray.
/// </summary>
public sealed partial class EditorTraceScene : ILightTracer
{
    /// <summary>The light precompute's first pass mask (LightPrecompute_TraceRay).</summary>
    public const uint LightMask = 0xc00060b1;

    /// <summary>One placement of an owner's environment.</summary>
    public sealed class Instance
    {
        /// <summary>Local to world (instance +0x10) and world to local (+0x40, FUN_18125ade0).</summary>
        public float[] ToWorld = [], ToLocal = [];

        /// <summary>The owner's flag word (the environment's first word).</summary>
        public uint ObjectFlags;

        /// <summary>
        /// The flag word of the instance's own scene (its first word), which
        /// RefineNode reads: 0x10000 makes its kd leaves hold 32 triangles.
        /// </summary>
        internal uint SceneFlags;

        /// <summary>Per triangle: the 13 float record (FUN_180118e90), its flag word.</summary>
        internal float[] Records = [];
        internal ushort[] Flags = [];

        /// <summary>Where it came from, for diagnostics.</summary>
        public string Source = "";

        /// <summary>
        /// A scene of scenes instead of triangles: a model's scene holds one
        /// instance of each mesh's own scene at the identity (Model_RayScene),
        /// and a ray is taken into each level in turn.
        /// </summary>
        internal List<Instance>? Children;

        internal Vector3 Mins, Maxs;
        internal Bvh? Tree;
        internal TracerKd? Kd;

        /// <summary>
        /// The instance's box as Valve's scene holds it (RayScene_SetInstanceTransform
        /// +0x70): its scene's box (every local corner) through Matrix3x4_TransformAABB
        /// (18125cd10, centre and extents). Set by Prepare.
        /// </summary>
        internal (Vector3 Mins, Vector3 Maxs)? ValveBox;
        internal int Count => Children is { } c ? c.Count : Flags.Length;
    }

    /// <summary>A hit: world distance along the ray and the triangle's flag word.</summary>
    public readonly record struct SceneHit(float Distance, ushort Flags, int Instance, int Triangle);

    private readonly List<Instance> _instances;
    private readonly Bvh _top;

    public IReadOnlyList<Instance> Instances => _instances;

    public EditorTraceScene(List<Instance> instances)
    {
        _instances = [.. instances.Where(i => i.Count > 0)];
        foreach (var inst in _instances)
            Prepare(inst);
        _top = Bvh.Build(_instances.Count, i => (_instances[i].Mins, _instances[i].Maxs));
    }

    /// <summary>
    /// The scene of a map's own meshes (CMapMesh owners). Each mesh is one
    /// owner placed at its instance path times its own matrix with the scales
    /// on its columns (owner vf 0x10 = node vf 0xb0, FUN_18125bfe0). Its flag
    /// word (FUN_1810e11a0): 0x4000 under an entity, 0x20 when hidden,
    /// disableShadows 1 adds 0xa000 and 2 adds 0x2000. Its triangles are its
    /// faces in its own space (RayScene_AddFace, FUN_1813ccd20): a face with no
    /// material or with face flag 1 or 2 is left out, face flag 4 adds 0x20,
    /// the material's flag word (<see cref="TraceScene.MaterialFlags"/>) is
    /// the rest; the face is cut by <see cref="PolygonTriangulator"/>.
    /// Not added here: subdivided faces, static props (CModelHelper owners:
    /// flags 0x80b0000 for a class with static_prop metadata, else 0x80b4000,
    /// so only static props are traced), cables, terrain, and the second
    /// level a referenced map's world adds (MapWorld_AddChildWorld): its
    /// meshes are placed here with the composed path in one step.
    /// </summary>
    public static List<Instance> MapMeshInstances(DmxBinary.Document doc, Func<string, ushort> materialFlags)
        => MapMeshInstances(MapMeshes.Read(doc), materialFlags);

    internal static List<Instance> MapMeshInstances(IEnumerable<MapMeshes.Mesh> meshes, Func<string, ushort> materialFlags)
    {
        var list = new List<Instance>();
        foreach (var mesh in meshes)
        {
            var node = mesh.Element!;
            uint flags = 0;
            if (mesh.ParentType is "CMapEntity" or "CMapSmartProp")
                flags |= 0x4000;
            if (mesh.Hidden)
                flags |= 0x20;
            var shadows = node.GetValue<int>("disableShadows") ?? 0;
            if (shadows == 1)
                flags |= 0xa000;
            else if (shadows == 2)
                flags |= 0x2000;
            // An instance's copy is where the collapse left it (the document is
            // collapsed before the preprocess), else the path times its matrix.
            var toWorld = (float[])mesh.World.Clone();
            Scale(toWorld, node.GetValue<Vector3>("scales") ?? Vector3.One);
            var triangles = LocalTriangles(node, materialFlags);
            // The mesh's scene word starts at 0x30000 (MapMeshGeometry_BuildRayScene
            // 1813ce820) and is 0xb0000 plus the owner bits by the light pass
            // (capture_scenekd.py --bounds survey on Mako).
            var instance = MakeInstance(triangles, toWorld, flags, $"mesh {mesh.NodeId}");
            instance.SceneFlags = 0xb0000 | flags;
            list.Add(instance);
        }
        return list;
    }

    /// <summary>
    /// The scene the light precompute traces for a map: its meshes, then its
    /// static props, then its smart props, materials read from <paramref name="content"/>.
    /// </summary>
    public static EditorTraceScene ForMap(DmxBinary.Document document, GameContent content)
    {
        ushort Flags(string m) => TraceScene.MaterialFlags(content.Material(m));
        var (_, entities) = MapMeshes.ReadWithEntities(document);
        return new EditorTraceScene([.. MapMeshInstances(document, Flags), .. StaticPropInstances(entities, content, Flags),
                                     .. SmartPropInstances(entities, content, content.SmartProp, Flags)]);
    }

    /// <summary>
    /// The static props' owners (CModelHelper, 180f0d670): each prop_static
    /// the walk reaches, once per placement, placed as a mesh owner is (where
    /// the instance collapse left it, else the path times its own AngleMatrix,
    /// the scales on its columns); its model's render triangles in its own space
    /// (GameContent.RenderTriangles), each with its material's flag word. A
    /// static prop's owner flags are 0x80b0000, which the light mask lets
    /// through; disableshadows 1 adds 0xa000 and 2 adds 0x2000, a hidden node
    /// 0x20, and those are left out.
    /// </summary>
    public static List<Instance> StaticPropInstances(IEnumerable<MapMeshes.EntityNode> nodes, GameContent content,
                                                     Func<string, ushort> materialFlags)
    {
        var list = new List<Instance>();
        foreach (var n in nodes)
        {
            var props = n.Element.Get<DmxBinary.Element>("entity_properties");
            if (n.Hidden || props?.Get<string>("classname") is not { } cls || !cls.EqualsAscii("prop_static"))
                continue;
            if (CNumbers.Atoi(props.Get<string>("disableshadows") ?? "0") is 1 or 2)
                continue;
            if (props.Get<string>("model") is not { Length: > 0 } model)
                continue;
            var meshes = content.RenderMeshes(model);
            if (meshes.Sum(m => m.Count) == 0)
                continue;
            var world = n.PrefabChain.Count > 0 ? SettleWorld.NestedPlacement(n.Element, n.Through, n.PrefabChain)
                : n.Through.Count == 0 ? MapMeshes.Concat(n.Path, MapMeshes.Local(n.Element))
                : SettleWorld.Baked(n.Element, [.. n.Through]);
            Scale(world, n.Element.GetValue<Vector3>("scales") ?? Vector3.One);
            // The model's scene: each mesh's own scene at the identity.
            float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
            var children = meshes.Where(m => m.Count > 0)
                .Select(m =>
                {
                    // Each mesh scene carries the prop's owner word (0x80b0000 and up, survey).
                    var mesh = MakeInstance([.. m.Select(t => (t.A, t.B, t.C, materialFlags(t.Material)))], identity, 0, "mesh");
                    mesh.SceneFlags = 0x80b0000;
                    return mesh;
                })
                .ToList();
            list.Add(new Instance
            {
                ToWorld = world, ToLocal = Invert(world), ObjectFlags = 0x80b0000, Children = children,
                Source = $"prop {n.Element.GetValue<int>("nodeID")}",
            });
        }
        return list;
    }

    /// <summary>
    /// The smart props' owners (built by their procedural update, rc 1810a0490
    /// under 180f71020): one container per smart prop the walk reaches, placed
    /// at the node's transform with owner flags 0, holding each placed part's
    /// model render triangles already moved into the node's space, at the
    /// identity (Mako's light scene, capture_rayscene.py). A smart prop the
    /// evaluator cannot do is listed in <paramref name="notes"/> and left out.
    /// Not modelled: a part's material group.
    /// </summary>
    public static List<Instance> SmartPropInstances(IEnumerable<MapMeshes.EntityNode> nodes, GameContent content,
        Func<string, ValveKeyValue.KVObject?> smartProps, Func<string, ushort> materialFlags, List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var list = new List<Instance>();
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        foreach (var n in nodes)
        {
            if (n.Hidden || n.Element.Type != "CMapSmartProp")
                continue;
            if (SmartPropEvaluator.ForNode(n, smartProps, notes) is not var (node, placements))
                continue;
            var children = new List<Instance>();
            foreach (var placement in placements)
            {
                // The part in the node's space, as PropPlacement takes it apart.
                var local = CTransform.Compose(node.Inverse(), placement.Transform);
                var part = new CTransform(local.Position, 1f, local.Rotation).Matrix();
                var s = local.Scale * node.Scale;
                Scale(part, new Vector3(s * placement.ModelScale.X, s * placement.ModelScale.Y, s * placement.ModelScale.Z));
                foreach (var mesh in content.RenderMeshes(placement.Model).Where(m => m.Count > 0))
                    children.Add(MakeInstance([.. mesh.Select(t => (MapMeshes.Transform(part, t.A), MapMeshes.Transform(part, t.B),
                                                                    MapMeshes.Transform(part, t.C), materialFlags(t.Material)))],
                                              identity, 0, "smart part"));
            }
            if (children.Count == 0)
                continue;
            var world = node.Matrix();
            list.Add(new Instance
            {
                ToWorld = world, ToLocal = Invert(world), ObjectFlags = 0, Children = children,
                Source = $"smartprop {n.Element.GetValue<int>("nodeID")}",
            });
        }
        return list;
    }

    /// <summary>The node's scales on its matrix's columns.</summary>
    private static void Scale(float[] m, Vector3 s)
    {
        m[0] *= s.X; m[1] *= s.Y; m[2] *= s.Z;
        m[4] *= s.X; m[5] *= s.Y; m[6] *= s.Z;
        m[8] *= s.X; m[9] *= s.Y; m[10] *= s.Z;
    }

    /// <summary>
    /// A mesh node's triangles in its own space, as RayScene_AddFace adds them
    /// (<see cref="MeshTessellation.RayScene"/>): faces with a negative
    /// material or flags 1 or 2 are left out; each triangle carries its
    /// material's flag word (0 past the material list), with 0x20 for face
    /// flag 4.
    /// </summary>
    private static List<(Vector3, Vector3, Vector3, ushort)> LocalTriangles(DmxBinary.Element node, Func<string, ushort> materialFlags)
    {
        var found = new List<(Vector3, Vector3, Vector3, ushort)>();
        var data = node.Get<DmxBinary.Element>("meshData");
        if (data is null)
            return found;
        object?[] Stream(string group, string name) => data.Get<DmxBinary.Element>(group)?.GetElements("streams")
            .FirstOrDefault(st => st.Name.StartsWith(name + ":", StringComparison.Ordinal))?.Get<object?[]>("data") ?? [];
        var materials = Stream("faceData", "materialindex").Select(x => x is int i ? i : -1).ToArray();
        var faceFlags = Stream("faceData", "flags").Select(x => x is int i ? i : 0).ToArray();
        var names = (data.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var flagsOf = new Dictionary<int, ushort>();
        ushort? Flags(int f)
        {
            var m = f < materials.Length ? materials[f] : -1;
            var ff = f < faceFlags.Length ? faceFlags[f] : 0;
            if (m < 0 || (ff & 3) != 0)
                return null;
            if (!flagsOf.TryGetValue(m, out var mf))
                flagsOf[m] = mf = m < names.Length ? materialFlags(names[m]) : (ushort)0;
            return (ushort)((ff & 4) != 0 ? mf | 0x20 : mf);
        }
        var (corners, faces) = MeshTessellation.RayScene(data);
        for (var t = 0; t < faces.Count; t++)
            if (Flags(faces[t]) is { } tf)
                found.Add((corners[3 * t], corners[(3 * t) + 1], corners[(3 * t) + 2], tf));
        return found;
    }

    /// <summary>
    /// Adds an owner's triangles, given in its own space, placed by
    /// <paramref name="toWorld"/>. Degenerate triangles are dropped (the
    /// record cannot be made).
    /// </summary>
    public static Instance MakeInstance(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, ushort Flags)> triangles,
                                        float[] toWorld, uint objectFlags, string source)
    {
        var records = new List<float>();
        var flags = new List<ushort>();
        Span<float> corners = stackalloc float[9];
        Span<float> r = stackalloc float[13];
        foreach (var (a, b, c, f) in triangles)
        {
            corners[0] = a.X; corners[1] = a.Y; corners[2] = a.Z;
            corners[3] = b.X; corners[4] = b.Y; corners[5] = b.Z;
            corners[6] = c.X; corners[7] = c.Y; corners[8] = c.Z;
            r.Clear();
            if (!RayTraceEnvironment.RecordFromCorners(corners, r))
                continue;
            foreach (var x in r)
                records.Add(x);
            for (var k = 0; k < 9; k++)
                records.Add(corners[k]);
            flags.Add(f);
        }
        return new Instance
        {
            ToWorld = toWorld, ToLocal = Invert(toWorld), ObjectFlags = objectFlags,
            Records = [.. records], Flags = [.. flags], Source = source,
        };
    }

    /// <summary>
    /// FUN_18125ade0: a general 3x4 inverse by the adjugate; identity when
    /// the determinant is below 1.17549435e-35 in size.
    /// </summary>
    public static float[] Invert(float[] m)
    {
        float m0 = m[0], m1 = m[1], m2 = m[2], m4 = m[4], m5 = m[5], m6 = m[6], m8 = m[8], m9 = m[9], m10 = m[10];
        var det = ((m6 * m1 - m5 * m2) * m8 + (m9 * m2 - m10 * m1) * m4) + (m5 * m10 - m6 * m9) * m0;
        if (!(1.17549435e-35f <= MathF.Abs(det)))
            return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        var k = 1f / det;
        var o = new float[12];
        o[0] = (m5 * m10 - m9 * m6) * k;
        o[1] = (m9 * m2 - m1 * m10) * k;
        o[2] = (m1 * m6 - m5 * m2) * k;
        o[4] = (m6 * m8 - m10 * m4) * k;
        o[5] = (m10 * m0 - m2 * m8) * k;
        o[6] = (m2 * m4 - m6 * m0) * k;
        o[8] = (m9 * m4 - m5 * m8) * k;
        o[9] = (m1 * m8 - m9 * m0) * k;
        o[10] = (m5 * m0 - m1 * m4) * k;
        float tx = -m[3], ty = -m[7], tz = -m[11];
        o[3] = (tz * o[2] + ty * o[1]) + tx * o[0];
        o[7] = (tz * o[6] + ty * o[5]) + tx * o[4];
        o[11] = (tz * o[10] + ty * o[9]) + tx * o[8];
        return o;
    }

    // The instance pass's point transform (181c10ea0, 181c12720): each row
    // t + ((x m0 + y m1) + z m2).
    private static Vector3 Xf(float[] m, Vector3 p) => new(
        m[3] + ((p.X * m[0] + p.Y * m[1]) + p.Z * m[2]),
        m[7] + ((p.X * m[4] + p.Y * m[5]) + p.Z * m[6]),
        m[11] + ((p.X * m[8] + p.Y * m[9]) + p.Z * m[10]));

    float? ILightTracer.Trace(Vector3 start, Vector3 end) => Trace(start, end, LightMask)?.Distance;

    LightTraceHit? ILightTracer.Hit(Vector3 start, Vector3 end, uint mask)
        => Trace(start, end, mask) is { } h ? new LightTraceHit(h.Distance, h.Flags) : null;

    /// <summary>
    /// One segment as Valve's batch traces a lone ray: RayBatch_Flush pads its
    /// packet with three copies of it (see <see cref="TracePacket"/>). The
    /// direction is the segment times the refined rcpps of its length; every
    /// instance whose owner flags miss the mask and whose box the ray meets is
    /// traced in its own space, the nearest hit wins (strictly), and a hit
    /// beyond the segment's length is none.
    /// </summary>
    public SceneHit? Trace(Vector3 start, Vector3 end, uint mask) => TraceBatch([start], [end], mask)[0];

    /// <summary>
    /// An instance's box in its parent's space as Valve's scene holds it (see
    /// <see cref="ValveBoxOf"/>) and, for a scene of scenes, a tree over its
    /// children's boxes (ours, padded; it only prunes what the box tests would
    /// refuse anyway).
    /// </summary>
    private static void Prepare(Instance inst)
    {
        if (inst.Children is { } children)
        {
            foreach (var child in children)
                Prepare(child);
            inst.Tree = Bvh.Build(children.Count, i => (children[i].Mins, children[i].Maxs));
        }
        inst.ValveBox = ValveBoxOf(inst);
        (inst.Mins, inst.Maxs) = inst.ValveBox.Value;
    }

    /// <summary>
    /// Diagnostics: a ray against one triangle of a triangle-level instance,
    /// as <see cref="Meets"/> sees it: the plane's denominator, the distance in
    /// local units and the two barycentric tests (a hit wants both at least 0
    /// and their sum at most 1). Null for a scene of scenes.
    /// </summary>
    /// <summary>Diagnostics: an instance's box in world space.</summary>
    internal (Vector3 Mins, Vector3 Maxs) BoxOf(int instance) => (_instances[instance].Mins, _instances[instance].Maxs);

    /// <summary>
    /// Diagnostics: every triangle numbered <paramref name="triangle"/> in any
    /// scene, nested ones included, that the segment's line meets within
    /// <paramref name="slack"/> of <paramref name="distance"/>, with the
    /// plane's denominator, the two barycentric tests, its flag word and the
    /// path of instances down to it.
    /// </summary>
    internal IEnumerable<(string Path, float Denominator, float T, float A, float B, ushort Flags)> FindTriangle(
        Vector3 start, Vector3 end, int triangle, float distance, float slack)
    {
        var dir = Vector3.Normalize(end - start);
        var found = new List<(string, float, float, float, float, ushort)>();
        void Walk(Instance inst, string path, Vector3 o, Vector3 d, float scale)
        {
            var lo = Xf(inst.ToLocal, o);
            var ld = Xf(inst.ToLocal, o + d) - lo;
            var len = ld.Length();
            ld /= len;
            var s = scale / len;
            if (inst.Children is { } kids)
            {
                for (var k = 0; k < kids.Count; k++)
                    Walk(kids[k], $"{path}/{k} {kids[k].Source}", lo, ld, s);
                return;
            }
            if (triangle >= inst.Flags.Length)
                return;
            var r = inst.Records.AsSpan(triangle * 22, 13);
            var denom = (ld.Z * r[2] + ld.Y * r[1]) + ld.X * r[0];
            var t = (r[3] - ((lo.Z * r[2] + lo.Y * r[1]) + r[0] * lo.X)) / denom;
            if (!(MathF.Abs(t * s - distance) <= slack))
                return;
            int u = (int)r[11], v = (int)r[12];
            var pu = t * Axis(ld, u) + Axis(lo, u);
            var pv = t * Axis(ld, v) + Axis(lo, v);
            found.Add(($"{path} local o {lo} d {ld}", denom, t * s, (r[5] * pu + r[6] * pv) + r[7], (r[8] * pu + r[9] * pv) + r[10], inst.Flags[triangle]));
        }
        for (var i = 0; i < _instances.Count; i++)
            Walk(_instances[i], $"{i} {_instances[i].Source} flags {_instances[i].ObjectFlags:x}", start, dir, 1f);
        return found;
    }

    internal (float Denominator, float T, float A, float B)? Probe(int instance, int triangle, Vector3 start, Vector3 end)
    {
        var inst = _instances[instance];
        if (inst.Children != null)
            return null;
        var dir = Vector3.Normalize(end - start);
        var m = inst.ToLocal;
        var lo = Xf(m, start);
        var lp = Xf(m, start + dir);
        var ld = Vector3.Normalize(lp - lo);
        var r = inst.Records.AsSpan(triangle * 22, 13);
        var denom = (ld.Z * r[2] + ld.Y * r[1]) + ld.X * r[0];
        var t = (r[3] - ((lo.Z * r[2] + lo.Y * r[1]) + r[0] * lo.X)) / denom;
        int u = (int)r[11], v = (int)r[12];
        var pu = t * Axis(ld, u) + Axis(lo, u);
        var pv = t * Axis(ld, v) + Axis(lo, v);
        return (denom, t, (r[5] * pu + r[6] * pv) + r[7], (r[8] * pu + r[9] * pv) + r[10]);
    }

    /// <summary>
    /// A triangle-level instance's triangles as the light scene's kd tree sees
    /// them (RayTracingEnvironment::SetupAccelerationStructure, rc 181c19be0):
    /// each record's 13 floats, its flag word, its local corners, and the box of
    /// every corner.
    /// </summary>
    private sealed class InstanceTriangles : IKdTriangles
    {
        private readonly Instance _inst;

        public InstanceTriangles(Instance inst)
        {
            _inst = inst;
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (var t = 0; t < inst.Flags.Length; t++)
                for (var k = 0; k < 3; k++)
                {
                    var c = inst.Records.AsSpan(t * 22 + 13 + k * 3, 3);
                    var v = new Vector3(c[0], c[1], c[2]);
                    lo = Vector3.Min(lo, v);
                    hi = Vector3.Max(hi, v);
                }
            TracedBounds = (lo, hi);
        }

        public (Vector3 Mins, Vector3 Maxs) TracedBounds { get; }

        public ReadOnlySpan<float> TracedRecord(int index) => _inst.Records.AsSpan(index * 22, 13);

        public ushort Flags(int index) => _inst.Flags[index];

        public Vector3[]? TracedCorners(int index)
        {
            var c = _inst.Records.AsSpan(index * 22 + 13, 9);
            return [new(c[0], c[1], c[2]), new(c[3], c[4], c[5]), new(c[6], c[7], c[8])];
        }

        public float[] Corners(int index) => _inst.Records.AsSpan(index * 22 + 13, 9).ToArray();
    }

    /// <summary>
    /// The instance's box (RayScene_SetInstanceTransform +0x70, set in
    /// SetupAccelerationStructure 181c19be0): its scene's box through
    /// Matrix3x4_TransformAABB (18125cd10, centre and extents). A scene of
    /// triangles is boxed by its local corners; a scene of scenes by the union
    /// of its instances' boxes (the root of its instance tree); an empty one
    /// stays inverted.
    /// </summary>
    private static (Vector3, Vector3) ValveBoxOf(Instance inst)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        if (inst.Children is { } kids)
            foreach (var k in kids)
            {
                k.ValveBox ??= ValveBoxOf(k);
                lo = Vector3.Min(lo, k.ValveBox.Value.Mins);
                hi = Vector3.Max(hi, k.ValveBox.Value.Maxs);
            }
        else
            for (var t = 0; t < inst.Flags.Length; t++)
                for (var k = 0; k < 3; k++)
                {
                    var c = inst.Records.AsSpan(t * 22 + 13 + k * 3, 3);
                    lo = Vector3.Min(lo, new Vector3(c[0], c[1], c[2]));
                    hi = Vector3.Max(hi, new Vector3(c[0], c[1], c[2]));
                }
        // An empty scene's box stays inverted (SetupAccelerationStructure skips the transform).
        if (hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z)
            return (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        var m = inst.ToWorld;
        var cen = new Vector3((hi.X + lo.X) * 0.5f, (hi.Y + lo.Y) * 0.5f, (hi.Z + lo.Z) * 0.5f);
        float ex = hi.X - cen.X, ey = hi.Y - cen.Y, ez = hi.Z - cen.Z;
        var cw = MapMeshes.Transform(m, cen);
        var wx = MathF.Abs(ey * m[1]) + MathF.Abs(ex * m[0]) + MathF.Abs(ez * m[2]);
        var wy = MathF.Abs(ey * m[5]) + MathF.Abs(ex * m[4]) + MathF.Abs(ez * m[6]);
        var wz = MathF.Abs(ey * m[9]) + MathF.Abs(ex * m[8]) + MathF.Abs(ez * m[10]);
        return (new Vector3(cw.X - wx, cw.Y - wy, cw.Z - wz), new Vector3(wx + cw.X, wy + cw.Y, wz + cw.Z));
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static float Rsqrt(float x) => Simulation.CpuEstimate.Rsqrt(x);

    private static float Rcp(float x) => Simulation.CpuEstimate.Rcp(x);

    /// <summary>A bounding volume hierarchy over boxes, padded so a ray on a face is never cut.</summary>
    internal sealed class Bvh
    {
        private readonly List<(Vector3 Lo, Vector3 Hi, int First, int Count, int Left)> _nodes = [];
        public int[] Items { get; private set; } = [];

        public static Bvh Build(int count, Func<int, (Vector3, Vector3)> box)
        {
            var bvh = new Bvh();
            var items = Enumerable.Range(0, count).ToArray();
            var boxes = Enumerable.Range(0, count).Select(box).ToArray();
            bvh._nodes.Add(default);
            bvh.Split(0, items, 0, count, boxes);
            bvh.Items = items;
            return bvh;
        }

        private void Split(int node, int[] items, int first, int count, (Vector3 Lo, Vector3 Hi)[] boxes)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (var k = first; k < first + count; k++)
            {
                lo = Vector3.Min(lo, boxes[items[k]].Lo);
                hi = Vector3.Max(hi, boxes[items[k]].Hi);
            }
            var pad = new Vector3(1e-3f) + (Vector3.Abs(lo) + Vector3.Abs(hi)) * 1e-5f;
            lo -= pad;
            hi += pad;
            if (count <= 4)
            {
                _nodes[node] = (lo, hi, first, count, 0);
                return;
            }
            var size = hi - lo;
            var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            float Centre(int i) => axis == 0 ? boxes[i].Lo.X + boxes[i].Hi.X : axis == 1 ? boxes[i].Lo.Y + boxes[i].Hi.Y : boxes[i].Lo.Z + boxes[i].Hi.Z;
            Array.Sort(items, first, count, Comparer<int>.Create((a, b) => Centre(a).CompareTo(Centre(b))));
            var half = count / 2;
            var left = _nodes.Count;
            _nodes.Add(default);
            _nodes.Add(default);
            _nodes[node] = (lo, hi, 0, 0, left);
            Split(left, items, first, half, boxes);
            Split(left + 1, items, first + half, count - half, boxes);
        }

        public (int First, int Count, int Left) Node(int n) => (_nodes[n].First, _nodes[n].Count, _nodes[n].Left);

        public bool Enters(int n, Vector3 o, Vector3 inverse, float limit)
        {
            var (lo, hi, _, _, _) = _nodes[n];
            double t0 = 0, t1 = limit * 1.001 + 1;
            for (var a = 0; a < 3; a++)
            {
                double oo = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
                double inv = a == 0 ? inverse.X : a == 1 ? inverse.Y : inverse.Z;
                double l = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z;
                double h = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
                if (double.IsInfinity(inv))
                {
                    if (oo < l || oo > h)
                        return false;
                    continue;
                }
                var ta = (l - oo) * inv;
                var tb = (h - oo) * inv;
                if (ta > tb)
                    (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta);
                t1 = Math.Min(t1, tb);
                if (t0 > t1)
                    return false;
            }
            return true;
        }
    }
}
