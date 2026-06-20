// R5 — VISIBILITY-BUFFER MATERIAL RESOLVE (compute). Reads the vis id { DrawIndex, (meshletIndex<<8)|localPrim }
// per pixel, fetches the hit triangle's 3 verts, recovers perspective-correct barycentrics from the clip-space
// positions, interpolates pos/normal/uv/tangent, computes UV gradients via QUAD wave ops (HW-equivalent ddx/ddy),
// then decodes the material EXACTLY like GBufferBindless::PSMain (SampleGrad with the manual gradients) and writes
// the SAME fat G-buffer the deferred lighting reads. So the lit result matches the raster path bar the inherent
// sub-pixel raster tie-break (same class as R3a/R4) — downstream is unchanged.
//
// Dispatched as 8x8 tiles so the quad (2x2) wave-lane neighbourhood is valid for QuadReadAcross gradient.

cbuffer ResolveCB : register(b0) {
    float4x4 InvViewProj;   // unjittered, for world pos reconstruction if needed
    float4x4 ViewProjCur;   // unjittered current (motion)
    float4x4 ViewProjPrev;  // unjittered previous (motion)
    float2 RtSize;          // render target px size
    float NormalLodBias;
    float _pad;
};

struct PerDraw { float4x4 Mvp; float4x4 Model; uint MaterialId; uint3 _pad; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct Meshlet { uint VertOffset, VertCount, PrimOffset, PrimCount; };

StructuredBuffer<PerDraw>     PerDraws     : register(t0);
StructuredBuffer<GpuMaterial> GpuMaterials : register(t1);
StructuredBuffer<Meshlet>     Meshlets     : register(t2);
StructuredBuffer<uint>        MeshletVerts : register(t4);
StructuredBuffer<uint>        MeshletPrims : register(t5);
StructuredBuffer<float3>      Positions    : register(t6);
StructuredBuffer<float3>      Normals      : register(t7);
StructuredBuffer<float2>      UVs          : register(t8);
StructuredBuffer<float4>      Tangents     : register(t9);
Texture2D<uint2>              VisId        : register(t10);

RWTexture2D<float4> OutAlbedo   : register(u0);
RWTexture2D<float4> OutNormal   : register(u1);
RWTexture2D<float4> OutMaterial : register(u2);
RWTexture2D<float4> OutEmissive : register(u3);
RWTexture2D<float2> OutMotion   : register(u4);
SamplerState LinearWrap : register(s0);

float3 NormalFromMap(Texture2D normalMap, float normalFlipY, float normalStrength,
                     float2 uv, float2 dx, float2 dy, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = normalMap.SampleGrad(LinearWrap, uv, dx, dy).rg;   // NormalLodBias folded via gradient scale below
    if (normalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(normalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));
    float3 B = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID, uint2 gtid : SV_GroupThreadID) {
    int2 px = (int2)tid.xy;
    bool inBounds = px.x < (int)RtSize.x && px.y < (int)RtSize.y;
    uint2 id = inBounds ? VisId.Load(int3(px, 0)) : uint2(0xFFFFFFFF, 0xFFFFFFFF);
    bool hit = id.x != 0xFFFFFFFF;

    // Decode { DrawIndex, (meshletIndex<<8)|localPrim }.
    uint drawIndex = id.x;
    uint meshletIdx = id.y >> 8;
    uint localPrim = id.y & 0xFF;

    float2 uv = 0, duvdx = 0, duvdy = 0;
    float3 nW = float3(0, 1, 0), pW = 0, tW = 0; float tSign = 1; uint matId = 0;

    if (hit) {
        PerDraw pd = PerDraws[drawIndex];
        Meshlet m = Meshlets[meshletIdx];
        uint packed = MeshletPrims[m.PrimOffset + localPrim];
        uint l0 = packed & 0xFF, l1 = (packed >> 8) & 0xFF, l2 = (packed >> 16) & 0xFF;
        uint g0 = MeshletVerts[m.VertOffset + l0];
        uint g1 = MeshletVerts[m.VertOffset + l1];
        uint g2 = MeshletVerts[m.VertOffset + l2];

        // Clip-space positions (same Mvp as the raster path).
        float4 c0 = mul(float4(Positions[g0], 1.0), pd.Mvp);
        float4 c1 = mul(float4(Positions[g1], 1.0), pd.Mvp);
        float4 c2 = mul(float4(Positions[g2], 1.0), pd.Mvp);

        // This pixel's NDC (pixel center) → solve perspective-correct barycentrics.
        float2 ndc = (float2(px) + 0.5) / RtSize * float2(2, -2) + float2(-1, 1);
        // Screen-space 2D barycentric on the post-divide positions, then perspective-correct by 1/w.
        float2 s0 = c0.xy / c0.w, s1 = c1.xy / c1.w, s2 = c2.xy / c2.w;
        float2 e0 = s1 - s0, e1 = s2 - s0, ep = ndc - s0;
        float den = e0.x * e1.y - e1.x * e0.y;
        float invDen = abs(den) > 1e-12 ? 1.0 / den : 0.0;
        float bb = (ep.x * e1.y - e1.x * ep.y) * invDen;
        float cc = (e0.x * ep.y - ep.x * e0.y) * invDen;
        float aa = 1.0 - bb - cc;
        // Perspective correction: weight by 1/w, renormalize.
        float iw0 = 1.0 / c0.w, iw1 = 1.0 / c1.w, iw2 = 1.0 / c2.w;
        float wa = aa * iw0, wb = bb * iw1, wc = cc * iw2;
        float wsum = wa + wb + wc; float inv = wsum != 0 ? 1.0 / wsum : 0;
        wa *= inv; wb *= inv; wc *= inv;

        uv = UVs[g0] * wa + UVs[g1] * wb + UVs[g2] * wc;
        float3 n = Normals[g0] * wa + Normals[g1] * wb + Normals[g2] * wc;
        float4 t0 = Tangents[g0], t1 = Tangents[g1], t2 = Tangents[g2];
        float4 tt = t0 * wa + t1 * wb + t2 * wc;
        nW = normalize(mul(float4(n, 0.0), pd.Model).xyz);
        tW = normalize(mul(float4(tt.xyz, 0.0), pd.Model).xyz);
        tSign = t0.w;   // handedness is constant across the tri
        pW = mul(float4(Positions[g0] * wa + Positions[g1] * wb + Positions[g2] * wc, 1.0), pd.Model).xyz;
        matId = pd.MaterialId;
    }

    // UV gradients from the QUAD neighbourhood (HW-equivalent ddx/ddy). Valid because we dispatch 8x8 tiles so the
    // 2x2 quad lanes are co-resident. Helper lanes (non-hit) still carry a uv from their own solve; the gradient is
    // only used by hit lanes.
    float2 uvX = QuadReadAcrossX(uv);
    float2 uvY = QuadReadAcrossY(uv);
    duvdx = uv - uvX; duvdy = uv - uvY;
    // sign-correct (QuadReadAcross direction depends on lane parity)
    if ((gtid.x & 1) == 1) duvdx = -duvdx;
    if ((gtid.y & 1) == 1) duvdy = -duvdy;
    // NormalLodBias: bias the gradient magnitude (2^bias) to sample coarser, matching SampleBias in the raster PS.
    float biasScale = exp2(NormalLodBias);

    if (!inBounds) return;
    if (!hit) {
        // Sky / no-geometry pixels: leave the G-buffer as the geometry pass cleared it (the raster path writes
        // nothing there either). Write neutral so a stale resolve target can't leak — albedo 0, normal up, etc.
        OutAlbedo[px] = float4(0, 0, 0, 0);
        OutNormal[px] = float4(0.5, 0.5, 1.0, 0);   // +Z packed, marks "no surface"
        OutMaterial[px] = float4(0, 1, 1, 0);
        OutEmissive[px] = float4(0, 0, 0, 0);
        OutMotion[px] = float2(0, 0);
        return;
    }

    GpuMaterial mat = GpuMaterials[matId];
    Texture2D diffuseMap   = ResourceDescriptorHeap[mat.DiffuseIdx];
    Texture2D normalMap    = ResourceDescriptorHeap[mat.NormalIdx];
    Texture2D metallicMap  = ResourceDescriptorHeap[mat.MetallicIdx];
    Texture2D roughnessMap = ResourceDescriptorHeap[mat.RoughnessIdx];
    Texture2D aoMap        = ResourceDescriptorHeap[mat.AoIdx];
    Texture2D emissiveMap  = ResourceDescriptorHeap[mat.EmissiveIdx];

    float4 albedoSample = diffuseMap.SampleGrad(LinearWrap, uv, duvdx, duvdy);
    // Cutout discard → leave as sky-neutral.
    if (mat.Cutout > 0.5 && albedoSample.a < 0.5) {
        OutAlbedo[px] = float4(0, 0, 0, 0); OutNormal[px] = float4(0.5, 0.5, 1, 0);
        OutMaterial[px] = float4(0, 1, 1, 0); OutEmissive[px] = float4(0, 0, 0, 0); OutMotion[px] = 0; return;
    }
    float3 albedo = albedoSample.rgb * mat.BaseColorFactor.rgb;
    float3 mr = metallicMap.SampleGrad(LinearWrap, uv, duvdx, duvdy).rgb;
    float metallicSample = mat.HasMetallicMap > 0.5 ? (mat.PackedOrm > 0.5 ? mr.b : mr.r) : 1.0;
    float metallic = saturate(metallicSample * mat.Metallic);
    float roughSample = mat.HasRoughnessMap > 0.5 ? roughnessMap.SampleGrad(LinearWrap, uv, duvdx, duvdy).r
                                                  : (mat.PackedOrm > 0.5 ? mr.g : 1.0);
    float roughness = clamp(roughSample * mat.Roughness, 0.045, 1.0);
    float ao = aoMap.SampleGrad(LinearWrap, uv, duvdx, duvdy).r;
    float3 N = NormalFromMap(normalMap, mat.NormalFlipY, mat.NormalStrength, uv, duvdx * biasScale, duvdy * biasScale, nW, tW, tSign);
    float3 emissive = (mat.HasEmissive > 0.5) ? emissiveMap.SampleGrad(LinearWrap, uv, duvdx, duvdy).rgb * mat.EmissiveFactor.rgb : 0.0.xxx;

    // Motion (UNJITTERED), same formula as the raster ScreenMotion.
    float4 clipCur  = mul(float4(pW, 1.0), ViewProjCur);
    float4 clipPrev = mul(float4(pW, 1.0), ViewProjPrev);
    float2 uvCur  = (clipCur.xy  / clipCur.w)  * float2(0.5, -0.5) + 0.5;
    float2 uvPrev = (clipPrev.xy / clipPrev.w) * float2(0.5, -0.5) + 0.5;

    OutAlbedo[px]   = float4(albedo, mat.SpecularReflectance);
    OutNormal[px]   = float4(N * 0.5 + 0.5, 1.0);
    OutMaterial[px] = float4(metallic, roughness, ao, mat.Cutout > 0.5 ? 1.0 : 0.0);
    OutEmissive[px] = float4(emissive, 1.0);
    OutMotion[px]   = uvPrev - uvCur;
}
