#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// Renderer debug visualisation (editor "shading mode" dropdown). Reuses the G-buffer textures the
// lighting already produces — the world-normal attachment and the depth buffer — so the modes cost a
// single fullscreen blit with no extra geometry pass. Mode 1 = Normals, Mode 2 = linear Depth.
// (Wireframe is handled on the CPU side via glPolygonMode around the opaque pass, not here.)

uniform sampler2D normalTexture;  // world normal packed 0..1 (rgb), roughness/flag in a
uniform sampler2D depthTexture;
uniform mat4 InvProjection;
uniform int Mode;                 // 1 = Normals, 2 = Depth
uniform float DepthScale;         // 1 / far-ish, to bring linear view depth into 0..1 for display

void main() {
    float depth = texture(depthTexture, TexCoords).r;

    if (Mode == 1) {
        // Normals: unpack the world normal and show it as RGB (the classic normal-map look).
        vec3 n = texture(normalTexture, TexCoords).rgb;        // already 0..1 packed
        // Sky / un-shaded pixels (no normal written) read ~0; show them mid-grey so the silhouette
        // is legible instead of black.
        if (dot(n, n) < 0.001 || depth >= 1.0)
            n = vec3(0.5);
        FragColor = vec4(n, 1.0);
        return;
    }

    // Depth: reconstruct view-space Z and map to a 0..1 grey ramp (near = white, far = black).
    if (depth >= 1.0) {
        FragColor = vec4(0.0, 0.0, 0.0, 1.0);   // sky = far = black
        return;
    }
    vec4 ndc = vec4(TexCoords * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    float viewZ = -view.z / view.w;             // positive distance in front of the camera
    float g = clamp(1.0 - viewZ * DepthScale, 0.0, 1.0);
    FragColor = vec4(vec3(g), 1.0);
}
