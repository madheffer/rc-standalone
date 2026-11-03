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
        int? renderDocCapture = null;
        for (var i = 0; i + 1 < own.Length; i++)
            if (own[i] == "--cs2")
                cs2 = own[i + 1];
            else if (own[i] == "--renderdoc-capture")
                renderDocCapture = int.Parse(own[i + 1]);

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
        if (renderDocCapture is { } capture)
            SetRenderDocCapture(capture);
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

    // client.dll's buildcubemaps_renderdoc_capture (which cubemap BuildCubemaps captures with RenderDoc under
    // -renderdoc) is development-only (registered with flags 2, client 10-15 1800538d8): neither the console nor
    // the command line sets it in a release build. In our own process we set it the way the convar system would:
    // the cvar's data pointer is a client global (18237eab0 in the 10-15 and 10-21 builds, 1823806d8 in 10-23, 182382858 in 10-29, 10-30 and 11-03:
    // the handle 1800538ed passes to the registration, plus 8), its name at +0 and its int value at +0x58. The
    // build is told by client.dll's PE time stamp and the name is checked; another client build is left alone.
    private const string RenderDocConvar = "buildcubemaps_renderdoc_capture";
    private static readonly Dictionary<uint, int> ClientConvarGlobals = new()
    {
        [0x6ad01314] = 0x237eab0,   // 2026-10-15
        [0x6ad7b41c] = 0x237eab0,   // 2026-10-21
        [0x6ada7340] = 0x23806d8,   // 2026-10-23 (1.41.9.2)
        [0x6ae217a2] = 0x2382858,   // 2026-10-29 (1.41.9.3)
        [0x6ae4fb84] = 0x2382858,   // 2026-10-30 (1.41.9.4; the handle's lea at 1800538ed unchanged)
        [0x6ae91e98] = 0x2382858,   // 2026-11-03 (1.41.9.5; the same lea)
    };

    private static void SetRenderDocCapture(int index)
    {
        var thread = new Thread(() =>
        {
            var name = Marshal.StringToHGlobalUni("client.dll");
            for (var tries = 0; tries < 6000; tries++, Thread.Sleep(50))
            {
                var client = GetModuleHandleW(name);
                if (client == 0)
                    continue;
                var stamp = (uint)Marshal.ReadInt32(client + Marshal.ReadInt32(client + 0x3c) + 8);
                if (!ClientConvarGlobals.TryGetValue(stamp, out var global))
                {
                    Console.WriteLine($"cubehost: client.dll build {stamp:x8} has no known {RenderDocConvar} global; not set.");
                    return;
                }
                var data = Marshal.ReadIntPtr(client + global);
                if (data == 0)
                    continue;
                var convarName = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data));
                // Until the convar registers, the global points at the convar system's placeholder.
                if (convarName == "<undefined>")
                    continue;
                if (convarName != RenderDocConvar)
                {
                    Console.WriteLine($"cubehost: client's convar at +{global:x} is \"{convarName}\", not {RenderDocConvar}; not set (another client build).");
                    return;
                }
                Marshal.WriteInt32(data + 0x58, index);
                Console.WriteLine($"cubehost: {RenderDocConvar} = {index}");
                return;
            }
            Console.WriteLine($"cubehost: {RenderDocConvar} not found; not set.");
        }) { IsBackground = true };
        thread.Start();
    }
}
