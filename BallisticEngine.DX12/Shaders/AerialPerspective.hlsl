// Aerial perspective for the DX12 procedural sky — the #1 missing realism cue. Distant opaque geometry should
// pick up the atmosphere's scattering haze that grows with view distance (the cue that sells scale: far
// buildings/mountains desaturate and shift toward the sky colour). Unreal applies a 3D aerial-perspective LUT
// to all opaque surfaces; this is exactly that: a Hillaire FROXEL VOLUME (AerialPerspectiveLut.hlsl) is baked
// each frame from the camera, storing per-froxel accumulated single-scatter inscatter (rgb) + mean transmittance
// (a). This pass just SAMPLES that volume by (screenUV, viewDistance) and blends it into the RAW HDR scene
// BEFORE the composite tonemap so it shares the exposure.
//
// A SEPARATE pass (sky -> THIS -> transparents). It does NOT touch the deferred lighting shader. Sky pixels
// (depth==far) are skipped — the sky already integrates the full atmosphere column. The haze colour matches the
// sky it fades into because the froxel volume marches the SAME Rayleigh/Mie atmosphere the sky kernel uses.
//
// REWRITE (dx12-aerial-perspective-rework): the old version was an ad-hoc analytic march with a hardcoded
// lux-scaled blue tint over a fake LINEAR distance term — a flat blue-white veil over the whole far scene. The
// physically-baked froxel volume replaces it: real exp(-beta*d) optical depth, sky-matched colour, near-field
// gated in the bake. All tuning lives in the AerialPerspective Volume component (PostFX bridge).

cbuffer ApConstants : register(b0) {
    float4x4 InvViewProj;   // unproject screen+depth -> world (transposed on upload)
    float3   CameraPos;     float MaxDistance;    // world camera pos; froxel-volume far depth (m) — MUST match the bake
    float    Enabled;       float3 _padAp;        // 0 = pass is a clean no-op (discard)
};

Texture2D    DepthTex   : register(t0);
Texture3D    ApVolume   : register(t1);
SamplerState PointClamp  : register(s0);   // depth (point)
SamplerState LinearClamp : register(s1);   // froxel volume (trilinear)

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 WorldFromDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

float4 PSMain(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(PointClamp, i.Uv, 0).r;
    if (depth >= 1.0 || Enabled < 0.5)
        discard;   // sky (full column already has atmosphere) / disabled -> leave the scene untouched

    // View distance to this opaque pixel, then map to the froxel volume's W slice. The bake distributes slices
    // as farThisSlice = MaxDistance * sliceT^2 (near slices finer), so invert: sliceT = sqrt(dist/MaxDistance).
    float3 world = WorldFromDepth(i.Uv, depth);
    float dist = length(world - CameraPos);
    float sliceT = sqrt(saturate(dist / max(MaxDistance, 1.0)));

    // Trilinear fetch: (screenUV, sliceT). rgb = pre-integrated inscatter, a = mean transmittance.
    float4 ap = ApVolume.SampleLevel(LinearClamp, float3(i.Uv, sliceT), 0.0);
    float3 inscatter = ap.rgb;
    float  transmittance = ap.a;

    // Composite exactly as the fog pass does (fixed-function blend, set in the PSO):
    //   dest = dest * srcAlpha(transmittance) + src(inscatter)
    // The chromatic dimming lives in the additive inscatter (the sky-blue tilt the volume baked); the scalar
    // transmittance just darkens the distant scene. This is the standard Hillaire AP simplification.
    return float4(inscatter, transmittance);
}
