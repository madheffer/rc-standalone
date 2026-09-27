using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps;

/// <summary>
/// A map's smart props (CMapSmartProp), evaluated as the compile evaluates
/// them (smartprops.dll 0923). The .vsmart's element tree is walked from the
/// node's world transform with variables and an element path; each element's
/// random stream is seeded from the node's stored configuration for its path;
/// a Model element emits its model with the current transform.
/// resourcecompiler (181230360) brings each placement back into the node's
/// space and makes it a prop entity placed by the node's transform again
/// (18122d020); the round trip through the world is what the props' bits
/// carry (atixref's radiator: 13 of 13 mesh pieces bit for bit). Ported: the
/// root, groups, models, FitOnLine (random pick, fixed lengths), the sizer,
/// Translate and the variable filter; anything else throws.
/// </summary>
internal static class SmartPropEvaluator
{
    /// <summary>One emitted model: its path, element path and transform in the world.</summary>
    public sealed record Placement(string Model, int[] Path, CTransform Transform, Vector3 ModelScale);

    /// <summary>A node's stored configuration entry: the element's random seed and its locators' deltas.</summary>
    public sealed record Entry(int Seed, List<(string Name, Vector3 DeltaMin, Vector3 DeltaMax)> Locators);

    /// <summary>tier0's CUniformRandomStream (ran1: Park-Miller with a 32-entry shuffle).</summary>
    public sealed class UniformRandomStream
    {
        private int _idum;
        private int _iy;
        private readonly int[] _iv = new int[32];

        public void SetSeed(int seed)
        {
            _iy = 0;
            _idum = seed < 0 ? seed : -seed;
        }

        private static int Next(int idum)
        {
            var k = idum / 127773;
            idum = (16807 * (idum - (k * 127773))) - (2836 * k);
            return idum < 0 ? idum + 2147483647 : idum;
        }

        public int GenerateRandomNumber()
        {
            if (_idum <= 0 || _iy == 0)
            {
                _idum = -_idum < 1 ? 1 : -_idum;
                for (var j = 39; j >= 0; j--)
                {
                    _idum = Next(_idum);
                    if (j < 32)
                        _iv[j] = _idum;
                }
                _iy = _iv[0];
            }
            _idum = Next(_idum);
            var n = _iy / (1 + (2147483646 / 32));
            if (n >= 32 || n < 0)
                n = (n % 32) & 0x7fffffff;
            _iy = _iv[n];
            _iv[n] = _idum;
            return _iy;
        }

        public int RandomInt(int low, int high)
        {
            var x = (uint)(high - low) + 1;
            if (x <= 1 || (uint)(high - low) >= 0x80000000)
                return low;
            var max = 0x7fffffffu - (uint)(0x80000000ul % x);
            uint n;
            do
                n = (uint)GenerateRandomNumber();
            while (n > max);
            return low + (int)(n % x);
        }
    }

