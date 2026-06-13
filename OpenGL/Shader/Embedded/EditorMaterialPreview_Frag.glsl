#version 330 core
in vec3 n;
in vec2 vUv;
out vec4 outColor;
uniform vec4 baseColor;
uniform float roughness;
uniform float metallic;
uniform sampler2D albedoMap;
uniform sampler2D normalMap;
uniform int hasAlbedo;
uniform int hasNormal;
void main() {
    vec3 N = normalize(n);
    // Perturb the normal by the normal map (tangent-space approx: the sphere's own basis). Subtle, but
    // it makes a normal-mapped material read as bumpy in the preview.
    if (hasNormal == 1) {
        vec3 nm = texture(normalMap, vUv).rgb * 2.0 - 1.0;
        N = normalize(N + nm * 0.6);
    }
    vec3 L = normalize(vec3(0.45, 0.65, 0.7));
    vec3 V = vec3(0.0, 0.0, 1.0);
    vec3 H = normalize(L + V);
    // Albedo = base colour TINTED by the diffuse map when present (so the actual texture shows).
    vec3 albedo = baseColor.rgb;
    if (hasAlbedo == 1) albedo *= texture(albedoMap, vUv).rgb;
    // Contrasty wrap light so the SPHERE FORM always reads (a plain white material was washing flat).
    float ndl = dot(N, L);
    float diff = clamp(ndl * 0.5 + 0.5, 0.0, 1.0);
    diff = diff * diff;
    float shininess = mix(8.0, 200.0, 1.0 - roughness);
    float spec = pow(max(dot(N, H), 0.0), shininess) * (1.0 - roughness);
    vec3 specColor = mix(vec3(1.0), albedo, metallic);
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.25;
    vec3 lit = albedo * (0.12 + diff * 0.95) + specColor * spec + vec3(rim);
    lit = pow(clamp(lit, 0.0, 1.0), vec3(1.0/2.2));
    outColor = vec4(lit, 1.0);
}
