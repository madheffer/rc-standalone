// cubehost: runs CS2's engine (engine2.dll's Source2Main, as cs2.exe calls it) in our own process, so the
// game's cubemap build (-buildcubemaps) can be observed and captured without touching cs2.exe (docs/CUBEMAPS.md).
//
// Safety, enforced here: it refuses to start while steam.exe, steamwebhelper.exe or cs2.exe runs, and requires
// -insecure -nosteam on its own command line. VAC is delivered by the Steam client into Steam-launched game sessions; with Steam
// closed there is no client, no account session and no VAC to load.
//
// cubehost [--cs2 <CS2 dir>] -- <engine command line>
//   e.g. cubehost -- -insecure -nosteam -vulkan -novid -console -addon s2c_cubeprobe +map probe01 -buildcubemaps
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Source2Main(nint hInstance, nint hPrevInstance, nint commandLine, int showCommand, nint baseDir, nint game);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("kernel32")]
    private static extern nint GetModuleHandleW(nint name);

    private static readonly string[] Forbidden = ["steam", "steamwebhelper", "cs2"];

    private static int Main(string[] args)
    {
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var split = Array.IndexOf(args, "--");
        var own = split < 0 ? args : args[..split];
        var engineArgs = split < 0 ? [] : args[(split + 1)..];
        for (var i = 0; i + 1 < own.Length; i++)
            if (own[i] == "--cs2")
                cs2 = own[i + 1];

        var running = Process.GetProcesses().Select(p => p.ProcessName.ToLowerInvariant()).Where(n => Forbidden.Contains(n)).Distinct().ToList();
        if (running.Count > 0)
        {
            Console.Error.WriteLine($"cubehost: refusing to start while {string.Join(", ", running)} runs (close Steam completely first).");
            return 2;
        }

        // tier0 may read the process's own command line (GetCommandLine) rather than the one handed to
        // Source2Main, so both switches must be on ours too.
        if (!engineArgs.Contains("-insecure") || !engineArgs.Contains("-nosteam"))
        {
            Console.Error.WriteLine("cubehost: the engine arguments after -- must include -insecure and -nosteam.");
            return 2;
        }

        var bin = Path.Combine(cs2, "game", "bin", "win64");
        var engine = Path.Combine(bin, "engine2.dll");
        if (!File.Exists(engine))
        {
            Console.Error.WriteLine($"cubehost: no {engine}");
            return 2;
        }
        Directory.SetCurrentDirectory(bin);
        SetDllDirectory(bin);
        var module = NativeLibrary.Load(engine);
        var main = Marshal.GetDelegateForFunctionPointer<Source2Main>(NativeLibrary.GetExport(module, "Source2Main"));

        // The command line handed to Source2Main: the engine's arguments (which hold -insecure and -nosteam).
        var line = string.Join(' ', engineArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        Console.WriteLine($"cubehost: Source2Main(\"{line}\", base {bin}, game csgo)");
        var lineUtf8 = Marshal.StringToCoTaskMemUTF8(line);
        var baseUtf8 = Marshal.StringToCoTaskMemUTF8(bin);
        var gameUtf8 = Marshal.StringToCoTaskMemUTF8("csgo");
        try
        {
            return main(GetModuleHandleW(0), 0, lineUtf8, 1, baseUtf8, gameUtf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(lineUtf8);
            Marshal.FreeCoTaskMem(baseUtf8);
            Marshal.FreeCoTaskMem(gameUtf8);
        }
    }
}
