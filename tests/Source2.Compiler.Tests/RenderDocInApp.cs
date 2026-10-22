using System.Runtime.InteropServices;

namespace Source2.Compiler.Tests;

/// <summary>
/// RenderDoc's in-application API (renderdoc_app.h, 1.6.0), when its Vulkan layer is loaded into this process
/// (run with ENABLE_VULKAN_RENDERDOC_CAPTURE=1): frame captures around our own submissions, saved under
/// <see cref="CaptureTemplate"/>. Used to compare our device's replay of a draw with the game's capture in
/// RenderDoc (pixel debugging, post-VS data).
/// </summary>
internal static unsafe class RenderDocInApp
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);

    // Slots of RENDERDOC_API_1_6_0.
    private const int SetCaptureFilePathTemplate = 11, StartFrameCapture = 19, EndFrameCapture = 21;

    private static readonly nint* Api = Load();

    private static nint* Load()
    {
        var module = GetModuleHandleW("renderdoc.dll");
        if (module == 0)
            return null;
        var getApi = (delegate* unmanaged<int, void**, int>)GetProcAddress(module, "RENDERDOC_GetAPI");
        void* api = null;
        return getApi != null && getApi(10600, &api) == 1 ? (nint*)api : null;
    }

    public static bool Available => Api != null;

    public static string CaptureTemplate { get; } = Path.Combine(Path.GetTempPath(), "RenderDoc", "s2c_replay");

    public static void Start()
    {
        if (Api == null)
            throw new InvalidOperationException("RenderDoc is not loaded: run with ENABLE_VULKAN_RENDERDOC_CAPTURE=1");
        var path = Marshal.StringToHGlobalAnsi(CaptureTemplate);
        ((delegate* unmanaged<nint, void>)Api[SetCaptureFilePathTemplate])(path);
        Marshal.FreeHGlobal(path);
        ((delegate* unmanaged<nint, nint, void>)Api[StartFrameCapture])(0, 0);
    }

    public static void End()
    {
        if (Api != null)
            ((delegate* unmanaged<nint, nint, uint>)Api[EndFrameCapture])(0, 0);
    }
}
