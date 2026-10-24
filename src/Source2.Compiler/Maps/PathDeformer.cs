using System.Numerics;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Hammer's CMapDeformerPath: a lattice (<see cref="LatticeDeformer"/>, mode
/// 3, one division in y and z) fitted along the CMapPath its targetPath names.
/// Its update (rc 18117a900) runs from each setter and from CopyFrom
/// (181178b00):
/// <list type="number">
/// <item>The lattice back on its grid (181290410).</item>
/// <item>The target: a CMapPath of the same document, found by the
/// reference's targetReferenceID (the path node's referenceID).</item>
/// <item>The path's control points in world space (1810b7ca0, mode 1), as a
/// Bezier polygon: each node, it plus its out tangent, the next node plus its
/// in tangent, and the last node. Each point moved into the deformer's space by
/// the inverse of the lattice's transform (181253780, then the quaternion
/// rotate inline).</item>
/// <item>The fit (18128dc90): the polygon made a curve (181283930) and
/// resampled at 100 points a segment (181282b90), framed from +z (1812820e0),
/// cut to [start, end] of its normalised length (18128f4d0) and refitted as
/// one to sixteen cubics within maxError per cent of the length (181285470,
/// Schneider's least squares with Newton reparameterisation, 1812858b0). The
/// refit curve is sampled again at 100 points a segment, framed from the cut's
/// own normal (181281aa0), and each corner of the 1 x 1 section (centred, half
/// the size each way) fitted segment by segment, which gives the control
/// points and handles.</item>
/// </list>
/// Every float expression keeps the order the binary computes it in.
/// </summary>
internal static partial class PathDeformer
{
    /// <summary>The target path's world control points, attached to the deformer node by <see cref="Resolve"/>.</summary>
    internal const string ControlsKey = "s2c.pathcontrols";

    // The target path's element and whether its first node is repeated (a closed loop of a supports_loop class).
    private const string TargetKey = "s2c.pathtarget";
    private const string WrapsKey = "s2c.pathwraps";

    /// <summary>
    /// What the load leaves of every CMapDeformerPath of the document. Its
    /// targetPath (an embedded CMapNodeReference, its targetReferenceID) names
    /// a node of the same document by referenceID (181072aa0), which must be a
    /// path (vf 0x770 IsA CMapPath; a cable is one). Its target's world control
    /// points are attached to the node for the fit (1810b7ca0: a closed loop
    /// of more than one node repeats its first node when the class has
    /// supports_loop, which is why a closed one needs <paramref name="schema"/>).
    /// Once the load has made the nodes, each reference resolves (180fca9b0)
    /// in tree order and its change (vf 0x740, 181178ce0) recentres the node
    /// (18117aeb0): its origin moves to the path's point at (end + start) 0.5
    /// of its length (<see cref="Centre"/>, through 181077c20), which takes
    /// every node below it along (181076850: the old origin the pivot, the
    /// move the delta, 181076a70), then the lattice is fitted (18117a900) to
    /// the path as it then stands, which the move has carried along when the
    /// path is below the deformer (captured, s2c_deformnest: the load sends no
    /// change of the path back to it). An unresolved reference leaves the node
    /// where it is and the lattice unfitted. The recentre is skipped for a path
    /// whose visibility state (+0x3c8, 1810be450) is set, on it or on a path
    /// node; resourcecompiler only ever zeroes it (the node constructor
    /// 180f8f920 is its one writer; force_hidden goes to +0x366 through
    /// 180f94900), so a hidden path is recentred like any other. Each node
    /// also learns its parent and the path deformers above it, and each
    /// instance's target that it is one, for the copies (<see cref="Place"/>).
    /// </summary>
    internal static void Resolve(DmxBinary.Document document, FgdSchema? schema = null)
    {
        foreach (var e in document.Elements)
            foreach (var child in e.GetElements("children"))
                child.Attributes.TryAdd(ParentKey, e);
        foreach (var instance in document.OfType("CMapInstance"))
            if (instance.Get<DmxBinary.Element>("target") is { } group)
                group.Attributes[TargetGroupKey] = true;
        Dictionary<ulong, DmxBinary.Element>? byReference = null;
        var order = document.OfType("CMapWorld").SelectMany(w => Descendants(w)).Where(e => e.Type == "CMapDeformerPath").ToList();
        order.AddRange(document.Elements.Where(e => e.Type == "CMapDeformerPath").Except(order));
        foreach (var node in order)
        {
            if (node.Attributes.ContainsKey(ControlsKey))
                continue;
            foreach (var below in Descendants(node))
            {
                var above = below.Get<DmxBinary.Element[]>(AboveKey) ?? [];
                if (!above.Contains(node))
                    below.Attributes[AboveKey] = (DmxBinary.Element[])[.. above, node];
            }
            if (node.Get<DmxBinary.Element>("targetPath")?.GetValue<ulong>("targetReferenceID") is not { } id || id == 0)
                continue;
            byReference ??= document.Elements.Where(e => e.GetValue<ulong>("referenceID") is not null)
                                             .GroupBy(e => e.GetValue<ulong>("referenceID")!.Value).ToDictionary(g => g.Key, g => g.First());
            if (!byReference.TryGetValue(id, out var path) || path.Type is not ("CMapPath" or "CMapCable"))
                continue;
            if (MapEntities.ReadNode(path) is not { PathNodes: { } nodes } entity)
                continue;
            if (entity.ClosedLoop && nodes.Count > 1 && schema == null)
                throw new NotSupportedException($"path deformer {node.GetValue<int>("nodeID")}: a closed target path needs the FGD (supports_loop)");
            var wraps = EntityLumpAuthor.Wraps(entity, schema);
            var controls = EntityLumpAuthor.PathControlsWorld(entity, wraps);
            node.Attributes[TargetKey] = path;
            node.Attributes[WrapsKey] = wraps;
            if (Centre(controls, SettingsOf(node)) is { } centre)
            {
                MoveTo(node, centre);
                controls = EntityLumpAuthor.PathControlsWorld(MapEntities.ReadNode(path)!, wraps);
            }
            node.Attributes[ControlsKey] = controls;
        }
    }

