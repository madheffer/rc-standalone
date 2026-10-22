using System.Numerics;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The sun's cascaded shadow views of a cubemap face (scenesystem 10-15: the cascade setup 180496ef0 and the
/// per-cascade fit 180494b40, called from 1801a5c00), float for float. In progress: the light view so far.
/// </summary>
public static class CubemapShadowCascades
{
    /// <summary>One cascade: its view (world to light space, translated so the nearest z is 0), its orthographic projection, the snapped bounds and the slice diameter.</summary>
    public sealed record Cascade(float[] View, float[] Projection, float MinX, float MaxX, float MinY, float MaxY, float Depth, float Diameter);

    /// <summary>
    /// Where the game's cascade inputs come from (client 10-21). csgo.fgd removes every cascade key from
    /// light_environment and the server's CLightComponent constructor (18017c8d0) zeroes them, so the light gives no
    /// distances and the convars and view attributes decide.
    /// </summary>
    public static class Inputs
    {
        /// <summary>
        /// csm_max_shadow_dist_override: "560" in all four shadow-quality presets the video settings apply (client
        /// 180df0f20, 180df13a0, 180df1820, 180df1ca0; the convar's own default is -1). 180496ef0 uses it when it is
        /// above 0, else the view attribute ShadowFarZ (0x7b2a64e1, default 1000, never set).
        /// </summary>
        public const float MaxShadowDistance = 560f;

        /// <summary>
        /// The view attribute 0x61fac369, which replaces cascade 0's distance after the split: the render pipeline
        /// (client 1811d9d30, at 1811da3f4) sets it to 1808898d0's 75.0 (.rdata 181b14fe0) on every game view when
        /// 180cb2100 finds a scripted camera, which it does during buildcubemaps (captured 10-22, ledger 121).
        /// </summary>
        public const float Cascade0Distance = 75f;

        /// <summary>r_csgo_csm_max_visible_distance (default 7500), set as the view attribute CsmMaxVisibleDistance (0xb506b94b).</summary>
        public const float MaxVisible = 7500f;

        /// <summary>r_csgo_csm_pushback_distance (default 7000), set as the view attribute CsmPushbackDistance (0xb7a3d0d5).</summary>
        public const float Pushback = 7000f;

        /// <summary>
        /// csm_max_num_cascades_override and lb_csm_cascade_size_override of shadow quality 2 (client 180df0f20): 3
        /// cascades of 1280 (qualities 0, 1, 3: 2 of 768, 2 of 1024, 3 of 2048).
        /// </summary>
        public const int Count = 3, Resolution = 1280;

        /// <summary>lb_shadow_texture_width_override / lb_shadow_texture_height_override of shadow quality 2: the shadow atlas.</summary>
        public const int AtlasWidth = 4352, AtlasHeight = 5248;

        /// <summary>The viewmodel cascade: csm_viewmodel_max_shadow_dist 21, csm_viewmodel_nearz 0.5, csm_viewmodel_max_visible_dist 1000, r_csgo_viewmodel_csm_pushback_distance 1500.</summary>
        public const float ViewmodelDistance = 21f, ViewmodelNear = 0.5f, ViewmodelMaxVisible = 1000f, ViewmodelPushback = 1500f;
    }

    /// <summary>
    /// 180496ef0's whole range M: the override when above 0 (and the view's flag bit 4 is clear), else ShadowFarZ,
    /// at most the view's far plane.
    /// </summary>
    public static float MaxDistance(float viewFar, float maxShadowOverride = Inputs.MaxShadowDistance, float shadowFarZ = 1000f)
        => Min(maxShadowOverride > 0f ? maxShadowOverride : shadowFarZ, viewFar);

    /// <summary>
    /// A cubemap face's split distances from the game's inputs: the practical split of the whole range, then cascade
    /// 0 replaced by the 0x61fac369 attribute (1804976e5, after the split).
    /// </summary>
    public static float[] GameDistances(float near, float viewFar)
    {
        var d = SplitDistances([], Inputs.Count, near, MaxDistance(viewFar));
        d[0] = Inputs.Cascade0Distance;
        return d;
    }

