using System.Runtime.InteropServices;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Detours on vphysics2 functions, so a test can see Valve's calls as they
/// happen inside a world step and still let them run. The function's first
/// instructions (a prologue that holds no relative addressing, checked
/// against the bytes given) move to a stub that jumps back after them; the
/// entry becomes <c>mov rax, hook; jmp rax</c>. <see cref="Dispose"/> puts the
/// bytes back. A hook must not throw: an exception cannot cross into Valve's
/// frames.
/// </summary>
internal sealed unsafe class Vphysics2Hooks : IDisposable
{
    private readonly byte* _stubs;
    private int _used;
    private readonly List<(nint At, byte[] Saved)> _patches = [];

    public Vphysics2Hooks()
    {
        _stubs = (byte*)VirtualAlloc(0, 0x1000, 0x3000, 0x40);
        Assert.True(_stubs != null, "no executable memory for the stubs");
    }

    /// <summary>
    /// Detours <paramref name="target"/> to <paramref name="hook"/> and
    /// returns the address that runs the original. <paramref name="prologue"/>
    /// is the target's first bytes, whole instructions, at least 12 of them.
    /// </summary>
    public nint Detour(nint target, nint hook, ReadOnlySpan<byte> prologue)
    {
        Assert.True(prologue.Length >= 12, "the prologue must cover the 12-byte patch");
        Assert.True(new ReadOnlySpan<byte>((void*)target, prologue.Length).SequenceEqual(prologue),
                    $"unexpected bytes at {target:x}");
        var stub = _stubs + _used;
        prologue.CopyTo(new Span<byte>(stub, prologue.Length));
        var jump = stub + prologue.Length;
        jump[0] = 0xff;
        jump[1] = 0x25;
        *(int*)(jump + 2) = 0;
        *(nint*)(jump + 6) = target + prologue.Length;
        _used += (prologue.Length + 14 + 15) & ~15;
        var patch = new byte[12];
        patch[0] = 0x48;
        patch[1] = 0xb8;
        BitConverter.TryWriteBytes(patch.AsSpan(2), (long)hook);
        patch[10] = 0xff;
        patch[11] = 0xe0;
        _patches.Add((target, new ReadOnlySpan<byte>((void*)target, 12).ToArray()));
        Write(target, patch);
        return (nint)stub;
    }

    public void Dispose()
    {
        for (var i = _patches.Count - 1; i >= 0; i--)
            Write(_patches[i].At, _patches[i].Saved);
        _patches.Clear();
    }

    private static void Write(nint at, byte[] bytes)
    {
        Assert.True(VirtualProtect(at, (nuint)bytes.Length, 0x40, out var old));
        bytes.CopyTo(new Span<byte>((void*)at, bytes.Length));
        VirtualProtect(at, (nuint)bytes.Length, old, out _);
        FlushInstructionCache(-1, at, (nuint)bytes.Length);
    }

    [DllImport("kernel32.dll")]
    private static extern void* VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}

/// <summary>
/// Tests that patch vphysics2's code: they run alone, never beside another
/// test that calls into the DLL, since a patch is seen by every thread.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Vphysics2PatchCollection
{
    public const string Name = "vphysics2 code patches";
}
