namespace Source2.Compiler.Physics;

/// <summary>
/// The bool attributes a loaded material answers from its shader: what CS2's
/// compiled pixel shaders declare per static combo (csgo and csgo_core
/// shaders_pc_dir.vpk, 1.41.8.5, read with ShaderAttributeProbe), with each
/// S_ combo taken from the material's F_ parameter of the same name. A shader
/// not listed declares neither. csgo_unlitgeneric declares "translucent" on 80
/// combos whose condition is not read; it is left out.
/// </summary>
public static class ShaderAttributes
{
    /// <summary>"translucent".</summary>
    public static bool Translucent(string shader, IReadOnlyDictionary<string, long> features)
    {
        bool On(string key) => features.TryGetValue(key, out var v) && v != 0;
        long Value(string key) => features.TryGetValue(key, out var v) ? v : 0;
        return Name(shader) switch
        {
            "csgo_complex" or "csgo_vertexlitgeneric" or "csgo_depth_only" or "cables"
                => !On("F_ALPHA_TEST") && On("F_TRANSLUCENT"),
            "csgo_weapon" => Value("F_ENABLE_ADJUSTMENTS") == 0 && !On("F_ALPHA_TEST") && On("F_TRANSLUCENT") && !On("F_OPAQUE_REFRACT"),
            "csgo_character" => !On("F_EYEBALLS") && !On("F_ALPHA_TEST") && On("F_TRANSLUCENT"),
            "csgo_lightmappedgeneric" => Value("F_LAYERS") == 0 && !On("F_ALPHA_TEST") && On("F_TRANSLUCENT") && Value("F_DETAILBLENDMODE") == 0,
            "csgo_glass" => !On("F_OPAQUE_CUBEMAP_REFRACTION"),
            "csgo_water_fancy" or "csgo_static_overlay" or "csgo_effects" or "grasstile" or "grasstile_preview" or "luminaire"
                or "csgo_decalmodulate" or "csgo_refract" or "csgo_water" => true,
            _ => false,
        };
    }

    /// <summary>"alphatest".</summary>
    public static bool AlphaTest(string shader, IReadOnlyDictionary<string, long> features)
    {
        bool On(string key) => features.TryGetValue(key, out var v) && v != 0;
        long Value(string key) => features.TryGetValue(key, out var v) ? v : 0;
        return Name(shader) switch
        {
            "csgo_complex" or "csgo_vertexlitgeneric" => On("F_ALPHA_TEST") && !On("F_TRANSLUCENT") && !On("F_ADDITIVE_BLEND"),
            "csgo_weapon" => On("F_ALPHA_TEST") && !On("F_TRANSLUCENT") && !On("F_ADDITIVE_BLEND") && !On("F_OPAQUE_REFRACT"),
            "csgo_character" => !On("F_EYEBALLS") && On("F_ALPHA_TEST") && !On("F_TRANSLUCENT") && !On("F_ADDITIVE_BLEND"),
            "csgo_lightmappedgeneric" => Value("F_LAYERS") == 0 && On("F_ALPHA_TEST") && !On("F_TRANSLUCENT") && Value("F_DETAILBLENDMODE") == 0,
            "csgo_depth_only" or "cables" => On("F_ALPHA_TEST") && !On("F_TRANSLUCENT"),
            "csgo_environment_blend" or "csgo_foliage" => On("F_ALPHA_TEST"),
            // csgo_environment's combo is S_ALPHA_TEST_LAYER, set by F_ALPHA_TEST
            // (measured: metalrail011a's triangles carry the bit in Mako's .rte).
            "csgo_environment" => On("F_ALPHA_TEST"),
            "csgo_static_overlay" or "csgo_unlitgeneric" => Value("F_BLEND_MODE") == 2,
            _ => false,
        };
    }

    private static string Name(string shader) => Path.GetFileNameWithoutExtension(shader).ToLowerInvariant();
}
