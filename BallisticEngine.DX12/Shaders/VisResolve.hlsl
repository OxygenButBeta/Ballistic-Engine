// R5 — VISIBILITY-BUFFER MATERIAL RESOLVE (compute). Reads the vis id { DrawIndex, (localMeshlet<<8)|localPrim }
// per pixel, looks up that draw's VisDraw record (Mvp/Model/MaterialId + BINDLESS SRV indices for its OWN vertex
// streams + meshlet buffers), fetches the hit triangle's 3 verts, recovers perspective-correct barycentrics from
// the clip-space positions, interpolates pos/normal/uv/tangent, computes UV gradients via QUAD wave ops
// (HW-equivalent ddx/ddy), then decodes the material EXACTLY like GBufferBindless::PSMain (SampleGrad with the
// manual gradients) and writes the SAME fat G-buffer the deferred lighting reads. So the lit result matches the
// raster path bar the inherent sub-pixel raster tie-break — downstream is unchanged.
//
// KEY DESIGN (this engine has PER-MESH vertex buffers + PER-SUBMESH meshlet buffers, not one global geometry
// buffer). A compute pass runs ONCE over the whole screen and can't rebind per pixel — so each DrawIndex's
// geometry buffers are addressed BINDLESSLY: VisDraw carries the bindless heap index of each stream
// (ResourceDescriptorHeap[idx]). One VisDraw per submesh draw; the vis-id's DrawIndex selects it.
//
// Dispatched as 8x8 tiles so the quad (2x2) wave-lane neighbourhood is valid for QuadReadAcross gradient.

cbuffer ResolveCB : register(b0) {
    float4x4 InvViewProj;   // unjittered, for world pos reconstruction if needed
    float4x4 ViewProjCur;   // unjittered current (motion)
    float4x4 ViewProjPrev;  // unjittered previous (motion)
    float2 RtSize;          // render target px size
    float NormalLodBias;
    uint VisIdIndex;        // bindless heap slot of the RG32_UINT vis-id target (read via ResourceDescriptorHeap)
};

struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct Meshlet { uint VertOffset, VertCount, PrimOffset, PrimCount; };
// One per vis-buffer submesh draw. Mvp/Model match the raster path; the *Idx fields are BINDLESS heap indices
// (ResourceDescriptorHeap[idx]) of this draw's OWN geometry buffers, so the resolve reads the right mesh/submesh.
struct VisDraw {
    float4x4 Mvp;   float4x4 Model;
    uint MaterialId;
    uint PosIdx, NrmIdx, UvIdx, TanIdx;          // per-mesh vertex stream SRVs (StructuredBuffer<float3/float2/float4>)
    uint MeshletIdx, MeshletVertIdx, MeshletPrimIdx;  // per-submesh meshlet SRVs (StructuredBuffer<Meshlet/uint/uint>)
};

StructuredBuffer<VisDraw>     VisDraws     : register(t0);
StructuredBuffer<GpuMaterial> GpuMaterials : register(t1);
// VisId (RG32_UINT) is read BINDLESSLY via ResourceDescriptorHeap[VisIdIndex] (a Texture2D can't be a root SRV).

RWTexture2D<float4> OutAlbedo   : register(u0);
RWTexture2D<float4> OutNormal   : register(u1);
RWTexture2D<float4> OutMaterial : register(u2);
RWTexture2D<float4> OutEmissive : register(u3);
RWTexture2D<float2> OutMotion   : register(u4);
SamplerState LinearWrap : register(s0);