    /// <summary>A cubemap face's sun cascades as the game renders them: the <see cref="Inputs.Count"/> view cascades, then the viewmodel cascade.</summary>
    public static List<Cascade> GameCascades(CubemapFaceViews.View v, Vector3 sunDirection, float near, float viewFar)
    {
        var cascades = Fit(v, sunDirection, GameDistances(near, viewFar), near, Inputs.MaxVisible, Inputs.Pushback, Inputs.Resolution);
        cascades.AddRange(Fit(v, sunDirection, [Inputs.ViewmodelDistance], Inputs.ViewmodelNear, Inputs.ViewmodelMaxVisible, Inputs.ViewmodelPushback, Inputs.Resolution));
        return cascades;
    }

    /// <summary>
    /// The frustum's four corners at a distance (client 18174ef50 = scenesystem 1804a8450, the same code): centre
    /// C = forward * d, U = up * (tan / max(1/1024, aspect)) d, R = -left * tan d; corner (x, y) = ((R x + C) + U y),
    /// for (x0, y0), (x1, y0), (x0, y1), (x1, y1). FOV 90 only (tan 1, see <see cref="CubemapFaceViews"/>).
    /// </summary>
    public static Vector3[] FrustumCorners(Vector3 forward, Vector3 up, Vector3 left, float distance, float fov, float aspect, float x0 = -1f, float y0 = -1f, float x1 = 1f, float y1 = 1f)
    {
        if (fov != 90f)
            throw new NotSupportedException("only FOV 90 (tan exactly 1) is ported");
        const float t = 1f;
        var hh = t / MathF.Max(0.0009765625f, aspect) * distance;
        var hw = t * distance;
        var c = new Vector3(forward.X * distance, forward.Y * distance, forward.Z * distance);
        var u = new Vector3(up.X * hh, up.Y * hh, up.Z * hh);
        var r = new Vector3(-left.X * hw, -left.Y * hw, -left.Z * hw);
        Vector3 Corner(float x, float y) => new((r.X * x + c.X) + u.X * y, (r.Y * x + c.Y) + u.Y * y, (r.Z * x + c.Z) + u.Z * y);
        return [Corner(x0, y0), Corner(x1, y0), Corner(x0, y1), Corner(x1, y1)];
    }

    /// <summary>
    /// 1804969e0: a slice of the view frustum between two distances: its 8 corners (near then far, world space:
    /// the origin added last) and its diameter, the largest distance between two corners, sqrt((dz^2 + dy^2) +
    /// dx^2) over the corners before the origin is added.
    /// </summary>
    public static (Vector3[] Corners, float Diameter) Slice(CubemapFaceViews.View v, float near, float far)
    {
        var forward = new Vector3(v.CameraToWorld[0], v.CameraToWorld[4], v.CameraToWorld[8]);
        var left = new Vector3(v.CameraToWorld[1], v.CameraToWorld[5], v.CameraToWorld[9]);
        var up = new Vector3(v.CameraToWorld[2], v.CameraToWorld[6], v.CameraToWorld[10]);
        Vector3[] p = [.. FrustumCorners(forward, up, left, near, 90f, 1f), .. FrustumCorners(forward, up, left, far, 90f, 1f)];
        var diameter = 0f;
        for (var i = 0; i < 8; i++)
            for (var j = i + 1; j < 8; j++)
            {
                float dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, dz = p[i].Z - p[j].Z;
                var d = MathF.Sqrt((dz * dz + dy * dy) + dx * dx);
                diameter = d > diameter ? d : diameter;
            }
        for (var i = 0; i < 8; i++)
            p[i] = new Vector3(v.Origin.X + p[i].X, v.Origin.Y + p[i].Y, v.Origin.Z + p[i].Z);
        return (p, diameter);
    }

    private static float Max(float a, float b) => a > b ? a : b;   // maxss a, b
    private static float Min(float a, float b) => a < b ? a : b;   // minss a, b

    /// <summary>A point through a row-major 4 x 4 with the divide, as 180494b40 writes it.</summary>
    private static Vector3 Transform(float[] m, Vector3 p)
    {
        var w = ((p.X * m[12] + p.Y * m[13]) + p.Z * m[14]) + m[15];
        var inv = 1f / w;
        return new Vector3(
            (((p.X * m[0] + p.Y * m[1]) + p.Z * m[2]) + m[3]) * inv,
            (((p.X * m[4] + p.Y * m[5]) + p.Z * m[6]) + m[7]) * inv,
            (((m[8] * p.X + m[9] * p.Y) + p.Z * m[10]) + m[11]) * inv);
    }

