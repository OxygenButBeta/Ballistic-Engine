// UI Toolkit 2D overlay shader (Dx12UIRenderer). One batched pass over all UI quads.
//
// Vertex format mirrors UIVertex in Dx12UIRenderer.cs. Coordinates arrive in LOGICAL/PANEL pixels
// (top-left origin, +Y down); the vertex shader maps them to NDC via an ortho transform passed in b0.
// `mode` selects how the pixel shader fills the quad. R2: solid + rounded corners + border (one band).
// Text/gradient/image land in later slices but the plumbing is here so the format never changes.

cbuffer UIConstants : register(b0)
{
    float2 InvCanvas;   // 2/canvasW, 2/canvasH  -> pixel->NDC scale
    float  SdfPx;       // SDF spread in atlas texels (FontAtlas.SdfPadding) for AA width
    float  _pad0;
};

// The per-segment bound texture at t0 serves three modes:
//   mode 1 (gradient): a 256x1 RGBA ramp the CPU baked from the stops; the PS computes t and samples it.
//   mode 2 (text):     the R8 SDF glyph atlas (read as .r).
//   mode 3 (image):    the element's RGBA texture, multiplied by the tint in `color`.
// Solid/rounded (mode 0) never samples it. One generic RGBA view covers gradient + image; text reads .r
// of the same view (an R8 atlas exposes its coverage in .r and 0 elsewhere — we only read .r for text).
Texture2D<float4> Tex : register(t0);
SamplerState TexSampler : register(s0);

struct VSIn
{
    float2 pos    : POSITION;     // logical pixels
    float2 uv     : TEXCOORD0;
    float4 color  : COLOR0;       // fill (premultiplied opacity)
    float4 rect   : TEXCOORD1;    // (centerX, centerY, halfW, halfH) in pixels
    float4 radius : TEXCOORD2;    // per-corner radius TL,TR,BR,BL (pixels)
    uint   mode   : TEXCOORD3;    // 0=solid/rounded 1=gradient 2=text 3=image
    float4 border : TEXCOORD4;    // border color RGBA (premultiplied opacity)
    float  bwidth : TEXCOORD5;    // border width (pixels); 0 = no border
};

struct VSOut
{
    float4 pos    : SV_POSITION;
    float4 color  : COLOR0;
    float2 uv     : TEXCOORD0;
    float2 local  : TEXCOORD1;    // pixel position relative to rect center (for SDF)
    float4 rect   : TEXCOORD2;
    float4 radius : TEXCOORD3;
    nointerpolation uint mode : TEXCOORD4;
    float4 border : TEXCOORD5;
    float  bwidth : TEXCOORD6;
};

VSOut VSMain(VSIn i)
{
    VSOut o;
    // Pixel -> NDC: x in [0,W] -> [-1,1]; y in [0,H] -> [1,-1] (flip Y, top-left origin).
    float2 ndc = float2(i.pos.x * InvCanvas.x - 1.0, 1.0 - i.pos.y * InvCanvas.y);
    o.pos    = float4(ndc, 0.0, 1.0);
    o.color  = i.color;
    o.uv     = i.uv;
    o.local  = i.pos - i.rect.xy;
    o.rect   = i.rect;
    o.radius = i.radius;
    o.mode   = i.mode;
    o.border = i.border;
    o.bwidth = i.bwidth;
    return o;
}

