using System.Runtime.InteropServices;
using System.Text;
using Source2.Compiler.Io;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="Tier0Paths"/> and <see cref="ResourceNames"/> against the code
/// they were read from: tier0's exports, and resourcecompiler's
/// FixupResourceName (181c1ea80) and name hash (1800c7c10) called in place.
/// Returns without asserting when the installed build is not the one read.
/// </summary>
public unsafe class ResourceNamesOracleTests(ITestOutputHelper output)
{
    private static readonly string[] Named =
    [
        "", "a", "materials/dev/dev_measuregeneric01.vmat", "Materials\\Dev\\Dev.VMAT", "materials//dev///x.vmat",
        "materials/./dev/x.vmat", "./x.vmat", "materials/dev/../x.vmat", "../x.vmat", "a/b/../../c.vmat", "a/../../c.vmat",
        "c:/x.vmat", "C:x.vmat", "/x.vmat", "\\x.vmat", "\\\\srv\\x.vmat", "//x.vmat", "vpk:x.vmat", "VPK:vpk:x.vmat",
        "vpk:ugc:x.vmat", "ugc:x.vmat", "x.vtex", "x.vmatt", "x.vma", "x.", ".vmat", "x..vmat", "noext", "dir.d/noext",
        "$(x)/../b.vmat", "${x}/../b.vmat", "$x/../b.vmat", "%x%/../y", "%x/../y", "$(a(b))/../c", "${x/../b", "a/./.", "a/.",
        "a:/../b", "a/b:/../c", "É/x.vmat", "a\\.\\b", "..", ".", "./", "a/..", "a/b/..", "x/y/z/../../w.vmat",
    ];

