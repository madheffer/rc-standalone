using Source2.Compiler.Kv3;
using ValveKeyValue;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// An integer Valve stored as UINT8 (a model's <c>m_cRenderColor</c>) is written back as UINT8 after a
/// decode, and after an edit that replaced it, as long as the value fits (<see cref="Kv3WireTypes"/>).
/// </summary>
public class Kv3WireTypesTests
{
    private static KVObject Byte(uint v)
    {
        var o = new KVObject(v);
        Kv3WireTypes.Set(o, Kv3WireTypes.UInt8);
        return o;
    }

    private static (Kv3Type Array, Kv3Type Element) ColorTypes(byte[] block)
    {
        var t = Kv3Tree.Read(block);
        var color = t.Need(t.Root, "m_cRenderColor");
        return (color.Type, color.ElementType);
    }

    private static KVObject Tree()
    {
        var color = KVObject.Array();
        foreach (var c in new uint[] { 255, 128, 0, 7 })
            color.Add(Byte(c));
        var root = KVObject.Collection();
        root.Add("m_cRenderColor", color);
        root.Add("m_nCount", new KVObject(300u));
        return root;
    }

    [Fact]
    public void Uint8_SurvivesDecodeAndEncode()
    {
        var format = Kv3Formats.Get("generic");
        var block = Kv3Binary.Write(Tree(), format);
        Assert.Equal((Kv3Type.ArrayTypeAuxiliaryBuffer, Kv3Type.Int32AsByte), ColorTypes(block));

        var again = Kv3Binary.Write(Kv3Binary.Read(block).Root, format);
        Assert.Equal(block, again);
    }

    [Fact]
    public void Inherit_KeepsTheWidthOfAReplacedValue()
    {
        var format = Kv3Formats.Get("generic");
        var original = Kv3Binary.Read(Kv3Binary.Write(Tree(), format)).Root;

        // An edit that builds new values (as VRF's reader or a clone would): no recorded width.
        var edited = KVObject.Collection();
        var color = KVObject.Array();
        foreach (var c in new uint[] { 10, 20, 30, 40 })
            color.Add(new KVObject(c));
        edited.Add("m_cRenderColor", color);
        edited.Add("m_nCount", new KVObject(300u));
        Assert.Equal(Kv3Type.UInt32, ColorTypes(Kv3Binary.Write(edited, format)).Element);

        Kv3WireTypes.Inherit(original, edited);
        Assert.Equal((Kv3Type.ArrayTypeAuxiliaryBuffer, Kv3Type.Int32AsByte), ColorTypes(Kv3Binary.Write(edited, format)));
    }

    [Fact]
    public void AValueThatNoLongerFits_IsWidened()
    {
        var format = Kv3Formats.Get("generic");
        var original = Kv3Binary.Read(Kv3Binary.Write(Tree(), format)).Root;
        var edited = KVObject.Collection();
        var color = KVObject.Array();
        foreach (var c in new uint[] { 10, 20, 30, 4000 })
            color.Add(new KVObject(c));
        edited.Add("m_cRenderColor", color);
        Kv3WireTypes.Inherit(original, edited);
        Assert.Equal(Kv3Type.UInt32, ColorTypes(Kv3Binary.Write(edited, format)).Element);
    }
}
