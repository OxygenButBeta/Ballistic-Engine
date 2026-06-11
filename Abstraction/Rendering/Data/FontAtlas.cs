using System.Collections.Generic;

namespace BallisticEngine;

// CPU-side baked font: a single-channel SDF atlas plus per-glyph metrics. Lives in Abstraction/ with
// the other CPU render-data types (MeshData, TextureData) — no GL, no font library here. The asset
// pipeline's FontBaker produces it from a .ttf; the GL backend uploads Pixels as an R8 texture and
// positions glyph quads from the metrics. Distances are stored as SDF so text stays crisp at any
// font size / panel scale (the shader thresholds the field).
public sealed class FontAtlas
{
    // One glyph's placement in the atlas and its layout metrics, all in PIXELS at the atlas's baked
    // pixel-height. The renderer scales these by (requestedFontSize / BakePixelHeight).
    public struct Glyph
    {
        public char Codepoint;
        // Atlas sub-rect in texel coordinates (x0,y0 = top-left, x1,y1 = bottom-right).
        public int AtlasX0, AtlasY0, AtlasX1, AtlasY1;
        // Offset from the pen origin (baseline, current x) to the glyph quad's top-left, in pixels.
        public float OffsetX, OffsetY;
        // Glyph quad size in pixels (matches the atlas sub-rect minus SDF padding handling).
        public float Width, Height;
        // How far to advance the pen after drawing this glyph, in pixels.
        public float Advance;
    }

    // R8 coverage/SDF data, row-major, AtlasWidth*AtlasHeight bytes (255 = inside, 0 = outside, the
    // edge crosses ~128). One channel; the shader reads .r.
    public byte[] Pixels;
    public int AtlasWidth;
    public int AtlasHeight;

    // The pixel height the glyphs were rasterized at. Requested font sizes scale relative to this.
    public float BakePixelHeight;

    // The SDF spread (padding) in pixels baked around each glyph edge — the shader's smoothstep width
    // derives from this so antialiasing matches the bake.
    public float SdfPadding;

    // Vertical metrics in pixels at BakePixelHeight: distance above/below the baseline + recommended
    // line advance. Used to position the baseline and lay out multi-line text.
    public float Ascent;
    public float Descent;   // typically negative (below baseline)
    public float LineHeight;

    // codepoint -> glyph, for O(1) lookup during layout. Missing glyphs fall back to the replacement
    // (or are skipped).
    public Dictionary<char, Glyph> Glyphs = new();

    public bool TryGetGlyph(char c, out Glyph glyph) => Glyphs.TryGetValue(c, out glyph);

    // CPU text measurement (no GL) so the layout engine can size a Label around its glyphs. Returns
    // the advance width + line height at `fontSize`, including letter spacing between glyphs.
    public (float width, float height) Measure(string text, float fontSize, float letterSpacing = 0f)
    {
        float scale = BakePixelHeight > 0 ? fontSize / BakePixelHeight : 1f;
        float lineH = LineHeight * scale;
        if (string.IsNullOrEmpty(text)) return (0f, lineH);
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
            if (Glyphs.TryGetValue(text[i], out var g))
                w += g.Advance * scale + (i < text.Length - 1 ? letterSpacing : 0f);
        return (w, lineH);
    }
}
