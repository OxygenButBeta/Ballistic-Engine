// Voxel cone tracing — gathers indirect diffuse (6 hemisphere cones) and a glossy reflection cone
// from the voxel radiance mip pyramid. Injected into the forward Frag.glsl ambient section, gated
// by UseVoxelGI. Front-to-back accumulation through trilinearly-filtered mips = the soft, colored,
// multi-bounce "Lumen look". Sampling coarser mips with distance keeps the cost bounded.
//
// Requires (declared in Frag.glsl): sampler3D VoxelRadianceTex, vec3 VoxelVolumeMin,
// vec3 VoxelVolumeInvSize, float VoxelWorldSize (size of one voxel in metres), float VoxelGiIntensity.

vec3 worldToVoxelUVW(vec3 wp) {
    return (wp - VoxelVolumeMin) * VoxelVolumeInvSize;  // [0,1]
}

// Trace one cone: march from `origin` along `dir`, stepping by the cone's current radius, sampling
// the mip whose voxel size matches the radius. Accumulate radiance front-to-back with the sampled
// alpha (occupancy) as opacity. `aperture` = tan(half-angle); wider = softer/diffuse.
vec4 traceCone(vec3 origin, vec3 dir, float aperture, float maxDistM) {
    vec3 color = vec3(0.0);
    float alpha = 0.0;

    float voxel = VoxelWorldSize;
    float dist = voxel * 1.5;             // start a bit off the surface to avoid self-occlusion
    float maxMip = log2(float(textureSize(VoxelRadianceTex, 0).x));

    while (dist < maxDistM && alpha < 0.95) {
        float coneRadius = max(aperture * dist, voxel);
        float mip = clamp(log2(coneRadius / voxel), 0.0, maxMip);

        vec3 samplePos = origin + dir * dist;
        vec3 uvw = worldToVoxelUVW(samplePos);
        if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
            break;

        vec4 s = textureLod(VoxelRadianceTex, uvw, mip);
        // Front-to-back compositing.
        float a = s.a;
        color += (1.0 - alpha) * a * s.rgb;
        alpha += (1.0 - alpha) * a;

        dist += coneRadius;               // step by the cone radius (one mip-texel)
    }
    return vec4(color, alpha);
}

// 6 diffuse cones over the hemisphere around N (one straight up + 5 ring at ~60deg), area-weighted.
vec3 voxelDiffuseGI(vec3 wp, vec3 N) {
    // Build a tangent basis.
    vec3 up = abs(N.y) < 0.95 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 T = normalize(cross(up, N));
    vec3 B = cross(N, T);

    const float APERTURE = 0.577;   // tan(30deg) -> 60deg cones, the standard diffuse setup
    const float MAXD = 24.0;        // metres of reach

    // Offset the cone origin a voxel along N so cones don't immediately self-hit.
    vec3 o = wp + N * VoxelWorldSize * 1.5;

    vec3 acc = traceCone(o, N, APERTURE, MAXD).rgb * 0.25; // weight the straight-up cone heaviest

    // 5 side cones tilted 60deg from N, spun 72deg apart.
    const float SIN60 = 0.866, COS60 = 0.5;
    for (int i = 0; i < 5; ++i) {
        float ang = float(i) * 1.2566370614;   // 72deg
        vec3 dir = normalize(N * COS60 + (T * cos(ang) + B * sin(ang)) * SIN60);
        acc += traceCone(o, dir, APERTURE, MAXD).rgb * 0.15;
    }
    return acc;
}

// One glossy reflection cone along R; aperture widens with roughness (sharp->wide).
vec3 voxelSpecularGI(vec3 wp, vec3 N, vec3 R, float roughness) {
    float aperture = clamp(tan(roughness * 1.5707963), 0.02, 0.577);
    vec3 o = wp + N * VoxelWorldSize * 1.5;
    return traceCone(o, R, aperture, 32.0).rgb;
}
