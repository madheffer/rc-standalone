using Source2.Compiler.Io;
using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A baked static prop's vertices placed in the world: CMesh_TransformByMatrix
/// (1802b1ff0), called by WRBNode_BakePropMeshes on each of the prop's meshes.
/// <list type="bullet">
/// <item>The matrix (180245770): Concat(AngleMatrix(angles), diag(scales))
/// with the origin as translation; inside an instance, the collapsed copy's
/// placement.</item>
/// <item>Positions by the matrix (Matrix3x4_TransformPoint); normals by the
/// transpose of the matrix's 4x4 inverse (181263c30 through 1812636e0),
/// tangents by the matrix (Matrix3x4_Rotate), both renormalised (18013e340);
/// the tangent's w kept.</item>
/// <item>Texcoords, the first and second texcoord stream: u (when not
/// negative) scaled about 0 by a column length of the matrix, 1 - v (when
/// not negative) scaled about 1, then u + 0 and 1 - (0 + (1 - v)). The axes
/// are the material's TexCoordScaleByModelU/V and TexCoord2ScaleByModelU/V
/// attributes (-1 none). The origin, offset, step and UV2FromUV1 attributes
/// it also reads are declared by no CS2 shader, so they keep their defaults
/// and are not ported.</item>
/// </list>
/// </summary>
internal static class PropTransform
{
    /// <summary>The flags AddStaticProps gives a prop (+0x168): 3, or 8 when one of its materials needs local space vertices.</summary>
    public const int Place = 1, Texcoords = 2, LocalSpace = 8;

    /// <summary>180245770's prop matrix.</summary>
    public static float[] Matrix(Vector3 origin, Vector3 angles, Vector3 scales)
    {
        var m = MapMeshes.Concat(MapMeshes.AngleMatrix(angles), [scales.X, 0, 0, 0, 0, scales.Y, 0, 0, 0, 0, scales.Z, 0]);
        (m[3], m[7], m[11]) = (origin.X, origin.Y, origin.Z);
        return m;
    }

    /// <summary>Texcoord axes from the material: each -1 (none) or a column of the matrix.</summary>
    public readonly record struct Axes(int U, int V, int U2, int V2)
    {
        public static readonly Axes None = new(-1, -1, -1, -1);
    }

    /// <summary>
    /// The transform over interleaved vertices, in place. Each stream offset
    /// is -1 when the mesh lacks it; <paramref name="flags"/> holds
    /// <see cref="Place"/> and <see cref="Texcoords"/>.
    /// </summary>
    public static void Apply(float[] v, int stride, int position, int normal, int tangent, int texcoord0, int texcoord1, float[] m, Axes axes, int flags)
    {
        var place = (flags & Place) != 0;
        var texcoords = (flags & Texcoords) != 0;
        if (!place && !texcoords)
            return;
        var inverse = new float[16];
        LightMath.Inverse4(LightMath.To4(m), inverse);
        float[] n = [inverse[0], inverse[4], inverse[8], inverse[12], inverse[1], inverse[5], inverse[9], inverse[13], inverse[2], inverse[6], inverse[10], inverse[14]];
        // FUN_1812586a0: the column lengths, squares summed down the column.
        float[] lengths =
        [
            MathF.Sqrt((m[0] * m[0]) + (m[4] * m[4]) + (m[8] * m[8])),
            MathF.Sqrt((m[1] * m[1]) + (m[5] * m[5]) + (m[9] * m[9])),
            MathF.Sqrt((m[2] * m[2]) + (m[6] * m[6]) + (m[10] * m[10])),
        ];
        float Scale(int axis) => axis == -1 ? 1f : lengths[axis];
        float su = Scale(axes.U), sv = Scale(axes.V), su2 = Scale(axes.U2), sv2 = Scale(axes.V2);
        for (var at = 0; at + stride <= v.Length; at += stride)
        {
            if (texcoords)
            {
                if (texcoord0 != -1)
                    Texcoord(v, at + texcoord0, su, sv);
                if (texcoord1 != -1)
                    Texcoord(v, at + texcoord1, su2, sv2);
            }
            if (!place)
                continue;
            if (position != -1)
                Set(v, at + position, LightMath.Transform34(m, Get(v, at + position)));
            if (normal != -1)
                Set(v, at + normal, TJunctionFix.Normalise(LightMath.Rotate34(n, Get(v, at + normal))));
            if (tangent != -1)
                Set(v, at + tangent, TJunctionFix.Normalise(LightMath.Rotate34(m, Get(v, at + tangent))));
        }
    }

    // The u origin is 0, the flipped v origin 1 - 0, and the offsets fmod(|0|, 1).
    private static void Texcoord(float[] v, int at, float su, float sv)
    {
        var u = v[at];
        var f = 1f - v[at + 1];
        if (0f <= u)
            u = ((u - 0f) * su) + 0f;
        if (0f <= f)
            f = ((f - 1f) * sv) + 1f;
        v[at] = 0f + u;
        v[at + 1] = 1f - (0f + f);
    }

    private static Vector3 Get(float[] v, int at) => new(v[at], v[at + 1], v[at + 2]);

    private static void Set(float[] v, int at, Vector3 x) => (v[at], v[at + 1], v[at + 2]) = (x.X, x.Y, x.Z);

    /// <summary>
    /// TexCoordScaleByModelU/V (all of csgo's model shaders) and
    /// TexCoord2ScaleByModelU/V (csgo_environment_blend): the shader's
    /// expression g_nScaleTexCoord*ByModelScaleAxis - 1, the parameter's
    /// 0 being none.
    /// </summary>
    public static Axes AxesOf(string? shader, IReadOnlyDictionary<string, long>? parameters)
    {
        var name = Path.GetFileNameWithoutExtension(shader ?? "");
        int Axis(string param, bool declared) => declared ? (int)(parameters?.GetValueOrDefault(param) ?? 0) - 1 : -1;
        var first = ScaleByModelShaders.Contains(name);
        var second = name == "csgo_environment_blend";
        return new Axes(Axis("g_nScaleTexCoordUByModelScaleAxis", first), Axis("g_nScaleTexCoordVByModelScaleAxis", first),
            Axis("g_nScaleTexCoord2UByModelScaleAxis", second), Axis("g_nScaleTexCoord2VByModelScaleAxis", second));
    }

    // The shaders whose vs declares TexCoordScaleByModelU/V (shaders_pc_dir.vpk, 1.41.8.4).
    private static readonly HashSet<string> ScaleByModelShaders = new(Tier0Strings.IgnoreCase)
    {
        "csgo_beachfoam", "csgo_character", "csgo_complex", "csgo_decalmodulate", "csgo_depth_only", "csgo_environment_blend",
        "csgo_environment", "csgo_flashbang_overlay", "csgo_legs_prepass", "csgo_moondome", "csgo_simple_2way_blend",
        "csgo_simple_liquid", "csgo_simple", "csgo_static_overlay", "csgo_unlitgeneric", "csgo_vertexlitgeneric",
        "csgo_water_fancy", "csgo_water", "csgo_weapon", "simple",
    };
}
