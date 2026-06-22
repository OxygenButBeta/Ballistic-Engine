using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12XessUpscaler : IDisposable {
    IntPtr context;
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int QualityMode { get; }
    public bool Valid => context != IntPtr.Zero;

    Dx12XessUpscaler(IntPtr ctx, int outW, int outH, int qualityMode) {
        context = ctx; OutputWidth = outW; OutputHeight = outH; QualityMode = qualityMode;
    }

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
                InitFlags = XessApi.InitFlagEnableAutoexposure,
                CreationNodeMask = 0, VisibleNodeMask = 0,
            };
            rc = XessApi.xessD3D12Init(ctx, in init);
            if (rc < XessApi.ResultSuccess) {
                Console.WriteLine($"[XeSS] Init failed (rc={rc}) — falling back.");
                XessApi.xessDestroyContext(ctx);
                return null;
            }

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

    public bool Dispatch(ID3D12GraphicsCommandList4 cl,
        ID3D12Resource color, ID3D12Resource depth, ID3D12Resource motion, ID3D12Resource output,
        int renderW, int renderH, float jitterX, float jitterY, bool reset) {
        if (context == IntPtr.Zero) return false;
        XessApi.xessSetVelocityScale(context, renderW, renderH);
        var p = new XessApi.D3D12ExecuteParams {
            ColorTexture = color?.NativePointer ?? IntPtr.Zero,
            VelocityTexture = motion?.NativePointer ?? IntPtr.Zero,
            DepthTexture = depth?.NativePointer ?? IntPtr.Zero,
            ExposureScaleTexture = IntPtr.Zero,
            ResponsivePixelMaskTexture = IntPtr.Zero,
            OutputTexture = output?.NativePointer ?? IntPtr.Zero,
            JitterOffsetX = jitterX,
            JitterOffsetY = jitterY,
            ExposureScale = 1f,
            ResetHistory = (uint)(reset ? 1 : 0),
            InputWidth = (uint)renderW,
            InputHeight = (uint)renderH,
        };
        int rc = XessApi.xessD3D12Execute(context, cl.NativePointer, in p);
        return rc >= XessApi.ResultSuccess;
    }

    public void Dispose() {
        if (context != IntPtr.Zero) { XessApi.xessDestroyContext(context); context = IntPtr.Zero; }
    }
}
