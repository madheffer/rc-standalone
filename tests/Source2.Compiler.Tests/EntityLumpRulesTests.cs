using System.Numerics;
using Source2.Compiler;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The entity lump's rules on a small synthetic FGD and hand-built entities, so
/// each rule is pinned on its own and without a game install. Every rule here was
/// read out of resourcecompiler and measured against Valve's lumps; the tests say
/// which.
/// </summary>
public sealed class EntityLumpRulesTests : IDisposable
{
    private const string Fgd = """
        @BaseClass = Targetname
        [
        	targetname(target_source) : "Name"
        ]

        @BaseClass base(Targetname)
        	metadata
        	{
        		supports_loop = true
        		path_node_class = "node_a"
        	}
        = Base2
        [
        	first(integer) : "First" : 5
        	second(string) : "Second" : "x"
        	fourth(integer) : "Fourth" : 4
        ]

        @PointClass base(Base2) = thing
        [
        	second(float) : "Second" : "1.5"
        	third(color255) : "Third" : "1 2 3"
        	model(studio) : "Model" : ""
        	fx(resource:particle) : "Effect" : ""
        	blob(kv3) : "Blob" : ""
        	fourth(remove_key)
        	flags(flags) : "Flags" : 0
        	on(boolean) : "On" : 1
        	size(vector2d) : "Size" : "1 2"
        	quad(vector4d) : "Quad" : "1 2 3 4"
        	node(node_id) : "Node" : 0
        	count(intchoices) : "Count" : 2
        	scale(floatchoices) : "Scale" : "0.5"
        	parentname(target_destination) : "Parent" : ""
        ]

        @OverrideClass = thing
        [
        	first(remove_key)
        	fifth(string) : "Fifth" : "five"
        ]

        @PointClass base(Targetname) = broken
        [
        	nonsense(not_a_type) : "Nope"
        ]

        @PathNodeClass = node_a
        [
        	radius_scale(float) : "Radius" : "1.0"
        	pin(bool) { write_to_path_key = "pathNodePins" } : "Pin" : "1"
        ]

        @PathClass base(Base2) = path_a
        [
        ]

        @PointClass base(Targetname)
        	metadata
        	{
        		create_entity_template_lumps =
        		[
        			{
        				lumpMode = "PointTemplate"
        				targetWorldKey = "worldName"
        				targetLumpKey = "entityLumpName"
        			}
        		]
        	}
        = point_template
        [
        	spawnflags(flags) =
        	[
        		1 : "Keep" : 0
        		2 : "Preserve" : 0
        	]
        	Template01(target_destination) : "T1"
        	Template02(target_destination) : "T2"
        	Template03(target_destination) : "T3"
        ]

        @PointClass base(Targetname) = relay
        [
        	message(string) : "Message" : ""
        	parentname(target_destination) : "Parent" : ""
        ]

        @PointClass base(Targetname)
        	metadata
        	{
        		class_game_keys = [ { key = "isThing" value = true } ]
        	}
        = gamekeyed
        [
        ]

        @PointClass = visibility_hint
        [
        ]

        @PointClass base(Targetname) = gone_class
        [
        ]

        @exclude gone_class
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("s2c_fgd").FullName;
    private readonly FgdSchema _schema;

    public EntityLumpRulesTests()
    {
        var path = Path.Combine(_dir, "test.fgd");
        File.WriteAllText(path, Fgd);
        _schema = FgdSchema.Load(path);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- the schema -------------------------------------------------------------

    [Fact]
    public void Schema_FinalizesBasesFirstAndKeepsARedeclaredKeysPlace()
    {
        // Base2's keys come after Targetname's, then thing's own. second is
        // redeclared as a float and keeps its place; fourth is removed; first is
        // removed by the override; fifth is appended by it.
        Assert.Equal(
            ["targetname", "second", "third", "model", "fx", "blob", "flags", "on", "size", "quad", "node",
             "count", "scale", "parentname", "fifth"],
            _schema.KeysOf("thing").Select(k => k.Name));
        Assert.Equal(FgdSchema.FieldType.Float, _schema.TypeOf("thing", "second"));
        Assert.Equal("1.5", _schema.KeyOf("thing", "second")!.Default);
        Assert.Null(_schema.KeyOf("thing", "fourth"));
        Assert.Null(_schema.KeyOf("thing", "first"));
    }

    [Theory]
    [InlineData("third", FgdSchema.FieldType.Color, "")]
    [InlineData("model", FgdSchema.FieldType.Resource, "vmdl")]
    [InlineData("fx", FgdSchema.FieldType.Resource, "vpcf")]
    [InlineData("blob", FgdSchema.FieldType.Kv3, "")]
    [InlineData("flags", FgdSchema.FieldType.Flags, "")]
    [InlineData("on", FgdSchema.FieldType.Boolean, "")]
    [InlineData("size", FgdSchema.FieldType.Vector2, "")]
    [InlineData("quad", FgdSchema.FieldType.Vector4, "")]
    [InlineData("node", FgdSchema.FieldType.Int32, "")]
    [InlineData("count", FgdSchema.FieldType.Integer, "")]
    [InlineData("scale", FgdSchema.FieldType.Float, "")]
    [InlineData("parentname", FgdSchema.FieldType.EntityName, "")]
    [InlineData("targetname", FgdSchema.FieldType.EntityName, "")]
    public void Schema_ResolvesTypesThroughResourceCompilersTable(string key, FgdSchema.FieldType type, string extension)
    {
        var declared = _schema.KeyOf("thing", key)!;
        Assert.Equal(type, declared.Type);
        Assert.Equal(extension, declared.Extension);
    }

    [Fact]
    public void Schema_DropsAClassWithAnUnknownTypeAndHonoursExclude()
    {
        Assert.Empty(_schema.KeysOf("broken"));
        Assert.Empty(_schema.KeysOf("gone_class"));
        Assert.DoesNotContain("gone_class", _schema.ClassNames);
    }

    [Fact]
    public void Schema_InheritsMetadataAndReadsPathKeys()
    {
        Assert.True(_schema.HasFlag("path_a", "supports_loop"));
        Assert.Equal("node_a", _schema.MetadataOf("path_a", "path_node_class"));
        Assert.True(_schema.IsPathNodeClass("node_a"));
        Assert.Equal("pathNodePins", _schema.KeyOf("node_a", "pin")!.WriteToPathKey);
        Assert.Equal(
            [new FgdSchema.TemplateLump("PointTemplate", "", "worldName", "entityLumpName")],
            _schema.TemplateLumpsOf("point_template"));
    }

    // ---- one entity -------------------------------------------------------------

    private static MapEntities.Entity Entity(string cls, int id, params (string Key, string Value)[] keys)
        => new(cls, id, [.. new[] { ("classname", cls) }.Concat(keys).Select(k => new KeyValuePair<string, string>(k.Item1, k.Item2))],
               Vector3.Zero, Vector3.Zero, Vector3.One, IsWorld: cls == "worldspawn", []);

    private KVObject Build(MapEntities.Entity entity, int index = 3, bool fixup = false, string? world = "testmap")
        => EntityLumpAuthor.BuildEntity(entity, index,
            EntityLumpAuthor.Context.For([entity], _schema, world, fixup));

    private static KVObject ValuesOf(KVObject entity) => entity["keyValues3Data"]["values"];

    [Fact]
    public void Entity_WritesItsKeyTableBackwardsThenTheCompilesOwnKeys()
    {
        // Source keys in source order, then the class's missing keys in its
        // finalized order, all written in reverse; then compile_source_id,
        // origin, angles, scales and hammerUniqueId.
        var values = ValuesOf(Build(Entity("thing", 7, ("second", "2.5"), ("targetname", "a"))));
        var expected = new List<string> { "classname", "second", "targetname" };
        expected.AddRange(new[] { "third", "model", "fx", "flags", "on", "size", "quad", "node", "count", "scale",
                                  "parentname", "fifth" });
        expected.Reverse();
        expected.AddRange(["compile_source_id", "origin", "angles", "scales", "hammerUniqueId"]);
        Assert.Equal(expected, values.Keys.ToList());
    }

    [Fact]
    public void Worldspawn_WritesItsIdFirst()
    {
        var values = ValuesOf(Build(Entity("worldspawn", 1)));
        Assert.Equal("compile_source_id", values.Keys.First());
        Assert.Equal(["worldname", "mapUsageType"], values.Keys.TakeLast(2));
    }

    [Fact]
    public void Entity_TypesEveryValueAsTheLumpWriterDoes()
    {
        var values = ValuesOf(Build(Entity("thing", 7,
            ("third", "10 20 30"), ("model", "Models\\Crate.MDL"), ("fx", "sparks"), ("flags", "6144"),
            ("on", "true"), ("size", "3"), ("quad", "1 2"), ("node", "12abc"), ("count", "0.6"), ("scale", "0.1"),
            ("blob", "{ a = 1 }"))));

        Assert.Equal([10u, 20u, 30u], values["third"].Values.Select(v => (uint)v));
        Assert.All(values["third"].Values, v => Assert.Equal(KVValueType.UInt32, v.ValueType));
        Assert.Equal("models/crate.vmdl", values["model"].ToString());
        Assert.Equal(KVFlag.ResourceName, values["model"].Flag);
        Assert.Equal("sparks.vpcf", values["fx"].ToString());
        Assert.Equal(KVValueType.UInt32, values["flags"].ValueType);
        Assert.Equal(KVValueType.Boolean, values["on"].ValueType);
        Assert.Equal([3.0, 0.0], values["size"].Values.Select(v => (double)v));
        Assert.Equal([1.0, 2.0, 0.0, 0.0], values["quad"].Values.Select(v => (double)v));
        Assert.Equal(0L, (long)values["node"]);
        Assert.Equal(KVValueType.Int64, values["count"].ValueType);
        Assert.Equal((double)0.1f, (double)values["scale"]);
        // kv3 keys are skipped by the key writer.
        Assert.False(values.ContainsKey("blob"));
        // An unknown key stays the mapper's string.
        Assert.Equal(KVValueType.String, values["classname"].ValueType);
    }

    [Fact]
    public void Entity_WritesZeroAndOneFlagsAsTheKv3Singletons()
    {
        Assert.Equal(KVValueType.Int64, ValuesOf(Build(Entity("thing", 7, ("flags", "1"))))["flags"].ValueType);
        Assert.Equal(KVValueType.Int64, ValuesOf(Build(Entity("thing", 7, ("flags", "0"))))["flags"].ValueType);
    }

    [Fact]
    public void Entity_ColourWithAlphaKeepsIt()
    {
        var values = ValuesOf(Build(Entity("thing", 7, ("third", "1 2 3 4"))));
        Assert.Equal([1u, 2u, 3u, 4u], values["third"].Values.Select(v => (uint)v));
    }

    [Fact]
    public void Entity_PrefixesNamesByTypeWhenTheMapAsks()
    {
        var values = ValuesOf(Build(Entity("thing", 7, ("targetname", "a"), ("parentname", "!activator")), fixup: true));
        Assert.Equal("[PR#]a", values["targetname"].ToString());
        Assert.Equal("!activator", values["parentname"].ToString());
        // A key the source lacks takes its FGD default.
        Assert.Equal("five", values["fifth"].ToString());
    }

    [Fact]
    public void Entity_WritesClassGameKeysAfterItsOwnKeys()
    {
        var values = ValuesOf(Build(Entity("gamekeyed", 7)));
        var keys = values.Keys.ToList();
        Assert.Equal(keys.IndexOf("compile_source_id") - 1, keys.IndexOf("isThing"));
    }

    [Fact]
    public void Connections_AreSerialisedThroughTheirSchema()
    {
        var entity = Entity("relay", 9, ("targetname", "r")) with
        {
            Connections = [new MapEntities.Connection("OnTrigger", "door", "Open", "r", 1.5f, -1),
                           new MapEntities.Connection("OnTrigger", "!self", "Kill", "", 0f, 1)],
        };
        var connections = Build(entity, fixup: true)["m_connections"].Values.ToList();

        var first = connections[0];
        Assert.Equal(["m_outputName", "m_targetType", "m_targetName", "m_inputName", "m_overrideParam",
                      "m_flDelay", "m_nTimesToFire", "m_paramMap"], first.Keys);
        Assert.Equal(KVValueType.UInt32, first["m_targetType"].ValueType);
        Assert.Equal(7u, (uint)first["m_targetType"]);
        Assert.Equal("[PR#]door", first["m_targetName"].ToString());
        // A parameter that names one of the map's entities is prefixed too.
        Assert.Equal("[PR#]r", first["m_overrideParam"].ToString());
        Assert.Equal(KVValueType.FloatingPoint64, first["m_flDelay"].ValueType);
        Assert.Equal(KVValueType.Int32, first["m_nTimesToFire"].ValueType);
        Assert.True(first["m_paramMap"].IsNull);

        Assert.Equal("!self", connections[1]["m_targetName"].ToString());
        Assert.Equal(KVValueType.Int64, connections[1]["m_nTimesToFire"].ValueType);
    }

    [Fact]
    public void ConsumedClasses_NeverReachTheLump()
    {
        Assert.False(EntityLumpAuthor.ReachesTheLump(Entity("visibility_hint", 1), _schema));
        Assert.False(EntityLumpAuthor.ReachesTheLump(Entity("point_scale_reference_human", 1), _schema));
        Assert.True(EntityLumpAuthor.ReachesTheLump(Entity("relay", 1), _schema));
    }

    [Fact]
    public void InfoWorldLayer_NamesItsLump()
    {
        var values = ValuesOf(Build(Entity("info_world_layer", 5, ("layerName", "Upper"))));
        Assert.Equal("world_layer_Upper", values["layerName"].ToString());
        Assert.Equal("worldname", values.Keys.Last());
        Assert.Equal("testmap", values["worldname"].ToString());
    }

    [Fact]
    public void InstanceCopy_IsANewEntityOfItsClass()
    {
        // A walked entity keeps its source's order; the copy an instance places is
        // built as a new entity: classname, targetname, then the class's keys in
        // finalized order, which is what Mako's func_door copies ship reversed.
        var walked = Entity("thing", 7, ("fifth", "5"), ("targetname", "a"), ("third", "1 1 1"));
        var copy = walked with { Instanced = true };
        var copyKeys = ValuesOf(Build(copy)).Keys.TakeWhile(k => k != "compile_source_id").Reverse().ToList();
        Assert.Equal(["classname", "targetname", "second", "third", "model", "fx", "flags", "on", "size", "quad",
                      "node", "count", "scale", "parentname", "fifth"], copyKeys);
        var walkedKeys = ValuesOf(Build(walked)).Keys.TakeWhile(k => k != "compile_source_id").Reverse().ToList();
        Assert.Equal(["classname", "fifth", "targetname", "third"], walkedKeys.Take(4));
    }

    [Fact]
    public void SmartProps_CountTheLocatorsADefinitionCreates()
    {
        // Measured: radiator_01 (one CreateSizer) makes one locator, the wall AC
        // unit (one CreateLocator) one, the industrial lamp none.
        KVObject Element(string cls, params KVObject[] children)
        {
            var e = KVObject.Collection();
            e.Add("_class", new KVObject(cls));
            var array = KVObject.Array();
            foreach (var c in children)
                array.Add(c);
            e.Add("m_Children", array);
            return e;
        }
        var definition = Element("CSmartPropRoot",
            Element("CSmartPropElement_Group", Element("CSmartPropOperation_CreateSizer")),
            Element("CSmartPropElement_Model", Element("CSmartPropOperation_CreateLocator"), Element("CSmartPropOperation_Translate")));
        Assert.Equal(2, SmartProps.LocatorsOf(definition));
        Assert.Equal(0, SmartProps.LocatorsOf(Element("CSmartPropElement_Model")));
    }

    // ---- paths -------------------------------------------------------------------

    private static MapEntities.PathNode Node(float x, float y, float z, params (string, string)[] keys)
        => new(new Vector3(x, y, z),
               [.. new[] { ("classname", "node_a") }.Concat(keys).Select(k => new KeyValuePair<string, string>(k.Item1, k.Item2))]);

    [Fact]
    public void Path_WritesItsNodesTangentsAndArrays()
    {
        var path = Entity("path_a", 20) with
        {
            Origin = new Vector3(100, 0, 0),
            PathNodes = [Node(100, 0, 0, ("radius_scale", "2")), Node(100, 30, 0, ("radius_scale", "2"), ("pin", "0"))],
        };
        var values = ValuesOf(Build(path));
        var keys = values.Keys.ToList();
        Assert.Equal(["hammerUniqueId", "pathNodes", "pathNodeRadiusScales", "pathNodeRadiusHeightScales", "closed_loop",
                      "pathNodePins"], keys.Skip(keys.IndexOf("hammerUniqueId")));

        // Type 0 tangents: a third of the way to each neighbour, zero at the ends.
        Assert.Equal("[\n\t[\n\t\t0.0, 0.0, 0.0, 0.0,\n\t\t0.0, 0.0, 0.0, 10.0,\n\t\t0.0,\n\t],\n"
                   + "\t[\n\t\t0.0, 30.0, 0.0, 0.0,\n\t\t-10.0, 0.0, 0.0, 0.0,\n\t\t0.0,\n\t],\n]",
                     values["pathNodes"].ToString());
        Assert.Equal("[ 2.0, 2.0 ]", values["pathNodeRadiusScales"].ToString());
        // A node that does not carry the key reads as zero (FUN_180f31c30): nodes
        // are not given their class's defaults.
        Assert.Equal("[ false, false ]", values["pathNodePins"].ToString());
        Assert.Equal(0L, (long)values["closed_loop"]);
    }

    [Fact]
    public void Path_ClosedLoopRepeatsItsFirstNode()
    {
        var path = Entity("path_a", 20) with
        {
            ClosedLoop = true,
            PathNodes = [Node(0, 0, 0), Node(30, 0, 0), Node(30, 30, 0)],
        };
        var text = ValuesOf(Build(path))["pathNodes"].ToString()!;
        var rows = text.Split("\t],\n");
        Assert.Equal(5, rows.Length);                      // four rows and the closing bracket
        Assert.Equal(rows[0].TrimStart('[', '\n'), rows[3]);
    }

    [Fact]
    public void Path_InterpolationTypeOneUsesBothNeighbours()
    {
        // With both neighbours the middle node's handles are parallel to the
        // chord between them, each a third of its own leg long.
        var path = Entity("path_a", 20) with
        {
            InterpolationType = 1,
            PathNodes = [Node(0, 0, 0), Node(30, 0, 0), Node(30, 30, 0)],
        };
        var text = ValuesOf(Build(path))["pathNodes"].ToString()!;
        var middle = text.Split("\t],\n")[1];
        var numbers = middle.Split([',', '\n', '\t', '[', ' '], StringSplitOptions.RemoveEmptyEntries).Select(float.Parse).ToArray();
        var inward = new Vector3(numbers[3], numbers[4], numbers[5]);
        var outward = new Vector3(numbers[6], numbers[7], numbers[8]);
        Assert.Equal(10f, inward.Length(), 4);
        Assert.Equal(10f, outward.Length(), 4);
        Assert.Equal(-1f, Vector3.Dot(Vector3.Normalize(inward), Vector3.Normalize(outward)), 4);
        Assert.Equal(Vector3.Normalize(new Vector3(1, 1, 0)), Vector3.Normalize(outward));
    }

    // ---- the template pass -------------------------------------------------------

    private static Dictionary<string, KVObject> Read(IReadOnlyList<EntityLumpSet.Lump> lumps)
        => lumps.ToDictionary(l => l.Name, l =>
        {
            using var resource = ResourceTrees.Read(l.Bytes, l.Path);
            return ((EntityLump)resource.DataBlock!).Data;
        });

    private static List<KVObject> EntitiesOf(KVObject lump) => [.. lump["m_entityKeyValues"].Values];

    private static string NameOf(KVObject entity)
        => ValuesOf(entity).ContainsKey("targetname") ? ValuesOf(entity)["targetname"].ToString()! : "";

    private IReadOnlyList<EntityLumpSet.Lump> Author(params MapEntities.Entity[] entities)
        => EntityLumpSet.Author([Entity("worldspawn", 1), .. entities], _schema, "testmap");

    [Fact]
    public void Template_CopiesItsMembersIntoALumpAndRemovesTheOriginals()
    {
        var template = Entity("point_template", 50, ("targetname", "tmpl"), ("spawnflags", "0"),
                              ("Template01", "a1"), ("Template02", "b"), ("Template03", "b")) with
        { Origin = new Vector3(10, 0, 0) };
        var a1 = Entity("relay", 51, ("targetname", "a1"), ("message", "b")) with
        {
            Origin = new Vector3(11, 0, 0),
            Connections = [new MapEntities.Connection("OnTrigger", "b", "Fire", "a1", 0f, -1)],
        };
        var b = Entity("relay", 52, ("targetname", "b"), ("parentname", "a1"));
        var other = Entity("relay", 53, ("targetname", "other"), ("message", "b"));
        var lumps = Read(Author(template, a1, b, other));

        // default_ents keeps the template and the entity nobody named.
        var main = EntitiesOf(lumps["default_ents"]);
        Assert.Equal(["", "tmpl", "other"], main.Select(NameOf));
        Assert.Equal(["maps/testmap/entities/50#entitylumpname.vents"],
                     lumps["default_ents"]["m_childLumps"].Values.Select(v => v.ToString()));

        // The lump holds a copy per match, per slot: b is named twice.
        var members = EntitiesOf(lumps["50#entityLumpName"]);
        Assert.Equal(["a1&0000", "b&0000", "b&0000"], members.Select(NameOf));
        Assert.Equal([0L, 1L, 2L], members.Select(m => (long)ValuesOf(m)["_template_lump_ent_index"]));
        Assert.Equal("_template_lump_ent_index", ValuesOf(members[2]).Keys.Last());

        // Members sit in the template's space.
        Assert.Equal([1.0, 0.0, 0.0], ValuesOf(members[0])["origin"].Values.Select(v => (double)v));

        // References to members are renamed by value inside the lump, and nowhere
        // else.
        Assert.Equal("b&0000", ValuesOf(members[0])["message"].ToString());
        Assert.Equal("a1&0000", ValuesOf(members[1])["parentname"].ToString());
        Assert.Equal("b&0000", members[0]["m_connections"].Values.First()["m_targetName"].ToString());
        Assert.Equal("a1&0000", members[0]["m_connections"].Values.First()["m_overrideParam"].ToString());
        Assert.Equal("b", ValuesOf(main[2])["message"].ToString());

        // The template records its lump, then TemplateFixup since it renamed.
        var values = ValuesOf(main[1]);
        Assert.Equal(["hammerUniqueId", "worldName", "entityLumpName", "TemplateFixup"], values.Keys.TakeLast(4));
        Assert.Equal("maps\\testmap", values["worldName"].ToString());
        Assert.Equal("50#entityLumpName", values["entityLumpName"].ToString());
        Assert.True((bool)values["TemplateFixup"]);
    }

    [Fact]
    public void Template_MatchesWithTheWildcardsInTheEntitysName()
    {
        // V_CompareNameWithWildcards is called with the entity's targetname first,
        // so a wildcard in the NAME matches the template's literal, not the reverse.
        var template = Entity("point_template", 50, ("targetname", "tmpl"), ("spawnflags", "2"),
                              ("Template01", "bee"), ("Template02", "c*"));
        var star = Entity("relay", 51, ("targetname", "b*"));
        var literal = Entity("relay", 52, ("targetname", "cat"));
        var lumps = Read(Author(template, star, literal));
        Assert.Equal(["b*"], EntitiesOf(lumps["50#entityLumpName"]).Select(NameOf));
        Assert.Equal(["", "tmpl", "cat"], EntitiesOf(lumps["default_ents"]).Select(NameOf));
    }

    [Fact]
    public void Template_SpawnflagsKeepOriginalsAndPreserveNames()
    {
        var template = Entity("point_template", 50, ("targetname", "tmpl"), ("spawnflags", "3"), ("Template01", "a"));
        var a = Entity("relay", 51, ("targetname", "a"));
        var lumps = Read(Author(template, a));

        Assert.Equal(["", "tmpl", "a"], EntitiesOf(lumps["default_ents"]).Select(NameOf));
        Assert.Equal(["a"], EntitiesOf(lumps["50#entityLumpName"]).Select(NameOf));
        Assert.False(ValuesOf(EntitiesOf(lumps["default_ents"])[1]).ContainsKey("TemplateFixup"));
    }

    [Fact]
    public void Template_FindsAHiddenMemberThatDefaultEntsDoesNotShip()
    {
        // Mako's Baha_Mat_Earth_Physic is hidden and still ships in its lump.
        var template = Entity("point_template", 50, ("targetname", "tmpl"), ("spawnflags", "3"), ("Template01", "a"));
        var a = Entity("relay", 51, ("targetname", "a")) with { Hidden = true };
        var lumps = Read(Author(template, a));

        Assert.Equal(["", "tmpl"], EntitiesOf(lumps["default_ents"]).Select(NameOf));
        Assert.Single(EntitiesOf(lumps["50#entityLumpName"]));
    }

    [Fact]
    public void Template_WithNoMembersRecordsNothing()
    {
        var template = Entity("point_template", 50, ("targetname", "tmpl"), ("Template01", "nobody"));
        var lumps = Read(Author(template));
        Assert.False(ValuesOf(EntitiesOf(lumps["default_ents"])[1]).ContainsKey("entityLumpName"));
        Assert.Empty(EntitiesOf(lumps["50#entityLumpName"]));
    }
}
