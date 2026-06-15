using System;
using System.Numerics;

namespace BallisticEngine.DX12;

// Single source of truth for the procedural-sky cloud/cirrus/star constants on the DX12 backend, shared by
// the background sky draw (DX12HDRenderer.DrawProcSky) and the IBL env-cube bake (Dx12IblBaker.EnsureBaked)
// so clouds are identical in both. Mirrors GLProceduralSkyPass: clamps match the GL uniform setters and the
// wind offset uses the same quantized cloud-time so a paused/deterministic capture is stable (and animated
// clouds re-bake the IBL once per CloudUpdateInterval instead of every frame).
internal static class Dx12SkyCloudParams {
    // Quantized cloud-wind time (0 = static clouds: animate only when CloudUpdateInterval > 0).
    public static float CloudTime(ProceduralSky sky) {
        if (sky.CloudsEnabled && sky.CloudUpdateInterval > 0f && sky.CloudWindSpeed != 0f)
            return MathF.Floor((float)Time.TotalTime / sky.CloudUpdateInterval) * sky.CloudUpdateInterval;
        return 0f;
    }

    // Wind compass direction in radians (0 = +Z, 90deg = +X), used for the cirrus streak alignment.
    public static float WindRadians(ProceduralSky sky) => sky.CloudWindDirection * (MathF.PI / 180f);

    // Accumulated cloud drift in meters = dir * speed * time.
    public static Vector3 WindOffset(ProceduralSky sky, float cloudTime) {
        float rad = WindRadians(sky);
        return new Vector3(MathF.Sin(rad), 0f, MathF.Cos(rad)) * (sky.CloudWindSpeed * cloudTime);
    }
}
