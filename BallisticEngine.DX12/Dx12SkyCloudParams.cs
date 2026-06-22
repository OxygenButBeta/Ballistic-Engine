namespace BallisticEngine.DX12;

internal static class Dx12SkyCloudParams {
    public static float CloudTime(ProceduralSky sky) {
        if (sky.CloudsEnabled && sky.CloudUpdateInterval > 0f && sky.CloudWindSpeed != 0f)
            return MathF.Floor((float)Time.TotalTime / sky.CloudUpdateInterval) * sky.CloudUpdateInterval;
        return 0f;
    }

    public static float WindRadians(ProceduralSky sky) => sky.CloudWindDirection * (MathF.PI / 180f);

    public static Vector3 WindOffset(ProceduralSky sky, float cloudTime) {
        float rad = WindRadians(sky);
        return new Vector3(MathF.Sin(rad), 0f, MathF.Cos(rad)) * (sky.CloudWindSpeed * cloudTime);
    }
}
