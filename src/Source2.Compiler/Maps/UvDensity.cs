using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A draw call's <c>m_flUvDensity</c> (FUN_1812d9920, called from
/// <c>CResourceCompilerMesh::AddDrawDescriptors</c>): for every triangle with
/// some texcoord area, sqrt(world area / texcoord area); the values sorted,
/// and the one at <c>((n - 1) * percentile) / 100</c> taken, or 0 when no
/// triangle counts. The percentile is 20, or 95 for a material that prefers
/// high density triangles. Float operations are in the binary's order (read
/// from its disassembly).
/// </summary>
public static class UvDensity
{
    /// <summary>The density of the triangles <paramref name="indices"/> name.</summary>
    public static float Compute(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector2> texcoords, ReadOnlySpan<int> indices, int percentile = 20)
    {
        var values = new List<float>();
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var i0 = indices[t];
            var i1 = indices[t + 1];
            var i2 = indices[t + 2];
            Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
            Vector2 a = texcoords[i0], b = texcoords[i1], c = texcoords[i2];
            var d1z = p1.Z - p0.Z;
            var ez = p0.Z - p2.Z;
            var ex = p0.X - p2.X;
            var d1x = p1.X - p0.X;
            var ey = p0.Y - p2.Y;
            var d1y = p1.Y - p0.Y;
            var nx = ez * d1y - d1z * ey;
            var ny = d1z * ex - d1x * ez;
            var nz = d1x * ey - d1y * ex;
            var uv = c.X * a.Y - b.X * a.Y;
            uv += b.Y * a.X;
            uv -= b.Y * c.X;
            uv -= a.X * c.Y;
            uv += b.X * c.Y;
            var uvArea = MathF.Abs(uv) * 0.5f;
            if (!(uvArea > 0f))
                continue;
            var length = MathF.Sqrt((nz * nz + ny * ny) + nx * nx);
            values.Add(MathF.Sqrt(length * 0.5f / uvArea));
        }
        if (values.Count == 0)
            return 0f;
        values.Sort();
        var pct = percentile < 0 ? 0 : Math.Min(percentile, 100);
        return values[(values.Count - 1) * pct / 100];
    }
}
