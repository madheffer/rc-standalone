using System.Numerics;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// vphysics2's triangle sources for round shapes, as RnShape_GetTriangles (1802661f0) gives them to
/// nav generation: the sphere (type 0, 18016c9b0 -> 18016c380 with 5 segments and 12 rings, see
/// <see cref="Sphere"/>) and the capsule (type 1, 18016f5d0 -> 18016ed50 with 12 segments and 2 rings). The
/// vertices are built in the capsule's own frame (x along the axis, poles at -r and L + r), then each
/// goes through the rows (axis, u, axis x u, centre A) of 18008d6d0. V_sinf / V_cosf are the CRT's
/// sinf / cosf, which MathF.Sin / Cos match bit for bit (20,015 samples against tier0 20261015).
/// </summary>
public static class NavGenShapes
{
    /// <summary>
    /// 18016ed50(capsule, verts, indices, segments, rings) as 18016f5d0 calls it (12, 2): the positions
    /// (vertex records are position and normal; only positions are kept) and the triangle indices.
    /// </summary>
    public static (Vector3[] Verts, int[] Indices) Capsule(Vector3 a, Vector3 b, float r, int segments = 12, int rings = 2)
    {
        var perColumn = rings * 2 + 2;
        var count = perColumn * segments + 2;
        var pos = new Vector3[count];
        var indices = new int[(rings + 1) * segments * 12];
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dz = b.Z - a.Z;
        var len = MathF.Sqrt(dz * dz + dy * dy + dx * dx);
        var pole0 = perColumn * segments;
        var pole1 = pole0 + 1;
        var zero = r * 0f;
        pos[pole0] = new Vector3(0f - r, zero, zero);
        pos[pole1] = new Vector3(r + len, zero, zero);
        var ix = 0;
        var column = 0;
        var phi0 = 0f / (rings + 1);
        for (var i = 0; i < segments; i++)
        {
            var angle = i * -6.2831855f / segments;
            var s = MathF.Sin(angle);
            var c = MathF.Cos(angle);
            var next = (i + 1) % segments * perColumn;
            indices[ix++] = next;
            indices[ix++] = column;
            indices[ix++] = next + 1;
            indices[ix++] = next + 1;
            indices[ix++] = column;
            indices[ix++] = column + 1;
            void Ring(int at, float phi)
            {
                var sn = MathF.Sin(phi);
                var cs = MathF.Cos(phi);
                var zz = cs * s;
                var yy = cs * c;
                pos[at] = new Vector3(r * -sn + 0f, r * yy, r * zz);
                pos[at + 1] = new Vector3(r * sn + len, r * yy, r * zz);
            }
            Ring(column, phi0);
            var k = 0;
            if (rings >= 1)
            {
                var cur = next;
                var step = 2;
                do
                {
                    var below = column - next + cur;
                    var upper = next + step;
                    var here = column + step;
                    indices[ix + 0] = cur + 1;
                    indices[ix + 1] = below + 1;
                    indices[ix + 2] = upper + 1;
                    indices[ix + 3] = upper + 1;
                    indices[ix + 4] = below + 1;
                    indices[ix + 5] = here + 1;
                    indices[ix + 6] = cur;
                    indices[ix + 7] = upper;
                    indices[ix + 8] = below;
                    indices[ix + 9] = upper;
                    indices[ix + 10] = here;
                    indices[ix + 11] = below;
                    ix += 12;
                    cur += 2;
                    step = 2 - next + cur;
                    k++;
                    Ring(column + 2 * k, k * 3.1415927f * 0.5f / (rings + 1));
                }
                while (k < rings);
            }
            indices[ix++] = pole0;
            indices[ix++] = k * 2 + column;
            indices[ix++] = k * 2 + next;
            indices[ix++] = pole1;
            indices[ix++] = k * 2 + next + 1;
            indices[ix++] = k * 2 + column + 1;
            column += perColumn;
        }
        // 18008d5b0: the axis (1, 0, 0 when under 1e-5 long), a perpendicular u normalised, made
        // orthogonal to the axis and normalised again (180088f50); the third row is axis x u.
        Vector3 axis;
        if (len <= 1e-05f)
            axis = new Vector3(1f, 0f, 0f);
        else
        {
            var inv = 1f / len;
            axis = new Vector3(dx * inv, dy * inv, dz * inv);
        }
        var u = Normalize(new Vector3((1f - axis.Z) * (axis.Y * axis.Y - 0f) + axis.Z, 0f, -axis.X));
        var dot = u.Z * axis.Z + u.Y * axis.Y + u.X * axis.X;
        u = Normalize(new Vector3(u.X - dot * axis.X, u.Y - axis.Y * dot, u.Z - axis.Z * dot));
        var w = new Vector3(u.Z * axis.Y - axis.Z * u.Y, axis.Z * u.X - u.Z * axis.X, u.Y * axis.X - axis.Y * u.X);
        // 18008d6d0: each row (axis, u, w, a) against (x, y, z, 1): (a + y u) + (z w + x axis).
        var verts = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var p = pos[i];
            verts[i] = new Vector3(
                (1f * a.X + p.Y * u.X) + (p.Z * w.X + p.X * axis.X),
                (1f * a.Y + p.Y * u.Y) + (p.Z * w.Y + p.X * axis.Y),
                (1f * a.Z + p.Y * u.Z) + (p.Z * w.Z + p.X * axis.Z));
        }
        return (verts, indices);
    }

    /// <summary>
    /// 18016c380(sphere, verts, indices, segments, rings) as 18016c9b0 calls it (5, 12): the poles (-r, 0, 0)
    /// and (r, 0, 0) after segments x rings vertices; segment i at angle i -2 pi / segments, ring j at
    /// j pi / (rings + 1) - pi / 2 (CRT sinf / cosf), its vertex (r sin(phi), (cos(phi) cos(theta)) r,
    /// (cos(phi) sin(theta)) r); six indices per ring band between this segment's column and the next,
    /// and two pole triangles per segment; then the centre added to every vertex.
    /// </summary>
    public static (Vector3[] Verts, int[] Indices) Sphere(Vector3 centre, float r, int segments = 5, int rings = 12)
    {
        var count = segments * rings + 2;
        var pos = new Vector3[count];
        var indices = new int[segments * rings * 6];
        var south = segments * rings;
        pos[south] = new Vector3(-r, 0f, 0f);
        pos[south + 1] = new Vector3(r, 0f, 0f);
        const float Pi = 3.1415927f, HalfPi = 1.5707964f;
        var divisions = (float)(rings + 1);
        var ix = 0;
        var column = 0;
        for (var i = 0; i < segments; i++)
        {
            var theta = i * -6.2831855f / segments;
            var st = MathF.Sin(theta);
            var ct = MathF.Cos(theta);
            var next = (i + 1) % segments * rings;
            void Ring(int at, float phi)
            {
                var sp = MathF.Sin(phi);
                var cp = MathF.Cos(phi);
                pos[at] = new Vector3(r * sp, (cp * ct) * r, (cp * st) * r);
            }
            Ring(column, Pi / divisions - HalfPi);
            for (var k = 1; k < rings; k++)
            {
                indices[ix++] = next + k - 1;
                indices[ix++] = column + k - 1;
                indices[ix++] = next + k;
                indices[ix++] = next + k;
                indices[ix++] = column + k - 1;
                indices[ix++] = column + k;
                Ring(column + k, (k + 1) * Pi / divisions - HalfPi);
            }
            indices[ix++] = south;
            indices[ix++] = column;
            indices[ix++] = next;
            indices[ix++] = south + 1;
            indices[ix++] = next + rings - 1;
            indices[ix++] = column + rings - 1;
            column += rings;
        }
        for (var i = 0; i < count; i++)
            pos[i] = new Vector3(centre.X + pos[i].X, centre.Y + pos[i].Y, centre.Z + pos[i].Z);
        return (pos, indices);
    }

    // 180088f50: (y^2 + z^2) + x^2; scaled by 1 / length when it lies in [1e-17, 1e17]; a zero vector
    // becomes the routine's default (not reached from a perpendicular here).
    private static Vector3 Normalize(Vector3 v)
    {
        var l = MathF.Sqrt(v.Y * v.Y + v.Z * v.Z + v.X * v.X);
        if (9.99999983775159e-18f > l || l > 9.999999843067494e+16f)
            return v;
        var inv = 1f / l;
        return new Vector3(inv * v.X, inv * v.Y, inv * v.Z);
    }
}
