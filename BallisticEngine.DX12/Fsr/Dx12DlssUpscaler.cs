using System;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// NVIDIA DLSS (Super Resolution) temporal upscaler via NGX direct (nvngx.dll). Mirrors Dx12FsrUpscaler /
// Dx12XessUpscaler: init once (NGX init + capability probe + DLSS feature create at a fixed render/display res),
// dispatch per frame (internal-res HDR color + depth + motion + jitter -> output-res HDR). DLSS does its own
// temporal AA, so like FSR it REPLACES TAA when active.
//
// === HARDWARE NOTE ===
// DLSS REQUIRES an NVIDIA RTX GPU and the NVIDIA driver-installed nvngx.dll core loader (we ship only the
// nvngx_dlss.dll model snippet, not the core). On the project's AMD RX 9070 XT test box nvngx.dll is absent, so
// the FIRST NGX P/Invoke throws DllNotFoundException → TryCreate returns null → the renderer falls back to the
// FSR equivalent. THIS PATH IS THEREFORE CODE-COMPLETE BUT UNVERIFIED ON HARDWARE (no NVIDIA GPU available).
// The NGX call sequence, parameter keys, jitter/MV conventions, and HDR flags follow the NGX 1.5 programming
// guide and nvsdk_ngx_helpers.h, but the exact x64 ABI of the exported C accessors could only be confirmed on
// an RTX machine.
//
// Jitter convention (NGX): InJitterOffsetX/Y is the sub-pixel offset in INPUT (render) pixel space — the SAME
// signed value applied to the projection (UE/Unity convention), NOT negated like FSR. currentJitter is already
// in pixels. MV scale = render dims (our motion is UV-space prevUV-currUV).
public sealed class Dx12DlssUpscaler : IDisposable {
    IntPtr ngxParams;     // capability/eval parameters (NGX-owned, freed via DestroyParameters)
    IntPtr dlssFeature;   // the created SuperSampling feature handle
    bool ngxInited;
    bool featurePending = true;   // create the feature on the first Dispatch (needs a recording cmd list)

    public int RenderWidth { get; }
    public int RenderHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int PerfQuality { get; }
    public bool Valid { get; private set; }

    Dx12DlssUpscaler(int rw, int rh, int ow, int oh, int quality, IntPtr pars) {
        RenderWidth = rw; RenderHeight = rh; OutputWidth = ow; OutputHeight = oh; PerfQuality = quality;
        ngxParams = pars; ngxInited = true; Valid = true;
    }

    // NGX init + DLSS-availability probe. Returns null (logs once) if DLSS can't run here (non-NVIDIA GPU, no
    // driver nvngx.dll, or DLSS feature not available). The DLSS feature itself is created lazily on the first
    // Dispatch (CreateFeature needs a recording command list).
    public static Dx12DlssUpscaler TryCreate(Dx12Device dev, int renderW, int renderH, int outputW, int outputH, int perfQuality) {
        bool inited = false;
        IntPtr pars = IntPtr.Zero;
        try {
            // App id 0 is accepted for non-shipping/eval; the data path is the working dir.
            uint rc = NgxApi.NVSDK_NGX_D3D12_Init(0UL, ".", dev.Device.NativePointer, NgxApi.VersionApi);
            if (rc != NgxApi.ResultSuccess) {
                Console.WriteLine($"[DLSS] NGX init failed (0x{rc:X8}) — falling back (likely no NVIDIA GPU/driver).");
                return null;
            }
            inited = true;
            rc = NgxApi.NVSDK_NGX_D3D12_GetCapabilityParameters(out pars);
            if (rc != NgxApi.ResultSuccess || pars == IntPtr.Zero) {
                Console.WriteLine($"[DLSS] GetCapabilityParameters failed (0x{rc:X8}) — falling back.");
                NgxApi.NVSDK_NGX_D3D12_Shutdown();
                return null;
            }
            // Is the SuperSampling (DLSS) feature available on this GPU/driver?
            NgxApi.NVSDK_NGX_Parameter_GetI(pars, NgxApi.P_SuperSamplingAvailable, out int available);
            if (available == 0) {
                Console.WriteLine("[DLSS] SuperSampling not available on this GPU — falling back.");
                NgxApi.NVSDK_NGX_D3D12_DestroyParameters(pars);
                NgxApi.NVSDK_NGX_D3D12_Shutdown();
                return null;
            }
            Console.WriteLine($"[DLSS] available — feature will create at {renderW}x{renderH} -> {outputW}x{outputH} (q={perfQuality}).");
            return new Dx12DlssUpscaler(renderW, renderH, outputW, outputH, perfQuality, pars);
        } catch (DllNotFoundException) {
            Console.WriteLine("[DLSS] nvngx.dll not found (NVIDIA driver/core missing) — falling back.");
            if (inited) { try { NgxApi.NVSDK_NGX_D3D12_Shutdown(); } catch { } }
            return null;
        } catch (Exception e) {
            Console.WriteLine($"[DLSS] unavailable: {e.Message} — falling back.");
            if (inited) { try { NgxApi.NVSDK_NGX_D3D12_Shutdown(); } catch { } }
            return null;
        }
    }

