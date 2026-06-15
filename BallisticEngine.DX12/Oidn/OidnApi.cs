using System;
using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// P/Invoke bindings for Intel Open Image Denoise (OIDN) 2.5.0 C API, exported by OpenImageDenoise.dll
// (which loads OpenImageDenoise_core.dll + a device DLL: _device_cpu / _device_hip). This is the engine's
// ONE denoiser per the standing directive — OIDN for ALL denoise (SSGI now, RT GI/reflections/shadows
// later). Header: native/oidn/include/OpenImageDenoise/oidn.h. Handles are opaque pointers (IntPtr);
// strings are ASCII const char*; C bools are 1 byte.
internal static class OidnApi {
    const string Dll = "OpenImageDenoise.dll";

    public enum DeviceType { Default = 0, Cpu = 1, Sycl = 2, Cuda = 3, Hip = 4, Metal = 5 }
    public enum Error { None = 0, Unknown = 1, InvalidArgument = 2, InvalidOperation = 3, OutOfMemory = 4, UnsupportedHardware = 5, Cancelled = 6 }
    public enum Format { Undefined = 0, Float = 1, Float2 = 2, Float3 = 3, Float4 = 4, Half = 257, Half2 = 258, Half3 = 259, Half4 = 260 }
    public enum Quality { Default = 0, Fast = 4, Balanced = 5, High = 6 }

    // --- Device ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oidnNewDevice(DeviceType type);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnCommitDevice(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnReleaseDevice(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern Error oidnGetDeviceError(IntPtr device, out IntPtr outMessage);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnSetDeviceInt(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string name, int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int oidnGetNumPhysicalDevices();

    // --- Buffer ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oidnNewBuffer(IntPtr device, nuint byteSize);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnReleaseBuffer(IntPtr buffer);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oidnGetBufferData(IntPtr buffer);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnReadBuffer(IntPtr buffer, nuint byteOffset, nuint byteSize, IntPtr dstHostPtr);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnWriteBuffer(IntPtr buffer, nuint byteOffset, nuint byteSize, IntPtr srcHostPtr);

    // --- Filter ---
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oidnNewFilter(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string type);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnReleaseFilter(IntPtr filter);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnSetFilterImage(IntPtr filter, [MarshalAs(UnmanagedType.LPStr)] string name,
        IntPtr buffer, Format format, nuint width, nuint height, nuint byteOffset, nuint pixelByteStride, nuint rowByteStride);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnSetFilterBool(IntPtr filter, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnSetFilterInt(IntPtr filter, [MarshalAs(UnmanagedType.LPStr)] string name, int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnSetFilterFloat(IntPtr filter, [MarshalAs(UnmanagedType.LPStr)] string name, float value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnCommitFilter(IntPtr filter);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void oidnExecuteFilter(IntPtr filter);
}
