using System;
using BallisticEngine;

namespace BallisticEngine.DX12;

// Shared upscaler-family resolution: which vendor a given UpscaleMode targets (KindOf), and the per-dimension
// internal render-resolution ratio for that mode (RatioFor) — the SINGLE source of truth all three families
// (FSR / DLSS / XeSS) use, so they derive byte-identical internal resolutions for the same quality tier. The
// renderer queries this to pick the internal render size and to map the mode to each vendor's quality enum.
//
// Ratio table (per dimension): Quality 1.5x, Balanced 1.7x, Performance 2.0x, UltraPerformance 3.0x, NativeAA/
// DLAA 1.0x. Matches FSR's RenderResolutionFor and XeSS's documented scale factors, so the fallback chain
// (Dlss*/Xess* → the equivalent FSR ratio) keeps the same internal res when it degrades to FSR or native.
internal static class Dx12Upscaler {
    public static UpscalerKind KindOf(UpscaleMode m) => m switch {
        UpscaleMode.DlssQuality or UpscaleMode.DlssBalanced
            or UpscaleMode.DlssPerformance or UpscaleMode.DlssUltraPerformance => UpscalerKind.Dlss,
        UpscaleMode.XessQuality or UpscaleMode.XessBalanced
            or UpscaleMode.XessPerformance or UpscaleMode.XessUltraPerformance => UpscalerKind.Xess,
        _ => UpscalerKind.Fsr,
    };

    // Per-dimension render ratio for any mode (Off/Auto resolved by the caller before this is reached).
    public static float RatioFor(UpscaleMode m) => m switch {
        UpscaleMode.NativeAA => 1.0f,
        UpscaleMode.Quality or UpscaleMode.DlssQuality or UpscaleMode.XessQuality => 1.5f,
        UpscaleMode.Balanced or UpscaleMode.DlssBalanced or UpscaleMode.XessBalanced => 1.7f,
        UpscaleMode.Performance or UpscaleMode.DlssPerformance or UpscaleMode.XessPerformance => 2.0f,
        UpscaleMode.UltraPerformance or UpscaleMode.DlssUltraPerformance or UpscaleMode.XessUltraPerformance => 3.0f,
        _ => 1.5f,
    };

    // Internal (render) resolution for a display size + mode, from the shared ratio table. Rounded to even so
    // half-res post passes (SSR/GTAO/bloom) tile cleanly.
    public static (int w, int h) RenderResolutionFor(int displayW, int displayH, UpscaleMode m) {
        float r = RatioFor(m);
        int w = (int)MathF.Round(displayW / r);
        int h = (int)MathF.Round(displayH / r);
        w = Math.Max(2, w & ~1);
        h = Math.Max(2, h & ~1);
        return (w, h);
    }

    // The FSR mode with the SAME ratio tier as a Dlss*/Xess* mode (the fallback target when the vendor upscaler
    // is unavailable). NativeAA/Quality/Balanced/Performance/UltraPerformance pass through unchanged.
    public static UpscaleMode FsrEquivalent(UpscaleMode m) => m switch {
        UpscaleMode.DlssQuality or UpscaleMode.XessQuality => UpscaleMode.Quality,
        UpscaleMode.DlssBalanced or UpscaleMode.XessBalanced => UpscaleMode.Balanced,
        UpscaleMode.DlssPerformance or UpscaleMode.XessPerformance => UpscaleMode.Performance,
        UpscaleMode.DlssUltraPerformance or UpscaleMode.XessUltraPerformance => UpscaleMode.UltraPerformance,
        _ => m,
    };

    // Map a mode to the XeSS quality enum (xess_quality_settings_t).
    public static int XessQuality(UpscaleMode m) => m switch {
        UpscaleMode.XessQuality => XessApi.QualityQuality,
        UpscaleMode.XessBalanced => XessApi.QualityBalanced,
        UpscaleMode.XessPerformance => XessApi.QualityPerformance,
        UpscaleMode.XessUltraPerformance => XessApi.QualityUltraPerformance,
        _ => XessApi.QualityQuality,
    };

    // Map a mode to the NGX/DLSS PerfQuality value.
    public static int DlssQuality(UpscaleMode m) => m switch {
        UpscaleMode.DlssQuality => NgxApi.PerfQualityMaxQuality,
        UpscaleMode.DlssBalanced => NgxApi.PerfQualityBalanced,
        UpscaleMode.DlssPerformance => NgxApi.PerfQualityMaxPerf,
        UpscaleMode.DlssUltraPerformance => NgxApi.PerfQualityUltraPerformance,
        _ => NgxApi.PerfQualityMaxQuality,
    };
}
