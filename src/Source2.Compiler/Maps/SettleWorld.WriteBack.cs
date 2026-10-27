using System.Globalization;
using System.Numerics;
using System.Text;
using Source2.Compiler.Simulation;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps;

public static partial class SettleWorld
{
    /// <summary>
    /// CTransform_Invert (rc 181253780). The conjugate (x, y, z times -1); at
    /// scale 1 the position is 0 - (the position rotated by the unnormalised
    /// conjugate) and the conjugate is then normalised by its dpps length;
    /// at any other scale the conjugate is normalised first, the rotated
    /// position is times -1 / s and the scale is 1 / s. A zero length or
    /// scale stands in as 1.1920929e-07.
    /// </summary>
    public static CTransform Invert(CTransform t)
    {
        const float Epsilon = 1.1920929e-07f;
        float a = t.Rotation.X * -1f, b = t.Rotation.Y * -1f, c = t.Rotation.Z * -1f, w = t.Rotation.W * 1f;
        var length = MathF.Sqrt(((a * a) + (b * b)) + ((c * c) + (w * w)));
        if (length == 0f)
            length = Epsilon;
        var p = t.Position;
        Vector3 Rotated(float qa, float qb, float qc, float qw)
        {
            var ux = (p.Z * qb) - (p.Y * qc);
            var uy = (p.X * qc) - (p.Z * qa);
            var uz = (p.Y * qa) - (p.X * qb);
            ux += ux;
            uy += uy;
            uz += uz;
            return new Vector3(((uz * qb) - (uy * qc)) + ((qw * ux) + p.X),
                               ((ux * qc) - (uz * qa)) + ((qw * uy) + p.Y),
                               ((uy * qa) - (ux * qb)) + ((qw * uz) + p.Z));
        }
        if (t.Scale == 1f)
        {
            var r = Rotated(a, b, c, w);
            return new CTransform(new Vector3(0f - r.X, 0f - r.Y, 0f - r.Z), 1f,
                                  new Quaternion(a / length, b / length, c / length, w / length));
        }
        float na = a / length, nb = b / length, nc = c / length, nw = w / length;
        var inverse = 1f / (t.Scale == 0f ? Epsilon : t.Scale);
        var negative = 0f - inverse;
        var q = Rotated(na, nb, nc, nw);
        return new CTransform(new Vector3(q.X * negative, q.Y * negative, q.Z * negative), inverse, new Quaternion(na, nb, nc, nw));
    }

    /// <summary>A body's transform as its GetTransform gives it (vfn 0x1c0; <see cref="SettleWriteBack.BodyFrame"/>).</summary>
    public static CTransform BodyTransform(in RnBodyState body)
    {
        var f = SettleWriteBack.BodyFrame(body);
        return new CTransform(new Vector3(f.Position.X, f.Position.Y, f.Position.Z), f.Scale,
                              new Quaternion(f.Rotation.X, f.Rotation.Y, f.Rotation.Z, f.Rotation.W));
    }

    /// <summary>
    /// A settled single body's entity transform (PhysPart_GetEntityTransform,
    /// rc 18105a410, its single-body branch): the body's transform composed
    /// with the inverse of bind pose 0 as 1801ae160 decomposes it, or the
    /// body's as it is when the physics data has no bone names (+0x18).
    /// </summary>
    public static CTransform SingleBodyEntity(CTransform body, PhysAggregateData phys)
    {
        if ((phys.BoneNames?.Length ?? 0) == 0)
            return body;
        if (phys.BindPose.Length == 0)
            throw new NotSupportedException("a physics part with bone names and no bind pose (rc reads a null bind pose) is not ported");
        return CTransform.Compose(body, Invert(BindPoseTransform(Physics.StaticPropHulls.BindPose(phys, 0))));
    }

    /// <summary>
    /// A settled aggregate's write-back (rc 18105a410's aggregate branch, then
    /// 18105cac0). The entity transform is body 0's composed with the inverse
    /// of bind pose 0, its translation times the aggregate scale s (instance
    /// +0x28), decomposed as 18125b270 / 18125de90 do. Each body's bone is the
    /// inverse entity transform composed with the body's, its position times
    /// 1 / s, under the part's bone name (instance vfn 0xd0: physics data
    /// +0x10, count +0x18). The bones are then found in the model's skeleton
    /// by name, case ignored (181749980), dropped when absent, and listed in
    /// skeleton order (181053ee0 sorts on the bone index).
    /// </summary>
    public static (CTransform Entity, List<(string Name, CTransform Bone)> Bones) AggregateWriteBack(
        AggregateBuild a, IReadOnlyList<CTransform> bodies, ModelBones skeleton)
    {
        var s = a.NodeScale;
        var bind = (float[])Physics.StaticPropHulls.BindPose(a.Phys, 0).Clone();
        bind[3] *= s;
        bind[7] *= s;
        bind[11] *= s;
        var entity = CTransform.Compose(bodies[0], Invert(BindPoseTransform(bind)));
        var inverse = Invert(entity);
        var reciprocal = 1f / s;
        var names = a.Phys.BoneNames ?? [];
        var found = new List<(int Index, string Name, CTransform Bone)>();
        for (var i = 0; i < bodies.Count; i++)
        {
            var bone = CTransform.Compose(inverse, bodies[i]);
            bone = bone with { Position = new Vector3(bone.Position.X * reciprocal, bone.Position.Y * reciprocal, bone.Position.Z * reciprocal) };
            var name = i < names.Length ? names[i] : "";
            var index = Array.FindIndex(skeleton.Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                found.Add((index, name, bone));
        }
        if (found.Select(f => f.Index).Distinct().Count() != found.Count)
            throw new NotSupportedException("two aggregate parts on one bone (181053ee0's unstable sort) are not ported");
        return (entity, [.. found.OrderBy(f => f.Index).Select(f => (f.Name, f.Bone))]);
    }

    /// <summary>
    /// The boneTransforms value (rc 18174aeb0): "%s: %f %f %f %f %f %f %f;" per
    /// bone, position then rotation (x, y, z, w), each float printed through
    /// double with six decimals.
    /// </summary>
    public static string BoneTransformsText(IEnumerable<(string Name, CTransform Bone)> bones)
    {
        var text = new StringBuilder();
        foreach (var (name, b) in bones)
        {
            text.Append(name).Append(':');
            foreach (var f in new[] { b.Position.X, b.Position.Y, b.Position.Z, b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W })
                text.Append(' ').Append(Fixed6(f));
            text.Append(';');
        }
        return text.ToString();
    }

    /// <summary>printf's "%f" of a float: six decimals, a minus sign whenever the sign bit is set (-0.000000 included).</summary>
    private static string Fixed6(float f)
    {
        var magnitude = Math.Abs((double)f).ToString("F6", CultureInfo.InvariantCulture);
        return float.IsNegative(f) ? "-" + magnitude : magnitude;
    }
}