    /// <summary>
    /// 18117aeb0's point: the path's own curve (1810c0530: its world control
    /// points as nodes, 1812869e0, and their lengths) at share (end + start)
    /// 0.5 (181282e60: the segment holding it, its parameter by the shares,
    /// the Bernstein sum); null for fewer than two nodes.
    /// </summary>
    internal static Vector3? Centre(float[] controls, Settings settings)
    {
        var count = controls.Length / 9;
        if (count < 2)
            return null;
        var curve = new Curve();
        for (var k = 0; k < count; k++)
            curve.Nodes.Add((new(controls[k * 9], controls[k * 9 + 1], controls[k * 9 + 2]),
                             new(controls[k * 9 + 3], controls[k * 9 + 4], controls[k * 9 + 5]),
                             new(controls[k * 9 + 6], controls[k * 9 + 7], controls[k * 9 + 8])));
        curve.Measure();
        var u = (settings.End + settings.Start) * 0.5f;
        var k0 = curve.SegmentAt(u);
        if (k0 < 0)
            return null;
        float s0 = curve.Shares[k0], s1 = curve.Shares[k0 + 1];
        var t = 0f;
        if (s0 < s1)
        {
            t = (u - s0) / (s1 - s0);
            if (t <= 0f)
                t = 0f;
            if (1f <= t)
                t = 1f;
        }
        return Cubic(curve.Bezier(k0), t);
    }

    // 181077c20 with a new origin: when it differs, the node takes it and every
    // node below it is moved by 181076a70 with the old origin as the pivot and
    // the difference as the move: the pivot's inverse, then the move, then the
    // pivot, composed after its own transform (CTransform_Compose).
    private static void MoveTo(DmxBinary.Element node, Vector3 centre)
    {
        var origin = node.GetValue<Vector3>("origin") ?? Vector3.Zero;
        if (origin.X == centre.X && origin.Y == centre.Y && origin.Z == centre.Z)
            return;
        node.Attributes["origin"] = centre;
        foreach (var child in Descendants(node))
        {
            if (child.GetValue<Vector3>("origin") is not { } at)
                continue;
            var angles = child.GetValue<Vector3>("angles") ?? Vector3.Zero;
            var (o, a) = Moved(at, angles, origin, centre);
            child.Attributes["origin"] = o;
            if (angles != Vector3.Zero)
                child.Attributes["angles"] = a;
        }
    }

    // 181076a70 for one node: pivot o move o pivot^-1 o node, its angles from the quaternion through the setter (an
    // unturned node's are left as they are, as the port has always kept them).
    private static (Vector3 Origin, Vector3 Angles) Moved(Vector3 at, Vector3 angles, Vector3 from, Vector3 to)
    {
        var pivot = new CTransform(from, 1f, Quaternion.Identity);
        var move = new CTransform(new Vector3(to.X - from.X, to.Y - from.Y, to.Z - from.Z), 1f, Quaternion.Identity);
        var own = new CTransform(at, 1f, CTransform.AngleQuaternion(SettleWorld.SetAngles(angles)));
        var moved = CTransform.Compose(pivot, CTransform.Compose(move, CTransform.Compose(pivot.Inverse(), own)));
        if (angles == Vector3.Zero)
            return (moved.Position, angles);
        var q = moved.Rotation;
        var a = Simulation.SettleWriteBack.CleanAngles(Simulation.SettleWriteBack.Angles(new Simulation.Quat(q.X, q.Y, q.Z, q.W)));
        return (moved.Position, new Vector3(a.X, a.Y, a.Z));
    }

    private static IEnumerable<DmxBinary.Element> Descendants(DmxBinary.Element node)
    {
        foreach (var child in node.GetElements("children"))
        {
            yield return child;
            foreach (var below in Descendants(child))
                yield return below;
        }
    }

    /// <summary>
    /// The lattice's transform (181020fa0 with CMapDeformerPath's pivot mode
    /// 1, set by its vf10 through 181022b50): the node's angles as the load's
    /// angle setter leaves them (180fdeda0, <see cref="SettleWorld.SetAngles"/>:
    /// a yaw of -20 is held as 340, which turns the quaternion), its origin
    /// less half the size turned by them (t = 2 (q x h), then (q x t) + (w t +
    /// h)), so the section is centred on the curve.
    /// </summary>
    internal static CTransform LatticeTransform(Vector3 origin, Vector3 angles, Vector3 size)
    {
        var q = CTransform.AngleQuaternion(angles);
        var h = new Vector3(size.X * 0.5f, size.Y * 0.5f, size.Z * 0.5f);
        var tx = (h.Z * q.Y) - (h.Y * q.Z);
        var ty = (h.X * q.Z) - (h.Z * q.X);
        var tz = (h.Y * q.X) - (h.X * q.Y);
        tx += tx;
        ty += ty;
        tz += tz;
        var r = new Vector3(((tz * q.Y) - (ty * q.Z)) + ((q.W * tx) + h.X),
                            ((tx * q.Z) - (tz * q.X)) + ((q.W * ty) + h.Y),
                            ((ty * q.X) - (tx * q.Y)) + ((q.W * tz) + h.Z));
        return new CTransform(new Vector3(origin.X - r.X, origin.Y - r.Y, origin.Z - r.Z), 1f, q);
    }

