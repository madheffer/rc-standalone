using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The angles a map node holds once resourcecompiler has loaded it: every node's angles go through
/// CMapNode's setter (vfn 0x18, 180f770a0) to CMapPoint's (180fdeda0, <see cref="SettleWorld.SetAngles"/>),
/// which zeroes a component under 0.001 and adds 360 to a negative yaw until it is not negative; pitch
/// and roll keep their sign and a yaw past 360 stays. A .vmap Hammer saved already holds such angles;
/// one written another way (a negative yaw) is built from the normalised ones: s2c_navangles' markups
/// carry the matrices of the normalised angles, and its lump the normalised angles (ledger 145).
/// <see cref="DmxBinary.ReadFile"/> applies it to every .vmap it reads.
/// </summary>
public static class MapNodeAngles
{
    /// <summary>Normalises the angles of every map node (an element with a node id) in place; returns the document.</summary>
    public static DmxBinary.Document Apply(DmxBinary.Document document)
    {
        if (document.Root?.Type != "CMapRootElement")
            return document;
        foreach (var e in document.Elements)
            if (e.Attributes.ContainsKey("nodeID") && e.Attributes.TryGetValue("angles", out var value) && value is Vector3 angles
                && SettleWorld.SetAngles(angles) is var held && !SameBits(held, angles))
                e.Attributes["angles"] = held;
        return document;
    }

    // A -0 the setter turns into 0 counts as a change: the matrices' zero entries keep its sign.
    private static bool SameBits(Vector3 a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
           && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
}
