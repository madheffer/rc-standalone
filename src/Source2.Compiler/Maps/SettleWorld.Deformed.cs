using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps;

/// <summary>
/// The shapes of a prop the settle world builds through a lattice deformer
/// (PhysPart_BuildFromModel, rc 18105dbd0, when the record's deformer at +0x40
/// is set). Each sphere (1810557d0), capsule (181054970), hull (181054e80) and
/// mesh (1810554d0) becomes a triangle mesh: its points through the placement
/// matrix (the matrix path is forced, the scale matrix 18125c200 when the
/// model has no bind pose), deformed with the record's transform as the
/// caller's matrix (181287fd0 -> PropDeformer_Transform, the matrix from
/// 1801feb70), cooked by vphysics2's vf 0x238 (RnMeshCreate), each cooked
/// triangle's first and third corner swapped when the lattice is mirrored
/// (+0x70), and added as a mesh shape at scale 1.
/// </summary>
public static partial class SettleWorld
{
    /// <summary>
    /// The deformer a prop's record takes (181022540): only for a node of
    /// kind 3 (an entity whose class has the static_prop or deformable flag,
    /// in CS2's FGDs only prop_static, <see cref="Deformable"/>), the nearest CMapDeformer reached by
    /// climbing parents of kind 1 or 3 (groups and such props), kept when
    /// PropDeformer_IsSet. The climb runs over the baked tree: a copy's root
    /// group stands where its instance stood, and a prefab's map hangs in a
    /// group under the prefab's parent (180f96d40), so the climb leaves a
    /// target group or prefab world for the instance's or prefab's parent.
    /// </summary>
    internal static LatticeDeformer? PropDeformer(DmxBinary.Element node, IReadOnlyDictionary<DmxBinary.Element, DmxBinary.Element> parents,
                                                  IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs, FgdSchema? schema)
    {
        if (node.Type != "CMapEntity" || !Deformable(node.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "", schema))
            return null;
        int instances = through.Count, chain = prefabs.Count;
        var e = parents.GetValueOrDefault(node);
        while (e != null)
        {
            if (e.Type.StartsWith("CMapDeformer", StringComparison.Ordinal))
            {
                var d = LatticeDeformer.FromNode(e, [.. through.Take(instances)], [.. prefabs.Take(chain)]);
                if (!d.Enabled)
                    return null;
                // 181288690: the copy's transform composed after the node's step
                // (MapInstance_StepMatrix, the identity once the bake is done) as a
                // CTransform: no move, scale 1, MatrixQuaternion of the identity.
                d.Transform = CTransform.Compose(new CTransform(Vector3.Zero, 1f, Quaternion.Identity), d.Transform);
                return d;
            }
            if (instances > 0 && ReferenceEquals(e, through[instances - 1].Get<DmxBinary.Element>("target")))
            {
                e = parents.GetValueOrDefault(through[--instances]);
                continue;
            }
            if (chain > 0 && ReferenceEquals(e, prefabs[chain - 1].Get<DmxBinary.Element>(MapPrefabs.WorldKey)))
            {
                e = parents.GetValueOrDefault(prefabs[--chain]);
                continue;
            }
            if (e.Type != "CMapGroup"
                && !(e.Type == "CMapEntity" && Deformable(e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") ?? "", schema)))
                return null;
            e = parents.GetValueOrDefault(e);
        }
        return null;
    }

    /// <summary>
    /// Whether an entity of the class is of kind 3 (vf 0x4a0, 181002590): its
    /// class has the static_prop or deformable flag (180dd54d0). Without a
    /// schema, prop_static, the one CS2 class with either.
    /// </summary>
    internal static bool Deformable(string className, FgdSchema? schema)
        => schema is null ? className == "prop_static" : schema.HasFlag(className, "static_prop") || schema.HasFlag(className, "deformable");

    /// <summary>1801feb70: a CTransform's matrix (181260150), its three rotation columns times the scale when that is not 1.</summary>
    private static float[] RecordMatrix(Vector3 position, float scale, Quaternion orientation)
    {
        var m = new CTransform(position, 1f, orientation).Matrix();
        if (scale != 1f)
            foreach (var i in (int[])[0, 1, 2, 4, 5, 6, 8, 9, 10])
                m[i] = scale * m[i];
        return m;
    }

    /// <summary>
    /// A deformed shape's mesh: the points through <paramref name="place"/>
    /// (1812651a0), deformed for <paramref name="record"/>, cooked with no
    /// materials but the mesh's own, and flipped for a mirrored lattice. Null
    /// when the cook keeps no triangle.
    /// </summary>
    private static RnMesh? DeformedMesh(IReadOnlyList<Vector3> points, int[] indices, byte[]? materials, float[] place, float[] record, LatticeDeformer deformer)
    {
        var evaluator = deformer.For(record);
        var moved = points.Select(p => evaluator.Deform(LightMath.Transform34(place, p))).ToArray();
        var mesh = RnMeshBuilder.Create(indices, moved, materials);
        if (mesh != null && deformer.Mirror)
            for (var t = 0; t < mesh.Triangles.Length; t++)
                mesh.Triangles[t] = (mesh.Triangles[t].C, mesh.Triangles[t].B, mesh.Triangles[t].A);
        return mesh;
    }

    /// <summary>
    /// 1819d5810 with 5 and 12 (1819d51e0): the poles at -r and +r on x (the
    /// last two points), then 5 meridians at (i * -2 pi) / 5, each 12 points at
    /// latitude (m pi) / 13 - pi / 2, m = 1..12: (r sin, (cos cos) r, (cos sin) r)
    /// of latitude and meridian. Per meridian, two triangles per step to the
    /// next meridian and one to each pole; every point moved by the centre.
    /// </summary>
    internal static (Vector3[] Points, int[] Indices) SphereMesh(Vector3 center, float radius)
    {
        const int Meridians = 5, Rings = 12;
        var points = new Vector3[Meridians * Rings + 2];
        var indices = new List<int>(6 * Meridians * Rings);
        points[Meridians * Rings] = new Vector3(-radius, 0f, 0f);
        points[Meridians * Rings + 1] = new Vector3(radius, 0f, 0f);
        int south = Meridians * Rings, north = south + 1;
        for (var i = 0; i < Meridians; i++)
        {
            var theta = (i * -6.28318548f) / Meridians;
            float st = CrtMath.Sin(theta), ct = CrtMath.Cos(theta);
            int cur = i * Rings, next = (i + 1) % Meridians * Rings;
            for (var m = 1; m <= Rings; m++)
            {
                // Valve takes m = 1 as pi / 13 - pi / 2, which 1 * pi leaves the same.
                var phi = ((m * 3.14159274f) / (Rings + 1)) - 1.57079637f;
                float sp = CrtMath.Sin(phi), cp = CrtMath.Cos(phi);
                points[cur + m - 1] = new Vector3(radius * sp, cp * ct * radius, cp * st * radius);
                if (m > 1)
                {
                    var k = m - 2;
                    indices.AddRange([next + k, cur + k, next + k + 1, next + k + 1, cur + k, cur + k + 1]);
                }
            }
            indices.AddRange([south, cur, next, north, next + Rings - 1, cur + Rings - 1]);
        }
        for (var i = 0; i < points.Length; i++)
            points[i] = new Vector3(center.X + points[i].X, center.Y + points[i].Y, center.Z + points[i].Z);
        return (points, [.. indices]);
    }

    /// <summary>
    /// 1819d61d0 with 12 and 2 (1819d5950): the capsule built along +x from
    /// its first centre, length |b - a| (sqrt((dz dz + dy dy) + dx dx)), the
    /// poles at 0 - r and r + length (the last two points); 12 meridians at
    /// (i * -2 pi) / 12, each two points per latitude k = 0..2 at
    /// ((k pi) 0.5) / 3 (0 / 3 for the first), one per hemisphere: x = r (-sin)
    /// + 0 and r sin + length, y = r (cos cos), z = r (cos sin). Then every point
    /// through the frame whose columns are the axis (normalised by its
    /// reciprocal length, (1, 0, 0) at or below 1e-5), 18125d0d0's
    /// perpendicular, their cross product and the first centre.
    /// </summary>
    internal static (Vector3[] Points, int[] Indices) CapsuleMesh(Vector3 a, Vector3 b, float radius)
    {
        const int Meridians = 12, Latitudes = 2, Column = (Latitudes * 2) + 2;
        var points = new Vector3[Column * Meridians + 2];
        var indices = new List<int>((Latitudes + 1) * Meridians * 12);
        float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        var length = MathF.Sqrt(((dz * dz) + (dy * dy)) + (dx * dx));
        int south = Column * Meridians, north = south + 1;
        points[south] = new Vector3(0f - radius, radius * 0f, radius * 0f);
        points[north] = new Vector3(radius + length, radius * 0f, radius * 0f);
        for (var i = 0; i < Meridians; i++)
        {
            var theta = (i * -6.28318548f) / Meridians;
            float st = CrtMath.Sin(theta), ct = CrtMath.Cos(theta);
            int cur = i * Column, next = (i + 1) % Meridians * Column;
            indices.AddRange([next, cur, next + 1, next + 1, cur, cur + 1]);
            for (var k = 0; k <= Latitudes; k++)
            {
                var phi = k == 0 ? 0f / (Latitudes + 1) : ((k * 3.14159274f) * 0.5f) / (Latitudes + 1);
                float sp = CrtMath.Sin(phi), cp = CrtMath.Cos(phi);
                float cy = cp * ct, cz = cp * st;
                points[cur + (2 * k)] = new Vector3((radius * -sp) + 0f, radius * cy, radius * cz);
                points[cur + (2 * k) + 1] = new Vector3((radius * sp) + length, radius * cy, radius * cz);
                if (k > 0)
                {
                    var o = 2 * (k - 1);
                    indices.AddRange([next + o + 1, cur + o + 1, next + o + 3, next + o + 3, cur + o + 1, cur + o + 3,
                                      next + o, next + o + 2, cur + o, next + o + 2, cur + o + 2, cur + o]);
                }
            }
            indices.AddRange([south, cur + (2 * Latitudes), next + (2 * Latitudes), north, next + (2 * Latitudes) + 1, cur + (2 * Latitudes) + 1]);
        }
        Vector3 axis;
        if (length > 1e-05f)
        {
            var inverse = 1f / length;
            axis = new Vector3(dx * inverse, dy * inverse, dz * inverse);
        }
        else
        {
            axis = new Vector3(1f, 0f, 0f);
        }
        var u = Perpendicular(axis);
        var w = new Vector3((u.Z * axis.Y) - (axis.Z * u.Y), (axis.Z * u.X) - (u.Z * axis.X), (u.Y * axis.X) - (axis.Y * u.X));
        float[] frame = [axis.X, u.X, w.X, a.X, axis.Y, u.Y, w.Y, a.Y, axis.Z, u.Z, w.Z, a.Z];
        for (var i = 0; i < points.Length; i++)
            points[i] = LightMath.Transform34(frame, points[i]);
        return (points, [.. indices]);
    }

    /// <summary>
    /// 18125d0d0: ((1 - z) (y y - 0) + z, 0, -x) normalised (18013e340), less its
    /// projection on the axis ((u.z z + y u.y) + u.x x), normalised again.
    /// </summary>
    private static Vector3 Perpendicular(Vector3 d)
    {
        var u = new Vector3(((1f - d.Z) * ((d.Y * d.Y) - 0f)) + d.Z, 0f, -d.X);
        LightMath.Normalize(ref u);
        var dot = ((u.Z * d.Z) + (d.Y * u.Y)) + (u.X * d.X);
        u = new Vector3(u.X - (dot * d.X), u.Y - (d.Y * dot), u.Z - (d.Z * dot));
        LightMath.Normalize(ref u);
        return u;
    }

    /// <summary>
    /// 1819589f0 at scale 1: the hull's vertices, and each face as a fan from
    /// its half-edge's origin over the next half-edges until the loop closes.
    /// </summary>
    internal static (Vector3[] Points, int[] Indices) HullMesh(RnHull hull)
    {
        var indices = new List<int>();
        foreach (var start in hull.Faces)
        {
            var e0 = hull.Edges[start];
            var e1 = hull.Edges[e0.Next];
            var e2 = hull.Edges[e1.Next];
            while (true)
            {
                indices.AddRange([e0.Origin, e1.Origin, e2.Origin]);
                var after = e2.Next;
                if (after == start)
                    break;
                (e1, e2) = (e2, hull.Edges[after]);
            }
        }
        return ([.. hull.VertexPositions.Select(v => new Vector3(v.X * 1f, v.Y * 1f, v.Z * 1f))], [.. indices]);
    }
}
