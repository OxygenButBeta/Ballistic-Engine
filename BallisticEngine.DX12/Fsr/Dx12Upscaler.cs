namespace BallisticEngine.DX12;

internal static class Dx12Upscaler {
    public static UpscalerKind KindOf(UpscaleMode m) => m switch {
        UpscaleMode.DlssQuality or UpscaleMode.DlssBalanced
            or UpscaleMode.DlssPerformance or UpscaleMode.DlssUltraPerformance => UpscalerKind.Dlss,
        UpscaleMode.XessQuality or UpscaleMode.XessBalanced
            or UpscaleMode.XessPerformance or UpscaleMode.XessUltraPerformance => UpscalerKind.Xess,
        _ => UpscalerKind.Fsr,
    };

    public static float RatioFor(UpscaleMode m) => m switch {
        UpscaleMode.NativeAA => 1.0f,
        UpscaleMode.Quality or UpscaleMode.DlssQuality or UpscaleMode.XessQuality => 1.5f,
        UpscaleMode.Balanced or UpscaleMode.DlssBalanced or UpscaleMode.XessBalanced => 1.7f,
        UpscaleMode.Performance or UpscaleMode.DlssPerformance or UpscaleMode.XessPerformance => 2.0f,
        UpscaleMode.UltraPerformance or UpscaleMode.DlssUltraPerformance or UpscaleMode.XessUltraPerformance => 3.0f,
        _ => 1.5f,
    };

    public static (int w, int h) RenderResolutionFor(int displayW, int displayH, UpscaleMode m) {
        float r = RatioFor(m);
        int w = (int)MathF.Round(displayW / r);
        int h = (int)MathF.Round(displayH / r);
        w = Math.Max(2, w & ~1);
        h = Math.Max(2, h & ~1);
        return (w, h);
    }

    public static UpscaleMode FsrEquivalent(UpscaleMode m) => m switch {
        UpscaleMode.DlssQuality or UpscaleMode.XessQuality => UpscaleMode.Quality,
        UpscaleMode.DlssBalanced or UpscaleMode.XessBalanced => UpscaleMode.Balanced,
        UpscaleMode.DlssPerformance or UpscaleMode.XessPerformance => UpscaleMode.Performance,
        UpscaleMode.DlssUltraPerformance or UpscaleMode.XessUltraPerformance => UpscaleMode.UltraPerformance,
        _ => m,
    };

    public static int XessQuality(UpscaleMode m) => m switch {
        UpscaleMode.XessQuality => XessApi.QualityQuality,
        UpscaleMode.XessBalanced => XessApi.QualityBalanced,
        UpscaleMode.XessPerformance => XessApi.QualityPerformance,
        UpscaleMode.XessUltraPerformance => XessApi.QualityUltraPerformance,
        _ => XessApi.QualityQuality,
    };

    public static int DlssQuality(UpscaleMode m) => m switch {
        UpscaleMode.DlssQuality => NgxApi.PerfQualityMaxQuality,
        UpscaleMode.DlssBalanced => NgxApi.PerfQualityBalanced,
        UpscaleMode.DlssPerformance => NgxApi.PerfQualityMaxPerf,
        UpscaleMode.DlssUltraPerformance => NgxApi.PerfQualityUltraPerformance,
        _ => NgxApi.PerfQualityMaxQuality,
    };
}
