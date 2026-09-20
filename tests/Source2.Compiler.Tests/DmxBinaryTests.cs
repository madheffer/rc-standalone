using System.Numerics;
using System.Text;
using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Pins the binary DMX reader, which is the front door to a map: a <c>.vmap</c>
/// is a DMX element graph and nothing else.
///
/// <para>Two tests, doing different jobs. The synthetic one owns every byte it
/// reads, so it can assert the rules that are not guessable from the shape - the
/// array type base, and strings inside an array being inline where a scalar string
/// is a table index. The corpus one reads Valve's own <c>.vmap</c> sources out of
/// the CS2 content tree and asserts the reader consumes each to its last byte,
/// which is the check that a length was misjudged somewhere.</para>
/// </summary>
public class DmxBinaryTests
{
    [Fact]
    public void Reader_HandlesEveryRuleTheFormatDoesNotAdvertise()
    {
        var dmx = new DmxWriter()
            .Header("binary", 9, "vmap", 40)
            .PrefixElement(p => p
                .InlineAttribute("thumbnail_format", 5, w => w.InlineString("jpg"))
                .InlineAttribute("map_asset_references", 37, w => w.Count(1).InlineString("materials/x.vmat")))
            .Strings("CMapRootElement", "CMapEntity", "world", "children", "origin", "classname", "targets", "")
            .Element(type: 0, name: 7)
            .Element(type: 1, name: 7)
            .Attributes(
                a => a.Attribute(2, 1, w => w.Int(1)),         // world -> element 1
                a => a.Attribute(3, 33, w => w.Count(1).Int(1)))  // children -> [element 1]
            .Attributes(
                a => a.Attribute(4, 10, w => w.Vector3(1, 2, 3)),
                a => a.Attribute(5, 5, w => w.Int(1)),         // classname -> string table
                a => a.Attribute(6, 37, w => w.Count(2).InlineString("a").InlineString("b")))
            .Build();

        var doc = DmxBinary.Read(dmx);

        Assert.Equal(("binary", 9, "vmap", 40), (doc.Encoding, doc.EncodingVersion, doc.Format, doc.FormatVersion));
        Assert.Equal(0, doc.TrailingBytes);

        var prefix = Assert.Single(doc.Prefix);
        Assert.Equal("jpg", prefix.Get<string>("thumbnail_format"));
        Assert.Equal(["materials/x.vmat"], prefix.Get<object?[]>("map_asset_references"));

        var root = Assert.IsType<DmxBinary.Element>(doc.Root);
        Assert.Equal("CMapRootElement", root.Type);

        // An element attribute resolves to the element itself, not to its index.
        var entity = Assert.IsType<DmxBinary.Element>(root.Get<DmxBinary.Element>("world"));
        Assert.Equal("CMapEntity", entity.Type);
        Assert.Same(entity, Assert.Single(root.GetElements("children")));

        Assert.Equal(new Vector3(1, 2, 3), entity.GetValue<Vector3>("origin"));
        Assert.Equal("CMapEntity", entity.Get<string>("classname"));
        Assert.Equal(["a", "b"], entity.Get<object?[]>("targets"));
    }

    [Fact]
    public void Reader_ConsumesValvesOwnMapSourcesToTheLastByte()
    {
        var sources = MapFixtures.VmapSources();
        if (sources.Count == 0) { MapFixtures.Skip(".vmap sources under content/csgo_addons"); return; }

        foreach (var path in sources)
        {
            var doc = DmxBinary.ReadFile(path);
            var what = Path.GetFileName(path);
            Assert.Equal(("binary", 9), (doc.Encoding, doc.EncodingVersion));
            Assert.Equal("vmap", doc.Format);
            // A misjudged length shows up here and nowhere else: the graph would
            // still "parse", just against bytes that mean something else.
            Assert.True(doc.TrailingBytes == 0, $"{what}: {doc.TrailingBytes} bytes unread");
            Assert.Equal("CMapRootElement", doc.Root!.Type);
            Assert.NotEmpty(doc.OfType("CMapWorld"));
        }
    }

    /// <summary>Builds a binary DMX byte for byte, so the reader is tested against
    /// a file whose every field is known rather than against its own output.</summary>
    private sealed class DmxWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly List<Action<DmxWriter>> _attributeSets = [];

        public DmxWriter Header(string encoding, int encodingVersion, string format, int formatVersion)
        {
            Write(Encoding.ASCII.GetBytes($"<!-- dmx encoding {encoding} {encodingVersion} format {format} {formatVersion} -->\n"));
            _stream.WriteByte(0);
            Count(1);                       // prefix element count, written by PrefixElement
            return this;
        }

        public DmxWriter PrefixElement(Func<DmxWriter, DmxWriter> attributes)
        {
            var before = _pendingAttributes;
            _pendingAttributes = 0;
            var placeholder = _stream.Position;
            Count(0);
            attributes(this);
            Patch(placeholder, _pendingAttributes);
            _pendingAttributes = before;
            return this;
        }

        public DmxWriter InlineAttribute(string name, byte type, Action<DmxWriter> value)
        {
            Write(Encoding.UTF8.GetBytes(name));
            _stream.WriteByte(0);
            _stream.WriteByte(type);
            value(this);
            _pendingAttributes++;
            return this;
        }

        public DmxWriter Strings(params string[] strings)
        {
            Count(strings.Length);
            foreach (var s in strings)
            {
                Write(Encoding.UTF8.GetBytes(s));
                _stream.WriteByte(0);
            }
            _elementCountAt = _stream.Position;
            Count(0);
            return this;
        }

        public DmxWriter Element(int type, int name)
        {
            Int(type);
            Int(name);
            Write(Guid.NewGuid().ToByteArray());
            _elements++;
            return this;
        }

        public DmxWriter Attributes(params Action<DmxWriter>[] attributes)
        {
            _attributeSets.Add(w =>
            {
                w.Count(attributes.Length);
                foreach (var a in attributes)
                    a(w);
            });
            return this;
        }

        public DmxWriter Attribute(int nameIndex, byte type, Action<DmxWriter> value)
        {
            Int(nameIndex);
            _stream.WriteByte(type);
            value(this);
            return this;
        }

        public DmxWriter Count(int n) { Int(n); return this; }

        public DmxWriter Int(int v) { Write(BitConverter.GetBytes(v)); return this; }

        public DmxWriter InlineString(string s)
        {
            Write(Encoding.UTF8.GetBytes(s));
            _stream.WriteByte(0);
            return this;
        }

        public DmxWriter Vector3(float x, float y, float z)
        {
            foreach (var f in new[] { x, y, z })
                Write(BitConverter.GetBytes(f));
            return this;
        }

        public byte[] Build()
        {
            Patch(_elementCountAt, _elements);
            foreach (var set in _attributeSets)
                set(this);
            return _stream.ToArray();
        }

        private void Write(byte[] bytes) => _stream.Write(bytes);

        private void Patch(long at, int value)
        {
            var end = _stream.Position;
            _stream.Position = at;
            _stream.Write(BitConverter.GetBytes(value));
            _stream.Position = end;
        }

        private long _elementCountAt;
        private int _elements;
        private int _pendingAttributes;
    }
}