    /// <summary>The settings CMapDeformerPath's factory (rc 181178e70) starts with: start 0, end 1, maxError 0.5.</summary>
    internal readonly record struct Settings(float Start, float End, float MaxError);

    /// <summary>
    /// The settings a load leaves: the start setter (18117a8b0) clamps to
    /// [0, end - 0.01], the end setter (18117a820) to [start + 0.01, 1], each
    /// against the other's value at the time (maxss, then minss). The load
    /// (180f77eba: 180f84b70 in mode 1) walks the class's embedded properties
    /// in the order vf 0xe8 (181178650) registers them, CMapNode's, then
    /// CMapDeformer's, then targetPath, start, end, maxError (181179840),
    /// whatever order the file stores them in: each found by name, set
    /// through its setter and removed. So start is clamped against the
    /// factory's end of 1, and end against that start.
    /// </summary>
    internal static Settings SettingsOf(DmxBinary.Element node)
    {
        var start = 0f;
        var end = 1f;
        if (node.GetValue<float>("start") is { } a)
            start = MinSs(MaxSs(a, 0f), end - 0.01f);
        if (node.GetValue<float>("end") is { } b)
            end = MinSs(MaxSs(b, start + 0.01f), 1f);
        return new(start, end, node.GetValue<float>("maxError") ?? 0.5f);
    }

    // maxss and minss: the second operand when the comparison fails (NaN included).
    private static float MaxSs(float a, float b) => a > b ? a : b;

    private static float MinSs(float a, float b) => a < b ? a : b;

    /// <summary>
    /// The Bezier polygon of the path's world control points (nine floats a
    /// node, <see cref="EntityLumpAuthor.PathControlsWorld"/>) in the space of
    /// <paramref name="transform"/>; none for fewer than two nodes.
    /// </summary>
    internal static Vector3[] Polygon(float[] controls, CTransform transform)
    {
        var nodes = controls.Length / 9;
        if (nodes < 2)
            return [];
        Vector3 At(int k, int o) => new(controls[k * 9 + o], controls[k * 9 + o + 1], controls[k * 9 + o + 2]);
        var polygon = new List<Vector3>();
        for (var k = 0; k < nodes - 1; k++)
        {
            Vector3 p = At(k, 0), outward = At(k, 6), next = At(k + 1, 0), inward = At(k + 1, 3);
            polygon.Add(p);
            polygon.Add(new(outward.X + p.X, outward.Y + p.Y, outward.Z + p.Z));
            polygon.Add(new(inward.X + next.X, inward.Y + next.Y, inward.Z + next.Z));
        }
        polygon.Add(At(nodes - 1, 0));
        var inverse = transform.Inverse();
        return [.. polygon.Select(p => Move(inverse, p))];
    }

    // 18117a900's loop, packed: t = 2 (q x p), then ((q x t) + (w t + p)) times the scale plus the position.
    private static Vector3 Move(CTransform m, Vector3 p)
    {
        var q = m.Rotation;
        var tx = (p.Z * q.Y) - (p.Y * q.Z);
        var ty = (p.X * q.Z) - (p.Z * q.X);
        var tz = (p.Y * q.X) - (p.X * q.Y);
        tx += tx;
        ty += ty;
        tz += tz;
        return new Vector3(
            ((((tz * q.Y) - (ty * q.Z)) + ((q.W * tx) + p.X)) * m.Scale) + m.Position.X,
            ((((tx * q.Z) - (tz * q.X)) + ((q.W * ty) + p.Y)) * m.Scale) + m.Position.Y,
            ((((ty * q.X) - (tx * q.Y)) + ((q.W * tz) + p.Z)) * m.Scale) + m.Position.Z);
    }

    /// <summary>
    /// A curve of nodes, each a position and its in and out tangents (0x24
    /// bytes), with each node's share of the curve's length (+0x20) and the
    /// last segment a lookup found (+0x4c).
    /// </summary>
    internal sealed class Curve
    {
        public readonly List<(Vector3 Position, Vector3 In, Vector3 Out)> Nodes = [];
        public float[] Lengths = [];
        public float[] Shares = [];
        public float Total;
        private int _cache;

        public int Segments => Math.Max(Nodes.Count - 1, 0);

        /// <summary>
        /// 181283930 in mode 2: node k at polygon point 3k, its in tangent to
        /// point 3k - 1 (none for the first), its out tangent to point 3k + 1
        /// (none for the last), then the lengths.
        /// </summary>
        public static Curve FromPolygon(Vector3[] polygon)
        {
            var curve = new Curve();
            var n = polygon.Length;
            if (n < 2)
                return curve;
            var last = n - 1;
            var count = last / 3 + 1;
            for (var k = 0; k < count; k++)
            {
                var at = 3 * k + 1;
                var p = polygon[3 * k];
                var before = polygon[at - 2 < 0 ? 0 : at - 2];
                var after = polygon[at < last ? at : last];
                curve.Nodes.Add((p, new(before.X - p.X, before.Y - p.Y, before.Z - p.Z), new(after.X - p.X, after.Y - p.Y, after.Z - p.Z)));
            }
            curve.Measure();
            return curve;
        }

        /// <summary>181283870: segment k's Bezier, P1 its out tangent plus the node, P2 the next in tangent plus the next node.</summary>
        public Vector3[] Bezier(int k)
        {
            var (p, _, o) = Nodes[k];
            var (q, i, _) = Nodes[k + 1];
            return [p, new(o.X + p.X, o.Y + p.Y, o.Z + p.Z), new(i.X + q.X, i.Y + q.Y, i.Z + q.Z), q];
        }