    [Fact]
    public void Tier0HelpersMatchTheExports()
    {
        if (ResourceCompilerOracle.Load() == null)
            return;
        var isAbsolute = (delegate* unmanaged<byte*, byte>)ResourceCompilerOracle.Tier0("V_IsAbsolutePath");
        var extension = (delegate* unmanaged<byte*, byte*>)ResourceCompilerOracle.Tier0("V_GetFileExtension");
        var removeDots = (delegate* unmanaged<byte*, byte, byte>)ResourceCompilerOracle.Tier0("V_RemoveDotSlashes");
        var failures = new List<string>();
        foreach (var name in Corpus())
        {
            var ours = ResourceNames.Terminated(name);
            fixed (byte* p = ResourceNames.Terminated(name))
            {
                if ((isAbsolute(p) != 0) != Tier0Paths.IsAbsolutePath(ours))
                    failures.Add($"IsAbsolutePath '{name}'");
                var e = extension(p);
                var theirs = e == null ? -1 : (int)(e - p);
                if (theirs != Tier0Paths.GetFileExtension(ours))
                    failures.Add($"GetFileExtension '{name}': {theirs} vs {Tier0Paths.GetFileExtension(ours)}");
            }
            foreach (var sep in new[] { (byte)'\\', (byte)'/' })
            {
                var mine = ResourceNames.Terminated(name);
                var okMine = Tier0Paths.RemoveDotSlashes(mine, sep);
                var buffer = ResourceNames.Terminated(name);
                fixed (byte* p = buffer)
                {
                    var okTheirs = removeDots(p, sep) != 0;
                    if (okTheirs != okMine || ResourceNames.Text(buffer) != ResourceNames.Text(mine))
                        failures.Add($"RemoveDotSlashes '{name}' sep {(char)sep}: '{ResourceNames.Text(buffer)}' {okTheirs} vs '{ResourceNames.Text(mine)}' {okMine}");
                }
            }
        }
        output.WriteLine($"compared {Corpus().Count()} names against tier0");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void ToolMaterialHashMatchesTheCompiler()
    {
        if (ResourceCompilerOracle.Load() is not { } module)
            return;
        var fixup = (delegate* unmanaged<byte*, byte*, long, byte>)ResourceCompilerOracle.At(module, 0x181c1ea80);
        var hash = (delegate* unmanaged<byte*, uint, uint, uint>)ResourceCompilerOracle.At(module, 0x1800c7c10);
        var failures = new List<string>();
        foreach (var name in Corpus())
        {
            // A CBufferString with 200 bytes inline, as 180c25900 builds it.
            var buffer = (byte*)NativeMemory.AllocZeroed(0x100);
            *(uint*)(buffer + 4) = 0xc00000c8;
            uint theirs = 0;
            string theirText;
            fixed (byte* p = ResourceNames.Terminated(name))
                fixup(buffer, p, 0x74616d76);
            var flags = *(uint*)(buffer + 4);
            var text = (flags >> 30 & 1) != 0 ? buffer + 8 : (flags & 0x3fffffff) == 0 ? null : *(byte**)(buffer + 8);
            if (text != null && *text != 0)
            {
                var length = 0u;
                while (text[length] != 0)
                    length++;
                theirs = hash(text, length, 0x31415926);
                theirText = Encoding.UTF8.GetString(text, (int)length);
            }
            else
            {
                theirText = "";
            }
            NativeMemory.Free(buffer);
            var mineText = ResourceNames.Fixup(name, "vmat") is { } f ? ResourceNames.Text(f) : "";
            var mine = ResourceNames.ToolMaterialHash(name);
            if (mine != theirs || mineText != theirText)
                failures.Add($"'{name}': '{theirText}' {theirs:x8} vs '{mineText}' {mine:x8}");
            if (Array.IndexOf(Named, name) is >= 0 and < 12)
                output.WriteLine($"'{name}' -> '{theirText}' {theirs:x8}");
        }
        output.WriteLine($"compared {Corpus().Count()} names against resourcecompiler");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void Tier0StringsMatchTheExports()
    {
        if (ResourceCompilerOracle.Load() == null)
            return;
        var stristr = (delegate* unmanaged<byte*, byte*, byte*>)ResourceCompilerOracle.Tier0("?V_stristr_fast@@YAPEBDPEBD0@Z");
        var split = (delegate* unmanaged<byte*, byte*, byte*, byte, void>)ResourceCompilerOracle.Tier0(
            "?V_SplitString@@YAXPEBD0AEAV?$CUtlVector@VCUtlString@@HV?$CUtlVectorMemory_Growable@VCUtlString@@H$0A@@@@@_N@Z");
        var remove = (delegate* unmanaged<nint*, nint*, byte*, byte, nint*>)ResourceCompilerOracle.Tier0("?Remove@CUtlString@@QEBA?AV1@PEBD_N@Z");
        string[] finds = ["water", ", window", ",", "Ab", "a"];
        var failures = new List<string>();
        var texts = Corpus().Concat(["Water, Window, window", "solid, window, WINDOW, water", ",,a,,b,", "a, b ,c", "", ","]).ToList();
        foreach (var text in texts)
        {
            foreach (var find in finds)
            {
                fixed (byte* t = ResourceNames.Terminated(text))
                fixed (byte* f = ResourceNames.Terminated(find))
                {
                    var hit = stristr(t, f);
                    var theirs = hit == null ? -1 : (int)(hit - t);
                    // Byte offsets equal char offsets on ASCII text only.
                    if (text.All(c => c < 0x80) && theirs != Tier0Strings.StriStr(text, find))
                        failures.Add($"StriStr '{text}' '{find}': {theirs} vs {Tier0Strings.StriStr(text, find)}");

                    var self = (nint)t;
                    nint result = 0;
                    remove(&self, &result, f, 0);
                    var removed = result == 0 ? "" : Marshal.PtrToStringUTF8(result) ?? "";
                    if (removed != Tier0Strings.RemoveIgnoreCase(text, find))
                        failures.Add($"Remove '{text}' '{find}': '{removed}' vs '{Tier0Strings.RemoveIgnoreCase(text, find)}'");

                    foreach (var empty in new[] { false, true })
                    {
                        var vector = (byte*)NativeMemory.AllocZeroed(0x20);
                        split(t, f, vector, empty ? (byte)1 : (byte)0);
                        var count = *(int*)vector;
                        var items = *(nint**)(vector + 8);
                        var pieces = Enumerable.Range(0, count).Select(i => items[i] == 0 ? "" : Marshal.PtrToStringUTF8(items[i]) ?? "").ToList();
                        NativeMemory.Free(vector);
                        var mine = Tier0Strings.SplitString(text, find, empty);
                        if (!pieces.SequenceEqual(mine))
                            failures.Add($"Split '{text}' '{find}' {empty}: [{string.Join("|", pieces)}] vs [{string.Join("|", mine)}]");
                    }
                }
            }
        }
        output.WriteLine($"compared {texts.Count} texts x {finds.Length} patterns against tier0");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }

    // The named cases, then 4000 random strings over path-ish characters.
    private static IEnumerable<string> Corpus()
    {
        foreach (var s in Named)
            yield return s;
        const string alphabet = "ab.././\\\\:$%(){}Vx_é";
        var random = new Random(12345);
        for (var i = 0; i < 4000; i++)
        {
            var sb = new StringBuilder();
            var n = random.Next(0, 14);
            for (var k = 0; k < n; k++)
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            if (random.Next(3) == 0)
                sb.Append(".vmat");
            yield return sb.ToString();
        }
    }
}
