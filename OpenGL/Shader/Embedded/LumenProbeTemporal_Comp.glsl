#version 460 core

// LUMEN octahedral screen-probe TEMPORAL accumulation (Phase 4b, 3/3). Each probe traced only ONE
// jittered ray per oct texel this frame -> a 1-spp noisy octmap (the CornellBox grid/speckle). This
// pass EMA-accumulates each probe's octmap across frames: it reprojects the probe by its WORLD position
// to last frame's screen, finds which probe covered that pixel last frame, samples the SAME oct texel
// of that history probe, and blends. Disocclusion (depth mismatch) resets to this frame's trace. Over a
// few frames each probe octmap converges to a clean estimate — Lumen's probe temporal filter.

layout(local_size_x = 8, local_size_y = 8) in;

layout(rgba16f, binding = 0) uniform image2D ProbeOut;   // in: this frame's raw trace; out: accumulated
layout(binding = 1) uniform sampler2D ProbeHistory;       // last frame's accumulated atlas
layout(binding = 2) uniform sampler2D DepthTex;           // full-res depth (this frame)

uniform mat4  InvProjection;     // clip->view (this frame)
uniform mat4  InvView;           // view->world (this frame)
uniform mat4  PrevViewProj;      // last frame's world->clip (un-jittered)
uniform ivec2 ProbeAtlasSize;
uniform ivec2 HalfSize;
uniform int   OctRes;
uniform int   ProbeStep;
uniform float MaxHistory;        // EMA window (frames); higher = smoother + laggier
uniform int   HasHistory;        // 0 on first frame / resize

vec3 ViewPosFromDepth(vec2 uv, float depth) {
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 v = InvProjection * ndc;
    return v.xyz / v.w;
}
vec3 San(vec3 v){return vec3(isnan(v.x)||isinf(v.x)?0.:v.x, isnan(v.y)||isinf(v.y)?0.:v.y, isnan(v.z)||isinf(v.z)?0.:v.z);}

void main() {
    ivec2 atlasPx = ivec2(gl_GlobalInvocationID.xy);
    if (atlasPx.x >= ProbeAtlasSize.x || atlasPx.y >= ProbeAtlasSize.y)
        return;

    vec4 cur = imageLoad(ProbeOut, atlasPx);
    if (HasHistory == 0 || cur.a < 0.5) {       // first frame, or this probe texel is invalid (sky probe)
        imageStore(ProbeOut, atlasPx, vec4(San(cur.rgb), cur.a));
        return;
    }

    ivec2 probe = atlasPx / OctRes;
    ivec2 octTexel = atlasPx - probe * OctRes;

    // This probe's world position = its representative pixel's surface this frame.
    ivec2 hp = min(probe * ProbeStep + ProbeStep / 2, HalfSize - ivec2(1));
    vec2 uv = (vec2(hp) + 0.5) / vec2(HalfSize);
    float depth = texture(DepthTex, uv).r;
    if (depth >= 1.0) { imageStore(ProbeOut, atlasPx, vec4(San(cur.rgb), cur.a)); return; }
    vec3 worldP = (InvView * vec4(ViewPosFromDepth(uv, depth), 1.0)).xyz;

    // Reproject to last frame's screen, then to last frame's PROBE coordinate.
    vec4 pc = PrevViewProj * vec4(worldP, 1.0);
    if (pc.w <= 1e-5) { imageStore(ProbeOut, atlasPx, vec4(San(cur.rgb), cur.a)); return; }
    vec2 prevUv = pc.xy / pc.w * 0.5 + 0.5;
    if (any(lessThan(prevUv, vec2(0.0))) || any(greaterThan(prevUv, vec2(1.0)))) {
        imageStore(ProbeOut, atlasPx, vec4(San(cur.rgb), cur.a)); return; // off last frame's screen
    }
    // Prev half-res pixel -> prev probe -> the SAME oct texel in the history atlas.
    vec2 prevHalf = prevUv * vec2(HalfSize);
    ivec2 prevProbe = ivec2(prevHalf) / ProbeStep;
    ivec2 prevAtlas = prevProbe * OctRes + octTexel;
    vec2 histUv = (vec2(prevAtlas) + 0.5) / vec2(ProbeAtlasSize);
    vec4 hist = texture(ProbeHistory, histUv);
    // history .a = running accumulation COUNT (>=1 once seeded). < 0.5 => no usable history (disoccluded
    // / first time this probe is covered) -> seed with this frame's trace at count 1.
    if (hist.a < 0.5) { imageStore(ProbeOut, atlasPx, vec4(San(cur.rgb), 1.0)); return; }

    // EMA: blend toward this frame's sample with weight 1/(n+1); the stored count n grows to MaxHistory
    // so convergence is fast early (alpha big) and stable once filled (alpha small). This is what turns
    // the 1-sample/texel/frame probe octmaps into a clean converged estimate.
    float n = min(hist.a + 1.0, MaxHistory);
    float alpha = 1.0 / n;
    vec3 blended = mix(hist.rgb, cur.rgb, alpha);
    imageStore(ProbeOut, atlasPx, vec4(San(blended), n));
}
