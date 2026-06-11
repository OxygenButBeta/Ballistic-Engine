#version 330 core

in vec3 normal;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 NormalRough;

// Debug probe sphere: evaluates the probe's stored L1 SH with the sphere normal - exactly
// the reconstruction the PBR shader runs - so each ball shows its probe's directional light
// (sun side bright, bounce side tinted, shadow side dark). Pre-exposed like the scene, so
// the composite tonemap treats it as ordinary HDR color.

uniform sampler3D ProbeSH0;
uniform sampler3D ProbeSH1;
uniform sampler3D ProbeSH2;
uniform sampler3D ProbeSH3;
uniform vec3 ProbeUVW;       // this probe's texel center
uniform float ProbeExposure; // EV pre-exposure (SH is stored un-exposed)

void main() {
    vec3 N = normalize(normal);
    vec3 sh0 = texture(ProbeSH0, ProbeUVW).rgb;
    vec3 sh1 = texture(ProbeSH1, ProbeUVW).rgb;   // linear Y
    vec3 sh2 = texture(ProbeSH2, ProbeUVW).rgb;   // linear Z
    vec3 sh3 = texture(ProbeSH3, ProbeUVW).rgb;   // linear X

    vec3 irradiance = sh0 * 0.886227 + (sh1 * N.y + sh2 * N.z + sh3 * N.x) * 1.023327;
    FragColor = vec4(max(irradiance, 0.0) * ProbeExposure, 1.0);
    NormalRough = vec4(N * 0.5 + 0.5, 1.0); // sane G-buffer data so SSR/TAA stay calm
}