        /// <summary>
        /// 181285b50: each segment's length (18110fc80 at tolerance 1), summed;
        /// each node's running sum divided by the total when that is above 0.
        /// </summary>
        public void Measure()
        {
            var count = Nodes.Count;
            Shares = new float[count];
            Lengths = new float[Math.Max(count - 1, 0)];
            Total = 0f;
            for (var k = 0; k < count - 1; k++)
            {
                var length = CableCollision.Length(Bezier(k), 1f);
                Lengths[k] = length;
                var sum = length + Total;
                Total = sum;
                Shares[k + 1] = sum;
            }
            if (0f < Total)
                for (var i = 0; i < count; i++)
                    Shares[i] /= Total;
        }

        /// <summary>
        /// 181285360: the segment holding share <paramref name="u"/> (clamped
        /// to the ends), the cached segment first, then a scan (under 64
        /// segments) or a bisection; -1 for a curve of one node.
        /// </summary>
        public int SegmentAt(float u)
        {
            var last = Math.Max(Nodes.Count - 1, 0);
            if (last == 0)
                return -1;
            var s = Shares;
            if (u < s[0] || s[last] < u)
            {
                if (u <= s[0])
                    u = s[0];
                if (s[last] <= u)
                    u = s[last];
            }
            var cached = _cache;
            int lo = 0, hi = last;
            if (cached < last && s[cached] <= u)
            {
                lo = cached + 1;
                if (u <= s[cached + 1])
                {
                    if (last - 1 <= cached || u != s[cached + 1])
                        return cached;
                    _cache = lo;
                    return lo;
                }
            }
            else if (cached < last)
            {
                hi = cached;
            }
            if (hi - lo < 0x40)
            {
                for (var k = lo; k < hi; k++, lo++)
                    if (u < s[k + 1])
                        break;
                if (hi - 1 <= lo)
                    lo = hi - 1;
                _cache = lo;
                return lo;
            }
            while (lo + 1 < hi)
            {
                var d = hi - lo;
                if (d < 0)
                    d++;
                var mid = (d >> 1) + lo;
                if (u < s[mid])
                    hi = mid;
                else
                    lo = mid;
            }
            _cache = lo;
            return lo;
        }
    }

    /// <summary>
    /// 181282b90 (1812802d0 in mode 2): each segment sampled at 100 summed
    /// parameters (1810bb3f0), the later segments dropping their first sample;
    /// each sample's share of the length, its segment's shares lerped by its
    /// parameter and clamped to [0, 1].
    /// </summary>
    internal static (Vector3[] Points, float[] Shares) Resample(Curve curve)
    {
        var nodes = curve.Nodes.Count;
        if (nodes < 2)
            return ([], []);
        var points = new List<Vector3>();
        var local = new List<float>();
        var starts = new int[nodes];
        var count = Math.Max((int)MathF.Round(100f, MidpointRounding.AwayFromZero), 2);
        for (var k = 0; k < nodes - 1; k++)
        {
            if (k != 0)
            {
                points.RemoveAt(points.Count - 1);
                local.RemoveAt(local.Count - 1);
            }
            starts[k] = points.Count;
            var bezier = curve.Bezier(k);
            var coefficients = CableCollision.Coefficients(bezier);
            var delta = 1f / (count - 1);
            var t = 0f;
            for (var i = 0; i < count; i++)
            {
                points.Add(CableCollision.Point(coefficients, bezier[0], t));
                local.Add(t);
                t += delta;
            }
        }
        starts[^1] = points.Count - 1;
        var shares = new float[points.Count];
        for (var k = 0; k < nodes - 1; k++)
        {
            var s0 = k < curve.Shares.Length ? curve.Shares[k] : 0f;
            var s1 = k + 1 < curve.Shares.Length ? curve.Shares[k + 1] : 0f;
            for (var i = starts[k]; i <= starts[k + 1]; i++)
            {
                var u = (s1 - s0) * local[i] + s0;
                if (u <= 0f)
                    u = 0f;
                if (1f <= u)
                    u = 1f;
                shares[i] = u;
            }
        }
        return ([.. points], shares);
    }

    /// <summary>
    /// 18128f4d0: the path's polygon resampled and cut to [start, end] of its
    /// length, the cut ends lerped in; refitted (<see cref="Fit"/>) with twice
    /// the segments the cut spans (2 to 16), from the cut's tangent at the
    /// start to its reversed tangent at the end. Also the frame's normal at the
    /// start.
    /// </summary>
    internal static (Curve Curve, Vector3 Normal) Cut(Vector3[] polygon, Settings settings)
    {
        var path = Curve.FromPolygon(polygon);
        var (points, shares) = Resample(path);
        var n = points.Length;
        var (normals, tangents) = n > 1 ? CableCollision.Frames(points) : ([], []);
        var start = settings.Start;
        var end = settings.End;
        var kept = new List<Vector3>();
        Vector3 startTangent = Vector3.Zero, normal = Vector3.Zero, endTangent = Vector3.Zero;
        for (var i = 0; i < n - 1; i++)
        {
            if (!(start <= shares[i + 1]))
                continue;
            if (shares[i] <= start && start < shares[i + 1])
            {
                var f = (start - shares[i]) / (shares[i + 1] - shares[i]);
                var g = 1f - f;
                Vector3 t0 = tangents[i], t1 = tangents[i + 1];
                startTangent = CableCollision.Normalise(g * t0.X + f * t1.X, g * t0.Y + f * t1.Y, g * t0.Z + f * t1.Z);
                Vector3 n0 = normals[i], n1 = normals[i + 1];
                var lerped = new Vector3(g * n0.X + f * n1.X, g * n0.Y + f * n1.Y, g * n0.Z + f * n1.Z);
                LightMath.Normalize(ref lerped);
                normal = lerped;
                kept.Add(Lerp(points[i], points[i + 1], g, f));
            }
            if (shares[i] <= end && end <= shares[i + 1])
            {
                var f = (end - shares[i]) / (shares[i + 1] - shares[i]);
                var g = 1f - f;
                Vector3 t0 = tangents[i], t1 = tangents[i + 1];
                var lerped = new Vector3(g * t0.X + f * t1.X, g * t0.Y + f * t1.Y, g * t0.Z + f * t1.Z);
                LightMath.Normalize(ref lerped);
                endTangent = lerped;
                kept.Add(Lerp(points[i], points[i + 1], g, f));
                break;
            }
            kept.Add(points[i + 1]);
        }
        var first = path.SegmentAt(start);
        var lastSegment = path.SegmentAt(end);
        var segments = (1 - first) + lastSegment;
        segments = segments < 1 ? 2 : Math.Min(segments * 2, 16);
        var curve = Fit([.. kept], startTangent, new Vector3(-endTangent.X, -endTangent.Y, -endTangent.Z), segments, settings.MaxError);
        return (curve, normal);
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float g, float f) => new(g * a.X + f * b.X, g * a.Y + f * b.Y, g * a.Z + f * b.Z);

