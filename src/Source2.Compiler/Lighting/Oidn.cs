using System.Runtime.InteropServices;

namespace Source2.Compiler.Lighting;

/// <summary>
/// Intel Open Image Denoise 2 through the game's own OpenImageDenoise.dll (game/bin/win64, beside its
/// CUDA, HIP, SYCL and CPU device modules), as vrad3 calls it: a device of type 0 (the default: the best
/// GPU there is, else the CPU), device buffers, the RTLightmap filter. The denoised output follows the
/// device: vrad3 on an NVIDIA card runs CUDA, and so does this on the same card.
/// </summary>
public sealed unsafe class Oidn : IDisposable
{
    private readonly nint _lib;
    private readonly nint _device;
    private readonly delegate* unmanaged<nint, nint, nint> _newFilter;
    private readonly delegate* unmanaged<nint, nuint, int, nint> _newBuffer;
    private readonly delegate* unmanaged<nint, nuint, nuint, void*, void> _writeAsync;
    private readonly delegate* unmanaged<nint, nuint, nuint, void*, void> _read;
    private readonly delegate* unmanaged<nint, void> _releaseBuffer, _releaseFilter, _commitFilter, _executeAsync, _sync, _releaseDevice;
    private readonly delegate* unmanaged<nint, nint, nint, int, nuint, nuint, nuint, nuint, nuint, void> _setImage;
    private readonly delegate* unmanaged<nint, nint, byte, void> _setBool;
    private readonly delegate* unmanaged<nint, nint, int, void> _setInt;
    private readonly delegate* unmanaged<nint, nint*, int> _error;

    /// <summary>The device's type as OIDN reports it (0 default, 1 CPU, 2 SYCL, 3 CUDA, 4 HIP, 5 Metal).</summary>
    public int DeviceType { get; }

    /// <summary>The first physical device's name (the one vrad3 logs as selected).</summary>
    public string DeviceName { get; }

    private Oidn(nint lib)
    {
        _lib = lib;
        T Fn<T>(string name) where T : unmanaged
        {
            var p = NativeLibrary.GetExport(lib, name);
            return *(T*)&p;
        }
        var newDevice = Fn<nint>("oidnNewDevice");
        var commitDevice = Fn<nint>("oidnCommitDevice");
        var getInt = Fn<nint>("oidnGetDeviceInt");
        var physical = Fn<nint>("oidnGetPhysicalDeviceString");
        _newFilter = (delegate* unmanaged<nint, nint, nint>)Fn<nint>("oidnNewFilter");
        _newBuffer = (delegate* unmanaged<nint, nuint, int, nint>)Fn<nint>("oidnNewBufferWithStorage");
        _writeAsync = (delegate* unmanaged<nint, nuint, nuint, void*, void>)Fn<nint>("oidnWriteBufferAsync");
        _read = (delegate* unmanaged<nint, nuint, nuint, void*, void>)Fn<nint>("oidnReadBuffer");
        _releaseBuffer = (delegate* unmanaged<nint, void>)Fn<nint>("oidnReleaseBuffer");
        _releaseFilter = (delegate* unmanaged<nint, void>)Fn<nint>("oidnReleaseFilter");
        _commitFilter = (delegate* unmanaged<nint, void>)Fn<nint>("oidnCommitFilter");
        _executeAsync = (delegate* unmanaged<nint, void>)Fn<nint>("oidnExecuteFilterAsync");
        _sync = (delegate* unmanaged<nint, void>)Fn<nint>("oidnSyncDevice");
        _releaseDevice = (delegate* unmanaged<nint, void>)Fn<nint>("oidnReleaseDevice");
        _setImage = (delegate* unmanaged<nint, nint, nint, int, nuint, nuint, nuint, nuint, nuint, void>)Fn<nint>("oidnSetFilterImage");
        _setBool = (delegate* unmanaged<nint, nint, byte, void>)Fn<nint>("oidnSetFilterBool");
        _setInt = (delegate* unmanaged<nint, nint, int, void>)Fn<nint>("oidnSetFilterInt");
        _error = (delegate* unmanaged<nint, nint*, int>)Fn<nint>("oidnGetDeviceError");
        var name = Marshal.StringToHGlobalAnsi("name");
        var type = Marshal.StringToHGlobalAnsi("type");
        try
        {
            DeviceName = Marshal.PtrToStringAnsi(((delegate* unmanaged<int, nint, nint>)physical)(0, name)) ?? "";
            _device = ((delegate* unmanaged<int, nint>)newDevice)(0);
            ((delegate* unmanaged<nint, void>)commitDevice)(_device);
            DeviceType = ((delegate* unmanaged<nint, nint, int>)getInt)(_device, type);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(type);
        }
        Check();
    }

    /// <summary>OIDN from <paramref name="folder"/> (the game's bin/win64), or null when it is not there.</summary>
    public static Oidn? Open(string folder)
    {
        var path = Path.Combine(folder, OperatingSystem.IsWindows() ? "OpenImageDenoise.dll" : "libOpenImageDenoise.so");
        return File.Exists(path) && NativeLibrary.TryLoad(path, out var lib) ? new Oidn(lib) : null;
    }

    private void Check()
    {
        nint message;
        if (_error(_device, &message) != 0)
            throw new InvalidOperationException("OIDN: " + Marshal.PtrToStringAnsi(message));
    }

    /// <summary>
    /// One RTLightmap run as vrad3 makes it: two device buffers of width x height float3, the filter, the
    /// color written, color and output images, directional, maxMemoryMB 6000, commit, execute, sync, read.
    /// </summary>
    public void RtLightmap(float[] color, float[] output, int width, int height, bool directional)
    {
        var bytes = (nuint)(width * height * 12);
        var input = _newBuffer(_device, bytes, 2);
        var result = _newBuffer(_device, bytes, 2);
        var filterType = Marshal.StringToHGlobalAnsi("RTLightmap");
        var names = new[] { "color", "output", "directional", "maxMemoryMB" }.Select(Marshal.StringToHGlobalAnsi).ToArray();
        try
        {
            var filter = _newFilter(_device, filterType);
            fixed (float* src = color)
                _writeAsync(input, 0, bytes, src);
            _setImage(filter, names[0], input, 3, (nuint)width, (nuint)height, 0, 0, 0);
            _setImage(filter, names[1], result, 3, (nuint)width, (nuint)height, 0, 0, 0);
            _setBool(filter, names[2], directional ? (byte)1 : (byte)0);
            _setInt(filter, names[3], 6000);
            _commitFilter(filter);
            _executeAsync(filter);
            Check();
            _sync(_device);
            fixed (float* dst = output)
                _read(result, 0, bytes, dst);
            _releaseFilter(filter);
        }
        finally
        {
            _releaseBuffer(input);
            _releaseBuffer(result);
            Marshal.FreeHGlobal(filterType);
            foreach (var n in names)
                Marshal.FreeHGlobal(n);
        }
    }

    public void Dispose()
    {
        if (_device != 0)
            _releaseDevice(_device);
    }
}
