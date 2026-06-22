namespace BallisticEngine;

public sealed class Exposure : VolumeComponent {
    [Tooltip("Fixed: use the EV dial. Automatic: meter the scene. Automatic Histogram: meter " +
             "with the darkest/brightest pixels rejected (steadier around the sun or black voids).")]
    public readonly EnumParameter<ExposureMode> mode = new(ExposureMode.Fixed);

    [Tooltip("Camera exposure in EV100. Higher = darker. ~15 matches an 80,000-lux physical sun. " +
             "Used by Fixed mode only.")]
    public readonly ClampedFloatParameter exposureEV = new(15f, 4f, 20f);

    [Tooltip("Exposure nudge in stops. +1 = one stop (2x) brighter. Applies in every mode.")]
    public readonly ClampedFloatParameter compensation = new(0f, -5f, 5f);

    [Tooltip("Which pixels the auto meter trusts: the whole frame, a center-weighted falloff, " +
             "or a small center spot.")]
    public readonly EnumParameter<MeteringMode> metering = new(MeteringMode.CenterWeighted);

    [Tooltip("Lowest EV auto exposure may adapt to (how far it opens up in the dark).")]
    public readonly ClampedFloatParameter limitMin = new(13f, 0f, 22f);

    [Tooltip("Highest EV auto exposure may adapt to (how far it stops down in bright light).")]
    public readonly ClampedFloatParameter limitMax = new(19f, 0f, 22f);

    [Tooltip("Adaptation speed in stops/second when the scene gets brighter (eyes adjust fast).")]
    public readonly ClampedFloatParameter speedDarkToLight = new(3f, 0.1f, 20f);

    [Tooltip("Adaptation speed in stops/second when the scene gets darker (eyes adjust slowly).")]
    public readonly ClampedFloatParameter speedLightToDark = new(2.5f, 0.1f, 20f);

    [Tooltip("Histogram mode: percentile below which dark pixels are ignored by the meter.")]
    public readonly ClampedFloatParameter histogramMin = new(40f, 0f, 100f);

    [Tooltip("Histogram mode: percentile above which bright pixels are ignored by the meter.")]
    public readonly ClampedFloatParameter histogramMax = new(95f, 0f, 100f);

    [Tooltip("Manual multiplier on top of the EV exposure. Leave at 1 for the pure physical path.")]
    public readonly ClampedFloatParameter multiplier = new(1f, 0.1f, 8f);
}
