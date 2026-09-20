using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The FGD is what turns Hammer's strings into the types a compiled entity lump
/// carries, so a wrong answer here is a wrong value in every entity of that class.
/// These read the game's own fgd rather than a fixture.
/// </summary>
public class FgdSchemaTests
{
    [Theory]
    // Declared on the class itself.
    [InlineData("info_player_counterterrorist", "priority", FgdSchema.FieldType.Integer)]
    [InlineData("info_player_counterterrorist", "enabled", FgdSchema.FieldType.Boolean)]
    [InlineData("worldspawn", "skyname", FgdSchema.FieldType.String)]
    [InlineData("worldspawn", "steamaudio_reverb_rays", FgdSchema.FieldType.Integer)]
    [InlineData("worldspawn", "steamaudio_reverb_ir_duration", FgdSchema.FieldType.Float)]
    // Inherited through base(), which is most of what an entity carries.
    [InlineData("prop_dynamic", "solid", FgdSchema.FieldType.String)]
    public void TypeOf_ReadsTheGamesOwnSchema(string className, string key, FgdSchema.FieldType expected)
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        Assert.Equal(expected, schema.TypeOf(className, key));
    }

    [Theory]
    // A four-component colour is still a colour.
    [InlineData("point_worldtext", "color", FgdSchema.FieldType.Color)]
    [InlineData("light_environment", "skyambientbounce", FgdSchema.FieldType.Color)]
    // A name reference, which is the set entity-name fixup rewrites.
    [InlineData("env_cubemap_fog", "cubemapfogskyentity", FgdSchema.FieldType.EntityName)]
    [InlineData("info_player_terrorist", "targetname", FgdSchema.FieldType.EntityName)]
    public void TypeOf_HandlesTheSchemaTricksThatChangeAValue(string className, string key, FgdSchema.FieldType expected)
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        Assert.Equal(expected, schema.TypeOf(className, key));
    }

    [Fact]
    public void RemovedKeys_LeaveTheSchemaEntirely()
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        // csgo.fgd's @OverrideClass takes five occlusion keys off
        // light_environment with remove_key. They are then UNKNOWN, not
        // "known but untyped": the compile writes no default for them, and a
        // value the source still carries ships as the raw string it was.
        Assert.Null(schema.TypeOf("light_environment", "ambient_occlusion"));
        Assert.Null(schema.TypeOf("light_environment", "max_occlusion_distance"));

        // The same override leaves everything else the class inherits alone.
        Assert.Equal(FgdSchema.FieldType.Color, schema.TypeOf("light_environment", "skyambientbounce"));
    }

    [Fact]
    public void GameKeysOf_ReadsWhatAClassShipsWithoutBeingAsked()
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        // This is what a point prefab actually is. The class is an ordinary
        // @PointClass whose metadata declares the keys every instance carries, so
        // the compile reads them off the schema - nothing resolves a content tree.
        Assert.Equal(
            [new("isPointPrefab", "true"),
             new("targetMapName", "prefabs/misc/counterterrorist_team_intro")],
            schema.GameKeysOf("counterterrorist_team_intro"));

        Assert.Empty(schema.GameKeysOf("info_player_terrorist"));
        Assert.Empty(schema.GameKeysOf("not_a_real_class"));
    }

    [Fact]
    public void Load_IgnoresCommentedOutKeys()
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        // csgo.fgd comments out env_sky's fog block. Reading those as live keys
        // put five keys into every env_sky that Valve's compile does not write.
        Assert.Null(schema.TypeOf("env_sky", "fog_type"));
        Assert.Null(schema.TypeOf("env_sky", "angular_fog_max_end"));
    }

    [Fact]
    public void TypeOf_SaysNothingRatherThanGuessing()
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        // An unknown key has no declared type, and the caller has to keep it a
        // string rather than infer one from how the value happens to look.
        Assert.Null(schema.TypeOf("info_player_terrorist", "not_a_real_key"));
        Assert.Null(schema.TypeOf("not_a_real_class", "targetname"));
    }

    [Fact]
    public void Load_FollowsIncludesIntoTheEngineSchema()
    {
        var schema = MapFixtures.GameSchema();
        if (schema is null) { MapFixtures.Skip("the game's csgo.fgd"); return; }

        // csgo.fgd declares neither of these: they come from base.fgd and
        // lights.fgd, which it includes.
        Assert.Contains("worldspawn", schema.ClassNames);
        Assert.Contains("light_omni2", schema.ClassNames);
    }
}
