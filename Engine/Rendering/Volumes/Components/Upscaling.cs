namespace BallisticEngine;

public sealed class Upscaling : VolumeComponent {
    [Tooltip("FSR upscaling quality. Off = native render. NativeAA = temporal AA at native res. " +
             "Quality/Balanced/Performance/UltraPerformance trade internal resolution for speed " +
             "(1.5x / 1.7x / 2.0x / 3.0x per dimension). Replaces TAA when active.")]
    public readonly EnumParameter<UpscaleMode> mode = new(UpscaleMode.Off);

    [Tooltip("Extra RCAS sharpening applied by the upscaler. 0 = none, 1 = maximum.")]
    public readonly ClampedFloatParameter sharpness = new(0.5f, 0f, 1f);
}
