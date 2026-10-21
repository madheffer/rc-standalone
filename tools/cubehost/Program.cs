// cubehost: runs CS2's engine (engine2.dll's Source2Main, as cs2.exe calls it) in our own process, so the
// game's cubemap build (-buildcubemaps) can be observed and captured without touching cs2.exe (docs/CUBEMAPS.md).
//
// Safety, enforced here: it refuses to start while steam.exe, steamwebhelper.exe or cs2.exe runs, and requires
// -insecure -nosteam on its own command line. VAC is delivered by the Steam client into Steam-launched game sessions; with Steam
// closed there is no client, no account session and no VAC to load.
//
// cubehost [--cs2 <CS2 dir>] -- <engine command line>
//   e.g. cubehost -- -insecure -nosteam -nobreakpad -vulkan -novid -console -addon s2c_cubeprobe +map probe01 -buildcubemaps
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Source2Main(nint hInstance, nint hPrevInstance, nint commandLine, int showCommand, nint baseDir, nint game);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetGameDirectory(nint path);

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
        // -nobreakpad keeps steam_api's crash handler (which loads steamclient) from starting.
        if (!engineArgs.Contains("-insecure") || !engineArgs.Contains("-nosteam") || !engineArgs.Contains("-nobreakpad"))
        {
            Console.Error.WriteLine("cubehost: the engine arguments after -- must include -insecure, -nosteam and -nobreakpad.");
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
        // Our stub steam_api64.dll first: a module already loaded under that name satisfies every later import
        // of it, wherever the importing DLL lives (loading by full path searches the DLL's own folder first).
        var stubPath = Path.Combine(AppContext.BaseDirectory, "steam_api64.dll");
        if (!File.Exists(stubPath))
        {
            Console.Error.WriteLine($"cubehost: no stub {stubPath} (build it with build_stub.ps1).");
            return 2;
        }
        NativeLibrary.Load(stubPath);

        // tier0 takes the game directory from the executable's place (cs2.exe sits in gamein\win64);
        // ours lives elsewhere, so it is set before the engine starts.
        var tier0 = NativeLibrary.Load(Path.Combine(bin, "tier0.dll"));
        var setGameDirectory = Marshal.GetDelegateForFunctionPointer<SetGameDirectory>(NativeLibrary.GetExport(tier0, "Plat_SetGameDirectory"));
        var gameDir = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(Path.Combine(bin, "..", "..")));
        setGameDirectory(gameDir);
        var module = NativeLibrary.Load(engine);
        // engine2 imports steam_api64.dll; it must have bound to our stub beside this exe, never the game's.
        var stub = stubPath;
        var bound = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => string.Equals(m.ModuleName, "steam_api64.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        if (bound is null || !string.Equals(Path.GetFullPath(bound), Path.GetFullPath(stub), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"cubehost: steam_api64.dll is {bound ?? "not loaded"}, not the stub {stub} (build it with build_stub.ps1); refusing.");
            return 2;
        }
        var main = Marshal.GetDelegateForFunctionPointer<Source2Main>(NativeLibrary.GetExport(module, "Source2Main"));

        // The command line handed to Source2Main, as cs2.exe hands its own: the program first (CreateCmdLine
        // takes the first token as it), then the engine's arguments (which hold -insecure and -nosteam).
        var line = string.Join(' ', new[] { $"\"{Path.Combine(bin, "cs2.exe")}\"" }.Concat(engineArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
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