    /// <summary>
    /// 180498af0: the orthographic projection, its differences and sums in double: P00 = 2 / (float)(p4 - p2), P03
    /// = (float)((p2 + p4) / (p2 - p4)), P11 = 2 / (float)(p5 - p3), P13 = (float)((p3 + p5) / (p3 - p5)), P22 = 1 /
    /// (float)(p7 - p6), P23 = (float)(p6 / (p6 - p7)), P33 = 1.
    /// </summary>
    public static float[] Ortho(double p2, double p3, double p4, double p5, double p6, double p7)
    {
        var m = new float[16];
        m[0] = 2f / (float)(p4 - p2);
        m[3] = (float)((p2 + p4) / (p2 - p4));
        m[5] = 2f / (float)(p5 - p3);
        m[7] = (float)((p3 + p5) / (p3 - p5));
        m[10] = 1f / (float)(p7 - p6);
        m[11] = (float)(p6 / (p6 - p7));
        m[15] = 1f;
        return m;
    }

    /// <summary>
    /// 180496ef0's split distances: the first <paramref name="given"/> distances as they are (the light's), the
    /// rest the practical split, for k = given + 1 .. count: lin = (M - n) (k / count) + n, d = ((pow(M / n, k / count)
    /// n - lin) 0.85 + lin) + 0.1, at least the previous; M = <paramref name="maxDistance"/>, n = max(0.125, near).
    /// pow is tier0's V_powf (<see cref="Simulation.CrtMath.Pow"/>; its x^1 is x).
    /// </summary>
    public static float[] SplitDistances(IReadOnlyList<float> given, int count, float near, float maxDistance)
    {
        var d = new float[count];
        for (var i = 0; i < given.Count && i < count; i++)
            d[i] = given[i];
        var n = Max(0.125f, near);
        float range = maxDistance - n, ratio = maxDistance / n;
        for (var i = given.Count; i < count; i++)
        {
            var t = (float)(i + 1) / count;
            var lin = range * t + n;
            var pow = t == 1f ? ratio : Simulation.CrtMath.Pow(ratio, t);
            var x = ((pow * n - lin) * 0.85f + lin) + 0.1f;
            d[i] = i > 0 ? Max(x, d[i - 1]) : x;
        }
        return d;
    }

    /// <summary>
    /// 180494b40 for a face view: the cascades for split distances <paramref name="distances"/> (the last is the
    /// whole range), from <paramref name="near"/>. The z range reaches L toward the sun, L = max(1.42 x the whole
    /// range's diameter, max(max(maxVisible, last distance), pushback)) (the view attributes 0xb506b94b and
    /// 0xb7a3d0d5, the client's r_csgo_csm_max_visible_distance 7500 and r_csgo_csm_pushback_distance 7000), and
    /// covers the whole range frustum stretched to 1.42 its diameter; every cascade keeps at least cascade 0's far z.
    /// x and y are padded to the slice's diameter and snapped to its texel (diameter / resolution).
    /// </summary>
    public static List<Cascade> Fit(CubemapFaceViews.View v, Vector3 sunDirection, IReadOnlyList<float> distances, float near, float maxVisible, float pushback, int resolution)
    {
        var whole = Slice(v, near, distances[^1]).Diameter;
        var stretched = whole * 1.42f;
        var extended = Slice(v, near, stretched).Corners;
        var reach = Max(stretched, Max(Max(maxVisible, distances[^1]), pushback));
        var light = LightView(sunDirection);
        var toSun = new Vector3(sunDirection.X * reach, sunDirection.Y * reach, sunDirection.Z * reach);
        var cascades = new List<Cascade>();
        var zMax0 = 0f;
        for (var i = 0; i < distances.Count; i++)
        {
            var start = i == 0 ? near : distances[i - 1];
            if (i != 0 && start > maxVisible)
                break;
            var (corners, diameter) = Slice(v, start, distances[i]);
            float minX = 9e12f, minY = 9e12f, maxX = -9e12f, maxY = -9e12f, minZ = 9e12f, maxZ = -9e12f;
            foreach (var c in corners)
            {
                var q = Transform(light, c);
                minZ = Min(minZ, q.Z);
                maxZ = Max(maxZ, q.Z);
                minX = Min(minX, q.X);
                minY = Min(minY, q.Y);
                maxX = Max(maxX, q.X);
                maxY = Max(maxY, q.Y);
            }
            foreach (var c in corners)
            {
                var z = Transform(light, new Vector3(toSun.X + c.X, toSun.Y + c.Y, toSun.Z + c.Z)).Z;
                minZ = Min(minZ, z);
                maxZ = Max(maxZ, z);
            }
            foreach (var c in extended)
            {
                var z = Transform(light, c).Z;
                minZ = Min(minZ, z);
                maxZ = Max(maxZ, z);
            }
            if (i == 0)
                zMax0 = maxZ;
            else
                maxZ = Max(maxZ, zMax0);
            float[] translate = [1f, 0f, 0f, -0f, 0f, 1f, 0f, -0f, 0f, 0f, 1f, -minZ, 0f, 0f, 0f, 1f];
            var view = CubemapFaceViews.Mul4(translate, light);
            var padX = (diameter - (maxX - minX)) * 0.5f;
            var padY = (diameter - (maxY - minY)) * 0.5f;
            var texel = diameter / resolution;
            var sMinX = MathF.Floor((minX - padX) / texel) * texel;
            var sMaxX = MathF.Floor((padX + maxX) / texel) * texel;
            var sMinY = MathF.Floor((minY - padY) / texel) * texel;
            var sMaxY = MathF.Floor((padY + maxY) / texel) * texel;
            var depth = MathF.Ceiling(maxZ - minZ);
            var projection = Ortho(sMaxX, sMinY, sMinX, sMaxY, MathF.Floor(0f), depth);
            cascades.Add(new Cascade(view, projection, sMinX, sMaxX, sMinY, sMaxY, depth, diameter));
        }
        return cascades;
    }