    // Create the DLSS feature on `cl` (the first Dispatch). HDR color (linear pre-tonemap), low-res motion,
    // non-inverted depth, auto-exposure (DLSS meters the frame). Returns false (and disables) on failure.
    bool EnsureFeature(ID3D12GraphicsCommandList4 cl) {
        if (!featurePending) return dlssFeature != IntPtr.Zero;
        featurePending = false;
        try {
            int createFlags = NgxApi.DlssFlagIsHDR | NgxApi.DlssFlagMVLowRes | NgxApi.DlssFlagAutoExposure;
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_Width, (uint)RenderWidth);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_Height, (uint)RenderHeight);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_OutWidth, (uint)OutputWidth);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_OutHeight, (uint)OutputHeight);
            NgxApi.NVSDK_NGX_Parameter_SetI(ngxParams, NgxApi.P_PerfQualityValue, PerfQuality);
            NgxApi.NVSDK_NGX_Parameter_SetI(ngxParams, NgxApi.P_FeatureCreateFlags, createFlags);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_EnableOutputSubrects, 0);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_CreationNodeMask, 1);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_VisibilityNodeMask, 1);
            uint rc = NgxApi.NVSDK_NGX_D3D12_CreateFeature(cl.NativePointer, NgxApi.FeatureSuperSampling, ngxParams, out dlssFeature);
            if (rc != NgxApi.ResultSuccess || dlssFeature == IntPtr.Zero) {
                Console.WriteLine($"[DLSS] CreateFeature failed (0x{rc:X8}) — disabling DLSS.");
                dlssFeature = IntPtr.Zero;
                Valid = false;
                return false;
            }
            return true;
        } catch (Exception e) {
            Console.WriteLine($"[DLSS] CreateFeature threw: {e.Message} — disabling DLSS.");
            Valid = false;
            return false;
        }
    }

    // Record the upscale into `cl`. Inputs must be in NON_PIXEL_SHADER_RESOURCE (NGX reads them in compute);
    // output in UNORDERED_ACCESS (the caller transitions them). renderW/H = the internal resolution.
    public bool Dispatch(ID3D12GraphicsCommandList4 cl,
        ID3D12Resource color, ID3D12Resource depth, ID3D12Resource motion, ID3D12Resource output,
        int renderW, int renderH, float jitterX, float jitterY, bool reset) {
        if (!ngxInited) return false;
        if (!EnsureFeature(cl)) return false;
        try {
            NgxApi.NVSDK_NGX_Parameter_SetD3d12Resource(ngxParams, NgxApi.P_Color, color?.NativePointer ?? IntPtr.Zero);
            NgxApi.NVSDK_NGX_Parameter_SetD3d12Resource(ngxParams, NgxApi.P_Output, output?.NativePointer ?? IntPtr.Zero);
            NgxApi.NVSDK_NGX_Parameter_SetD3d12Resource(ngxParams, NgxApi.P_Depth, depth?.NativePointer ?? IntPtr.Zero);
            NgxApi.NVSDK_NGX_Parameter_SetD3d12Resource(ngxParams, NgxApi.P_MotionVectors, motion?.NativePointer ?? IntPtr.Zero);
            // NGX jitter = applied sub-pixel offset in render pixels (NOT negated).
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_JitterX, jitterX);
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_JitterY, jitterY);
            NgxApi.NVSDK_NGX_Parameter_SetI(ngxParams, NgxApi.P_Reset, reset ? 1 : 0);
            // UV-space motion -> pixel space via MV scale = render dims (matches FSR/XeSS).
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_MVScaleX, renderW);
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_MVScaleY, renderH);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_RenderSubrectW, (uint)renderW);
            NgxApi.NVSDK_NGX_Parameter_SetUI(ngxParams, NgxApi.P_RenderSubrectH, (uint)renderH);
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_Sharpness, 0f);
            uint rc = NgxApi.NVSDK_NGX_D3D12_EvaluateFeature(cl.NativePointer, dlssFeature, ngxParams, IntPtr.Zero);
            return rc == NgxApi.ResultSuccess;
        } catch (Exception e) {
            Console.WriteLine($"[DLSS] EvaluateFeature threw: {e.Message} — disabling DLSS.");
            Valid = false;
            return false;
        }
    }

    public void Dispose() {
        try {
            if (dlssFeature != IntPtr.Zero) { NgxApi.NVSDK_NGX_D3D12_ReleaseFeature(dlssFeature); dlssFeature = IntPtr.Zero; }
            if (ngxParams != IntPtr.Zero) { NgxApi.NVSDK_NGX_D3D12_DestroyParameters(ngxParams); ngxParams = IntPtr.Zero; }
            if (ngxInited) { NgxApi.NVSDK_NGX_D3D12_Shutdown(); ngxInited = false; }
        } catch { /* shutdown best-effort */ }
    }
}
