using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: a world mesh's first piece vertex under several candidate
/// world moves, against a vertex a capture recorded.
/// <c>WORLDMOVE=&lt;.vmap&gt;|&lt;node id&gt;|x,y,z</c>.
/// </summary>
public class WorldMoveProbe(ITestOutputHelper output)
{
    [Fact]
    public void Candidates()
    {
        if (Environment.GetEnvironmentVariable("WORLDMOVE") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var doc = DmxBinary.ReadFile(p[0]);
        var id = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
        var want = p[2].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var mesh = MapMeshes.Read(doc).First(m => m.NodeId == id);
        var world = doc.OfType("CMapWorld").First();
        var piece = BrushHulls.PiecesWithCorners(mesh.Element!, world, null, true).First();
        var local = piece.Local[0];
        output.WriteLine($"node {id} angles {mesh.Angles} origin {mesh.Origin}; local {local.X:R},{local.Y:R},{local.Z:R}; want {want[0]:R},{want[1]:R},{want[2]:R}");
        var toEntity = CTransform.FromNode(world).Inverse().Matrix();
        void Show(string name, float[] m)
        {
            var v = MapMeshes.Transform(toEntity, MapMeshes.Transform(m, local));
            var ok = v.X == want[0] && v.Y == want[1] && v.Z == want[2];
            output.WriteLine($"  {name}: {v.X:R},{v.Y:R},{v.Z:R}{(ok ? "  MATCH" : "")}");
        }
        var t = CTransform.FromNode(mesh.Element!);
        Show("CTransform", t.Matrix());
        Show("AngleMatrix", MapMeshes.Local(mesh.Element!));
        Show("Compose(node, identity inverse)", CTransform.Compose(t, new CTransform(Vector3.Zero, 1f, Quaternion.Identity).Inverse()).Matrix());
        Show("Compose(identity, node)", CTransform.Compose(new CTransform(Vector3.Zero, 1f, Quaternion.Identity), t).Matrix());
        // Normalised quaternion.
        var q = Quaternion.Normalize(t.Rotation);
        Show("normalised quaternion", new CTransform(t.Position, 1f, q).Matrix());
    }
}