    /// <summary>
    /// The sun's direction three ways, from light_environment's angles (read back from the capture, bit for bit;
    /// the scene light keeps its orientation as AngleQuaternion's quaternion): <c>ToSun</c>, minus the forward
    /// column of QuaternionMatrix(AngleQuaternion(angles)), is what the cascade fit takes; the light block's
    /// g_vFastPathSunLightDir is ToSun times 1 / its length; the cascade views' camera direction is ToSun divided
    /// by its length (their cameras sit at that times the view's depth offset). The length is sqrt((x^2 + y^2) + z^2).
    /// </summary>
    public static (Vector3 ToSun, Vector3 LightBlock, Vector3 CameraDirection) SunDirections(Vector3 angles)
    {
        var m = CubemapFaceViews.QuaternionMatrix(CubemapFaceViews.AngleQuaternion(angles));
        float x = -m[0], y = -m[4], z = -m[8];
        var len = MathF.Sqrt((x * x + y * y) + z * z);
        var inv = 1f / len;
        return (new Vector3(x, y, z), new Vector3(x * inv, y * inv, z * inv), new Vector3(x / len, y / len, z / len));
    }

    /// <summary>
    /// 180156120: the light's view rotation from its direction d (row-major 4 x 4): forward f = -d, right r =
    /// cross((0, 0, 1), f) (or cross((0, 1, 0), f) when its length squared is under 0.001), normalised; up u =
    /// cross(f, r), normalised; rows r, u, f, (0, 0, 0, 1). Lengths outside [1e-17, 1e17] take a path not read.
    /// </summary>
    public static float[] LightView(Vector3 direction)
    {
        float fx = -direction.X, fy = -direction.Y, fz = -direction.Z;
        float fy0 = fy * 0f, fz0 = fz * 0f, fx0 = fx * 0f;
        float rx = fz0 - fy, ry = fx - fz0, rz = fy0 - fx0;
        if (0.001f > (ry * ry + rx * rx) + rz * rz)
        {
            rx = fz - fy0;
            ry = fx0 - fz0;
            rz = fy0 - fx;
        }
        var len = MathF.Sqrt((rz * rz + ry * ry) + rx * rx);
        if (len < 1e-17f || len > 1e17f)
            throw new NotSupportedException("a degenerate light direction (the robust normalise 18049c220) is not ported");
        var inv = 1f / len;
        rx = inv * rx;
        ry = inv * ry;
        rz = inv * rz;
        float ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;
        len = MathF.Sqrt((uz * uz + uy * uy) + ux * ux);
        if (len < 1e-17f || len > 1e17f)
            throw new NotSupportedException("a degenerate light up vector is not ported");
        inv = 1f / len;
        return [rx, ry, rz, 0f, inv * ux, inv * uy, inv * uz, 0f, fx, fy, fz, 0f, 0f, 0f, 0f, 1f];
    }
}
