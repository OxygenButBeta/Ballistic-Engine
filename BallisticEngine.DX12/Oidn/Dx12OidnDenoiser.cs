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
    // True when the device is a HIP GPU that can import D3D12 shared buffers — the zero-copy path is then
    // available (ImportSharedBuffer + ExecuteShared, no CPU readback). False → use the DenoiseHdr readback.
    public bool SharedCapable { get; private set; }

    // Zero-copy state: an OIDN buffer aliasing a D3D12 shared resource + an in-place HDR RT filter over it.
    IntPtr sharedBuf;
    IntPtr sharedFilter;
    // P6.1 GUIDED denoise: OIDN buffers aliasing the D3D12 shared ALBEDO/NORMAL aux resources. Bound to the
    // shared filter as "albedo"/"normal" guide images so the denoise preserves edges. Imported separately
    // (ImportAuxBuffers) and the filter is REBUILT to reference them. Null when denoising unguided.
    IntPtr sharedAlbedoBuf, sharedNormalBuf;
    int auxW, auxH, auxRowPitch;

    // adapterLuid (8 bytes from Dx12Device.AdapterLuidBytes): prefer a LUID-matched device so OIDN's HIP
    // device is the SAME GPU as the D3D12 adapter (required for buffer sharing). Falls back to the Default
    // device (still works for the CPU-readback path) if the LUID device fails or isn't share-capable.
    public Dx12OidnDenoiser(byte[] adapterLuid = null) {
        if (adapterLuid != null && adapterLuid.Length == 8) {
            var gch = GCHandle.Alloc(adapterLuid, GCHandleType.Pinned);
            try { device = OidnApi.oidnNewDeviceByLUID(gch.AddrOfPinnedObject()); }
            finally { gch.Free(); }
            if (device != IntPtr.Zero) {
                OidnApi.oidnCommitDevice(device);
                if (!CheckError()) { OidnApi.oidnReleaseDevice(device); device = IntPtr.Zero; }
            }
            if (device != IntPtr.Zero) {
                int type = OidnApi.oidnGetDeviceInt(device, "type");
                int ext = OidnApi.oidnGetDeviceInt(device, "externalMemoryTypes");
                SharedCapable = type == (int)OidnApi.DeviceType.Hip
                    && (ext & OidnApi.OIDN_EXTERNAL_MEMORY_TYPE_FLAG_D3D12_RESOURCE) != 0;
                Console.WriteLine($"[OIDN] LUID device type={type} externalMemoryTypes=0x{ext:X} sharedCapable={SharedCapable}");
            }
        }
        if (device == IntPtr.Zero) {
            device = OidnApi.oidnNewDevice(OidnApi.DeviceType.Default);
            if (device == IntPtr.Zero) { CheckError(); return; }
            OidnApi.oidnCommitDevice(device);
            if (!CheckError()) { OidnApi.oidnReleaseDevice(device); device = IntPtr.Zero; }
        }
    }

    // Import a D3D12 shared-resource NT handle as an OIDN buffer and build an IN-PLACE HDR RT filter over it.
    // The D3D12 buffer holds tightly-packed FLOAT4 pixels (16 bytes, row-major, rowByteStride = W*16); OIDN
    // reads/writes RGB via FLOAT3 + pixelByteStride 16 (the .a is untouched) — FLOAT precision matches the
    // CPU-readback path's denoise quality (a HALF denoise was visibly worse). Build once; reused every frame.
    // Returns false (→ caller falls back to DenoiseHdr readback) if the import/filter fails.
    public bool ImportSharedBuffer(IntPtr d3d12SharedHandle, ulong byteSize, int w, int h, int rowPitchBytes) {
        if (device == IntPtr.Zero || !SharedCapable) return false;
        ReleaseShared();
        sharedBuf = OidnApi.oidnNewSharedBufferFromWin32Handle(device,
            OidnApi.OIDN_EXTERNAL_MEMORY_TYPE_FLAG_D3D12_RESOURCE, d3d12SharedHandle, IntPtr.Zero, (nuint)byteSize);
        if (sharedBuf == IntPtr.Zero || !CheckError()) { ReleaseShared(); return false; }
        auxW = w; auxH = h; auxRowPitch = rowPitchBytes;
        return RebuildSharedFilter();
    }

    // P6.1: import the shared ALBEDO + NORMAL aux buffers and REBUILD the filter to use them as guide images.
    // Call AFTER ImportSharedBuffer. IDEMPOTENT — re-imports + rebuilds the filter ONLY on the first call (or
    // after a release/resize); the filter commit JITs the denoise network (~tens of ms) so it must NOT happen
    // every frame. Returns false (→ caller falls back to UNGUIDED, still valid) if either import fails. The aux
    // buffers are the same half-res + float4 stride as the color buffer.
    public bool ImportAuxBuffers(IntPtr albedoHandle, IntPtr normalHandle, ulong byteSize) {
        if (device == IntPtr.Zero || !SharedCapable || sharedBuf == IntPtr.Zero) return false;
        if (SharedGuided) return true;   // already imported + filter rebuilt with guides — per-frame no-op
        sharedAlbedoBuf = OidnApi.oidnNewSharedBufferFromWin32Handle(device,
            OidnApi.OIDN_EXTERNAL_MEMORY_TYPE_FLAG_D3D12_RESOURCE, albedoHandle, IntPtr.Zero, (nuint)byteSize);
        sharedNormalBuf = OidnApi.oidnNewSharedBufferFromWin32Handle(device,
            OidnApi.OIDN_EXTERNAL_MEMORY_TYPE_FLAG_D3D12_RESOURCE, normalHandle, IntPtr.Zero, (nuint)byteSize);
        if (sharedAlbedoBuf == IntPtr.Zero || sharedNormalBuf == IntPtr.Zero || !CheckError()) { ReleaseAux(); RebuildSharedFilter(); return false; }
        return RebuildSharedFilter();   // rebuild WITH the guides (once)
    }

    // (Re)build the in-place shared HDR RT filter over the current color buffer, binding albedo/normal guides
    // when they're imported. OIDN reads each as FLOAT3 over the same half-res float4-strided buffer.
    bool RebuildSharedFilter() {
        if (sharedFilter != IntPtr.Zero) { OidnApi.oidnReleaseFilter(sharedFilter); sharedFilter = IntPtr.Zero; }
        sharedFilter = OidnApi.oidnNewFilter(device, "RT");
        OidnApi.oidnSetFilterImage(sharedFilter, "color", sharedBuf, OidnApi.Format.Float3, (nuint)auxW, (nuint)auxH, 0, 16, (nuint)auxRowPitch);
        if (sharedAlbedoBuf != IntPtr.Zero)
            OidnApi.oidnSetFilterImage(sharedFilter, "albedo", sharedAlbedoBuf, OidnApi.Format.Float3, (nuint)auxW, (nuint)auxH, 0, 16, (nuint)auxRowPitch);
        if (sharedNormalBuf != IntPtr.Zero)
            OidnApi.oidnSetFilterImage(sharedFilter, "normal", sharedNormalBuf, OidnApi.Format.Float3, (nuint)auxW, (nuint)auxH, 0, 16, (nuint)auxRowPitch);
        OidnApi.oidnSetFilterImage(sharedFilter, "output", sharedBuf, OidnApi.Format.Float3, (nuint)auxW, (nuint)auxH, 0, 16, (nuint)auxRowPitch);
        OidnApi.oidnSetFilterBool(sharedFilter, "hdr", true);
        OidnApi.oidnSetFilterInt(sharedFilter, "quality", (int)OidnApi.Quality.Balanced);
        OidnApi.oidnCommitFilter(sharedFilter);
        if (CheckError()) return true;
        // Commit failed. If we HAD guides, the failure may be the guide setup → DROP the guides and retry an
        // UNGUIDED commit ONCE (graceful degradation, audit fix): keep the COLOR zero-copy path alive rather
        // than tearing it down for the whole session. Only if the unguided rebuild ALSO fails do we give up
        // (ReleaseShared → the caller falls back to CPU readback). Without this, a guide-commit failure killed
        // the color filter (ReleaseShared) even though the color path alone was fine.
        bool hadGuides = sharedAlbedoBuf != IntPtr.Zero || sharedNormalBuf != IntPtr.Zero;
        if (hadGuides) {
            ReleaseAux();
            return RebuildSharedFilter();   // recurse once, now guide-free (hadGuides is false next time)
        }
        ReleaseShared();
        return false;
    }

    public bool SharedGuided => sharedAlbedoBuf != IntPtr.Zero && sharedNormalBuf != IntPtr.Zero;

    // Execute the in-place shared-buffer denoise on the GPU (no CPU round-trip). The D3D12 copy that filled
    // the shared buffer must already be complete — the caller's ExecuteSync blocks on the GPU, so it is.
    // oidnSyncDevice waits for the HIP denoise to finish before the caller copies the buffer back out.
    public bool ExecuteShared() {
        if (sharedFilter == IntPtr.Zero) return false;
        OidnApi.oidnExecuteFilter(sharedFilter);
        OidnApi.oidnSyncDevice(device);
        return CheckError();
    }

    // Release the imported shared buffer + filter (caller must do this BEFORE disposing/closing the backing
    // D3D12 resource + handle, e.g. on a resolution change, or the OIDN alias dangles).
    public void ReleaseSharedBuffer() => ReleaseShared();

    void ReleaseShared() {
        if (sharedFilter != IntPtr.Zero) { OidnApi.oidnReleaseFilter(sharedFilter); sharedFilter = IntPtr.Zero; }
        if (sharedBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(sharedBuf); sharedBuf = IntPtr.Zero; }
        ReleaseAux();
    }

    void ReleaseAux() {
        if (sharedAlbedoBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(sharedAlbedoBuf); sharedAlbedoBuf = IntPtr.Zero; }
        if (sharedNormalBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(sharedNormalBuf); sharedNormalBuf = IntPtr.Zero; }
    }

    // Cached readback-path resources: an OIDN filter is EXPENSIVE to commit (it JIT-allocates the denoise
    // network — ~tens of ms), so we build the buffers + filter ONCE and reuse them every frame, only
    // rebuilding when the image size or AOV set changes. This was the dominant per-frame OIDN cost (measured
    // ~54ms of a 62ms readback frame); caching it makes the correct float readback path ~8ms.
    IntPtr rbColorBuf, rbOutBuf, rbAlbBuf, rbNrmBuf, rbFilter;
    int rbW, rbH; bool rbHasAlb, rbHasNrm;

    // Denoise an HDR FLOAT3 image (host arrays, length = w*h*3). albedo/normal are optional AOV guides
    // (pass null to skip). Output must be pre-sized w*h*3. Returns false on any OIDN error. Reuses a cached
    // filter across calls (rebuilt only on a size/AOV change).
    public bool DenoiseHdr(float[] color, float[] albedo, float[] normal, float[] output, int w, int h) {
        if (device == IntPtr.Zero) return false;
        nuint bytes = (nuint)((long)w * h * 3 * sizeof(float));
        bool hasAlb = albedo != null, hasNrm = normal != null;
        if (rbFilter == IntPtr.Zero || rbW != w || rbH != h || rbHasAlb != hasAlb || rbHasNrm != hasNrm) {
            ReleaseReadback();
            rbColorBuf = OidnApi.oidnNewBuffer(device, bytes);
            rbOutBuf = OidnApi.oidnNewBuffer(device, bytes);
            rbAlbBuf = hasAlb ? OidnApi.oidnNewBuffer(device, bytes) : IntPtr.Zero;
            rbNrmBuf = hasNrm ? OidnApi.oidnNewBuffer(device, bytes) : IntPtr.Zero;
            rbFilter = OidnApi.oidnNewFilter(device, "RT");
            OidnApi.oidnSetFilterImage(rbFilter, "color", rbColorBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
            if (hasAlb) OidnApi.oidnSetFilterImage(rbFilter, "albedo", rbAlbBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
            if (hasNrm) OidnApi.oidnSetFilterImage(rbFilter, "normal", rbNrmBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
            OidnApi.oidnSetFilterImage(rbFilter, "output", rbOutBuf, OidnApi.Format.Float3, (nuint)w, (nuint)h, 0, 0, 0);
            OidnApi.oidnSetFilterBool(rbFilter, "hdr", true);
            OidnApi.oidnSetFilterInt(rbFilter, "quality", (int)OidnApi.Quality.Balanced);
            OidnApi.oidnCommitFilter(rbFilter);
            rbW = w; rbH = h; rbHasAlb = hasAlb; rbHasNrm = hasNrm;
            if (!CheckError()) { ReleaseReadback(); return false; }
        }
        Write(rbColorBuf, color, bytes);
        if (hasAlb) Write(rbAlbBuf, albedo, bytes);
        if (hasNrm) Write(rbNrmBuf, normal, bytes);
        OidnApi.oidnExecuteFilter(rbFilter);
        bool ok = CheckError();
        Read(rbOutBuf, output, bytes);
        return ok;
    }

    void ReleaseReadback() {
        if (rbFilter != IntPtr.Zero) { OidnApi.oidnReleaseFilter(rbFilter); rbFilter = IntPtr.Zero; }
        if (rbColorBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(rbColorBuf); rbColorBuf = IntPtr.Zero; }
        if (rbOutBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(rbOutBuf); rbOutBuf = IntPtr.Zero; }
        if (rbAlbBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(rbAlbBuf); rbAlbBuf = IntPtr.Zero; }
        if (rbNrmBuf != IntPtr.Zero) { OidnApi.oidnReleaseBuffer(rbNrmBuf); rbNrmBuf = IntPtr.Zero; }
        rbW = rbH = 0;
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
        ReleaseShared();
        ReleaseReadback();
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
