namespace BallisticEngine.DX12;

public static class Dx12ShadowMath {
    public static void ComputeCascades(Matrix4x4 camView, Matrix4x4 camProj, Vector3 lightTravelDir,
        float shadowDistance, int shadowMapSize, Matrix4x4[] matrices, float[] depthRanges,
        float lambda = 0.7f, int cascadeCount = 0) {
        int count = cascadeCount > 0 ? Math.Min(cascadeCount, matrices.Length) : matrices.Length;
        float near = MathF.Max(shadowDistance * 0.02f, 0.3f);
        lambda = Math.Clamp(lambda, 0f, 1f);

        float prevT = 0f;
        for (int i = 0; i < count; i++) {
            float f = (i + 1) / (float)count;
            float linear = near + (shadowDistance - near) * f;
            float log = near * MathF.Pow(shadowDistance / near, f);
            float splitT = (lambda * log + (1f - lambda) * linear) / shadowDistance;
            if (i == count - 1) splitT = 1f;
            matrices[i] = Fit(camView, camProj, lightTravelDir, shadowDistance, shadowMapSize,
                prevT, splitT, out float radius);
            depthRanges[i] = radius * 4f + 60f;
            prevT = splitT;
        }
    }

    static Matrix4x4 Fit(Matrix4x4 camView, Matrix4x4 camProj, Vector3 lightTravelDir,
        float shadowDistance, int shadowMapSize, float slabStart, float slabEnd, out float radius) {
        Matrix4x4.Invert(camView * camProj, out Matrix4x4 invVP);

        Span<Vector3> corners = stackalloc Vector3[8];
        int idx = 0;
        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++) {
            var ndc = new Vector4(x * 2f - 1f, y * 2f - 1f, z, 1f);
            Vector4 w = Vector4.Transform(ndc, invVP);
            corners[idx++] = new Vector3(w.X, w.Y, w.Z) / w.W;
        }

        for (int c = 0; c < 8; c += 2) {
            Vector3 nearC = corners[c];
            Vector3 toFar = corners[c + 1] - nearC;
            float span = toFar.Length();
            float tMax = MathF.Min(1f, shadowDistance / MathF.Max(span, 1e-4f));
            corners[c] = nearC + toFar * (tMax * slabStart);
            corners[c + 1] = nearC + toFar * (tMax * slabEnd);
        }

        Vector3 center = Vector3.Zero;
        for (int c = 0; c < 8; c++) center += corners[c];
        center /= 8f;
        radius = 0f;
        for (int c = 0; c < 8; c++) radius = MathF.Max(radius, (corners[c] - center).Length());
        radius = MathF.Ceiling(radius * 16f) / 16f;

        Vector3 lightDir = Vector3.Normalize(lightTravelDir);
        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        float casterBackup = radius * 2f + 60f;

        Matrix4x4 lightView = Matrix4x4.CreateLookAt(center - lightDir * casterBackup, center, up);
        float texelSize = radius * 2f / shadowMapSize;
        Vector3 centerLs = Vector3.Transform(center, lightView);
        centerLs.X = MathF.Floor(centerLs.X / texelSize) * texelSize;
        centerLs.Y = MathF.Floor(centerLs.Y / texelSize) * texelSize;
        Matrix4x4.Invert(lightView, out Matrix4x4 invLightView);
        center = Vector3.Transform(centerLs, invLightView);

        lightView = Matrix4x4.CreateLookAt(center - lightDir * casterBackup, center, up);
        Matrix4x4 lightProj = Matrix4x4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f,
            casterBackup + radius * 2f);
        return lightView * lightProj;
    }
}
