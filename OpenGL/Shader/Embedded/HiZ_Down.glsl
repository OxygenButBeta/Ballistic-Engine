#version 460 core
// Hi-Z pyramid downsample: each output texel = the MAX (farthest) of the 4 source texels it
// covers. MAX is the conservative reduction for occlusion: a coarse texel reports the farthest
// surface in its region, so the cull can only ever say "this AABB is behind EVERYTHING here",
// never falsely claim an occluder is closer than it is.
//
// Source depth is non-linear [0,1] window depth (DepthComponent24 sampled as R). The MAX of
// window-depth is also the farthest in linear Z (the mapping is monotonic), so no linearization
// is needed. Odd source sizes: include the extra row/column so nothing is dropped (a missed far
// texel could under-report the occluder depth and let an occluded AABB through — only a perf
// loss, never a hole, but we cover it anyway).

in vec2 TexCoords;
out float FragDepth;

uniform sampler2D SourceDepth;
uniform vec2 SourceSize;   // size of the mip being read (passed as floats)

void main() {
    ivec2 srcSize = ivec2(SourceSize + 0.5);
    ivec2 dstSize = max(srcSize / 2, ivec2(1));
    ivec2 dst = ivec2(TexCoords * vec2(dstSize));
    ivec2 src = dst * 2;

    ivec2 hi = srcSize - 1;
    float d0 = texelFetch(SourceDepth, min(src + ivec2(0, 0), hi), 0).r;
    float d1 = texelFetch(SourceDepth, min(src + ivec2(1, 0), hi), 0).r;
    float d2 = texelFetch(SourceDepth, min(src + ivec2(0, 1), hi), 0).r;
    float d3 = texelFetch(SourceDepth, min(src + ivec2(1, 1), hi), 0).r;
    float m = max(max(d0, d1), max(d2, d3));

    // Odd dimension: also fold in the dropped edge texel so its (potentially farther) depth isn't
    // lost from the conservative MAX.
    if ((srcSize.x & 1) != 0 && src.x + 2 < srcSize.x) {
        m = max(m, texelFetch(SourceDepth, min(src + ivec2(2, 0), hi), 0).r);
        m = max(m, texelFetch(SourceDepth, min(src + ivec2(2, 1), hi), 0).r);
    }
    if ((srcSize.y & 1) != 0 && src.y + 2 < srcSize.y) {
        m = max(m, texelFetch(SourceDepth, min(src + ivec2(0, 2), hi), 0).r);
        m = max(m, texelFetch(SourceDepth, min(src + ivec2(1, 2), hi), 0).r);
    }

    FragDepth = m;
}
