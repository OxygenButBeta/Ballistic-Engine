using System;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// Intel XeSS temporal upscaler. Mirrors Dx12FsrUpscaler: one context created for a fixed output (display)
// resolution + quality mode; per frame it Execute()s the upscale (internal-res HDR color + depth + motion +
// jitter -> output-res HDR). XeSS does its own temporal AA, so like FSR it REPLACES TAA when active.
//
// Init can fail on a pre-SM6.4 GPU (xessD3D12CreateContext → UNSUPPORTED_DEVICE) or if libxess.dll is absent;
// the static TryCreate factory swallows that and returns null so the caller falls back to FSR. The XeSS render
// resolution is computed by the engine's shared ratio table (Dx12Upscaler.RenderResolutionFor) BEFORE creating
// the context — that ratio is then handed to the context as the quality mode, and Execute is fed the matching
// inputWidth/Height. (XeSS's own xessGetInputResolution returns the same ratios; we keep the engine table as the
// single source of truth so all three upscalers derive identical internal resolutions.)
//
// Jitter convention: XeSS jitter offset is in INPUT-PIXEL space, range [-0.5,0.5], SAME sign as the projection
// jitter (UE/Unity convention: it is the sub-pixel offset that was applied). The engine's currentJitter is in
// pixels already; we pass it straight through (NOT negated like FSR — FSR's jitterOffset is the negated value).
public sealed class Dx12XessUpscaler : IDisposable {
    IntPtr context;
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int QualityMode { get; }
    public bool Valid => context != IntPtr.Zero;

    Dx12XessUpscaler(IntPtr ctx, int outW, int outH, int qualityMode) {
        context = ctx; OutputWidth = outW; OutputHeight = outH; QualityMode = qualityMode;
    }

    // Create + init a XeSS context for the given display size and quality mode. Returns null (logs once) if XeSS
    // is unavailable on this GPU / the DLL can't load. HDR (linear, pre-tonemap) color + auto-exposure, standard
    // (non-inverted) depth, low-res (input-resolution) motion vectors in UV→pixel space (we set MV scale at
    // dispatch via the velocity scale to render dims).
    public static Dx12XessUpscaler TryCreate(Dx12Device dev, int outputW, int outputH, int xessQuality) {
        try {
            int rc = XessApi.xessD3D12CreateContext(dev.Device.NativePointer, out IntPtr ctx);
            if (rc != XessApi.ResultSuccess || ctx == IntPtr.Zero) {
                Console.WriteLine($"[XeSS] CreateContext failed (rc={rc}) — falling back.");
                return null;
            }
            var init = new XessApi.D3D12InitParams {
                OutputResolution = new XessApi.Xess2D { X = (uint)outputW, Y = (uint)outputH },
                QualitySetting = xessQuality,
                // HDR linear color + auto-exposure (XeSS meters the frame, like FSR). Low-res MV (default, no
                // HIGH_RES_MV) → motion is at INPUT resolution. JITTERED_MV: our motion buffer is computed from
                // UNJITTERED reprojection, so it is NOT jittered — leave the flag off.
                InitFlags = XessApi.InitFlagEnableAutoexposure,
                CreationNodeMask = 0, VisibleNodeMask = 0,
            };
            rc = XessApi.xessD3D12Init(ctx, in init);
            if (rc < XessApi.ResultSuccess) {   // <0 = error (warnings >0 are OK)
                Console.WriteLine($"[XeSS] Init failed (rc={rc}) — falling back.");
                XessApi.xessDestroyContext(ctx);
                return null;
            }
            // Our motion buffer is UV-space delta (prevUV-currUV); XeSS multiplies the sampled velocity by this
            // scale to reach pixel space. Set it to the RENDER (input) dims — set per-create here (constant for
            // the context's fixed input size). It is re-affirmed each Execute via the same value if needed.
            XessApi.xessGetVersion(out var v);
            Console.WriteLine($"[XeSS] context ready (libxess {v.Major}.{v.Minor}.{v.Patch}, quality={xessQuality}, {outputW}x{outputH}).");
            return new Dx12XessUpscaler(ctx, outputW, outputH, xessQuality);
        } catch (DllNotFoundException) {
            Console.WriteLine("[XeSS] libxess.dll not found — falling back.");
            return null;
        } catch (Exception e) {
            Console.WriteLine($"[XeSS] unavailable: {e.Message} — falling back.");
            return null;
        }
    }

    // Record the upscale into `cl`. Inputs MUST already be in NON_PIXEL_SHADER_RESOURCE; output in
    // UNORDERED_ACCESS (XeSS D3D12 contract — the caller transitions them). renderW/H = the internal resolution.
    public bool Dispatch(ID3D12GraphicsCommandList4 cl,
        ID3D12Resource color, ID3D12Resource depth, ID3D12Resource motion, ID3D12Resource output,
        int renderW, int renderH, float jitterX, float jitterY, bool reset) {
        if (context == IntPtr.Zero) return false;
        // Our motion is UV delta; convert to pixel space for XeSS by scaling velocity to render dims. XeSS reads
        // velocity * velocityScale; set scale = render dims (matches FSR's MotionVectorScale).
        XessApi.xessSetVelocityScale(context, renderW, renderH);
        var p = new XessApi.D3D12ExecuteParams {
            ColorTexture = color?.NativePointer ?? IntPtr.Zero,
            VelocityTexture = motion?.NativePointer ?? IntPtr.Zero,
            DepthTexture = depth?.NativePointer ?? IntPtr.Zero,
            ExposureScaleTexture = IntPtr.Zero,
            ResponsivePixelMaskTexture = IntPtr.Zero,
            OutputTexture = output?.NativePointer ?? IntPtr.Zero,
            // XeSS jitter is the applied sub-pixel offset in pixels, [-0.5,0.5], same sign as the projection
            // jitter (NOT negated). currentJitter is in pixels.
            JitterOffsetX = jitterX,
            JitterOffsetY = jitterY,
            ExposureScale = 1f,
            ResetHistory = (uint)(reset ? 1 : 0),
            InputWidth = (uint)renderW,
            InputHeight = (uint)renderH,
            // all *Base fields default to (0,0); DescriptorHeap null (XeSS owns its heap).
        };
        int rc = XessApi.xessD3D12Execute(context, cl.NativePointer, in p);
        return rc >= XessApi.ResultSuccess;   // <0 = error
    }

    public void Dispose() {
        if (context != IntPtr.Zero) { XessApi.xessDestroyContext(context); context = IntPtr.Zero; }
    }
}