// Signed distance to a rounded box centered at origin. `p` = point relative to center, `b` = half-size,
// `r` = corner radius for the quadrant `p` falls in. Standard iq rounded-box SDF: <0 inside, >0 outside.
float sdRoundedBox(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// Pick the corner radius for the quadrant the pixel is in. radius = (TL, TR, BR, BL).
float cornerRadius(float2 p, float4 radius)
{
    // right side selects TR/BR, left selects TL/BL; bottom (p.y>0, +Y down) selects BR/BL.
    float top    = (p.x > 0.0) ? radius.y : radius.x;  // TR : TL
    float bottom = (p.x > 0.0) ? radius.z : radius.w;  // BR : BL
    return (p.y > 0.0) ? bottom : top;
}

float4 PSMain(VSOut i) : SV_Target
{
    // --- mode 2: text (SDF glyph from the R8 atlas, read via .r) ---
    if (i.mode == 2u)
    {
        float dist = Tex.Sample(TexSampler, i.uv).r;   // 0=outside .. ~0.5 edge .. 1=inside
        // screenPxRange AA (the msdfgen-recommended method, P1.7): convert the SDF's normalized distance
        // into SCREEN pixels so the AA band is always ~1px regardless of font scale. SdfPx is the SDF spread
        // in atlas texels (b0); the on-screen texels-per-uv is 1/fwidth(uv), so screen px per SDF-unit =
        // SdfPx * (uv texels per screen px). Derive it from the uv derivative robustly.
        float2 uvPerPx = fwidth(i.uv);
        float texPerPx = max(max(uvPerPx.x, uvPerPx.y) * 1.0, 1e-8);
        // dist is normalized [0..1] over the atlas; its slope per atlas-texel ~ 1/(2*SdfPx). So screen px
        // range of the edge = (2*SdfPx) / (texPerPx * atlasDim) — but we don't have atlasDim here; use the
        // direct sampled-derivative as the primary signal and floor it by a SdfPx-derived minimum so it
        // never collapses at large scale or over-blurs at tiny scale.
        float aaT = fwidth(dist);
        float minBand = 0.5 / max(SdfPx, 1.0);          // floor: keeps a crisp ~half-texel edge at huge scale
        aaT = clamp(aaT, minBand, 0.4);                 // cap: prevents wash-out at tiny scale
        float alpha = smoothstep(0.5 - aaT, 0.5 + aaT, dist);
        if (alpha <= 0.0) discard;
        return float4(i.color.rgb, i.color.a * alpha);
    }

    // --- mode 3: image (RGBA texture * tint), clipped to the rounded box ---
    if (i.mode == 3u)
    {
        float4 tex = Tex.Sample(TexSampler, i.uv);
        float4 outImg = tex * i.color;                 // color carries the tint (premultiplied opacity)
        // Respect rounded corners on images too.
        {
            float2 hh = i.rect.zw;
            float rr = cornerRadius(i.local, i.radius);
            rr = min(rr, min(hh.x, hh.y));
            float dd = sdRoundedBox(i.local, hh, rr);
            float aa = max(fwidth(dd), 1e-4);
            outImg.a *= 1.0 - smoothstep(-aa, aa, dd);
        }
        if (outImg.a <= 0.0) discard;
        return outImg;
    }

    // --- modes 0 (solid) and 1 (gradient): rounded box with optional border ---
    // Mode 1 reuses the whole rounded-box path but takes its fill color from the gradient ramp instead of
    // the vertex color: the CPU bakes the stops into a 256x1 ramp (bound at t0) and writes the per-corner
    // gradient parameter t into uv.x; hardware interpolation gives a smooth per-fragment t (linear exact;
    // radial approximated per-corner, fine for typical small UI boxes). color.a still carries opacity.
    float2 half = i.rect.zw;
    float r = cornerRadius(i.local, i.radius);
    r = min(r, min(half.x, half.y));         // clamp (walker already clamps, belt-and-suspenders)

    float d = sdRoundedBox(i.local, half, r);
    // Anti-alias the outer edge over ~1px using the SDF gradient.
    float aa = max(fwidth(d), 1e-4);
    float coverage = 1.0 - smoothstep(-aa, aa, d);
    if (coverage <= 0.0) discard;

    float4 outc = i.color;          // fill (straight RGBA, premultiplied opacity)
    if (i.mode == 1u)
    {
        // Gradient parameter t computed PER-FRAGMENT (radial needs this — per-corner interpolation never
        // reaches t=0 at the center). uv = local [0..1]; params packed into border/bwidth by the CPU:
        //   bwidth: 0 = linear, 1 = radial
        //   linear: border.xy = direction unit vector (local, +Y down)
        //   radial: border.xy = center [0..1], border.zw = radii (local fraction)
        float t;
        if (i.bwidth < 0.5)
        {
            float2 dir = i.border.xy;
            t = saturate(dot(i.uv - 0.5, dir) + 0.5);
        }
        else
        {
            float2 rd = (i.uv - i.border.xy) / max(i.border.zw, 1e-4);
            t = saturate(length(rd));
        }
        float4 ramp = Tex.Sample(TexSampler, float2(t, 0.5));
        outc = float4(ramp.rgb, ramp.a * i.color.a);
    }
    // Border only applies to the solid path (mode 0). Mode 1 overloads border/bwidth for gradient params.
    float bw = i.bwidth;
    if (i.mode == 0u && bw > 0.0 && i.border.a > 0.0)
    {
        // Border occupies the band [-bw, 0] of the SDF (the outermost bw pixels of the shape):
        // at the outer edge d≈0, deep inside d≈-half. borderMix = 1 in the ring, 0 in the interior.
        float borderMix = smoothstep(-bw - aa, -bw + aa, d);
        // Composite the (possibly translucent) border OVER the fill: standard src-over per channel.
        float ba = i.border.a * borderMix;
        outc.rgb = i.border.rgb * ba + outc.rgb * (1.0 - ba);
        outc.a   = ba + outc.a * (1.0 - ba);
    }

    outc.a *= coverage;             // outer-edge anti-alias
    return outc;
}
