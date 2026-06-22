using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12DlssUpscaler : IDisposable {
    IntPtr ngxParams;
    IntPtr dlssFeature;
    bool ngxInited;
    bool featurePending = true;

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

    public static Dx12DlssUpscaler TryCreate(Dx12Device dev, int renderW, int renderH, int outputW, int outputH, int perfQuality) {
        bool inited = false;
        IntPtr pars = IntPtr.Zero;
        try {
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
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_JitterX, jitterX);
            NgxApi.NVSDK_NGX_Parameter_SetF(ngxParams, NgxApi.P_JitterY, jitterY);
            NgxApi.NVSDK_NGX_Parameter_SetI(ngxParams, NgxApi.P_Reset, reset ? 1 : 0);
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
        } catch {
        }
    }
}
