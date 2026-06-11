using OpenTK.Mathematics;

namespace BallisticEngine;

public static class ShadowMath {
    // Cascaded shadow maps: split the camera frustum (out to shadowDistance) into depth slabs
    // and fit one stable ortho light frustum per slab. Near cascades cover metres instead of
    // the whole frustum, so close-up shadows get real texel density. Practical split scheme
    // (Zhang et al.): blend of linear and logarithmic so near cascades stay tight.
    // matrices/depthRanges spans define the cascade count; depthRanges[i] receives the light
    // frustum's world-space depth extent (for converting world bias into compare-space).
    public static void ComputeCascades(Matrix4 cameraView, Matrix4 cameraProjection,
        Vector3 lightTravelDirection, float shadowDistance, int shadowMapSize,
        Span<Matrix4> matrices, Span<float> depthRanges, float lambda = 0.7f,
        Span<float> radii = default) {
        var count = matrices.Length;
        var near = MathF.Max(shadowDistance * 0.02f, 0.3f);
        lambda = Math.Clamp(lambda, 0f, 1f); // 0 = uniform splits, 1 = fully logarithmic

        var prevT = 0f;
        for (var i = 0; i < count; i++) {
            var f = (i + 1) / (float)count;
            var linear = near + (shadowDistance - near) * f;
            var log = near * MathF.Pow(shadowDistance / near, f);
            var splitT = (lambda * log + (1f - lambda) * linear) / shadowDistance;
            if (i == count - 1)
                splitT = 1f;

            matrices[i] = ComputeLightSpaceMatrix(cameraView, cameraProjection, lightTravelDirection,
                shadowDistance, shadowMapSize, prevT, splitT, out var radius);
            depthRanges[i] = radius * 4f + 60f; // matches the ortho near/far span below
            if (!radii.IsEmpty)
                radii[i] = radius;
            prevT = splitT;
        }
    }

    // Fits a stable orthographic light frustum around the camera frustum out to
    // shadowDistance. Returns view * projection in OpenTK row-vector convention, so it
    // uploads and applies exactly like the scene matrices.
    public static Matrix4 ComputeLightSpaceMatrix(Matrix4 cameraView, Matrix4 cameraProjection,
        Vector3 lightTravelDirection, float shadowDistance, int shadowMapSize) {
        return ComputeLightSpaceMatrix(cameraView, cameraProjection, lightTravelDirection,
            shadowDistance, shadowMapSize, 0f, 1f, out _);
    }

    static Matrix4 ComputeLightSpaceMatrix(Matrix4 cameraView, Matrix4 cameraProjection,
        Vector3 lightTravelDirection, float shadowDistance, int shadowMapSize,
        float slabStart, float slabEnd, out float radius) {
        Matrix4 invViewProj = Matrix4.Invert(cameraView * cameraProjection);

        // World-space camera frustum corners; even indices near plane, odd indices far plane.
        Span<Vector3> corners = stackalloc Vector3[8];
        var i = 0;
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        for (var z = 0; z < 2; z++) {
            var ndc = new Vector4(x * 2f - 1f, y * 2f - 1f, z * 2f - 1f, 1f);
            Vector4 world = ndc * invViewProj;
            corners[i++] = world.Xyz / world.W;
        }

        // Pull the near/far corners onto this cascade's slab of the (shadowDistance-clamped)
        // frustum: each near/far ray pair is re-parameterized to [slabStart, slabEnd].
        for (var c = 0; c < 8; c += 2) {
            Vector3 nearCorner = corners[c];
            Vector3 toFar = corners[c + 1] - nearCorner;
            var span = toFar.Length;
            var tMax = MathF.Min(1f, shadowDistance / MathF.Max(span, 1e-4f));
            corners[c] = nearCorner + toFar * (tMax * slabStart);
            corners[c + 1] = nearCorner + toFar * (tMax * slabEnd);
        }

        // A bounding sphere keeps the ortho box a constant size while the camera rotates,
        // which stops the shadow texel grid from rescaling every frame.
        Vector3 center = Vector3.Zero;
        foreach (Vector3 corner in corners)
            center += corner;
        center /= 8f;

        radius = 0f;
        foreach (Vector3 corner in corners)
            radius = MathF.Max(radius, (corner - center).Length);
        radius = MathF.Ceiling(radius * 16f) / 16f;

        Vector3 lightDir = lightTravelDirection.Normalized();
        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // Pull the light eye well behind the slab so tall casters OUTSIDE a small near
        // cascade's sphere (a roof high above the street) still land in its depth map.
        var casterBackup = radius * 2f + 60f;

        // Snap the center to shadow-map texels to stop edge shimmer as the camera moves.
        Matrix4 lightView = Matrix4.LookAt(center - lightDir * casterBackup, center, up);
        var texelSize = radius * 2f / shadowMapSize;
        Vector3 centerLs = (new Vector4(center, 1f) * lightView).Xyz;
        centerLs.X = MathF.Floor(centerLs.X / texelSize) * texelSize;
        centerLs.Y = MathF.Floor(centerLs.Y / texelSize) * texelSize;
        center = (new Vector4(centerLs, 1f) * Matrix4.Invert(lightView)).Xyz;

        lightView = Matrix4.LookAt(center - lightDir * casterBackup, center, up);
        Matrix4 lightProjection = Matrix4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f,
            casterBackup + radius * 2f);
        return lightView * lightProjection;
    }
}