    /// <summary>
    /// 181285470: the points split into 1, 2 ... <paramref name="maxSegments"/>
    /// runs (run j ends at the rounded j (n - 1) / runs, past the one before),
    /// each fitted as a cubic (<see cref="FitOne"/>) from the given end
    /// tangents or the points' own (1812835e0), until the worst run is within
    /// <paramref name="maxError"/>; the last split's cubics as a curve.
    /// </summary>
    internal static Curve Fit(Vector3[] points, Vector3 startTangent, Vector3 endTangent, int maxSegments, float maxError)
    {
        var curve = new Curve();
        var count = points.Length;
        if (count <= 1)
            return curve;
        var beziers = new List<Vector3[]>();
        if (0 < maxSegments)
        {
            var last = count - 1;
            var runs = 1;
            float worst;
            do
            {
                beziers.Clear();
                var from = 0;
                worst = 0f;
                for (var j = 1; j <= runs; j++)
                {
                    var rounded = (int)MathF.Round(j * ((float)last / runs), MidpointRounding.AwayFromZero);
                    var to = rounded <= from + 1 ? from + 1 : rounded;
                    if (!(to < last))
                        to = last;
                    var t0 = from == 0 ? startTangent : Central(points, from);
                    Vector3 t1;
                    if (to == last)
                        t1 = endTangent;
                    else
                    {
                        var c = Central(points, to);
                        t1 = new Vector3(-c.X, -c.Y, -c.Z);
                    }
                    var (bezier, error) = FitOne(points, from, to, t0, t1);
                    beziers.Add(bezier);
                    if (worst <= error)
                        worst = error;
                    from = to;
                }
            }
            while (maxError < worst && ++runs <= maxSegments);
        }
        foreach (var b in beziers)
        {
            var o = new Vector3(b[1].X - b[0].X, b[1].Y - b[0].Y, b[1].Z - b[0].Z);
            if (curve.Nodes.Count == 0)
                curve.Nodes.Add((b[0], Vector3.Zero, o));
            else
                curve.Nodes[^1] = (b[0], curve.Nodes[^1].In, o);
            curve.Nodes.Add((b[3], new Vector3(b[2].X - b[3].X, b[2].Y - b[3].Y, b[2].Z - b[3].Z), Vector3.Zero));
        }
        curve.Measure();
        return curve;
    }

    /// <summary>1812835e0: the two chords at point i normalised, summed and normalised again.</summary>
    private static Vector3 Central(Vector3[] p, int i)
    {
        var a = CableCollision.Normalise(p[i].X - p[i - 1].X, p[i].Y - p[i - 1].Y, p[i].Z - p[i - 1].Z);
        var b = CableCollision.Normalise(p[i + 1].X - p[i].X, p[i + 1].Y - p[i].Y, p[i + 1].Z - p[i].Z);
        var sum = new Vector3(b.X + a.X, b.Y + a.Y, b.Z + a.Z);
        LightMath.Normalize(ref sum);
        return sum;
    }

    /// <summary>
    /// 1812858b0: points <paramref name="from"/> to <paramref name="to"/> as
    /// one cubic from the end tangents: two points give the chord's thirds;
    /// otherwise chord-length parameters (181280a60), then up to ten rounds of
    /// a least-squares fit (181280e20) and its worst squared distance
    /// (181281360), each round after the first reparameterised (181285e60),
    /// while the distance is not under (length times 0.001) squared. The
    /// error is the distance over the length, clamped to [0, 1], times 100.
    /// </summary>
    internal static (Vector3[] Bezier, float Error) FitOne(Vector3[] points, int from, int to, Vector3 t0, Vector3 t1)
    {
        var n = to - from + 1;
        var p = new ReadOnlySpan<Vector3>(points, from, n);
        if (n == 2)
        {
            Vector3 a = p[0], b = p[1];
            return ([a,
                     new((b.X - a.X) * 0.33333334f + a.X, (b.Y - a.Y) * 0.33333334f + a.Y, (b.Z - a.Z) * 0.33333334f + a.Z),
                     new((b.X - a.X) * 0.6666667f + a.X, (b.Y - a.Y) * 0.6666667f + a.Y, (b.Z - a.Z) * 0.6666667f + a.Z),
                     b], 0f);
        }
        var u = new float[n];
        var length = Chords(p, u);
        Vector3[] bezier = [];
        float worst;
        var round = 0;
        var tolerance = length * 0.001f;
        tolerance *= tolerance;
        do
        {
            if (0 < round)
                Reparameterise(bezier, p, u);
            bezier = LeastSquares(p, u, t0, t1);
            worst = Worst(bezier, p, u);
        }
        while (tolerance <= worst && ++round < 10);
        var distance = MathF.Sqrt(worst);
        var error = distance / length;
        if (distance / length <= 0f)
            error = 0f;
        if (1f <= error)
            error = 1f;
        return (bezier, error * 100f);
    }

