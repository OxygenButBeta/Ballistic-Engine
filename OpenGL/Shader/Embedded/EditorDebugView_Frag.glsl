#version 330 core
in vec2 uv;
out vec4 col;
uniform sampler2D src;
uniform int mode;
void main() {
    if (mode == 1) {            // Ambient Occlusion — read ONLY the R channel and show pure greyscale
        float a = texture(src, uv).r;   // white = unoccluded, black = occluded
        col = vec4(a, a, a, 1.0);
    } else {                    // SSGI / Lit — tonemap lightly so HDR doesn't blow out the view
        vec3 c = texture(src, uv).rgb;
        vec3 t = c / (c + vec3(1.0));
        col = vec4(pow(t, vec3(1.0/2.2)), 1.0);
    }
}
