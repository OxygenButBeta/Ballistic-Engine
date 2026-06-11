using OpenTK.Mathematics;

namespace BallisticEngine;

public static class ShadowMath {
    // Fits a stable orthographic light frustum around the camera frustum out to
    // shadowDistance. Returns view * projection in OpenTK row-vector convention, so it
    // uploads and applies exactly like the scene matrices.
    public static Matrix4 ComputeLightSpaceMatrix(Matrix4 cameraView, Matrix4 cameraProjection,
        Vector3 lightTravelDirection, float shadowDistance, int shadowMapSize) {
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

        // Pull the far corners in so the shadowed region only spans shadowDistance.
        for (var c = 0; c < 8; c += 2) {
            Vector3 toFar = corners[c + 1] - corners[c];
            var span = toFar.Length;
            var t = MathF.Min(1f, shadowDistance / MathF.Max(span, 1e-4f));
            corners[c + 1] = corners[c] + toFar * t;
        }

        // A bounding sphere keeps the ortho box a constant size while the camera rotates,
        // which stops the shadow texel grid from rescaling every frame.
        Vector3 center = Vector3.Zero;
        foreach (Vector3 corner in corners)
            center += corner;
        center /= 8f;

        var radius = 0f;
        foreach (Vector3 corner in corners)
            radius = MathF.Max(radius, (corner - center).Length);
        radius = MathF.Ceiling(radius * 16f) / 16f;

        Vector3 lightDir = lightTravelDirection.Normalized();
        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // Snap the center to shadow-map texels to stop edge shimmer as the camera moves.
        Matrix4 lightView = Matrix4.LookAt(center - lightDir * radius * 2f, center, up);
        var texelSize = radius * 2f / shadowMapSize;
        Vector3 centerLs = (new Vector4(center, 1f) * lightView).Xyz;
        centerLs.X = MathF.Floor(centerLs.X / texelSize) * texelSize;
        centerLs.Y = MathF.Floor(centerLs.Y / texelSize) * texelSize;
        center = (new Vector4(centerLs, 1f) * Matrix4.Invert(lightView)).Xyz;

        lightView = Matrix4.LookAt(center - lightDir * radius * 2f, center, up);
        Matrix4 lightProjection = Matrix4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f, radius * 4f);
        return lightView * lightProjection;
    }
}
