#version 330 core

// Depth-of-field stage 1: compute the signed circle of confusion (CoC) per pixel from the
// scene depth and a physical thin-lens model, and pack a half-res premultiplied color so the
// bokeh gather (stage 2) can read color + CoC in one tap.
//
// Output: rgb = scene color (HDR, pre-tonemap), a = CoC in [-1, 1] where
//   negative = foreground (nearer than focus), positive = background (farther than focus).
// The magnitude is the blur radius as a fraction of MaxCoc (clamped), so the gather scales a
// fixed-size kernel by |CoC|.

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D colorTexture;   // full-res HDR scene
uniform sampler2D depthTexture;   // full-res device depth

uniform mat4  InvProjection;      // unjittered: device depth -> view-space Z
uniform float FocusDistance;      // metres to the focal plane
uniform float FocalLength;        // lens focal length (metres); larger = shallower DoF
uniform float Aperture;           // f-number; smaller = shallower DoF
uniform float MaxCoc;             // CoC clamp (fraction of frame height)

// Reconstruct positive view-space distance (metres) from device depth.
float ViewDepth(vec2 uv) {
    float d = texture(depthTexture, uv).r * 2.0 - 1.0;       // NDC z
    vec4 clip = vec4(0.0, 0.0, d, 1.0);
    vec4 view = InvProjection * clip;
    return -view.z / view.w;                                  // view space looks down -Z
}

// Thin-lens CoC (metres on the sensor), normalised to a signed fraction of the frame.
// Full-frame sensor height (metres); the CoC diameter on the sensor is divided by this to get
// a fraction of frame height, so the blur radius is in the same units as MaxCoc.
const float SENSOR_HEIGHT = 0.024;

float SignedCoc(float dist) {
    // Standard thin-lens CoC diameter ON THE SENSOR (metres):
    //   coc = (f^2 / (N * (focus - f))) * |dist - focus| / dist
    // f = focal length, N = f-number. This is the physically correct form; dividing by the
    // sensor height converts it to a fraction of the frame, matching MaxCoc's units.
    float f = FocalLength;
    float focus = max(FocusDistance, f + 1e-3);
    float N = max(Aperture, 0.1);
    float lensCoc = (f * f) / (N * max(focus - f, 1e-4)) * abs(dist - focus) / max(dist, 1e-4);
    float frac = lensCoc / SENSOR_HEIGHT;
    // Sign: foreground negative, background positive.
    return clamp(sign(dist - focus) * frac, -MaxCoc, MaxCoc);
}

void main() {
    vec3 color = texture(colorTexture, TexCoords).rgb;
    float dist = ViewDepth(TexCoords);
    float coc = SignedCoc(dist);
    FragColor = vec4(color, coc);
}