    /// <summary>
    /// A CMapSmartProp's nodeData: its configuration by element path and its
    /// parameter values by name.
    /// </summary>
    public static (Dictionary<string, Entry> Configuration, Dictionary<string, object> Parameters) NodeData(DmxBinary.Element node)
    {
        var configuration = new Dictionary<string, Entry>();
        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var data = node.Get<DmxBinary.Element>("nodeData");
        if (data == null)
            return (configuration, parameters);
        foreach (var value in data.GetElements("configuration").Select(e => e.Get<DmxBinary.Element>("value")!))
        {
            var path = (value.Get<object?[]>("elementPath") ?? []).Select(Convert.ToInt32);
            var locators = value.GetElements("m_LocatorConfig").Select(e => e.Get<DmxBinary.Element>("value")!)
                .Select(l => (l.Get<string>("m_LocatorName") ?? "", Floats(l, "m_vDeltaMin"), Floats(l, "m_vDeltaMax"))).ToList();
            configuration[string.Join(",", path)] = new Entry(value.GetValue<int>("randomSeed") ?? int.MinValue, locators);
        }
        foreach (var value in data.Get<DmxBinary.Element>("parameters")?.GetElements("values").Select(e => e.Get<DmxBinary.Element>("value")!) ?? [])
        {
            parameters[value.Get<string>("parameterName") ?? ""] = value.Attributes.GetValueOrDefault("value") switch
            {
                object?[] a => a.Select(x => Convert.ToSingle(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                bool b => b,
                string s => s,
                { } x => Convert.ToSingle(x, System.Globalization.CultureInfo.InvariantCulture),
                null => 0f,
            };
        }
        return (configuration, parameters);

        static Vector3 Floats(DmxBinary.Element e, string name)
            => e.Attributes.GetValueOrDefault(name) switch
            {
                Vector3 v => v,
                object?[] a => new Vector3(Convert.ToSingle(a[0]), Convert.ToSingle(a[1]), Convert.ToSingle(a[2])),
                _ => Vector3.Zero,
            };
    }

    private sealed class Context
    {
        public CTransform Transform;
        public readonly List<int> Path = [];
        public readonly Dictionary<string, object> Variables = new(StringComparer.OrdinalIgnoreCase);
        public required IReadOnlyDictionary<string, Entry> Configuration { get; init; }
        public readonly List<Placement> Output = [];
        public readonly UniformRandomStream Master = new();

        // The current element's stream: reset on entering an element, seeded
        // on first use from the configuration for the current path (1800118a0).
        public UniformRandomStream? Stream;

        public string PathKey => string.Join(",", Path);

        public UniformRandomStream ElementStream()
        {
            if (Stream != null)
                return Stream;
            var seed = Configuration.TryGetValue(PathKey, out var e) && e.Seed != int.MinValue ? e.Seed : Master.RandomInt(0, 0x7fffffff);
            Stream = new UniformRandomStream();
            Stream.SetSeed(seed);
            return Stream;
        }
    }

    /// <summary>
    /// Evaluates a .vsmart root (CSmartPropRoot) with the node's stored
    /// configuration (by element path, "409,371") and parameter values,
    /// starting from the given transform (18000eb00 sets both the object and
    /// the element transform to it). The compile starts from the node's world
    /// transform (<see cref="NodeTransform"/>), so placements come out in the world.
    /// </summary>
    public static List<Placement> Evaluate(KVObject root, IReadOnlyDictionary<string, Entry> configuration, IReadOnlyDictionary<string, object> parameters, CTransform start)
    {
        var context = new Context { Configuration = configuration, Transform = start };
        foreach (var v in root.GetArray("m_Variables") ?? [])
        {
            var name = v.GetStringProperty("m_VariableName");
            context.Variables[name] = parameters.TryGetValue(name, out var p) ? p : Literal(v["m_DefaultValue"]);
        }
        foreach (var child in root.GetArray("m_Children") ?? [])
            EvaluateChild(child, context);
        return context.Output;
    }

    // 18000f320: skip a disabled element; push its id, reset the element
    // stream, run its modifiers (a filter can stop it), evaluate it, then
    // restore the path and the transform.
    private static void EvaluateChild(KVObject element, Context context)
    {
        if (element.ContainsKey("m_bEnabled") && !Bool(element["m_bEnabled"], context))
            return;
        var saved = context.Transform;
        context.Path.Add(element.GetInt32Property("m_nElementID"));
        context.Stream = null;
        if (ApplyModifiers(element, context))
            Evaluate(element, context);
        context.Path.RemoveAt(context.Path.Count - 1);
        context.Transform = saved;
    }

    private static void Evaluate(KVObject element, Context context)
    {
        switch (element.GetStringProperty("_class"))
        {
            case "CSmartPropElement_Group":
                foreach (var child in element.GetArray("m_Children") ?? [])
                    EvaluateChild(child, context);
                break;
            case "CSmartPropElement_Model":
                // A detail object takes its uniform scale instead, and a surface
                // override reaches the prop's physics: neither is ported.
                if ((element.ContainsKey("m_bDetailObject") && Bool(element["m_bDetailObject"], context))
                    || (element.ContainsKey("m_SurfacePropertyOverride") && Value(element["m_SurfacePropertyOverride"], context) is string { Length: > 0 }))
                    throw new NotSupportedException("smart prop detail objects and surface overrides are not ported");
                context.Output.Add(new Placement(element.GetStringProperty("m_sModelName"), [.. context.Path], context.Transform,
                    element.ContainsKey("m_vModelScale") ? Vector(element["m_vModelScale"], context) : Vector3.One));
                break;
            case "CSmartPropElement_FitOnLine":
                FitOnLine(element, context);
                break;
            default:
                throw new NotSupportedException($"smart prop element {element.GetStringProperty("_class")} is not ported");
        }
    }

    // 18000f250: each enabled modifier in order; false when a filter stops the element.
    private static bool ApplyModifiers(KVObject element, Context context)
    {
        foreach (var m in element.GetArray("m_Modifiers") ?? [])
        {
            if (m.ContainsKey("m_bEnabled") && !Bool(m["m_bEnabled"], context))
                continue;
            switch (m.GetStringProperty("_class"))
            {
                case "CSmartPropOperation_SetTintColor":
                case "CSmartPropOperation_MaterialTint":
                    break;
                case "CSmartPropFilter_VariableValue":
                {
                    var c = m["m_VariableComparison"];
                    var have = context.Variables.GetValueOrDefault(c.GetStringProperty("m_Name"));
                    var want = Literal(c["m_Value"]);
                    var equal = Equals(Normalise(have), Normalise(want));
                    var comparison = c.GetStringProperty("m_Comparison", "EQUAL");
                    if (comparison == "EQUAL" ? !equal : comparison == "NOT_EQUAL" ? equal : throw new NotSupportedException($"variable comparison {comparison}"))
                        return false;
                    break;
                }
                case "CSmartPropOperation_Translate":
                {
                    // 180018c00: the context moved by the offset, rotated and scaled
                    // by it (CTransform composition); the rotation is kept.
                    var v = Vector(m["m_vPosition"], context);
                    var t = context.Transform;
                    var moved = CTransform.Compose(t, new CTransform(v, 1f, Quaternion.Identity));
                    context.Transform = new CTransform(moved.Position, moved.Scale, t.Rotation);
                    break;
                }
                case "CSmartPropOperation_CreateSizer":
                    Sizer(m, context);
                    break;
                default:
                    throw new NotSupportedException($"smart prop modifier {m.GetStringProperty("_class")} is not ported");
            }
        }
        return true;
    }

    // 180018460: the sizer's box, its initial minimum and maximum plus the
    // stored locator deltas for this path (180094890), each axis then held to
    // its constraints when those make a range, written to its output
    // variables. Every initial and constraint value defaults to 0.
    private static void Sizer(KVObject m, Context context)
    {
        float F(string key) => m.ContainsKey(key) ? Float(m[key], context) : 0f;
        var min = new Vector3(F("m_flInitialMinX"), F("m_flInitialMinY"), F("m_flInitialMinZ"));
        var max = new Vector3(F("m_flInitialMaxX"), F("m_flInitialMaxY"), F("m_flInitialMaxZ"));
        var name = m.GetStringProperty("m_Name", "");
        if (context.Configuration.TryGetValue(context.PathKey, out var entry) && entry.Locators.FirstOrDefault(l => l.Name == name) is { Name: not null } locator)
        {
            min = new Vector3(locator.DeltaMin.X + min.X, locator.DeltaMin.Y + min.Y, locator.DeltaMin.Z + min.Z);
            max = new Vector3(locator.DeltaMax.X + max.X, locator.DeltaMax.Y + max.Y, locator.DeltaMax.Z + max.Z);
        }
        float Hold(float v, float lo, float hi)
        {
            if (v <= lo)
                v = lo;
            return hi <= v ? hi : v;
        }
        foreach (var axis in "XYZ")
        {
            float lo = F($"m_flConstraintMin{axis}"), hi = F($"m_flConstraintMax{axis}");
            if (!(lo < hi))
                continue;
            var i = axis - 'X';
            min = WithAxis(min, i, Hold(min[i], lo, hi));
            max = WithAxis(max, i, Hold(max[i], lo, hi));
        }
        static Vector3 WithAxis(Vector3 v, int i, float x) => i == 0 ? v with { X = x } : i == 1 ? v with { Y = x } : v with { Z = x };
        void Out(string key, float value)
        {
            if (m.ContainsKey(key) && m.GetStringProperty(key) is { Length: > 0 } variable)
                context.Variables[variable] = value;
        }
        Out("m_OutputVariableMinX", min.X);
        Out("m_OutputVariableMaxX", max.X);
        Out("m_OutputVariableMinY", min.Y);
        Out("m_OutputVariableMaxY", max.Y);
        Out("m_OutputVariableMinZ", min.Z);
        Out("m_OutputVariableMaxZ", max.Z);
    }

    private sealed record Item(KVObject Element, int Type, float Length, float Min, float Max);

    // 180026230: the children fitted along start-end (1800250b0), placed one
    // after another (180026030), each evaluated at its place with the
    // context's rotation.
    private static void FitOnLine(KVObject element, Context context)
    {
        var start = element.ContainsKey("m_vStart") ? Vector(element["m_vStart"], context) : Vector3.Zero;
        var end = element.ContainsKey("m_vEnd") ? Vector(element["m_vEnd"], context) : Vector3.Zero;
        var dir = Normalise(new Vector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z));
        float dx = start.X - end.X, dy = start.Y - end.Y, dz = start.Z - end.Z;
        var length = MathF.Sqrt((dy * dy) + (dz * dz) + (dx * dx));
        // The constructor's defaults (180027c00): pick LARGEST_FIRST, scale
        // NONE, points in ELEMENT space (a copy), not oriented along the line.
        var pick = element.GetStringProperty("m_nPickMode", "LARGEST_FIRST");
        var scaled = element.GetStringProperty("m_nScaleMode", "SCALE_NONE") != "SCALE_NONE";
        if (element.GetStringProperty("m_PointSpace", "ELEMENT") != "ELEMENT" || (element.ContainsKey("m_bOrientAlongLine") && Bool(element["m_bOrientAlongLine"], context)))
            throw new NotSupportedException("FitOnLine points outside element space, or oriented along the line, are not ported");
        var items = Select(element, context, pick, scaled, length);
        var places = new List<Vector3>();
        var distance = 0f;
        foreach (var item in items)
        {
            places.Add(new Vector3((distance * dir.X) + start.X, (distance * dir.Y) + start.Y, (distance * dir.Z) + start.Z));
            distance += item.Length;
        }
        var t = context.Transform;
        var q = t.Rotation;
        for (var i = 0; i < items.Count; i++)
        {
            var p = places[i];
            // The same lanes as the CTransform compose: cross + ((w * t) + p).
            var tx = (p.Z * q.Y) - (p.Y * q.Z);
            var ty = (p.X * q.Z) - (p.Z * q.X);
            var tz = (p.Y * q.X) - (p.X * q.Y);
            tx += tx;
            ty += ty;
            tz += tz;
            var at = new Vector3(
                ((((tz * q.Y) - (ty * q.Z)) + ((q.W * tx) + p.X)) * t.Scale) + t.Position.X,
                ((((tx * q.Z) - (tz * q.X)) + ((q.W * ty) + p.Y)) * t.Scale) + t.Position.Y,
                ((((ty * q.X) - (tx * q.Y)) + ((q.W * tz) + p.Z)) * t.Scale) + t.Position.Z);
            context.Path.Add(items[i].Type == 1 ? -1 : i);
            context.Transform = new CTransform(at, t.Scale, q);
            EvaluateChild(items[i].Element, context);
            context.Path.RemoveAt(context.Path.Count - 1);
        }
        context.Transform = t;
    }

    // 1800250b0: a start cap, the end cap (measured against the start alone),
    // then fillers while the line is not covered (at most 1000 items before
    // the end cap), the end cap last. A pick with more than one candidate draws
    // RandomInt from the FitOnLine's own stream. A scale mode only matters for
    // items that may stretch (min below max); those are not ported.
    private static List<Item> Select(KVObject element, Context context, string pick, bool scaled, float length)
    {
        if (pick != "RANDOM")
            throw new NotSupportedException($"FitOnLine pick mode {pick} is not ported");
        var children = element.GetArray("m_Children") ?? [];
        Func<int, float, List<Item>> candidates = (type, room) => Candidates(children, context, type, room, scaled);
        var list = new List<Item>();
        var used = 0f;
        Item? Cap(int type)
        {
            var found = candidates(type, Slack(length, used));
            if (found.Count == 0)
                return null;
            var at = found.Count - 1 > 0 ? context.ElementStream().RandomInt(0, found.Count - 1) : 0;
            return found[at];
        }
        var first = Cap(0);
        if (first != null)
        {
            list.Add(first);
            used += first.Length;
        }
        var last = Cap(1);
        if (last != null)
            used += last.Length;
        while (used < length && list.Count <= 999)
        {
            var found = candidates(2, Slack(length, used));
            if (found.Count == 0)
                break;
            var at = found.Count - 1 < 1 ? 0 : context.ElementStream().RandomInt(0, found.Count - 1);
            list.Add(found[at]);
            used += found[at].Length;
        }
        if (last != null)
            list.Add(last);
        return list;
    }

    private static float Slack(float length, float used)
    {
        var s = length * 0.0009765625f;
        if (s <= 0.03125f)
            s = 0.03125f;
        if (1f <= s)
            s = 1f;
        s += length - used;
        return 0f <= s ? s : 0f;
    }

    // 180024ad0: enabled children whose first enabled EndCap matches (start 0,
    // end 1, neither for 2; both flags default true, 180027400), with a length
    // (a filler's above 1.19e-7) whose minimum fits what is left. The length is
    // the first enabled LinearLength's (1800249a0; zero without one), its
    // bounds that length unless the line scales and the item allows it.
    private static List<Item> Candidates(IReadOnlyList<KVObject> children, Context context, int type, float room, bool scaled)
    {
        var result = new List<Item>();
        foreach (var child in children)
        {
            if (child.ContainsKey("m_bEnabled") && !Bool(child["m_bEnabled"], context))
                continue;
            bool isStart = false, isEnd = false, capped = false, measured = false;
            float len = 0f, min = 0f, max = 0f;
            bool Flag(KVObject c, string key) => !c.ContainsKey(key) || Bool(c[key], context);
            float Number(KVObject c, string key) => c.ContainsKey(key) ? Float(c[key], context) : 1f;
            foreach (var c in child.GetArray("m_SelectionCriteria") ?? [])
            {
                if (!Flag(c, "m_bEnabled"))
                    continue;
                switch (c.GetStringProperty("_class"))
                {
                    case "CSmartPropSelectionCriteria_EndCap" when !capped:
                        capped = true;
                        isStart = Flag(c, "m_bStart");
                        isEnd = Flag(c, "m_bEnd");
                        break;
                    case "CSmartPropSelectionCriteria_LinearLength" when !measured:
                        // Defaults (180026af0): length, minimum and maximum 1, no stretching.
                        measured = true;
                        len = Number(c, "m_flLength");
                        (min, max) = scaled && c.ContainsKey("m_bAllowScale") && Bool(c["m_bAllowScale"], context)
                            ? (Number(c, "m_flMinLength"), Number(c, "m_flMaxLength")) : (len, len);
                        if (min != max)
                            throw new NotSupportedException("FitOnLine items that stretch are not ported");
                        break;
                }
            }
            var take = type switch { 0 => isStart, 1 => isEnd, _ => !isStart && !isEnd };
            if (!take)
                continue;
            if ((type == 2 && len < 1.1920929e-07f) || room < min)
                continue;
            result.Add(new Item(child, type, len, min, max));
        }
        return result;
    }

    // 180011a50: scaled by 1 / length when it is sane.
    private static Vector3 Normalise(Vector3 v)
    {
        var l = MathF.Sqrt((v.Y * v.Y) + (v.Z * v.Z) + (v.X * v.X));
        if (l >= 1e-17f && l <= 1e17f)
        {
            var r = 1f / l;
            return new Vector3(r * v.X, r * v.Y, r * v.Z);
        }
        return l == 0f ? Vector3.Zero : throw new NotSupportedException("normalising a denormal vector");
    }

    // An attribute value: a literal, or { m_SourceName } naming a variable.
    private static object Value(KVObject v, Context context)
    {
        if (v.ValueType == KVValueType.Collection && v.ContainsKey("m_SourceName"))
            return context.Variables.GetValueOrDefault(v.GetStringProperty("m_SourceName")) ?? 0f;
        return Literal(v);
    }

    private static object Literal(KVObject v) => v.ValueType switch
    {
        KVValueType.Null => 0f,
        KVValueType.Boolean => (bool)v,
        KVValueType.String => (string)v,
        KVValueType.Array => v.Values.Select(x => (float)(double)x).ToArray(),
        _ => (float)(double)v,
    };

    private static object? Normalise(object? v) => v switch
    {
        string s when bool.TryParse(s, out var b) => b,
        _ => v,
    };

    private static float Float(KVObject v, Context context) => Value(v, context) switch
    {
        float f => f,
        bool b => b ? 1f : 0f,
        _ => 0f,
    };

    private static bool Bool(KVObject v, Context context) => Value(v, context) switch
    {
        bool b => b,
        float f => f != 0f,
        string s => bool.TryParse(s, out var b) && b,
        _ => false,
    };

    private static Vector3 Vector(KVObject v, Context context)
    {
        if (v.ValueType == KVValueType.Collection && v.ContainsKey("m_Components"))
        {
            var c = v.GetArray("m_Components");
            return new Vector3(Float(c[0], context), Float(c[1], context), Float(c[2], context));
        }
        if (Value(v, context) is float[] a && a.Length >= 3)
            return new Vector3(a[0], a[1], a[2]);
        return Vector3.Zero;
    }

    /// <summary>
    /// A smart prop node's transform as the compile takes it (181230360,
    /// 18122d020): the node's world matrix split into column scales and a
    /// rotation (18125b270), the rotation's MatrixQuaternion (18125de90), the
    /// translation, and the node's own scale (read at node+0xb8; only 1 is
    /// measured, so only 1 is taken).
    /// </summary>
    public static CTransform NodeTransform(float[] nodeWorld)
    {
        var m = nodeWorld;
        var sx = MathF.Sqrt((m[0] * m[0]) + (m[4] * m[4]) + (m[8] * m[8]));
        var sy = MathF.Sqrt((m[1] * m[1]) + (m[5] * m[5]) + (m[9] * m[9]));
        var sz = MathF.Sqrt((m[2] * m[2]) + (m[6] * m[6]) + (m[10] * m[10]));
        float rx = sx != 0f ? 1f / sx : 1f, ry = sy != 0f ? 1f / sy : 1f, rz = sz != 0f ? 1f / sz : 1f;
        float[] n = [m[0] * rx, ry * m[1], rz * m[2], m[3], rx * m[4], ry * m[5], rz * m[6], m[7], rx * m[8], ry * m[9], rz * m[10], m[11]];
        return new CTransform(new Vector3(n[3], n[7], n[11]), 1f, MatrixQuaternion(n));
    }

    /// <summary>
    /// resourcecompiler's prop entity for a placement made from the node's
    /// transform: the placement brought back into the node's space by the
    /// inverse (181c51040 after the evaluation), composed with the node's
    /// transform again (181c51410), the angles read back through
    /// QuaternionAngles (181260200), the scales the placement's times the
    /// model's times the node's.
    /// </summary>
    public static (Vector3 Origin, Vector3 Angles, Vector3 Scales) PropPlacement(CTransform node, Placement placement)
    {
        var local = CTransform.Compose(node.Inverse(), placement.Transform);
        var placed = CTransform.Compose(node, new CTransform(local.Position, 1f, local.Rotation));
        var angles = Physics.StaticPropHulls.MatrixAngles(new CTransform(Vector3.Zero, 1f, placed.Rotation).Matrix());
        var s = local.Scale;
        return (placed.Position, angles, new Vector3(s * placement.ModelScale.X * node.Scale, s * placement.ModelScale.Y * node.Scale, s * placement.ModelScale.Z * node.Scale));
    }

    // 18125de90 (MatrixQuaternion).
    internal static Quaternion MatrixQuaternion(float[] m)
    {
        float m0 = m[0], m5 = m[5], m10 = m[10];
        var trace = (m0 + m5) + m10;
        if (0f <= trace)
        {
            var s = MathF.Sqrt(trace + 1f);
            var h = 0.5f / s;
            return new Quaternion((m[9] - m[6]) * h, (m[2] - m[8]) * h, (m[4] - m[1]) * h, s * 0.5f);
        }
        if (m10 > m0 && m10 > m5)
        {
            var s = MathF.Sqrt((m10 - (m0 + m5)) + 1f);
            var h = 0.5f / s;
            return new Quaternion((m[8] + m[2]) * h, (m[9] + m[6]) * h, s * 0.5f, (m[4] - m[1]) * h);
        }
        if (m10 <= m0 && m0 >= m5)
        {
            var s = MathF.Sqrt((m0 - (m10 + m5)) + 1f);
            var h = 0.5f / s;
            return new Quaternion(s * 0.5f, (m[4] + m[1]) * h, (m[8] + m[2]) * h, (m[9] - m[6]) * h);
        }
        {
            var s = MathF.Sqrt((m5 - (m10 + m0)) + 1f);
            var h = 0.5f / s;
            return new Quaternion((m[4] + m[1]) * h, s * 0.5f, (m[9] + m[6]) * h, (m[2] - m[8]) * h);
        }
    }
}
