using System;
using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// Intel Open Image Denoise wrapper — the engine's ONE denoiser (standing directive: OIDN for ALL denoise).
// Owns an OIDN device (auto-picks the best: HIP on the RX 9070 XT, else CPU). The first integration uses
// host float buffers (the caller reads a noisy GPU signal back to CPU, denoises, uploads the result); a
// zero-copy D3D12<->HIP shared-buffer path (oidnNewSharedBufferFromWin32Handle + D3D12_RESOURCE) is a
// later perf optimization. Used by SSGI now, and by the DXR GI/reflection/shadow denoise later.
public sealed class Dx12OidnDenoiser : IDisposable {
    IntPtr device;
    public bool Valid => device != IntPtr.Zero;

    public Dx12OidnDenoiser() {
        device = OidnApi.oidnNewDevice(OidnApi.DeviceType.Default);
        if (device == IntPtr.Zero) { CheckError(); return; }
        OidnApi.oidnCommitDevice(device);
        if (!CheckError()) { OidnApi.oidnReleaseDevice(device); device = IntPtr.Zero; }
    }

    // Denoise an HDR FLOAT3 image (host arrays, length = w*h*3). albedo/normal are optional AOV guides
    // (pass null to skip). Output must be pre-sized w*h*3. Returns false on any OIDN error.
    public bool DenoiseHdr(float[] color, float[] albedo, float[] normal, float[] output, int w, int h) {
        if (device == IntPtr.Zero) return false;
        nuint bytes = (nuint)((long)w * h * 3 * sizeof(float));
        IntPtr colorBuf = OidnApi.oidnNewBuffer(device, bytes);
        IntPtr outBuf = OidnApi.oidnNewBuffer(device, bytes);
        IntPtr albBuf = albedo != null ? OidnApi.oidnNewBuffer(device, bytes) : IntPtr.Zero;
        IntPtr nrmBuf = normal != null ? OidnApi.oidnNewBuffer(device, bytes) : IntPtr.Zero;
        Write(colorBuf, color, bytes);
        if (albBuf != IntPtr.Zero) Write(albBuf, albedo, bytes);
        if (nrmBuf != IntPtr.Zero) Write(nrmBuf, normal, bytes);

        IntPtr filter = OidnApi.oidnNewFilter(device, "RT");
        OidnApi.oidnSetFilterImage(filter, "color", colorBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
        if (albBuf != IntPtr.Zero) OidnApi.oidnSetFilterImage(filter, "albedo", albBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
        if (nrmBuf != IntPtr.Zero) OidnApi.oidnSetFilterImage(filter, "normal", nrmBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
        OidnApi.oidnSetFilterImage(filter, "output", outBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
        OidnApi.oidnSetFilterBool(filter, "hdr", true);
        OidnApi.oidnSetFilterInt(filter, "quality", (int)OidnApi.Quality.Balanced);
        OidnApi.oidnCommitFilter(filter);
        OidnApi.oidnExecuteFilter(filter);
        bool ok = CheckError();
        Read(outBuf, output, bytes);

        OidnApi.oidnReleaseFilter(filter);
        OidnApi.oidnReleaseBuffer(colorBuf);
        OidnApi.oidnReleaseBuffer(outBuf);
        if (albBuf != IntPtr.Zero) OidnApi.oidnReleaseBuffer(albBuf);
        if (nrmBuf != IntPtr.Zero) OidnApi.oidnReleaseBuffer(nrmBuf);
        return ok;
    }

    static void Write(IntPtr buf, float[] data, nuint bytes) {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { OidnApi.oidnWriteBuffer(buf, 0, bytes, h.AddrOfPinnedObject()); } finally { h.Free(); }
    }
    static void Read(IntPtr buf, float[] data, nuint bytes) {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { OidnApi.oidnReadBuffer(buf, 0, bytes, h.AddrOfPinnedObject()); } finally { h.Free(); }
    }

    bool CheckError() {
        OidnApi.Error e = OidnApi.oidnGetDeviceError(device, out IntPtr msg);
        if (e != OidnApi.Error.None) {
            string m = msg != IntPtr.Zero ? Marshal.PtrToStringAnsi(msg) : "(no message)";
            Console.WriteLine($"[OIDN] error {e}: {m}");
            return false;
        }
        return true;
    }

    public void Dispose() {
        if (device != IntPtr.Zero) { OidnApi.oidnReleaseDevice(device); device = IntPtr.Zero; }
    }

    // Self-test door (BALLISTIC_DX12_OIDN_TEST=1): create the device, denoise a synthetic noisy HDR image,
    // and verify the high-frequency noise dropped. Proves the P/Invoke ABI + DLL deployment + the denoiser
    // works on this GPU before SSGI/DXR use it.
    public static bool SelfTest() {
        try {
            Console.WriteLine($"[OidnTest] physical devices: {OidnApi.oidnGetNumPhysicalDevices()}");
            using var d = new Dx12OidnDenoiser();
            if (!d.Valid) { Console.WriteLine("[OidnTest] device create FAILED"); return false; }
            const int w = 256, h = 256;
            var color = new float[w * h * 3];
            var outp = new float[w * h * 3];
            uint seed = 12345;
            float Rand() { seed = seed * 1664525 + 1013904223; return (seed >> 8) / 16777216f; }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    int i = (y * w + x) * 3;
                    float g = x / (float)w;                 // smooth gradient signal
                    float nz = (Rand() - 0.5f) * 0.6f;      // per-pixel noise to remove
                    color[i] = MathF.Max(0, g + nz); color[i + 1] = MathF.Max(0, g + nz); color[i + 2] = MathF.Max(0, 0.5f + nz);
                }
            double hfIn = HfEnergy(color, w, h);
            bool ok = d.DenoiseHdr(color, null, null, outp, w, h);
            double hfOut = HfEnergy(outp, w, h);
            Console.WriteLine($"[OidnTest] denoise ok={ok}, hf-noise {hfIn:F4} -> {hfOut:F4} ({(hfOut < hfIn ? "REDUCED" : "not reduced")})");
            return ok && hfOut < hfIn * 0.6;   // a real denoise cuts adjacent-pixel noise substantially
        } catch (Exception e) {
            Console.WriteLine($"[OidnTest] FAILED: {e.Message}");
            return false;
        }
    }

    // Mean absolute difference between horizontally-adjacent pixels = a high-frequency-noise proxy.
    static double HfEnergy(float[] img, int w, int h) {
        double s = 0; long n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 1; x < w; x++) {
                int i = (y * w + x) * 3, j = (y * w + x - 1) * 3;
                s += Math.Abs(img[i] - img[j]); n++;
            }
        return n > 0 ? s / n : 0;
    }
}
