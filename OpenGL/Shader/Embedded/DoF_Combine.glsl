#version 330 core

// Depth-of-field stage 3: full-res composite. Cross-fade the sharp scene with the half-res
// bokeh result by the blur amount the gather computed. The half-res blur is bilinearly
// upsampled (clamp-to-edge linear sampling); TAA already ran before DoF so there is no extra
// temporal work here — this pass is fully deterministic.

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D sceneTexture;   // full-res sharp HDR
uniform sampler2D dofTexture;     // half-res: rgb = bokeh color, a = blur amount [0,1]

void main() {
    vec3 sharp = texture(sceneTexture, TexCoords).rgb;
    vec4 blurred = texture(dofTexture, TexCoords);
    // Smooth the transition so the in-focus->blur boundary has no hard edge.
    float blend = smoothstep(0.0, 0.35, blurred.a);
    FragColor = vec4(mix(sharp, blurred.rgb, blend), 1.0);
}