    /// <summary>181280a60: each point's running chord length over the total; the total.</summary>
    internal static float Chords(ReadOnlySpan<Vector3> p, float[] u)
    {
        var sum = 0f;
        u[0] = 0f;
        for (var i = 1; i < p.Length; i++)
        {
            float dx = p[i - 1].X - p[i].X, dy = p[i - 1].Y - p[i].Y, dz = p[i - 1].Z - p[i].Z;
            sum += MathF.Sqrt(dy * dy + dz * dz + dx * dx);
            u[i] = sum;
        }
        for (var i = 0; i < p.Length; i++)
            u[i] /= sum;
        return sum;
    }

    // The cubic's power basis as the fitter writes it: a = ((P1 3 - P0 1) + P2 -3) + P3,
    // b = (P0 3 + P1 -6) + P2 3, c = P1 3 - P0 3.
    private static (Vector3 A, Vector3 B, Vector3 C) Basis(Vector3[] b)
    {
        static float A(float p0, float p1, float p2, float p3) => ((p1 * 3f - p0 * 1f) + p2 * -3f) + p3;
        static float B(float p0, float p1, float p2) => (p0 * 3f + p1 * -6f) + p2 * 3f;
        static float C(float p0, float p1) => p1 * 3f - p0 * 3f;
        return (new(A(b[0].X, b[1].X, b[2].X, b[3].X), A(b[0].Y, b[1].Y, b[2].Y, b[3].Y), A(b[0].Z, b[1].Z, b[2].Z, b[3].Z)),
                new(B(b[0].X, b[1].X, b[2].X), B(b[0].Y, b[1].Y, b[2].Y), B(b[0].Z, b[1].Z, b[2].Z)),
                new(C(b[0].X, b[1].X), C(b[0].Y, b[1].Y), C(b[0].Z, b[1].Z)));
    }

    private static float Poly(float a, float b, float c, float d, float t) => ((a * t * t * t + b * t * t) + c * t) + d;

    private static Vector3 At((Vector3 A, Vector3 B, Vector3 C) k, Vector3 p0, float t)
        => new(Poly(k.A.X, k.B.X, k.C.X, p0.X, t), Poly(k.A.Y, k.B.Y, k.C.Y, p0.Y, t), Poly(k.A.Z, k.B.Z, k.C.Z, p0.Z, t));

    /// <summary>181281360: the largest squared distance from a point to the cubic at its parameter, summed y, x, z.</summary>
    internal static float Worst(Vector3[] bezier, ReadOnlySpan<Vector3> p, float[] u)
    {
        var k = Basis(bezier);
        var worst = 0f;
        for (var i = 0; i < p.Length; i++)
        {
            var q = At(k, bezier[0], u[i]);
            float dx = p[i].X - q.X, dy = p[i].Y - q.Y, dz = p[i].Z - q.Z;
            var d = dy * dy + dx * dx + dz * dz;
            worst = d <= worst ? worst : d;
        }
        return worst;
    }

    /// <summary>
    /// 181280e20: the cubic from the first to the last point whose inner
    /// points lie along the end tangents, at the distances that best fit the
    /// points at their parameters; a third of the chord each when either
    /// distance is under the chord times FLT_EPSILON.
    /// </summary>
    internal static Vector3[] LeastSquares(ReadOnlySpan<Vector3> p, float[] u, Vector3 t0, Vector3 t1)
    {
        Vector3 first = p[0], last = p[^1];
        float c00 = 0f, c01 = 0f, c11 = 0f, x0 = 0f, x1 = 0f;
        for (var i = 0; i < p.Length; i++)
        {
            var t = u[i];
            var s = 1f - t;
            var b1 = t * 3f * s * s;
            var b0 = s * s * s;
            var b2 = t * 3f * t * s;
            var b3 = t * t * t;
            float ay = t0.Y * b1, az = t0.Z * b1, ax = t0.X * b1;
            float by = t1.Y * b2, bz = t1.Z * b2, bx = t1.X * b2;
            c00 += az * az + ay * ay + ax * ax;
            c01 += bz * az + by * ay + bx * ax;
            c11 += bz * bz + by * by + bx * bx;
            var dx = p[i].X - (first.X * b1 + first.X * b0 + last.X * b2 + last.X * b3);
            var dy = p[i].Y - (first.Y * b1 + first.Y * b0 + last.Y * b2 + last.Y * b3);
            var dz = p[i].Z - (first.Z * b1 + first.Z * b0 + last.Z * b2 + last.Z * b3);
            x1 += dz * bz + dy * by + bx * dx;
            x0 += dz * az + dy * ay + ax * dx;
        }
        var det = c11 * c00 - c01 * c01;
        var alpha0 = det != 0f ? (x0 * c11 - x1 * c01) / det : 0f;
        var alpha1 = det != 0f ? (x1 * c00 - x0 * c01) / det : 0f;
        float ex = first.X - last.X, ey = first.Y - last.Y, ez = first.Z - last.Z;
        var chord = MathF.Sqrt(ez * ez + ey * ey + ex * ex);
        if (alpha0 < chord * 1.1920929e-07f || alpha1 < chord * 1.1920929e-07f)
        {
            alpha0 = chord / 3f;
            alpha1 = alpha0;
        }
        return [first,
                new(alpha0 * t0.X + first.X, alpha0 * t0.Y + first.Y, alpha0 * t0.Z + first.Z),
                new(alpha1 * t1.X + last.X, alpha1 * t1.Y + last.Y, alpha1 * t1.Z + last.Z),
                last];
    }

