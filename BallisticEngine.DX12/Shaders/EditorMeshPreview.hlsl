// Editor mesh-thumbnail shader (DX12 port of EditorMeshPreview_*.glsl). Simple lambert from a 3/4 view.
// Matrices uploaded transposed + multiplied vector-first (codebase convention).
cbuffer Cb : register(b0) { float4x4 Mvp; }

struct VSIn { float3 pos : POSITION; float3 normal : NORMAL; };
struct PSIn { float4 pos : SV_POSITION; float3 n : NORMAL; };

PSIn VSMain(VSIn i) {
    PSIn o;
    o.pos = mul(float4(i.pos, 1.0), Mvp);
    o.n = i.normal;
    return o;
}

float4 PSMain(PSIn i) : SV_Target {
    float3 l = normalize(float3(0.5, 0.8, 0.6));
    float d = max(dot(normalize(i.n), l), 0.0) * 0.75 + 0.3;
    return float4(float3(0.78, 0.80, 0.84) * d, 1.0);
}
