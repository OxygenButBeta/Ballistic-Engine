#version 330 core

// Horizon-based ambient occlusion (HBAO). For each of a few slice directions, march outward and
// track the maximum ELEVATION ANGLE above the surface's tangent plane that the surrounding
// geometry rises to. Occlusion per slice is sin(maxHorizon) minus the tangent (coplanar)
// baseline, so a flat open plane reads ZERO occlusion (its samples never rise above the tangent
// plane) and only real raised geometry darkens. This fixes the flat/muddy ambient the old
// hemisphere-point SSAO produced, giving graded contact darkening in crevices.
//
// Normals are reconstructed from depth (the normal G-buffer isn't written until the opaque pass,
// which runs after this) with a best-of-neighbours scheme so silhouettes don't streak.

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D depthTexture;
uniform mat4 Projection;
uniform mat4 InvProjection;
uniform float Radius;        // world-space falloff radius
uniform float Intensity;     // AO strength multiplier
uniform vec2 TexelSize;      // 1 / AO-buffer dimensions

const float PI = 3.14159265359;
const int SLICES = 4;        // azimuthal directions
const int STEPS = 6;         // march samples per slice

float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

vec3 ViewPos(vec2 uv) {
    float depth = texture(depthTexture, uv).r;
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.xyz / view.w;
}

// Reconstruct the view-space normal from depth, picking the closer of the forward/backward
// neighbour on each axis so silhouettes don't bend the normal across a depth discontinuity.
vec3 ReconstructNormal(vec2 uv, vec3 P) {
    vec3 pL = ViewPos(uv - vec2(TexelSize.x, 0.0));
    vec3 pR = ViewPos(uv + vec2(TexelSize.x, 0.0));
    vec3 pD = ViewPos(uv - vec2(0.0, TexelSize.y));
    vec3 pU = ViewPos(uv + vec2(0.0, TexelSize.y));
    vec3 dx = abs(pR.z - P.z) < abs(P.z - pL.z) ? (pR - P) : (P - pL);
    vec3 dy = abs(pU.z - P.z) < abs(P.z - pD.z) ? (pU - P) : (P - pD);
    return normalize(cross(dx, dy));
}

void main() {
    float depth = texture(depthTexture, TexCoords).r;
    if (depth >= 1.0) { // sky: fully unoccluded
        FragColor = vec4(1.0);
        return;
    }

    vec3 P = ViewPos(TexCoords);
    vec3 N = ReconstructNormal(TexCoords, P);

    // Project the world radius to a screen-space march length (shrinks with distance).
    float radiusPx = Radius / max(-P.z, 1e-3) * (0.5 / TexelSize.y);
    radiusPx = clamp(radiusPx, 2.0, 0.3 / TexelSize.y);

    float noise = rand(TexCoords * 197.0);
    float occlusion = 0.0;
    const float angleBias = 0.15; // ignore near-tangent occluders (self-occlusion on flat ground)

    for (int s = 0; s < SLICES; s++) {
        float phi = (float(s) + noise) * PI / float(SLICES);
        vec2 dir = vec2(cos(phi), sin(phi));

        // Largest elevation (sin of the angle above the tangent plane) found along this slice.
        float maxElevation = 0.0;
        for (int t = 1; t <= STEPS; t++) {
            float stepFrac = (float(t) - 0.5 + noise) / float(STEPS);
            vec2 offset = dir * stepFrac * radiusPx * TexelSize;
            vec3 sampleVec = ViewPos(TexCoords + offset) - P;
            float dist = length(sampleVec);
            if (dist < 1e-4 || dist > Radius) continue;

            // Elevation = how far the sample direction rises ABOVE the tangent plane (dot with N).
            // Coplanar samples (flat ground) give ~0 -> no occlusion. Distance falloff fades
            // far occluders so a wall across the street doesn't darken everything near it.
            float elevation = dot(sampleVec / dist, N);
            float falloff = clamp(1.0 - (dist / Radius) * (dist / Radius), 0.0, 1.0);
            maxElevation = max(maxElevation, (elevation - angleBias) * falloff);
        }
        occlusion += clamp(maxElevation, 0.0, 1.0);
    }

    occlusion /= float(SLICES);
    float ao = clamp(1.0 - occlusion * Intensity, 0.0, 1.0);
    FragColor = vec4(vec3(ao), 1.0);
}