    /// <summary>
    /// 181285e60: each parameter moved by a Newton step on the distance to its
    /// point, clamped to [0, 1]; a step that lands farther is undone, and when
    /// the distance is above 1e-4 a line search of up to ten steps follows, a
    /// tenth of the move from the previous point's parameter (at least 0.001)
    /// against the slope's sign.
    /// </summary>
    internal static void Reparameterise(Vector3[] b, ReadOnlySpan<Vector3> p, float[] u)
    {
        var k = Basis(b);
        var d0 = new Vector3((b[1].X - b[0].X) * 3f, (b[1].Y - b[0].Y) * 3f, (b[1].Z - b[0].Z) * 3f);
        var d1 = new Vector3((b[2].X - b[1].X) * 3f, (b[2].Y - b[1].Y) * 3f, (b[2].Z - b[1].Z) * 3f);
        var d2 = new Vector3((b[3].X - b[2].X) * 3f, (b[3].Y - b[2].Y) * 3f, (b[3].Z - b[2].Z) * 3f);
        var e0 = new Vector3((d1.X - d0.X) + (d1.X - d0.X), (d1.Y - d0.Y) + (d1.Y - d0.Y), (d1.Z - d0.Z) + (d1.Z - d0.Z));
        var e1 = new Vector3(d2.X - d1.X, d2.Y - d1.Y, d2.Z - d1.Z);
        static float D(float a, float b, float c, float t) => ((a - b * 2f) + c) * t * t + ((b + b) - a * 2f) * t + a;
        static float E(float a, float b, float t) => ((b + b) - a) * t + a;
        float Distance(Vector3 point, float t)
        {
            var q = At(k, b[0], t);
            float x = point.X - q.X, y = point.Y - q.Y, z = point.Z - q.Z;
            return x * x + y * y + z * z;
        }
        var previous = 0f;
        for (var i = 0; i < p.Length; i++)
        {
            var t = u[i];
            var q = At(k, b[0], t);
            float qx = D(d0.X, d1.X, d2.X, t), qy = D(d0.Y, d1.Y, d2.Y, t), qz = D(d0.Z, d1.Z, d2.Z, t);
            float fx = q.X - p[i].X, fy = q.Y - p[i].Y, fz = q.Z - p[i].Z;
            var slope = fz * qz + fy * qy + fx * qx;
            var curvature = (E(e0.Y, e1.Y, t) * fy + E(e0.Z, e1.Z, t) * fz + E(e0.X, e1.X, t) * fx) + (qz * qz + qy * qy + qx * qx);
            var moved = curvature != 0f ? t - slope / curvature : t;
            var clamped = moved <= 1f ? moved : 1f;
            var next = 0f <= clamped ? clamped : 0f;
            float ox = p[i].X - q.X, oy = p[i].Y - q.Y, oz = p[i].Z - q.Z;
            var before = oy * oy + ox * ox + oz * oz;
            var after = At(k, b[0], next);
            float ax = p[i].X - after.X, ay = p[i].Y - after.Y, az = p[i].Z - after.Z;
            if (before < ay * ay + ax * ax + az * az)
            {
                next = t;
                if (0.0001f < before)
                {
                    var step = t - previous;
                    if (step <= 0.001f)
                        step = 0.001f;
                    step /= 10f;
                    if (0f <= slope)
                        step = -step;
                    var best = before;
                    for (var round = 0; ;)
                    {
                        var tried = step + next <= 1f ? step + next : 1f;
                        var candidate = 0f <= tried ? tried : 0f;
                        var distance = Distance(p[i], candidate);
                        if (!(distance < best))
                            break;
                        best = distance;
                        next = candidate;
                        if (++round >= 10)
                            break;
                    }
                }
            }
            u[i] = next;
            previous = t;
        }
    }

    // 18127fdc0, association from the disassembly (as LatticeDeformer's cell blend).
    private static Vector3 Cubic(Vector3[] b, float t)
    {
        var g = 1f - t;
        var gg = g * g;
        var g3 = gg * g;
        var w1 = gg * 3f * t;
        var tt = t * t;
        var w2 = g * 3f * tt;
        var t3 = tt * t;
        return new Vector3(w1 * b[1].X + g3 * b[0].X + w2 * b[2].X + t3 * b[3].X,
                           w1 * b[1].Y + g3 * b[0].Y + w2 * b[2].Y + t3 * b[3].Y,
                           w1 * b[1].Z + g3 * b[0].Z + w2 * b[2].Z + t3 * b[3].Z);
    }

    // 18128dc90's tangent: 3 a t t + (b + b) t + c of the power basis, normalised (18013e340).
    private static Vector3 Tangent(Vector3[] b, float t)
    {
        static float A(float p0, float p1, float p2, float p3) => ((p1 * 3f - p0 * 1f) - p2 * 3f) + p3;
        static float B(float p0, float p1, float p2) => (p0 * 3f - p1 * 6f) + p2 * 3f;
        static float D(float a, float bb, float p0, float p1, float t) => a * 3f * t * t + (bb + bb) * t + (p1 * 3f - p0 * 3f);
        var v = new Vector3(
            D(A(b[0].X, b[1].X, b[2].X, b[3].X), B(b[0].X, b[1].X, b[2].X), b[0].X, b[1].X, t),
            D(A(b[0].Y, b[1].Y, b[2].Y, b[3].Y), B(b[0].Y, b[1].Y, b[2].Y), b[0].Y, b[1].Y, t),
            D(A(b[0].Z, b[1].Z, b[2].Z, b[3].Z), B(b[0].Z, b[1].Z, b[2].Z), b[0].Z, b[1].Z, t));
        LightMath.Normalize(ref v);
        return v;
    }

