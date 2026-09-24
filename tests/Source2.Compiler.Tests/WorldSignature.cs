using System.Text;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Tests;

/// <summary>
/// A world's step bookkeeping as text, one fact per line, with every object
/// named by something both a live read and a ported world agree on: bodies by
/// index, contacts by key, islands by their index in the manager. Two worlds
/// match when their lines do; the first differing line says where.
/// </summary>
internal static class WorldSignature
{
    public static List<string> Lines(RnWorld w)
    {
        var lines = new List<string>
        {
            $"flags {w.StepFlags:x} step {w.StepCount}",
            $"active bodies {string.Join(' ', w.ActiveBodies.Select(B))}",
            $"active contacts {string.Join(' ', w.ActiveContacts.Select(C))}",
            $"solid contacts {string.Join(' ', w.AllContacts[0].Select(C))}",
            $"sensor contacts {string.Join(' ', w.AllContacts[1].Select(C))}",
            $"destroyed {string.Join(' ', w.Destroyed.Select(C))}",
            $"continuous {string.Join(' ', w.ContinuousBodies[0].Select(B).Order())} | {string.Join(' ', w.ContinuousBodies[1].Select(B).Order())} | {string.Join(' ', w.ContinuousBodies[2].Select(B))}",
            $"nodes {string.Join(' ', w.Islands.All.Select(N))}",
            $"list A {string.Join(' ', w.Islands.Free.Select(N))}",
            $"list B {string.Join(' ', w.Islands.Serial.Select(N))}",
            $"list C {string.Join(' ', w.Islands.Coloured.Select(N))}",
            $"split {string.Join(' ', w.Islands.SplitPending.Select(N))}",
        };
        foreach (var n in w.Islands.All)
        {
            if (n is not RnIsland i)
                continue;
            lines.Add($"{N(i)} list {i.ListIndex} bodies {string.Join(' ', i.Bodies.Select(b => b == null ? "-" : B(b)))}");
            for (var g = 0; g < 2; g++)
                lines.Add($"{N(i)} group {g} {string.Join(' ', i.Contacts[g].Select(c => $"{C(c)}#{c.IslandIndex}:{c.SolverA},{c.SolverB}"))}");
            lines.Add($"{N(i)} edges {i.EdgeCount} awake {i.AwakeCount} iterations {i.VelocityIterations}/{i.PositionIterations} split {i.SplitIndex} flags {i.Flags} sizes {i.Sizes[0]},{i.Sizes[1]} coloured {i.Coloured}");
            if (i.Colouring is { } colouring)
                for (var b = 0; b < 17; b++)
                    for (var g = 0; g < 2; g++)
                        if (colouring.Contacts[b, g].Count > 0 || colouring.Sizes[b, g] != 0)
                            lines.Add($"{N(i)} colour {b} group {g} size {colouring.Sizes[b, g]} {string.Join(' ', colouring.Contacts[b, g].Select(C))}");
        }
        foreach (var b in w.Bodies)
        {
            var map = b.MapState switch
            {
                1 => $"{N(b.MapIsland!)}:{b.MapIndex}/{b.MapRefs}",
                2 => string.Join(',', b.MapHash!.Select(kv => $"{N(kv.Key)}:{kv.Value.Index}/{kv.Value.Refs}").Order()),
                _ => "",
            };
            lines.Add($"{B(b)} active {b.ActiveIndex} node {b.Node.ManagerIndex}/{b.Node.ListIndex} map {b.MapState} {map}");
            lines.Add($"{B(b)} state {State(b.State)}");
            if (b.Target is { } t)
                lines.Add($"{B(b)} target {Hex(t.Orientation)} {Hex(t.Position)} {Hex(t.Time)}");
            foreach (var s in b.Shapes)
            {
                lines.Add($"{B(b)} shape heads {string.Join(' ', s.Heads.Select(E))}");
                foreach (var h in s.Heads)
                    if (h is { } e && e.Contact.Key == 0)
                        lines.Add($"  debug {C(e.Contact)} {B(e.Contact.A.Body)}/{B(e.Contact.B.Body)} all {e.Contact.AllIndex} act {e.Contact.ActiveIndex} touch {e.Contact.TouchState} f {e.Contact.Flags78:x}");
            }
        }
        foreach (var c in w.AllContacts[0].Concat(w.AllContacts[1]).Concat(w.Destroyed).Distinct())
        {
            lines.Add($"{C(c)} touch {c.TouchState} island {(c.Island == null ? "-" : N(c.Island))}#{c.IslandIndex} in {c.InIsland} active {c.ActiveIndex} all {c.AllIndex} solver {c.SolverA},{c.SolverB} flags {c.Flags74:x}/{c.Flags78:x} group {c.Group} size {c.Size88}/{c.Size98} colour {c.Colour}#{c.ColourIndex}");
            lines.Add($"{C(c)} links {E(c.Next[0])} {E(c.Next[1])} {E(c.Prev[0])} {E(c.Prev[1])}");
            lines.Add($"{C(c)} manifolds {string.Join(' ', c.Manifolds.Select(Manifold))}");
            if (c.Mesh is { } m)
                lines.Add($"{C(c)} mesh {Hex(m.BoxMin)} {Hex(m.BoxMax)} tris {string.Join(',', m.Triangles)} caches {string.Join(',', m.Caches.Select(x => Hex(x)))}");
            else
                lines.Add($"{C(c)} sat {Hex(c.Sat)}");
        }
        return lines;
    }

    /// <summary>The first line that differs, with a little context, or null.</summary>
    public static string? Diff(List<string> expected, List<string> actual)
    {
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var e = i < expected.Count ? expected[i] : "<none>";
            var a = i < actual.Count ? actual[i] : "<none>";
            if (e != a)
                return $"line {i}\nvalve: {e}\nours:  {a}";
        }
        return null;
    }

    private static string B(RnBody b) => $"b{b.Index}";

    private static string C(RnContact c) => c.Key == 0 ? $"c?{c.Native:x}" : $"c{c.Key:x}";

    private static string N(IslandNode n) => n is BodyNode b ? B(b.Body) : $"I{n.ManagerIndex}";

    private static string E(EdgeRef? e) => e is { } x ? $"{C(x.Contact)}.{x.Half}" : "-";

    /// <summary>The header and each point to the count, bytes 0x00..0x25 (0x26..0x27 are never written).</summary>
    private static unsafe string Manifold(CachedManifold m)
    {
        var bytes = new ReadOnlySpan<byte>(&m, 0xe0);
        var sb = new StringBuilder(Convert.ToHexString(bytes[..0x40]));
        for (var k = 0; k < m.PointCount; k++)
            sb.Append('|').Append(Convert.ToHexString(bytes.Slice(0x40 + 0x28 * k, 0x26)));
        return sb.ToString();
    }

    /// <summary>The body's bytes without +0x20..+0x47, the island map and node, which are compared as fields.</summary>
    private static unsafe string State(RnBodyState s)
    {
        var bytes = new ReadOnlySpan<byte>(&s, sizeof(RnBodyState));
        return Convert.ToHexString(bytes[..0x20]) + "~" + Convert.ToHexString(bytes[0x48..]);
    }

    private static unsafe string Hex<T>(T value) where T : unmanaged
        => Convert.ToHexString(new ReadOnlySpan<byte>(&value, sizeof(T)));
}
