using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12FsrUpscaler : IDisposable {
    IntPtr context;
    IntPtr createUpscaleMem;
    IntPtr createBackendMem;

    public int MaxRenderWidth { get; }
    public int MaxRenderHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public bool Valid => context != IntPtr.Zero;

    public Dx12FsrUpscaler(Dx12Device dev, int maxRenderW, int maxRenderH, int outputW, int outputH) {
        MaxRenderWidth = maxRenderW; MaxRenderHeight = maxRenderH;
        OutputWidth = outputW; OutputHeight = outputH;

        createBackendMem = Marshal.AllocHGlobal(Marshal.SizeOf<FfxApi.CreateBackendDX12Desc>());
        createUpscaleMem = Marshal.AllocHGlobal(Marshal.SizeOf<FfxApi.CreateContextDescUpscale>());

        var backend = new FfxApi.CreateBackendDX12Desc {
            Header = new FfxApi.Header { Type = FfxApi.DescTypeCreateBackendDx12, PNext = IntPtr.Zero },
            Device = dev.Device.NativePointer,
        };
        Marshal.StructureToPtr(backend, createBackendMem, false);

        var create = new FfxApi.CreateContextDescUpscale {
            Header = new FfxApi.Header { Type = FfxApi.DescTypeCreateUpscale, PNext = createBackendMem },
            Flags = FfxApi.UpscaleEnableHdr | FfxApi.UpscaleEnableAutoExposure,
            MaxRenderSize = new FfxApi.Dimensions2D { Width = (uint)maxRenderW, Height = (uint)maxRenderH },
            MaxUpscaleSize = new FfxApi.Dimensions2D { Width = (uint)outputW, Height = (uint)outputH },
            FpMessage = IntPtr.Zero,
        };
        Marshal.StructureToPtr(create, createUpscaleMem, false);

        uint rc = FfxApi.ffxCreateContext(out context, createUpscaleMem, IntPtr.Zero);
        if (rc != FfxApi.ReturnOk) {
            context = IntPtr.Zero;
            FreeCreateMem();
            throw new InvalidOperationException($"FSR ffxCreateContext failed: 0x{rc:X8}");
        }
    }

    public static unsafe (int w, int h) RenderResolutionFor(int displayW, int displayH, uint qualityMode) {
        uint outW = 0, outH = 0;
        var q = new FfxApi.QueryDescGetRenderResolution {
            Header = new FfxApi.Header { Type = FfxApi.DescTypeQueryGetRenderRes, PNext = IntPtr.Zero },
            DisplayWidth = (uint)displayW, DisplayHeight = (uint)displayH, QualityMode = qualityMode,
            POutRenderWidth = (IntPtr)(&outW), POutRenderHeight = (IntPtr)(&outH),
        };
        uint rc = FfxApi.ffxQuery(IntPtr.Zero, (IntPtr)(&q));
        if (rc != FfxApi.ReturnOk || outW == 0 || outH == 0) return (displayW, displayH);
        return ((int)outW, (int)outH);
    }

    public unsafe bool Dispatch(ID3D12GraphicsCommandList4 cl,
        ID3D12Resource color, ID3D12Resource depth, ID3D12Resource motion, ID3D12Resource output,
        int renderW, int renderH, Vector2Jitter jitter, float frameTimeMs, bool reset, bool sharpen, float sharpness,
        float cameraNear, float cameraFar, float fovYRadians) {
        if (context == IntPtr.Zero) return false;
        var disp = new FfxApi.DispatchDescUpscale {
            Header = new FfxApi.Header { Type = FfxApi.DescTypeDispatchUpscale, PNext = IntPtr.Zero },
            CommandList = cl.NativePointer,
            Color  = MakeResource(color,  FfxApi.StatePixelRead, UsageReadOnly),
            Depth  = MakeResource(depth,  FfxApi.StatePixelRead, UsageDepthTarget),
            MotionVectors = MakeResource(motion, FfxApi.StatePixelRead, UsageReadOnly),
            Exposure = default, Reactive = default, TransparencyAndComposition = default,
            Output = MakeResource(output, FfxApi.StateUnorderedAccess, UsageUav),
            JitterOffset = new FfxApi.FloatCoords2D { X = -jitter.X, Y = -jitter.Y },
            MotionVectorScale = new FfxApi.FloatCoords2D { X = renderW, Y = renderH },
            RenderSize = new FfxApi.Dimensions2D { Width = (uint)renderW, Height = (uint)renderH },
            UpscaleSize = new FfxApi.Dimensions2D { Width = (uint)OutputWidth, Height = (uint)OutputHeight },
            EnableSharpening = (byte)(sharpen ? 1 : 0), Sharpness = sharpness,
            FrameTimeDelta = frameTimeMs, PreExposure = 1.0f,
            Reset = (byte)(reset ? 1 : 0),
            CameraNear = cameraNear, CameraFar = cameraFar, CameraFovAngleVertical = fovYRadians,
            ViewSpaceToMetersFactor = 0f, Flags = 0,
        };
        uint rc = FfxApi.ffxDispatch(ref context, (IntPtr)(&disp));
        return rc == FfxApi.ReturnOk;
    }

    const uint UsageReadOnly = 0;
    const uint UsageRenderTarget = 1 << 0;
    const uint UsageUav = 1 << 1;
    const uint UsageDepthTarget = 1 << 2;
    const uint TypeTexture2D = 2;

    static FfxApi.Resource MakeResource(ID3D12Resource res, uint state, uint usage) {
        if (res is null) return default;
        ResourceDescription d = res.Description;
        return new FfxApi.Resource {
            Res = res.NativePointer,
            State = state,
            Description = new FfxApi.ResourceDescription {
                Type = TypeTexture2D, Format = FfxFormat(d.Format),
                Width = (uint)d.Width, Height = d.Height, Depth = d.DepthOrArraySize,
                MipCount = d.MipLevels, Flags = 0, Usage = usage,
            },
        };
    }

    static uint FfxFormat(Format f) => f switch {
        Format.R16G16B16A16_Float => 4,
        Format.R16G16_Float => 18,
        Format.R32_Float or Format.D32_Float or Format.R32_Typeless => 28,
        Format.R8G8B8A8_UNorm => 10,
        Format.R8G8B8A8_UNorm_SRgb => 12,
        Format.R11G11B10_Float => 16,
        _ => 0,
    };

    void FreeCreateMem() {
        if (createUpscaleMem != IntPtr.Zero) { Marshal.FreeHGlobal(createUpscaleMem); createUpscaleMem = IntPtr.Zero; }
        if (createBackendMem != IntPtr.Zero) { Marshal.FreeHGlobal(createBackendMem); createBackendMem = IntPtr.Zero; }
    }

    public void Dispose() {
        if (context != IntPtr.Zero) {
            FfxApi.ffxDestroyContext(ref context, IntPtr.Zero);
            context = IntPtr.Zero;
        }
        FreeCreateMem();
    }

    public readonly struct Vector2Jitter {
        public readonly float X, Y;
        public Vector2Jitter(float x, float y) { X = x; Y = y; }
    }

    public static bool SelfTest(Dx12Device dev) {
        try {
            string[] names = { "NativeAA(1.0x)", "Quality(1.5x)", "Balanced(1.7x)", "Performance(2.0x)", "UltraPerf(3.0x)" };
            for (uint q = 0; q <= 4; q++) {
                var (rw, rh) = RenderResolutionFor(1920, 1080, q);
                Console.WriteLine($"[FsrTest] {names[q]}: 1920x1080 -> render {rw}x{rh}");
            }
            using var fsr = new Dx12FsrUpscaler(dev, 1920, 1080, 1920, 1080);
            Console.WriteLine($"[FsrTest] context created OK (valid={fsr.Valid}).");
            return fsr.Valid;
        } catch (Exception e) {
            Console.WriteLine($"[FsrTest] FAILED: {e.Message}");
            return false;
        }
    }
}