    /// <summary>
    /// 181281aa0: a normal per sample along the given tangents, the first the
    /// given one normalised with the tangent taken out (VectorPerpendicular
    /// when that leaves under 2^-23), each later one the previous turned by the
    /// angle between the two tangents (unless the cosine is past 0.999 either
    /// way) and squared up against its tangent.
    /// </summary>
    internal static Vector3[] Frames(Vector3 up, Vector3[] tangents)
    {
        var n = tangents.Length;
        var normals = new Vector3[n];
        if (n < 2)
            return normals;
        for (var i = 0; i < n; i++)
        {
            var t = tangents[i];
            if (i < 1)
            {
                var u = up;
                LightMath.Normalize(ref u);
                var d = u.Z * t.Z + u.Y * t.Y + u.X * t.X;
                float vy = u.Y - t.Y * d, vx = u.X - t.X * d, vz = u.Z - t.Z * d;
                var length = MathF.Sqrt(vy * vy + vz * vz + vx * vx);
                if (1.1920929e-07f <= length)
                {
                    var inverse = 1f / length;
                    normals[0] = new Vector3(vx * inverse, vy * inverse, vz * inverse);
                }
                else
                    normals[0] = CableCollision.Perpendicular(t);
                continue;
            }
            var before = tangents[i - 1];
            var normal = normals[i - 1];
            var cosine = before.Y * t.Y + before.Z * t.Z + before.X * t.X;
            var c = -1f;
            if (-1f <= cosine)
                c = cosine <= 1f ? cosine : 1f;
            if (MathF.Abs(c) <= 0.999f)
            {
                var axis = CableCollision.Normalise(before.Y * t.Z - before.Z * t.Y, before.Z * t.X - before.X * t.Z, before.X * t.Y - before.Y * t.X);
                var degrees = (float)(Math.Acos(c) * 57.29577951308232);
                normal = CableCollision.Rotate(CableCollision.AxisAngle(axis, degrees), normal);
            }
            var side = CableCollision.Normalise(t.Z * normal.Y - t.Y * normal.Z, normal.Z * t.X - normal.X * t.Z, normal.X * t.Y - normal.Y * t.X);
            var squared = new Vector3(t.Y * side.Z - t.Z * side.Y, side.X * t.Z - side.Z * t.X, side.Y * t.X - side.X * t.Y);
            LightMath.Normalize(ref squared);
            normals[i] = squared;
        }
        return normals;
    }

    // DAT_1823f8570: the section's corners, cell order (y then z).
    private static readonly (float Y, float Z)[] Corners = [(-0.5f, -0.5f), (0.5f, -0.5f), (-0.5f, 0.5f), (0.5f, 0.5f)];

    /// <summary>
    /// 18128dc90: the lattice in mode 3 (handles reset when it was not),
    /// fitted along <paramref name="polygon"/> as <see cref="Cut"/> gives it:
    /// the cut's segments each sampled at i / 99 (18127fdc0), tangents from the
    /// derivative, normals from the cut's (<see cref="Frames"/>); one lattice
    /// segment per curve segment (at least one), one division in y and z, back
    /// on the grid; then each corner of the section, (N x T) size.y y +
    /// N size.z z off the curve, fitted segment by segment from the tangent at
    /// its start to the reversed one at its end, its cubic's ends the control
    /// points and its inner points the handles.
    /// </summary>
    internal static void FitLattice(LatticeDeformer lattice, Vector3[] polygon, Settings settings)
    {
        if (lattice.Mode != 3)
            lattice.SetMode(3);
        var (curve, up) = Cut(polygon, settings);
        var segments = curve.Segments;
        var count = segments * 99 + 1;
        var points = new Vector3[count];
        var tangents = new Vector3[count];
        for (var k = 0; k < segments; k++)
        {
            var bezier = curve.Bezier(k);
            for (var i = 0; i < 100; i++)
            {
                var t = i / 99f;
                points[k * 99 + i] = Cubic(bezier, t);
                tangents[k * 99 + i] = Tangent(bezier, t);
            }
        }
        var normals = Frames(up, tangents);
        lattice.Regrid(Math.Max(segments, 1), 1, 1);
        var size = lattice.Size;
        var corner = new Vector3[count];
        for (var c = 0; c < 4; c++)
        {
            var (cy, cz) = Corners[c];
            for (var i = 0; i < count; i++)
            {
                Vector3 n = normals[i], t = tangents[i], p = points[i];
                corner[i] = new Vector3(
                    (n.Y * t.Z - n.Z * t.Y) * size.Y * cy + n.X * size.Z * cz + p.X,
                    (n.Z * t.X - t.Z * n.X) * size.Y * cy + n.Y * size.Z * cz + p.Y,
                    (t.Y * n.X - n.Y * t.X) * size.Y * cy + n.Z * size.Z * cz + p.Z);
            }
            for (var k = 0; k < segments; k++)
            {
                int from = k * 99, to = k * 99 + 99;
                var end = tangents[to];
                var (b, _) = FitOne(corner, from, to, tangents[from], new Vector3(-end.X, -end.Y, -end.Z));
                var at = 4 * k + c;
                lattice.Points[at] = b[0];
                lattice.Handles[at * 2] = b[1];
                lattice.Handles[at * 2 + 1] = b[2];
                lattice.Points[4 * (k + 1) + c] = b[3];
            }
        }
    }
}