float3 NormalFromMap(Texture2D normalMap, float normalFlipY, float normalStrength,
                     float2 uv, float2 dx, float2 dy, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = normalMap.SampleGrad(LinearWrap, uv, dx, dy).rg;
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
    Texture2D<uint2> VisId = ResourceDescriptorHeap[VisIdIndex];
    uint2 id = inBounds ? VisId.Load(int3(px, 0)) : uint2(0, 0);
    // The vis target clears to (0,0); a real hit always has a valid DrawIndex but DrawIndex 0 is legal, so the
    // "no geometry" sentinel is the id.y high bits: a cleared pixel is (0,0) → localMeshlet 0, localPrim 0, which
    // is ALSO a legal hit. Disambiguate with the alpha-style trick: the raster PS writes (DrawIndex+1) so 0 = miss.
    bool hit = id.x != 0;

    float2 uv = 0;
    float3 nW = float3(0, 1, 0), pW = 0, tW = 0; float tSign = 1; uint matId = 0;

    if (hit) {
        uint drawIndex = id.x - 1;          // un-bias (raster wrote DrawIndex+1)
        uint localMeshlet = id.y >> 8;
        uint localPrim = id.y & 0xFF;

        VisDraw vd = VisDraws[drawIndex];
        StructuredBuffer<float3>  Positions    = ResourceDescriptorHeap[vd.PosIdx];
        StructuredBuffer<float3>  Normals      = ResourceDescriptorHeap[vd.NrmIdx];
        StructuredBuffer<float2>  UVs          = ResourceDescriptorHeap[vd.UvIdx];
        StructuredBuffer<float4>  Tangents     = ResourceDescriptorHeap[vd.TanIdx];
        StructuredBuffer<Meshlet> Meshlets     = ResourceDescriptorHeap[vd.MeshletIdx];
        StructuredBuffer<uint>    MeshletVerts = ResourceDescriptorHeap[vd.MeshletVertIdx];
        StructuredBuffer<uint>    MeshletPrims = ResourceDescriptorHeap[vd.MeshletPrimIdx];

        Meshlet m = Meshlets[localMeshlet];
        uint packed = MeshletPrims[m.PrimOffset + localPrim];
        uint l0 = packed & 0xFF, l1 = (packed >> 8) & 0xFF, l2 = (packed >> 16) & 0xFF;
        uint g0 = MeshletVerts[m.VertOffset + l0];
        uint g1 = MeshletVerts[m.VertOffset + l1];
        uint g2 = MeshletVerts[m.VertOffset + l2];

        // Clip-space positions (same Mvp as the raster path).
        float4 c0 = mul(float4(Positions[g0], 1.0), vd.Mvp);
        float4 c1 = mul(float4(Positions[g1], 1.0), vd.Mvp);
        float4 c2 = mul(float4(Positions[g2], 1.0), vd.Mvp);

        // This pixel's NDC (pixel center) → solve perspective-correct barycentrics.
        float2 ndc = (float2(px) + 0.5) / RtSize * float2(2, -2) + float2(-1, 1);
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
        nW = normalize(mul(float4(n, 0.0), vd.Model).xyz);
        tW = normalize(mul(float4(tt.xyz, 0.0), vd.Model).xyz);
        tSign = t0.w;   // handedness is constant across the tri
        pW = mul(float4(Positions[g0] * wa + Positions[g1] * wb + Positions[g2] * wc, 1.0), vd.Model).xyz;
        matId = vd.MaterialId;
    }

    // UV gradients from the QUAD neighbourhood (HW-equivalent ddx/ddy). Valid because we dispatch 8x8 tiles so the
    // 2x2 quad lanes are co-resident. Helper lanes (non-hit) still carry a uv from their own solve; the gradient is
    // only used by hit lanes.
    float2 uvX = QuadReadAcrossX(uv);
    float2 uvY = QuadReadAcrossY(uv);
    float2 duvdx = uv - uvX, duvdy = uv - uvY;
    if ((gtid.x & 1) == 1) duvdx = -duvdx;
    if ((gtid.y & 1) == 1) duvdy = -duvdy;
    float biasScale = exp2(NormalLodBias);

    if (!inBounds) return;
    // MISS pixels (sky / CPU-path geometry that the geometry pass already filled, or pixels not covered by the vis
    // raster): leave the G-buffer UNTOUCHED. The geometry pass cleared the fat G-buffer to 0 and filled any CPU-path
    // (skinned/custom) renderers BEFORE the resolve, so not writing here preserves both the cleared sky pixels and
    // the CPU geometry. Only vis-hit pixels get the resolved material below.
    if (!hit) return;

    GpuMaterial mat = GpuMaterials[matId];
    Texture2D diffuseMap   = ResourceDescriptorHeap[mat.DiffuseIdx];
    Texture2D normalMap    = ResourceDescriptorHeap[mat.NormalIdx];
    Texture2D metallicMap  = ResourceDescriptorHeap[mat.MetallicIdx];
    Texture2D roughnessMap = ResourceDescriptorHeap[mat.RoughnessIdx];
    Texture2D aoMap        = ResourceDescriptorHeap[mat.AoIdx];
    Texture2D emissiveMap  = ResourceDescriptorHeap[mat.EmissiveIdx];

    float4 albedoSample = diffuseMap.SampleGrad(LinearWrap, uv, duvdx, duvdy);
    // Cutout: the vis raster does NOT discard (the vis PS has no albedo), so cutout-transparent texels are present
    // in the vis-id with valid depth. The resolve discards them to the cleared G-buffer (closest raster parity we
    // can get without an alpha-aware raster PS — a documented vis-buffer cutout limitation).
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
